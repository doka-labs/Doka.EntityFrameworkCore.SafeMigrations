namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerProjectedDependencyGraph
{
    private bool HasAllInlinePrerequisites(
        ExpectedTableDefinition table,
        ISafeMigrationProjectedColumnSource columns
    ) => HasForeignKeyPrerequisites(table, table.ForeignKeys, columns);

    /// <summary>Requalifies only the captured live-parent contract of an accepted empty new child.</summary>
    private SafeMigrationProviderAnalysis ValidateStandaloneForeignKey(
        ExpectedForeignKeyDefinition foreignKey,
        SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var ordinary = ValidateForeignKeys([foreignKey], analysis, columns);

        // WHY: Schema-global occupancy takes priority for both missing states.
        // Qualification below reads prerequisites without changing this map;
        // only accepted Observe transitions can release an occupied name.
        if (ordinary.ObservedState is SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.PrerequisiteMissing
            && _constraintNames.ContainsKey(new SchemaObjectKey(foreignKey.Schema ?? "dbo", foreignKey.Name)))
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None,
                false,
                "projected_constraint_name_occupied");
        }

        if (ordinary.ObservedState != SafeMigrationObservedState.PrerequisiteMissing
            || ordinary.Code != "projected_structure_state_unknown"
            || columns is not ISafeMigrationProjectedTableSource tables
            || !tables.TryGetProjectedTableState(foreignKey.Table, foreignKey.Schema, out var state)
            || !state.IsNewlyCreated
            || state.HasDataMutation
            || state.HasUnknownStructure
            || !_createdTables.TryGetValue(GetTable(foreignKey.Table, foreignKey.Schema), out var createdTable)
            || createdTable.Table != foreignKey.Table
            || (createdTable.Schema ?? "dbo") != (foreignKey.Schema ?? "dbo")
            || !_liveInlineReady.TryGetValue(new InlineProofKey(foreignKey, createdTable), out var proof)
            || !proof.IsStandalone)
        {
            return ordinary;
        }

        var capturedTable = proof.DependentTable;

        // WHY: A captured future child contract is not an accepted empty-table
        // proof. Core supplies ordered creation/data/structure facts; every FK
        // operand must still be the exact captured dependent definition.
        foreach (var name in foreignKey.Columns)
        {
            var captured = capturedTable.Columns.FirstOrDefault(column => column.Name == name);
            if (captured is null
                || !columns.TryGetProjectedColumn(foreignKey.Table, foreignKey.Schema, name, out var current)
                || !SafeMigrationDefinitionEquivalence.Column(captured, current))
            {
                return ordinary;
            }
        }

        var principal = GetTable(foreignKey.PrincipalTable, foreignKey.PrincipalSchema);
        if (principal != proof.Principal
            || _missingTables.Contains(principal)
            || proof.ColumnIds.Count != foreignKey.PrincipalColumns.Count)
        {
            return ordinary;
        }

        // WHY: Compare each current principal-column identity with its captured proof slot.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < foreignKey.PrincipalColumns.Count; ordinal++)
        {
            var columnId = _columnIds.GetValueOrDefault(new ColumnKey(principal, foreignKey.PrincipalColumns[ordinal]));

            if (columnId <= 0
                || columnId != proof.ColumnIds[ordinal]
                || _invalidatedStorage.Contains(new ColumnIdentity(principal, columnId)))
            {
                return ordinary;
            }
        }

        if (!HasForeignKeyPrerequisites(capturedTable, [foreignKey], columns))
        {
            return ordinary;
        }

        // WHY: Only the original structural-unknown classifier is discharged.
        // Runtime still checks its fresh physical contract and orphan state;
        // DML, opaque SQL, trigger uncertainty, and independent failures retain
        // their own rejection or traceable runtime-validation origin.
        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None,
            false,
            "projected_foreign_key_prerequisites_ready")
        {
            RequiresLiveDataProof = true,
        };
    }

    /// <summary>Checks the shared inline/standalone physical key and storage prerequisites without row scans.</summary>
    private bool HasForeignKeyPrerequisites(
        ExpectedTableDefinition table,
        IReadOnlyList<ExpectedForeignKeyDefinition> foreignKeys,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var dependentId = GetTable(table.Table, table.Schema);
        // WHY: SQL Server caps FK keys at 32 columns. A fixed stack buffer
        // avoids allocating requested id vectors for every inline assessment.
        Span<int> columnIds = stackalloc int[32];

        foreach (var foreignKey in foreignKeys)
        {
            var principalId = GetTable(foreignKey.PrincipalTable, foreignKey.PrincipalSchema);
            if (foreignKey.PrincipalColumns.Count > columnIds.Length
                || !_inlinePhysicalWidthSupported(table, foreignKey))
            {
                return false;
            }

            ResolveColumnIds(principalId, foreignKey.PrincipalColumns, columnIds);

            if (principalId != dependentId
                && (_missingTables.Contains(principalId)
                    || !_candidateKeys.Contains(
                        principalId,
                        foreignKey.PrincipalColumns,
                        columnIds[..foreignKey.PrincipalColumns.Count])))
            {
                return false;
            }

            var principal = principalId == dependentId ? table : _createdTables.GetValueOrDefault(principalId);
            if (principalId == dependentId
                && !(table.PrimaryKey?.Columns.SequenceEqual(foreignKey.PrincipalColumns) == true
                    || table.UniqueConstraints.Any(key => key.Columns.SequenceEqual(foreignKey.PrincipalColumns))))
            {
                return false;
            }

            // WHY: Compare dependent and principal FK members at corresponding key positions.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < foreignKey.Columns.Count; ordinal++)
            {
                var dependentColumn = table.Columns.First(column => column.Name == foreignKey.Columns[ordinal]);
                var name = foreignKey.PrincipalColumns[ordinal];
                var principalColumn = principal?.Columns.FirstOrDefault(column => column.Name == name);

                if (principalId != dependentId
                    && columns.TryGetProjectedColumn(
                        foreignKey.PrincipalTable,
                        foreignKey.PrincipalSchema,
                        name,
                        out var projectedColumn))
                {
                    principalColumn = projectedColumn;
                }

                if (principalColumn is not null)
                {
                    if (!_storageEquals(dependentColumn, principalColumn))
                    {
                        return false;
                    }

                    continue;
                }

                // WHY: A Boolean snapshot is not ordered evidence. Keep its
                // physical table/column identity and require the same surviving
                // candidate key; renames preserve ids, drops/replacements do not.
                if (!_liveInlineReady.TryGetValue(new InlineProofKey(foreignKey, table), out var proof)
                    || proof.Principal != principalId
                    || proof.DependentTable.Columns.FirstOrDefault(column => column.Name == foreignKey.Columns[ordinal])
                        is not { } capturedDependent
                    || !_storageEquals(capturedDependent, dependentColumn)
                    || columnIds[ordinal] <= 0
                    || proof.ColumnIds[ordinal] != columnIds[ordinal]
                    || _invalidatedStorage.Contains(new ColumnIdentity(principalId, columnIds[ordinal])))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private ColumnSafety? ReadColumn(
        string table,
        string? schema,
        string column,
        ISafeMigrationProjectedColumnSource columns,
        ExpectedTableDefinition? inlineTable
    )
    {
        ExpectedColumnDefinition? definition = null;
        if (inlineTable?.Table == table
            && (inlineTable.Schema ?? "dbo") == (schema ?? "dbo"))
        {
            definition = inlineTable.Columns.FirstOrDefault(value => value.Name == column);
        }

        if (definition is null)
        {
            columns.TryGetProjectedColumn(table, schema, column, out definition);
        }

        if (definition is not null)
        {
            return new ColumnSafety(
                definition.IsRowVersion
                || definition.StoreType is { } type
                && (type.Equals("rowversion", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("timestamp", StringComparison.OrdinalIgnoreCase)),
                definition.IsNullable,
                definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None);
        }

        return _columns.TryGetValue(new ColumnKey(GetTable(table, schema), column), out var live) ? live : null;
    }

    /// <summary>Binds one exact dependent alternative to a surviving native principal operand.</summary>
    private readonly record struct SparseStorageProof(
        int Principal,
        int PrincipalColumn,
        int OperandOrdinal,
        ExpectedColumnDefinition? DependentColumn
    );

    private readonly record struct InlineStorageProof(
        int Principal,
        IReadOnlyList<int> ColumnIds,
        ExpectedTableDefinition DependentTable,
        bool IsStandalone
    );

    /// <summary>Binds a captured FK certificate to the authored dependent table that supplied its operands.</summary>
    private readonly record struct InlineProofKey(
        ExpectedForeignKeyDefinition ForeignKey,
        ExpectedTableDefinition Table
    );

    private readonly record struct ColumnSafety(
        bool RowVersion,
        bool Nullable,
        bool HasDefault
    );

    private readonly record struct TriggerSafety(
        bool Delete,
        bool Update
    );
}
