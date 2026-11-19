namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private readonly Dictionary<(string Schema, string Table), Dictionary<string, MigrationOperation>>
        _acceptedDeclaredChecks = [];

    private Dictionary<SafeMigrationOperation, List<CheckRenameDependency>> _checkRenameDependencies = [];

    /// <summary>Resolves parsed CHECK member roles with native catalog equality in bounded metadata batches.</summary>
    private async Task ReadProjectedCheckRenameDependenciesAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<MigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        _acceptedDeclaredChecks.Clear();
        _checkRenameDependencies.Clear();
        if (operations.All(static operation => operation is not SafeMigrationOperation
            {
                Intent: RenameColumnIntent or DropColumnIntent,
            })
            || operations.All(static operation => operation is not (AddCheckConstraintOperation
                or CreateTableOperation { CheckConstraints.Count: > 0 }
                or SafeMigrationOperation
                {
                    Intent: EnsureCheckConstraintIntent
                    or EnsureTableIntent { Definition.CheckConstraints.Count: > 0 },
                })))
        {
            return;
        }

        Dictionary<(string Schema, string Table),
            Dictionary<string, List<(string[]? Names, MigrationOperation Declaration)>>> declared = [];

        List<(SafeMigrationOperation Rename, CheckRenameDependency Dependency, string[]? Names)> candidates = [];
        List<(SafeMigrationOperation Drop, CheckRenameDependency Dependency, string[]? Names)> drops = [];

        // WHY: The stable indexed snapshot preserves operation and declaration order.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var operationIndex0 = 0; operationIndex0 < operations.Count; operationIndex0++)
        {
            var operation = operations[operationIndex0];

            if (operation is not SafeMigrationOperation safe)
            {
                switch (operation)
                {
                    case CreateTableOperation table:
                        var checks = GetChecks(table.Name, table.Schema);
                        checks.Clear();

                        // WHY: The stable indexed snapshot preserves operation and declaration order.
                        // ReSharper disable once ForCanBeConvertedToForeach
                        // ReSharper disable once LoopCanBeConvertedToQuery
                        for (var checkIndex0 = 0; checkIndex0 < table.CheckConstraints.Count; checkIndex0++)
                        {
                            var check = table.CheckConstraints[checkIndex0];

                            checks[check.Name] = [(GetProviderCheckSourceNames(check), operation)];
                        }

                        break;
                    case AddCheckConstraintOperation check:
                        AddCheck(check.Table, check.Schema, check.Name, GetProviderCheckSourceNames(check), operation);
                        break;
                    case DropCheckConstraintOperation check:
                        if (declared.TryGetValue((check.Schema ?? "dbo", check.Table), out var dropped))
                        {
                            dropped.Remove(check.Name);
                        }

                        break;
                    case DropTableOperation table:
                        declared.Remove((table.Schema ?? "dbo", table.Name));
                        break;
                    case RenameTableOperation table:
                        if (declared.Remove((table.Schema ?? "dbo", table.Name), out var moved))
                        {
                            declared[(table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name)] = moved;
                        }

                        break;
                    case SqlOperation:
                        declared.Clear();
                        break;
                }

                continue;
            }

            switch (safe.Intent)
            {
                case EnsureTableIntent table:
                    // WHY: The stable indexed snapshot preserves operation and declaration order.
                    // ReSharper disable once ForCanBeConvertedToForeach
                    // ReSharper disable once LoopCanBeConvertedToQuery
                    for (var checkIndex0 = 0; checkIndex0 < table.Definition.CheckConstraints.Count; checkIndex0++)
                    {
                        var check = table.Definition.CheckConstraints[checkIndex0];

                        AddCheck(
                            table.Definition.Table,
                            table.Definition.Schema,
                            check.Name,
                            SqlServerSafeMigrationCatalogSqlBuilder.GetCheckRenamePrerequisiteColumns(check),
                            safe);
                    }

                    break;
                case EnsureCheckConstraintIntent check:
                    AddCheck(
                        check.Definition.Table,
                        check.Definition.Schema,
                        check.Definition.Name,
                        SqlServerSafeMigrationCatalogSqlBuilder.GetCheckRenamePrerequisiteColumns(check.Definition),
                        safe);

                    break;
                case DropCheckConstraintIntent check:
                    if (declared.TryGetValue((check.Schema ?? "dbo", check.Table), out var dropped))
                    {
                        dropped.Remove(check.Name);
                    }

                    break;
                case DropTableIntent table:
                    declared.Remove((table.Schema ?? "dbo", table.Table));
                    break;
                case RenameTableIntent table:
                    if (declared.Remove((table.Schema ?? "dbo", table.Name), out var moved))
                    {
                        declared[(table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name)] = moved;
                    }

                    break;
                case RenameColumnIntent rename when declared.TryGetValue(
                    (rename.Schema ?? "dbo", rename.Table),
                    out var checks):
                    foreach (var (name, history) in checks)
                    {
                        // WHY: ExistenceOnly may accept a different request without
                        // executing it. Keep prior candidates until an owned drop;
                        // the actual accepted declaration reference selects exactly
                        // one physical contract during sequential projection.
                        // WHY: The stable indexed snapshot preserves declaration order without mutating the source.
                        // ReSharper disable once ForCanBeConvertedToForeach
                        // ReSharper disable once LoopCanBeConvertedToQuery
                        for (var historyIndex0 = 0; historyIndex0 < history.Count; historyIndex0++)
                        {
                            var check = history[historyIndex0];

                            candidates.Add(
                                (safe, new CheckRenameDependency(
                                    check.Declaration,
                                    name,
                                    rename.Table,
                                    rename.Schema,
                                    check.Names is null), check.Names));
                        }
                    }

                    break;
                case DropColumnIntent drop when declared.TryGetValue(
                    (drop.Schema ?? "dbo", drop.Table),
                    out var dropChecks):
                    foreach (var (name, history) in dropChecks)
                    {
                        // WHY: The stable indexed snapshot preserves declaration order without mutating the source.
                        // ReSharper disable once ForCanBeConvertedToForeach
                        // ReSharper disable once LoopCanBeConvertedToQuery
                        for (var historyIndex1 = 0; historyIndex1 < history.Count; historyIndex1++)
                        {
                            var check = history[historyIndex1];

                            drops.Add(
                                (safe, new CheckRenameDependency(
                                    check.Declaration,
                                    name,
                                    drop.Table,
                                    drop.Schema,
                                    check.Names is null), check.Names));
                        }
                    }

                    break;
            }
        }

        if (candidates.Count == 0
            && drops.Count == 0)
        {
            return;
        }

        Dictionary<SafeMigrationOperation, List<CheckRenameDependency>> captured = [];

        // WHY: The stable indexed snapshot preserves declaration order without mutating the source.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var candidateIndex0 = 0; candidateIndex0 < drops.Count; candidateIndex0++)
        {
            var candidate = drops[candidateIndex0];

            var drop = (DropColumnIntent)candidate.Drop.Intent;
            var dependency = ClassifyDeclaredCheckDropSource(drop, candidate.Names);
            if (dependency == false)
            {
                continue;
            }

            if (!captured.TryGetValue(candidate.Drop, out var dependencies))
            {
                dependencies = [];
                captured.Add(candidate.Drop, dependencies);
            }

            dependencies.Add(candidate.Dependency with { Unknown = dependency is null });
        }

        if (candidates.Count == 0)
        {
            // No DROP-only round trip: exact authored roles and physical ids
            // already captured by the column batch own this narrower proof.
            _checkRenameDependencies = captured;

            return;
        }

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            candidates.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
            commandTimeout,
            (command, ordinal) =>
            {
                var (operation, _, names) = candidates[ordinal];
                var rename = (RenameColumnIntent)operation.Intent;
                var match = names switch
                {
                    null => "1",
                    { Length: 0 } => "0",
                    _ => $"""
                        CASE WHEN EXISTS(SELECT 1 FROM(VALUES {string.Join(
                            ",", names.Select(static name => "(" + KeyLiteral(name) + ")"))}) refs(name)
                        WHERE refs.name COLLATE CATALOG_DEFAULT={KeyLiteral(rename.Name)} COLLATE
                        CATALOG_DEFAULT)
                        THEN 1 ELSE 0 END
                        """,
                };

                // WHY: Native identifier equality includes accent and width
                // rules. Neither OrdinalIgnoreCase nor a data-collation guess
                // can substitute for the active catalog's exact comparison.
                command.CommandText = "SELECT " + ordinal.ToString(CultureInfo.InvariantCulture) + "," + match + ";";

                return new SafeMigrationCatalogProbeStatement(1, 0, ordinal);
            },
            async (reader, ordinal, _, callbackCancellationToken) =>
            {
                if (reader.FieldCount != 2
                    || !await reader.ReadAsync(callbackCancellationToken)
                    || reader.IsDBNull(0)
                    || reader.GetInt32(0) != ordinal
                    || reader.IsDBNull(1))
                {
                    throw new InvalidOperationException("SQL Server omitted its CHECK source identity proof.");
                }

                var dependent = reader.GetInt32(1);
                if (dependent is < 0 or > 1
                    || await reader.ReadAsync(callbackCancellationToken))
                {
                    throw new InvalidOperationException("SQL Server returned an invalid CHECK source identity proof.");
                }

                if (dependent == 1)
                {
                    var candidate = candidates[ordinal];
                    if (!captured.TryGetValue(candidate.Rename, out var dependencies))
                    {
                        dependencies = [];
                        captured.Add(candidate.Rename, dependencies);
                    }

                    dependencies.Add(candidate.Dependency);
                }
            },
            transaction,
            SqlServerCatalogParameterBindings.MaximumParameters,
            cancellationToken);

        _checkRenameDependencies = captured;

        return;

        Dictionary<string, List<(string[]?, MigrationOperation)>> GetChecks(
            string table,
            string? schema
        )
        {
            if (!declared.TryGetValue((schema ?? "dbo", table), out var checks))
            {
                checks = [];
                declared.Add((schema ?? "dbo", table), checks);
            }

            return checks;
        }

        void AddCheck(
            string table,
            string? schema,
            string name,
            string[]? names,
            MigrationOperation declaration
        )
        {
            var checks = GetChecks(table, schema);
            if (!checks.TryGetValue(name, out var history))
            {
                history = [];
                checks.Add(name, history);
            }

            history.Add((names, declaration));
        }
    }

    /// <summary>
    /// Qualifies declared CHECK source roles with exact spelling or current native column identities.
    /// </summary>
    private bool? ClassifyDeclaredCheckDropSource(
        DropColumnIntent drop,
        string[]? names
    )
    {
        if (names is null)
        {
            return null;
        }

        if (names.Contains(drop.Name, StringComparer.Ordinal))
        {
            return true;
        }

        if (names.Length == 0)
        {
            return false;
        }

        if (!_projectedColumnLayouts.TryGetValue((drop.Schema ?? "dbo", drop.Table), out var layout)
            || layout is null
            || !layout.Bindings.TryGetValue(drop.Name, out var source)
            || source <= 0)
        {
            return null;
        }

        var complete = true;
        foreach (var name in names)
        {
            if (!layout.Bindings.TryGetValue(name, out var member)
                || member <= 0)
            {
                complete = false;
            }
            else if (member == source)
            {
                return true;
            }
        }

        return complete
            ? false
            : null;
    }

    private SafeMigrationProviderAnalysis? QualifyProjectedCheckSourceRename(
        SafeMigrationOperation operation
    )
    {
        if (_checkRenameDependencies.TryGetValue(operation, out var dependencies))
        {
            // WHY: The stable indexed snapshot preserves declaration order without mutating the source.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var dependencyIndex0 = 0; dependencyIndex0 < dependencies.Count; dependencyIndex0++)
            {
                var dependency = dependencies[dependencyIndex0];

                if (_acceptedDeclaredChecks.TryGetValue((dependency.Schema ?? "dbo", dependency.Table), out var checks)
                    && checks.TryGetValue(dependency.Name, out var accepted)
                    && ReferenceEquals(accepted, dependency.Declaration))
                {
                    return new SafeMigrationProviderAnalysis(
                        dependency.Unknown
                            ? SafeMigrationObservedState.Unsupported
                            : SafeMigrationObservedState.Different,
                        SafeMigrationRepairCapability.None,
                        false,
                        dependency.Unknown
                            ? "projected_check_source_dependency_unproven"
                            : "projected_check_source_dependency");
                }
            }
        }

        return null;
    }

    private void ObserveProjectedCheckDependencies(
        SafeMigrationOperation operation,
        SafeMigrationDecision decision
    )
    {
        if (_checkRenameDependencies.Count == 0
            || (!decision.ShouldExecute && decision.Action != SafeMigrationAction.NoOp))
        {
            return;
        }

        switch (operation.Intent)
        {
            case EnsureTableIntent table:
                var tableKey = (table.Definition.Schema ?? "dbo", table.Definition.Table);
                if (!_acceptedDeclaredChecks.TryGetValue(tableKey, out var declared))
                {
                    declared = [];
                    _acceptedDeclaredChecks.Add(tableKey, declared);
                }

                // WHY: The stable indexed snapshot preserves operation and declaration order.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var checkIndex1 = 0; checkIndex1 < table.Definition.CheckConstraints.Count; checkIndex1++)
                {
                    var check = table.Definition.CheckConstraints[checkIndex1];

                    declared[check.Name] = operation;
                }

                break;
            case EnsureCheckConstraintIntent check:
                var key = (check.Definition.Schema ?? "dbo", check.Definition.Table);
                if (!_acceptedDeclaredChecks.TryGetValue(key, out var checks))
                {
                    checks = [];
                    _acceptedDeclaredChecks.Add(key, checks);
                }

                checks[check.Definition.Name] = operation;
                break;
            case DropCheckConstraintIntent check:
                if (_acceptedDeclaredChecks.TryGetValue((check.Schema ?? "dbo", check.Table), out var dropped))
                {
                    dropped.Remove(check.Name);
                }

                break;
            case DropTableIntent table:
                _acceptedDeclaredChecks.Remove((table.Schema ?? "dbo", table.Table));
                break;
            case RenameTableIntent table:
                if (_acceptedDeclaredChecks.Remove((table.Schema ?? "dbo", table.Name), out var moved))
                {
                    _acceptedDeclaredChecks[(table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name)]
                        = moved;
                }

                break;
        }
    }

    private void ObserveProviderCheckDependencies(
        MigrationOperation operation
    )
    {
        if (_checkRenameDependencies.Count == 0)
        {
            return;
        }

        switch (operation)
        {
            case CreateTableOperation table:
                Dictionary<string, MigrationOperation> declared = [];

                // WHY: The stable indexed snapshot preserves operation and declaration order.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var checkIndex1 = 0; checkIndex1 < table.CheckConstraints.Count; checkIndex1++)
                {
                    var check = table.CheckConstraints[checkIndex1];

                    declared[check.Name] = operation;
                }

                _acceptedDeclaredChecks[(table.Schema ?? "dbo", table.Name)] = declared;
                break;
            case AddCheckConstraintOperation check:
                var key = (check.Schema ?? "dbo", check.Table);
                if (!_acceptedDeclaredChecks.TryGetValue(key, out var added))
                {
                    added = [];
                    _acceptedDeclaredChecks.Add(key, added);
                }

                added[check.Name] = operation;
                break;
            case DropCheckConstraintOperation check:
                if (_acceptedDeclaredChecks.TryGetValue((check.Schema ?? "dbo", check.Table), out var checks))
                {
                    checks.Remove(check.Name);
                }

                break;
            case DropTableOperation table:
                _acceptedDeclaredChecks.Remove((table.Schema ?? "dbo", table.Name));
                break;
            case RenameTableOperation table:
                if (_acceptedDeclaredChecks.Remove((table.Schema ?? "dbo", table.Name), out var moved))
                {
                    _acceptedDeclaredChecks[(table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name)]
                        = moved;
                }

                break;
            case SqlOperation:
                _acceptedDeclaredChecks.Clear();
                break;
        }
    }

    /// <summary>Captures only parsed native CHECK member roles from an explicit EF declaration.</summary>
    private static string[]? GetProviderCheckSourceNames(
        AddCheckConstraintOperation check
    )
    {
        if (!SafeMigrationSqlExpressionParser.TryParseCapturedSource(check.Sql, out var expression))
        {
            return null;
        }

        var definition = ExpectedCheckConstraintDefinition.FromCapturedExpression(
            check.Name,
            check.Table,
            expression,
            check.Sql,
            check.Schema);

        return SqlServerSafeMigrationCatalogSqlBuilder.GetCheckRenamePrerequisiteColumns(definition);
    }

    private sealed record CheckRenameDependency(
        MigrationOperation Declaration,
        string Name,
        string Table,
        string? Schema,
        bool Unknown
    );
}
