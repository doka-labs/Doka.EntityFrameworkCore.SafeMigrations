namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private Dictionary<ExpectedColumnDefinition, List<SqlServerProjectedComputedKeyProof>> _projectedComputedKeys = [];

    /// <summary>Preserves the engine's enforced dependency boundary for newly declared computed sources.</summary>
    private SafeMigrationProviderAnalysis? QualifyProjectedComputedSourceRename(
        SafeMigrationOperation operation,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not RenameColumnIntent rename
            || !columns.TryGetProjectedColumn(rename.Table, rename.Schema, rename.Name, out var source))
        {
            return null;
        }

        foreach (var (definition, proofs) in _projectedComputedKeys)
        {
            if (columns.TryGetProjectedColumn(rename.Table, rename.Schema, definition.Name, out var accepted)
                && ReferenceEquals(accepted, definition)
                && proofs.Any(proof => proof.Matches(columns, rename.Table, rename.Schema)
                    && proof.Operands.Values.Any(binding => ReferenceEquals(binding, source))))
            {
                // WHY: SQL Server rejects sp_rename on an enforced computed
                // dependency. A declared empty owner cannot make that operation
                // legal, and renaming an unrelated source remains independent.
                return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                    SafeMigrationRepairCapability.None, false, "projected_computed_source_dependency");
            }
        }

        return null;
    }

    /// <summary>Captures native computed-index admission per immutable definition in bounded batches.</summary>
    private async Task ReadProjectedComputedKeyProofsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var hasComputed = false;
        // WHY: Stop the indexed stream scan as soon as a computed column is found.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count && !hasComputed; ordinal++)
        {
            switch (operations[ordinal].Intent)
            {
                case EnsureColumnIntent column:
                    hasComputed = SqlServerComputedColumnContract.IsComputed(column.Definition);
                    break;
                case EnsureTableIntent table:
                    // WHY: Stop the immutable column scan at its first computed member.
                    // ReSharper disable once ForCanBeConvertedToForeach
                    // ReSharper disable once LoopCanBeConvertedToQuery
                    for (var index = 0; index < table.Definition.Columns.Count && !hasComputed; index++)
                    {
                        hasComputed = SqlServerComputedColumnContract.IsComputed(table.Definition.Columns[index]);
                    }

                    break;
            }
        }

        if (!hasComputed)
        {
            _projectedComputedKeys.Clear();

            return;
        }

        var authored = new Dictionary<(string Schema, string Table), Dictionary<string, ExpectedColumnDefinition>>();
        HashSet<ExpectedColumnDefinition>? renamedDefinitions = null;
        var candidates = new List<(string Table, string? Schema, ExpectedColumnDefinition Definition,
            ExpectedTableDefinition? AuthoredTable, int ReferenceCount)>();

        foreach (var operation in operations)
        {
            switch (operation.Intent)
            {
                case EnsureTableIntent table:
                    authored[(table.Definition.Schema ?? "dbo", table.Definition.Table)]
                        = table.Definition.Columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
                    foreach (var column in table.Definition.Columns)
                    {
                        AddCandidate(table.Definition.Table, table.Definition.Schema, column);
                    }

                    break;
                case EnsureColumnIntent column:
                    AddCandidate(column.Table, column.Schema, column.Definition);
                    SetColumn(column.Table, column.Schema, column.Definition);
                    break;
                case AlterColumnIntent column:
                    SetColumn(column.Table, column.Schema, column.Definition);
                    break;
                case DropColumnIntent column:
                    if (authored.TryGetValue((column.Schema ?? "dbo", column.Table), out var columns))
                    {
                        columns.Remove(column.Name);
                    }

                    break;
                case RenameColumnIntent rename:
                    if (authored.TryGetValue((rename.Schema ?? "dbo", rename.Table), out var renamedColumns)
                        && renamedColumns.Remove(rename.Name, out var original))
                    {
                        var renamed = SafeMigrationPreflightProjection.CopyProjectedColumn(original, rename.NewName);
                        renamedColumns[rename.NewName] = renamed;
                        renamedDefinitions ??= [];
                        renamedDefinitions.Add(renamed);
                    }

                    break;
                case DropTableIntent table:
                    authored.Remove((table.Schema ?? "dbo", table.Table));
                    break;
            }
        }

        var captured = new Dictionary<ExpectedColumnDefinition, List<SqlServerProjectedComputedKeyProof>>();

        await SafeMigrationCatalogProbeBatch.ReadAsync(connection, candidates.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes, commandTimeout,
            (command, ordinal) =>
            {
                var item = candidates[ordinal];
                command.CommandText = _catalogSqlBuilder.BuildProjectedComputedKeyProofSql(
                    item.Table, item.Schema, item.Definition, item.AuthoredTable, ordinal);

                return new SafeMigrationCatalogProbeStatement(1, 0, ordinal);
            },
            async (reader, ordinal, _, token) =>
            {
                if (reader.FieldCount != 3 || !await reader.ReadAsync(token)
                    || reader.IsDBNull(0) || reader.GetInt32(0) != ordinal || reader.IsDBNull(1))
                {
                    throw new InvalidOperationException("SQL Server omitted its computed-column index proof.");
                }

                var proof = reader.GetInt32(1);
                var encodedBindings = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (proof is < 0 or > 6 || await reader.ReadAsync(token))
                {
                    throw new InvalidOperationException("SQL Server returned an invalid computed-column index proof.");
                }

                var definition = candidates[ordinal].Definition;
                var shape = SqlServerProjectedKeyShape.CreateComputedColumnProof(definition, _typeMappingSource, proof);
                if (shape is not null)
                {
                    var candidate = candidates[ordinal];
                    var bindings = proof == 0
                        ? new Dictionary<string, ExpectedColumnDefinition?>(StringComparer.Ordinal)
                        : encodedBindings is null ? throw new InvalidOperationException(
                            "SQL Server omitted its computed-column source bindings.")
                            : SqlServerComputedColumnBindings.Read(encodedBindings, candidate.ReferenceCount,
                                candidate.AuthoredTable);

                    if (!captured.TryGetValue(definition, out var proofs))
                    {
                        proofs = [];
                        captured.Add(definition, proofs);
                    }

                    _projectedColumnLayouts.TryGetValue((candidate.Schema ?? "dbo", candidate.Table), out var layout);
                    Dictionary<string, int>? liveIdentities = null;
                    HashSet<ExpectedColumnDefinition>? renamedBindings = null;
                    foreach (var (name, binding) in bindings)
                    {
                        if (binding is null)
                        {
                            liveIdentities ??= new Dictionary<string, int>(StringComparer.Ordinal);
                            liveIdentities[name] = layout?.Bindings.GetValueOrDefault(name) ?? 0;
                        }
                        else if (renamedDefinitions?.Contains(binding) == true)
                        {
                            renamedBindings ??= [];
                            renamedBindings.Add(binding);
                        }
                    }

                    proofs.Add(new SqlServerProjectedComputedKeyProof(shape, bindings,
                        candidate.Table, candidate.Schema, proof != 0)
                    {
                        LiveOperandIdentities = liveIdentities,
                        CurrentColumnBindings = layout?.Bindings,
                        RenamedOperandDefinitions = renamedBindings,
                    });
                }
            }, cancellationToken, transaction, SqlServerCatalogParameterBindings.MaximumParameters);

        // WHY: Only the completed invocation may publish these reference-bound
        // proofs. A cancellation or malformed result cannot lend partial success.
        _projectedComputedKeys = captured;

        void AddCandidate(string table, string? schema, ExpectedColumnDefinition definition)
        {
            if (!SqlServerComputedColumnContract.IsComputed(definition))
            {
                return;
            }

            var plan = _catalogSqlBuilder.Build(new SafeMigrationOperation(
                new EnsureColumnIntent(table, definition, schema), SafeMigrationPolicy.ThrowIfDifferent));

            if (!plan.IsStaticallyUnsupported)
            {
                authored.TryGetValue((schema ?? "dbo", table), out var columns);
                var tableDefinition = columns is { Count: > 0, }
                    ? new ExpectedTableDefinition(table, columns.Values, schema) : null;

                var names = new HashSet<string>(StringComparer.Ordinal);
                SafeMigrationSqlExpressionInspector.CollectIdentifiers(
                    SqlServerComputedColumnContract.Expression(definition)!, names);

                candidates.Add((table, schema, definition, tableDefinition, names.Count));
            }
        }

        void SetColumn(string table, string? schema, ExpectedColumnDefinition definition)
        {
            if (!authored.TryGetValue((schema ?? "dbo", table), out var columns))
            {
                columns = new Dictionary<string, ExpectedColumnDefinition>(StringComparer.Ordinal);
                authored.Add((schema ?? "dbo", table), columns);
            }

            columns[definition.Name] = definition;
        }
    }

    /// <summary>Transfers native owner identity only after a certified accepted table rename.</summary>
    private void ObserveProjectedComputedOwnerRename(SafeMigrationOperation operation, SafeMigrationDecision decision)
    {
        if (!decision.ShouldExecute || operation.Intent is not RenameTableIntent rename)
        {
            return;
        }

        foreach (var proofs in _projectedComputedKeys.Values)
        {
            // WHY: Replace matching proof records in their existing collection slots.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < proofs.Count; index++)
            {
                var proof = proofs[index];
                if (StringComparer.Ordinal.Equals(proof.Table, rename.Name)
                    && StringComparer.Ordinal.Equals(proof.Schema ?? "dbo", rename.Schema ?? "dbo"))
                {
                    proofs[index] = proof with
                    {
                        Table = rename.NewName ?? rename.Name,
                        Schema = rename.NewSchema ?? rename.Schema,
                    };
                }
            }
        }
    }

    /// <summary>Reuses native inferred facets only while every accepted source retains its exact binding.</summary>
    private SafeMigrationProviderAnalysis QualifyProjectedComputedColumn(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not EnsureColumnIntent column
            || !_projectedComputedKeys.TryGetValue(column.Definition, out var proofs)
            || analysis.IsInvariantUnsupported || analysis.IsOpaqueProjectionUnknown
            || analysis.ObservedState is not (SafeMigrationObservedState.Missing or SafeMigrationObservedState.Matching
                or SafeMigrationObservedState.PrerequisiteMissing))
        {
            return analysis;
        }

        foreach (var proof in proofs)
        {
            if (!proof.ColumnSupported || !proof.Matches(columns, column.Table, column.Schema))
            {
                continue;
            }

            if (analysis.ObservedState != SafeMigrationObservedState.PrerequisiteMissing
                || analysis.Code is not ("classified_prerequisite_missing" or "projected_structure_state_unknown"))
            {
                return analysis;
            }

            var ownerProven = _projectedColumnLayouts.TryGetValue((column.Schema ?? "dbo", column.Table),
                    out var layout) && layout is not null
                || columns is ISafeMigrationProjectedTableSource tables
                    && tables.TryGetProjectedTableState(column.Table, column.Schema, out var state)
                    && state.IsNewlyCreated;

            if (!ownerProven)
            {
                // WHY: A constant expression needs no source columns, but its
                // compiler result still cannot invent an absent user table.
                return analysis;
            }

            // WHY: The initial classifier cannot bind a source added earlier
            // in this stream. The native compiler captured its exact authored
            // type and unchanged live siblings, independently of indexability.
            return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None, false, "projected_computed_column_dependencies_proven");
        }

        return analysis.ObservedState is SafeMigrationObservedState.Missing or SafeMigrationObservedState.Matching
            ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationRepairCapability.None, false, "projected_computed_column_source_unknown")
            : analysis;
    }

    /// <summary>
    /// Restores only a new nonunique computed index whose complete native source lineage remains known.
    /// </summary>
    private SafeMigrationProviderAnalysis? QualifyProjectedComputedIndex(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis live,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not EnsureIndexIntent { Definition.Unique: false, } index
            || _columnMatchingEvidenceInvalidated
            || live.IsInvariantUnsupported || live.IsOpaqueProjectionUnknown
            || live.ObservedState is not (SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.PrerequisiteMissing)
            || index.Definition.Filter is not null || index.Definition.StructuredFilter is not null
            || !_projectedColumnLayouts.TryGetValue((index.Definition.Schema ?? "dbo", index.Definition.Table),
                out var layout) || layout is null
            || !_projectedKeyTables.TryGetValue((index.Definition.Schema ?? "dbo", index.Definition.Table),
                out var snapshot) || !snapshot.Exists || !snapshot.PhysicalSupported
            || columns is not ISafeMigrationProjectedTableSource tables
            || !tables.TryGetProjectedTableState(index.Definition.Table, index.Definition.Schema, out var state)
            || !state.HasUnknownStructure || state.IsNewlyCreated)
        {
            return null;
        }

        // WHY: SQL Server's PK/UNIQUE constraint allocates an identically named
        // backing index even though its logical constraint identity is separate.
        var accepted = columns.GetProjectedIndexIdentityAnalysis(index.Definition, live,
            includeConstraintBackingIndexNames: true);
        if (accepted is { ObservedState: SafeMigrationObservedState.Different, })
        {
            return accepted;
        }

        if (accepted is not null
            && accepted.ObservedState is not (SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.Matching))
        {
            return null;
        }

        var hasNewComputed = false;
        foreach (var key in index.Definition.Keys)
        {
            if (key.Column is null || !ProvesColumn(key.Column))
            {
                return null;
            }
        }

        foreach (var name in index.Definition.IncludedColumns)
        {
            if (!ProvesColumn(name))
            {
                return null;
            }
        }

        if (!hasNewComputed)
        {
            return null;
        }

        // WHY: A newly accepted computed member could not have belonged to an
        // original index. Exact operand identities preserve only that narrower
        // absence proof through sibling drops; no row or filter proof is waived.
        var projected = accepted ?? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "projected_computed_index_dependencies_proven");

        return SqlServerProjectedKeyShape.Validate(index, columns, live, projected, snapshot, _typeMappingSource,
            computedColumns: _projectedComputedKeys, structuralIdentityProven: true);

        bool ProvesColumn(string name)
        {
            if (columns.TryGetProjectedColumn(index.Definition.Table, index.Definition.Schema, name, out var column))
            {
                if (!SqlServerComputedColumnContract.IsComputed(column))
                {
                    return true;
                }

                if (!_projectedComputedKeys.TryGetValue(column, out var proofs)
                    || !proofs.Any(proof => proof.ColumnSupported
                        && proof.Matches(columns, index.Definition.Table, index.Definition.Schema)))
                {
                    return false;
                }

                hasNewComputed |= !snapshot.Columns.ContainsKey(name);

                return true;
            }

            return layout.Bindings.TryGetValue(name, out var identity) && identity > 0;
        }
    }
}
