namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private readonly Dictionary<SafeMigrationOperation, IntegerWideningProof> _integerWideningProofs = [];
    private readonly HashSet<TransitionDependency> _acceptedTransitionDrops = [];
    private readonly Dictionary<TransitionDependency, TransitionDependency> _transitionDropIdentities = [];
    private readonly Dictionary<(string Schema, string Table), Dictionary<string, ExpectedColumnDefinition>>
        _acceptedTransitionColumns = [];
    private readonly HashSet<(string Schema, string Table)> _transitionStructureChanged = [];
    private bool _transitionDataChangedGlobally;

    private void ResetProjectedColumnTransitions(
        bool retainInvalidatedCheckPredicates = false
    )
    {
        _integerWideningProofs.Clear();
        _acceptedTransitionDrops.Clear();
        _transitionDropIdentities.Clear();
        _acceptedTransitionColumns.Clear();
        _transitionStructureChanged.Clear();
        _transitionDataChangedGlobally = retainInvalidatedCheckPredicates;

        if (!retainInvalidatedCheckPredicates)
        {
            _checkPredicateProofs.Clear();
            _ddlRowDependentOperations.Clear();
            _ddlRowEffectRisk = SqlServerDdlRowEffectRisk.None;
            _ddlRowFreshnessInvalidated = false;
            _hasEnabledDmlTriggers = false;
            _freshnessInvalidatedByDml = false;
            _ddlRowOriginOrdinal = null;
            _currentProjectedOperationOrdinal = null;
        }

        _checkPredicateUnknownTables.Clear();
    }

    /// <summary>Captures source-bound candidates and named dependencies without assuming future drops.</summary>
    /// <param name="connection">The metadata-visible analysis connection.</param>
    /// <param name="transaction">The caller's analysis transaction.</param>
    /// <param name="operations">The immutable ordered stream.</param>
    /// <param name="liveAnalyses">The initial classifications in the same immutable stream order.</param>
    /// <param name="canReadExpressionDependencies">The invocation's proven protected catalog SELECT permission.</param>
    /// <param name="commandTimeout">The active command timeout.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task completing after the bounded transition certificates have been captured.</returns>
    internal async Task ReadProjectedColumnTransitionsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> liveAnalyses,
        bool canReadExpressionDependencies,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        if (operations.Count != liveAnalyses.Count)
        {
            throw new ArgumentException("Transition capture requires one live classification per operation.",
                nameof(liveAnalyses));
        }

        // WHY: A matching target is already widened, so its captured live
        // source cannot satisfy the authored old integer contract. Such a
        // certificate is discarded by qualification anyway. Excluding it also
        // prevents unused replay growth from reserving another column's budget.
        var candidates = operations.Where((operation, index) => canReadExpressionDependencies
            && liveAnalyses[index].ObservedState != SafeMigrationObservedState.Matching
            && !liveAnalyses[index].IsInvariantUnsupported
            && operation.Intent is AlterColumnIntent
            { OldDefinition: not null } column
            && _catalogSqlBuilder.IsSupportedIntegerWidening(column.OldDefinition, column.Definition))
            .Distinct()
            .ToArray();

        if (candidates.Length == 0 && !operations.Any(static operation =>
                operation.Intent is EnsureCheckConstraintIntent))
        {
            return;
        }

        var growth = candidates.GroupBy(static operation =>
            {
                var column = (AlterColumnIntent)operation.Intent;

                return (column.Schema ?? "dbo", column.Table);
            })
            .ToDictionary(static group => group.Key,
                group => group.Sum(operation =>
                    _catalogSqlBuilder.IntegerWideningGrowth((AlterColumnIntent)operation.Intent)));

        var analyses = new SafeMigrationProviderAnalysis[candidates.Length];

        for (var offset = 0; offset < candidates.Length;
            offset += SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture)
        {
            var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture,
                candidates.Length - offset);

            var plans = candidates.Skip(offset).Take(count).Select(operation =>
            {
                var column = (AlterColumnIntent)operation.Intent;
                var reserved = growth[(column.Schema ?? "dbo", column.Table)]
                    - _catalogSqlBuilder.IntegerWideningGrowth(column);

                return _catalogSqlBuilder.BuildIntegerWideningCapturePlan(column, reserved);
            }).ToArray();

            await ReadCatalogCaptureAsync(connection, transaction, commandTimeout, plans, offset, analyses,
                cancellationToken);
        }

        // WHY: Most admissible columns have no named blocker. Allocate a set
        // only when a catalog row actually belongs to that candidate; immutable
        // empty arrays carry dependency-free proofs without one empty set each.
        var dependencies = new HashSet<TransitionDependency>?[candidates.Length];
        var requestedDrops = operations.Select(static operation => TransitionDrop(operation.Intent))
            .OfType<TransitionDependency>()
            .Distinct()
            .ToArray();

        var capturedDrops = new Dictionary<TransitionDependency, TransitionDependency>();
        var dependencyStatements = (candidates.Length
                + SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement - 1)
            / SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;

        var dropStatements = (requestedDrops.Length + SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement - 1)
            / SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;

        await SafeMigrationCatalogProbeBatch.ReadAsync(connection,
            dependencyStatements + dropStatements,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes, commandTimeout,
            (command, slot) =>
            {
                var dropCapture = slot >= dependencyStatements;
                var offset = (dropCapture ? slot - dependencyStatements : slot)
                    * SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;

                var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                    (dropCapture ? requestedDrops.Length : candidates.Length) - offset);

                command.CommandText = dropCapture
                    ? string.Join(" UNION ALL ", requestedDrops.Skip(offset).Take(count)
                        .Select((drop, index) => BuildTransitionDropIdentitySql(offset + index, drop)))
                    : string.Join(" UNION ALL ", candidates.Skip(offset).Take(count)
                        .Select((operation, index) => BuildTransitionDependenciesSql(
                            offset + index, (AlterColumnIntent)operation.Intent)));

                return new SafeMigrationCatalogProbeStatement(1, 0, offset);
            },
            async (reader, slot, _, token) =>
            {
                var dropCapture = slot >= dependencyStatements;
                var offset = (dropCapture ? slot - dependencyStatements : slot)
                    * SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;

                var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                    (dropCapture ? requestedDrops.Length : candidates.Length) - offset);

                while (await reader.ReadAsync(token))
                {
                    var ordinal = reader.GetInt32(0);
                    if (ordinal < offset
                        || ordinal >= offset + count)
                    {
                        throw new InvalidOperationException(
                            "SQL Server returned an unowned integer transition dependency.");
                    }

                    var identity = new TransitionDependency(reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4));

                    if (dropCapture)
                    {
                        if (identity.Kind != requestedDrops[ordinal].Kind
                            || !capturedDrops.TryAdd(requestedDrops[ordinal], identity))
                        {
                            throw new InvalidOperationException(
                                "SQL Server returned an ambiguous transition drop identity.");
                        }
                    }
                    else
                    {
                        (dependencies[ordinal] ??= []).Add(identity);
                    }
                }
            }, cancellationToken, transaction, SqlServerCatalogParameterBindings.MaximumParameters);

        for (var index = 0; index < candidates.Length; index++)
        {
            CaptureProjectedIntegerWidening(candidates[index], analyses[index],
                (IReadOnlyCollection<TransitionDependency>?)dependencies[index]
                    ?? Array.Empty<TransitionDependency>());
        }

        foreach (var pair in capturedDrops)
        {
            CaptureProjectedTransitionDropIdentity(pair.Key, pair.Value);
        }
    }

    /// <summary>Retains a requested drop's physical identity after bounded catalog-collation resolution.</summary>
    /// <param name="requested">The authored kind, schema, table, and name used by the accepted-drop observer.</param>
    /// <param name="physical">The unique corresponding public-catalog identity returned by the owned query.</param>
    internal void CaptureProjectedTransitionDropIdentity(
        TransitionDependency requested,
        TransitionDependency physical
    ) => _transitionDropIdentities[requested] = physical;

    private TransitionDependency ResolveTransitionDropIdentity(
        TransitionDependency requested
    ) => _transitionDropIdentities.GetValueOrDefault(requested, requested);

    private static TransitionDependency? TransitionDrop(
        SafeMigrationIntent intent
    ) => intent switch
    {
        DropIndexIntent value => new TransitionDependency("index", value.Schema ?? "dbo", value.Table, value.Name),
        DropPrimaryKeyIntent value
            => new TransitionDependency("primary", value.Schema ?? "dbo", value.Table, value.Name),
        DropUniqueConstraintIntent value
            => new TransitionDependency("unique", value.Schema ?? "dbo", value.Table, value.Name),
        DropCheckConstraintIntent value
            => new TransitionDependency("check", value.Schema ?? "dbo", value.Table, value.Name),
        DropForeignKeyIntent value
            => new TransitionDependency("foreign", value.Schema ?? "dbo", value.Table, value.Name),
        _ => null,
    };

    private static string BuildTransitionDropIdentitySql(
        int ordinal,
        TransitionDependency requested
    ) => $"SELECT {ordinal},d.kind,s.name,t.name,d.name FROM sys.schemas s "
        + "JOIN sys.tables t ON t.schema_id=s.schema_id CROSS APPLY ("
        + "SELECT N'index' AS kind,i.name FROM sys.indexes i WHERE i.object_id=t.object_id AND i.index_id>0 "
        + "AND NOT EXISTS(SELECT 1 FROM sys.key_constraints kc WHERE kc.parent_object_id=i.object_id "
        + "AND kc.unique_index_id=i.index_id) UNION ALL SELECT CASE o.type WHEN N'PK' THEN N'primary' "
        + "WHEN N'UQ' THEN N'unique' WHEN N'C' THEN N'check' ELSE N'foreign' END,o.name FROM sys.objects o "
        + "WHERE o.parent_object_id=t.object_id AND o.type IN(N'PK',N'UQ',N'C',N'F')) d "
        + $"WHERE s.name={KeyLiteral(requested.Schema)} COLLATE CATALOG_DEFAULT "
        + $"AND t.name={KeyLiteral(requested.Table)} COLLATE CATALOG_DEFAULT "
        + $"AND d.kind={KeyLiteral(requested.Kind)} COLLATE CATALOG_DEFAULT "
        + $"AND d.name={KeyLiteral(requested.Name)} COLLATE CATALOG_DEFAULT";

    /// <summary>Publishes one source-bound candidate after classification and dependency capture succeed.</summary>
    /// <param name="operation">The immutable operation whose exact source was classified.</param>
    /// <param name="analysis">The guarded candidate's physical and optional row proof.</param>
    /// <param name="dependencies">The owned dependency collection, no longer mutated by the capture.</param>
    internal void CaptureProjectedIntegerWidening(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        IReadOnlyCollection<TransitionDependency> dependencies
    ) => _integerWideningProofs[operation] = new IntegerWideningProof(analysis, dependencies);

    private string BuildTransitionDependenciesSql(
        int ordinal,
        AlterColumnIntent column
    )
    {
        var table = _sqlGenerationHelper.DelimitIdentifier(column.Table, column.Schema ?? "dbo");
        var id = $"OBJECT_ID({KeyLiteral(table)},N'U')";
        var columnId = $"(SELECT c.column_id FROM sys.columns c WHERE c.object_id={id} "
            + $"AND c.name={KeyLiteral(column.Definition.Name)})";

        return $"SELECT {ordinal}, CASE WHEN kc.type=N'PK' THEN N'primary' WHEN kc.type=N'UQ' THEN N'unique' "
            + "ELSE N'index' END, s.name,t.name,COALESCE(kc.name,i.name) FROM sys.index_columns ic "
            + "JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id "
            + "JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id "
            + "LEFT JOIN sys.key_constraints kc ON kc.parent_object_id=i.object_id AND kc.unique_index_id=i.index_id "
            + $"WHERE ic.object_id={id} AND ic.column_id={columnId} AND i.index_id>0 UNION ALL "
            + $"SELECT {ordinal},N'check',s.name,t.name,cc.name FROM sys.check_constraints cc "
            + "JOIN sys.tables t ON t.object_id=cc.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id "
            + $"WHERE cc.parent_object_id={id} AND (cc.parent_column_id=0 OR cc.parent_column_id={columnId} "
            + "OR EXISTS(SELECT 1 FROM sys.sql_expression_dependencies d WHERE d.referencing_id=cc.object_id "
            + $"AND d.referenced_id={id} AND d.referenced_minor_id={columnId})) UNION ALL "
            + $"SELECT {ordinal},N'foreign',s.name,t.name,fk.name FROM sys.foreign_keys fk "
            + "JOIN sys.tables t ON t.object_id=fk.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id "
            + "WHERE EXISTS(SELECT 1 FROM sys.foreign_key_columns fkc WHERE fkc.constraint_object_id=fk.object_id "
            + $"AND ((fkc.parent_object_id={id} AND fkc.parent_column_id={columnId}) "
            + $"OR (fkc.referenced_object_id={id} AND fkc.referenced_column_id={columnId})))";
    }

    private SafeMigrationProviderAnalysis QualifyProjectedIntegerWidening(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not AlterColumnIntent column || analysis.IsInvariantUnsupported
            || !_integerWideningProofs.TryGetValue(operation, out var proof))
        {
            return analysis;
        }

        var key = (column.Schema ?? "dbo", column.Table);
        if (_transitionStructureChanged.Contains(key)
            || _acceptedTransitionColumns.TryGetValue(key, out var accepted)
                && accepted.TryGetValue(column.Definition.Name, out var changed)
                && !SafeMigrationDefinitionEquivalence.Column(changed, column.OldDefinition!)
            || columns.TryGetProjectedColumn(column.Table, column.Schema, column.Definition.Name, out var projected)
                && !SafeMigrationDefinitionEquivalence.Column(projected, column.OldDefinition!))
        {
            return analysis;
        }

        // WHY: Superset membership retains the exact physical identities while
        // avoiding a predicate closure for every projected widening proof.
        if (proof.Analysis.RepairCapability != SafeMigrationRepairCapability.Safe
            || proof.Analysis.ObservedState != SafeMigrationObservedState.Different
            || !_acceptedTransitionDrops.IsSupersetOf(proof.Dependencies))
        {
            return analysis;
        }

        if (column.OldDefinition!.IsNullable && !column.Definition.IsNullable
            && _transitionDataChangedGlobally)
        {
            return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationRepairCapability.None, false, "projected_data_state_unknown");
        }

        return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "column_integer_widening_safe",
            SafeMigrationOperationalImpact.TableRewritePossible, [])
        {
            RepairPreservesLiveValueDomain = true,
            RequiresLiveDataProof = column.OldDefinition.IsNullable && !column.Definition.IsNullable,
        };
    }

    /// <inheritdoc />
    SafeMigrationProviderAnalysis ISafeMigrationProjectedColumnAnalyzer.ValidateProjectedAlterColumn(
        AlterColumnIntent intent,
        ExpectedColumnDefinition source,
        SafeMigrationProjectedAlterColumnContext context,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
        => context.CanReuseLiveProof && liveAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
            && _catalogSqlBuilder.IsSupportedIntegerWidening(source, intent.Definition)
            ? liveAnalysis : projectedAnalysis;

    private void ObserveProjectedColumnTransition(
        SafeMigrationOperation operation,
        SafeMigrationDecision decision
    )
    {
        if (!decision.ShouldExecute || operation.Intent is RenameTableIntent rename
            && (rename.NewName ?? rename.Name) == rename.Name
            && (rename.NewSchema ?? rename.Schema ?? "dbo") == (rename.Schema ?? "dbo"))
        {
            return;
        }

        ObserveProjectedCheckPredicate(operation, decision);
        ObserveProjectedDdlRowEffects(operation, decision);

        var dependency = TransitionDrop(operation.Intent);

        if (dependency is { } drop)
        {
            // WHY: Catalog collation, not CLR case folding, establishes the
            // physical object removed by an accepted authored spelling.
            _acceptedTransitionDrops.Add(ResolveTransitionDropIdentity(drop));

            return;
        }

        switch (operation.Intent)
        {
            case AlterColumnIntent { OldDefinition: not null } column
                when _catalogSqlBuilder.IsSupportedIntegerWidening(column.OldDefinition, column.Definition):
                var key = (column.Schema ?? "dbo", column.Table);
                if (!_acceptedTransitionColumns.TryGetValue(key, out var accepted))
                {
                    accepted = new Dictionary<string, ExpectedColumnDefinition>(StringComparer.Ordinal);
                    _acceptedTransitionColumns.Add(key, accepted);
                }

                accepted[column.Definition.Name] = column.Definition;
                break;
            case ModelManagedDataIntent:
                // WHY: Managed writes can run DML triggers on other tables,
                // so local row evidence is not enough to retain a proof.
                _transitionDataChangedGlobally = true;
                break;
            case EnsureColumnIntent column:
                _transitionStructureChanged.Add((column.Schema ?? "dbo", column.Table));
                break;
            case DropColumnIntent column:
                _transitionStructureChanged.Add((column.Schema ?? "dbo", column.Table));
                break;
            case RenameColumnIntent column:
                _transitionStructureChanged.Add((column.Schema ?? "dbo", column.Table));
                break;
            case AlterColumnIntent column:
                _transitionStructureChanged.Add((column.Schema ?? "dbo", column.Table));
                break;
            case EnsureTableIntent table:
                _transitionStructureChanged.Add((table.Definition.Schema ?? "dbo", table.Definition.Table));
                break;
            case DropTableIntent table:
                _transitionStructureChanged.Add((table.Schema ?? "dbo", table.Table));
                break;
            case RenameTableIntent table:
                _transitionStructureChanged.Add((table.Schema ?? "dbo", table.Name));
                break;
            case EnsureIndexIntent index:
                _transitionStructureChanged.Add((index.Definition.Schema ?? "dbo", index.Definition.Table));
                break;
            case EnsureCheckConstraintIntent check:
                _transitionStructureChanged.Add((check.Definition.Schema ?? "dbo", check.Definition.Table));
                break;
            case EnsurePrimaryKeyIntent primary:
                _transitionStructureChanged.Add((primary.Definition.Schema ?? "dbo", primary.Definition.Table));
                break;
            case EnsureUniqueConstraintIntent unique:
                _transitionStructureChanged.Add((unique.Definition.Schema ?? "dbo", unique.Definition.Table));
                break;
            case EnsureForeignKeyIntent foreign:
                _transitionStructureChanged.Add((foreign.Definition.Schema ?? "dbo", foreign.Definition.Table));
                _transitionStructureChanged.Add((foreign.Definition.PrincipalSchema ?? "dbo",
                    foreign.Definition.PrincipalTable));
                break;
        }
    }

    private sealed record IntegerWideningProof(
        SafeMigrationProviderAnalysis Analysis,
        IReadOnlyCollection<TransitionDependency> Dependencies
    );

    /// <summary>Names one physical dependency whose exact accepted drop can discharge a widening blocker.</summary>
    /// <param name="Kind">The provider object's captured category.</param>
    /// <param name="Schema">The captured physical owner schema.</param>
    /// <param name="Table">The captured physical owner table.</param>
    /// <param name="Name">The captured physical object name.</param>
    internal readonly record struct TransitionDependency(
        string Kind,
        string Schema,
        string Table,
        string Name
    );
}
