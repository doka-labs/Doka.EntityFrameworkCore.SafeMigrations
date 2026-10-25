namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private HashSet<int>? _generatedCaseRequalificationCandidates;
    private Dictionary<int, string>? _stateEvaluationGuardShortCircuitCodes;
    private HashSet<LiveColumnIdentity>? _expiredGeneratedLiveColumns;
    private HashSet<LiveColumnIdentity>? _expiredGeneratedLiveOwners;
    private HashSet<LiveColumnIdentity>? _acceptedGeneratedCaseColumns;
    private HashSet<(string? Schema, string Name)>? _occupiedGeneratedIndexSymbols;
    private HashSet<(string? Schema, string Name)>? _requestedGeneratedIndexSymbols;
    private bool _generatedIndexHasOpaqueBoundary;

    /// <summary>Expires physical source snapshots only after an executable accepted lifetime change.</summary>
    private void ObserveGeneratedSourceLifetime(
        SafeMigrationOperation operation, SafeMigrationProviderAnalysis analysis)
    {
        if (_livePrerequisiteColumns is null)
        {
            return;
        }

        switch (operation.Intent)
        {
            case DropColumnIntent column:
                ExpireColumn(column.Table, column.Schema, column.Name);
                break;
            case AlterColumnIntent column:
                ExpireColumn(column.Table, column.Schema, column.Definition.Name);
                break;
            case RenameColumnIntent column:
                ExpireColumn(column.Table, column.Schema, column.Name);
                ExpireColumn(column.Table, column.Schema, column.NewName);
                break;
            case DropTableIntent table:
                (_expiredGeneratedLiveOwners ??= []).Add(LiveColumnKey(table.Table, table.Schema, string.Empty));
                VacateSymbol(table.Table, table.Schema);
                break;
            case RenameTableIntent table:
                (_expiredGeneratedLiveOwners ??= []).Add(LiveColumnKey(table.Name, table.Schema, string.Empty));
                _expiredGeneratedLiveOwners.Add(LiveColumnKey(table.NewName ?? table.Name,
                    table.NewSchema ?? table.Schema, string.Empty));
                VacateSymbol(table.Name, table.Schema);
                OccupySymbol(table.NewName ?? table.Name, table.NewSchema ?? table.Schema);
                break;
            case EnsureColumnIntent column when analysis.ObservedState == SafeMigrationObservedState.Missing
                && PostgreSqlSafeMigrationCatalogSqlBuilder
                .RequiresGeneratedCaseInputCapture(column):
                (_acceptedGeneratedCaseColumns ??= []).Add(LiveColumnKey(column.Table, column.Schema,
                    column.Definition.Name));
                break;
            case EnsureTableIntent table:
                OccupySymbol(table.Definition.Table, table.Definition.Schema);
                if (table.Definition.PrimaryKey is { } tablePrimary)
                {
                    OccupySymbol(tablePrimary.Name, tablePrimary.Schema);
                }

                foreach (var unique in table.Definition.UniqueConstraints)
                {
                    OccupySymbol(unique.Name, unique.Schema);
                }

                break;
            case EnsurePrimaryKeyIntent primary:
                OccupySymbol(primary.Definition.Name, primary.Definition.Schema);
                break;
            case EnsureUniqueConstraintIntent unique:
                OccupySymbol(unique.Definition.Name, unique.Definition.Schema);
                break;
            case DropPrimaryKeyIntent primary:
                VacateSymbol(primary.Name, primary.Schema);
                break;
            case DropUniqueConstraintIntent unique:
                VacateSymbol(unique.Name, unique.Schema);
                break;
            case EnsureIndexIntent index:
                OccupySymbol(index.Definition.Name, index.Definition.Schema);
                break;
            case DropIndexIntent index:
                VacateSymbol(index.Name, index.Schema);
                break;
            case RenameIndexIntent index:
                VacateSymbol(index.Name, index.Schema);
                OccupySymbol(index.NewName, index.Schema);
                break;
        }

        void ExpireColumn(string table, string? schema, string column)
        {
            var key = LiveColumnKey(table, schema, column);
            if (_livePrerequisiteColumns.ContainsKey(key))
            {
                (_expiredGeneratedLiveColumns ??= []).Add(key);
            }
        }

        void OccupySymbol(string name, string? schema)
        {
            var normalized = PostgreSqlSafeMigrationSchemaIdentity.Normalize(schema, _currentSchema);
            if (_requestedGeneratedIndexSymbols?.Contains((normalized, name)) == true)
            {
                (_occupiedGeneratedIndexSymbols ??= []).Add((normalized, name));
            }
        }

        void VacateSymbol(string name, string? schema) => _occupiedGeneratedIndexSymbols?
            .Remove((PostgreSqlSafeMigrationSchemaIdentity.Normalize(schema, _currentSchema), name));
    }

    /// <summary>Distinguishes unchanged physical input from an old column or owner lifetime.</summary>
    private bool HasCurrentGeneratedLiveBinding(string table, string? schema, string column)
        => _expiredGeneratedLiveColumns?.Contains(LiveColumnKey(table, schema, column)) != true
            && _expiredGeneratedLiveOwners?.Contains(LiveColumnKey(table, schema, string.Empty)) != true;

    /// <summary>Restores only a nonunique index over an accepted CASE with current native operand proofs.</summary>
    private SafeMigrationProviderAnalysis? QualifyGeneratedCaseIndex(
        SafeMigrationOperation operation, SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns)
    {
        if (analysis.ObservedState is not (SafeMigrationObservedState.PrerequisiteMissing
                or SafeMigrationObservedState.Missing)
            || analysis.Code is not ("projected_structure_state_unknown" or "classified_prerequisite_missing"
                or "classified_missing")
            || _generatedIndexHasOpaqueBoundary
            || operation.Intent is not EnsureIndexIntent { Definition.Unique: false } index
            || index.Definition.Filter is not null || index.Definition.StructuredFilter is not null
            || _acceptedGeneratedCaseColumns is null
            || _expiredGeneratedLiveOwners?.Contains(LiveColumnKey(index.Definition.Table,
                index.Definition.Schema, string.Empty)) == true)
        {
            return null;
        }

        if (columns is not ISafeMigrationProjectedTableSource tables
            || !tables.TryGetProjectedTableState(index.Definition.Table, index.Definition.Schema, out var state)
            || !state.HasUnknownStructure || state.IsNewlyCreated)
        {
            return null;
        }

        var definition = index.Definition;
        var accepted = columns.GetProjectedIndexIdentityAnalysis(definition, analysis,
            includeConstraintBackingIndexNames: true);
        if (accepted is { ObservedState: SafeMigrationObservedState.Different })
        {
            return accepted;
        }

        if (accepted is not null && accepted.ObservedState is not (SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.Matching))
        {
            return null;
        }

        if (accepted is null && (_occupiedGeneratedIndexSymbols?.Contains((
                    PostgreSqlSafeMigrationSchemaIdentity.Normalize(definition.Schema, _currentSchema),
                    definition.Name)) == true
                || _indexInputProofs?.TryGetValue(GeneratedIndexAbsenceIdentity(definition), out var absent) != true
                || absent != true))
        {
            return null;
        }

        var hasNewCase = false;
        foreach (var key in definition.Keys)
        {
            if (PostgreSqlSafeMigrationCatalogSqlBuilder.IndexPhysicalMember(key) is not { } member
                || !IndexMemberTableMatches(definition, member)
                || !TryGetCurrentForeignKeyStorage(definition.Table, definition.Schema,
                    member.Name, columns, out var storage)
                || _indexInputProofs?.TryGetValue(IndexInputIdentity(storage.TypeOid, definition, key, member),
                    out var eligible) != true || eligible != true)
            {
                return null;
            }

            if (!_acceptedGeneratedCaseColumns.Contains(LiveColumnKey(definition.Table, definition.Schema,
                    member.Name)))
            {
                continue;
            }

            if (!columns.TryGetProjectedColumn(definition.Table, definition.Schema, member.Name, out var projected))
            {
                return null;
            }

            var generated = new EnsureColumnIntent(definition.Table, projected, definition.Schema);
            if (!PostgreSqlSafeMigrationCatalogSqlBuilder.RequiresGeneratedCaseInputCapture(generated)
                || _catalogSqlBuilder.ValidateProjectedGeneratedCaseBinding(generated,
                    (table, schema, name) => TryGetCurrentForeignKeyStorage(table, schema, name, columns,
                        out var current) ? (current.TypeOid, current.CollationOid) : null) != true)
            {
                return null;
            }

            hasNewCase = true;
        }

        foreach (var included in definition.IncludedColumns)
        {
            if (!TryGetCurrentForeignKeyStorage(definition.Table, definition.Schema, included, columns, out _))
            {
                return null;
            }
        }

        // WHY: A fresh generated member proves neither namespace availability
        // nor native indexability by itself. Reuse accepted identity first,
        // then exact initial absence and current metadata-qualified key inputs.
        return hasNewCase ? accepted ?? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "projected_generated_index_dependencies_proven") : null;
    }

    /// <summary>Uses native identifier roles while retaining original definitions as provider evidence keys.</summary>
    private static IReadOnlyList<string> NativePrerequisiteColumns(SafeMigrationIntent intent)
        => SafeMigrationPrerequisiteColumns.Local(
            SafeMigrationCapturedExpressionNormalization.NormalizeIntent(intent, true));

    /// <summary>Invalidates CASE type proofs when accepted prefix operations replace a source contract.</summary>
    private SafeMigrationProviderAnalysis? QualifyProjectedGeneratedCaseTypes(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns)
    {
        var requalify = _projectedOperationOrdinal is { } ordinal
            && _generatedCaseRequalificationCandidates?.Contains(ordinal) == true
            && (analysis.ObservedState == SafeMigrationObservedState.Unsupported
                && analysis.Code is "generated_case_operand_type" or "structured_check_operand_type"
                || analysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                    && analysis.Code == "projected_structure_state_unknown");

        if (requalify && operation.Intent is EnsureCheckConstraintIntent check
            && _occupiedProjectedConstraintNames?.ContainsKey(LiveForeignKeyName(
                check.Definition.Table, check.Definition.Schema, check.Definition.Name)) == true)
        {
            // WHY: Initial exact absence expires when an accepted prefix
            // creates another constraint under that same owned symbol.
            return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None, postconditionSatisfied: false,
                "projected_generated_case_target_occupied");
        }

        if (analysis.ObservedState != SafeMigrationObservedState.Missing && !requalify
            || !PostgreSqlSafeMigrationCatalogSqlBuilder.RequiresGeneratedCaseInputCapture(operation.Intent))
        {
            return null;
        }

        var proof = _catalogSqlBuilder.ValidateProjectedGeneratedCaseBinding(operation.Intent,
            (table, schema, name) => TryGetCurrentForeignKeyStorage(table, schema, name, columns, out var storage)
                ? (storage.TypeOid, storage.CollationOid) : null);

        // WHY: Empty rows are not a PostgreSQL type-binding certificate. A
        // changed or unresolved source cannot inherit the earlier live proof.
        if (proof == true)
        {
            // WHY: This late hook runs only after Core's missing-owner,
            // opaque SQL/provider and deferred-origin gates and provider trigger
            // checks. Exact captured target absence plus current accepted source
            // types can discharge only this dynamic native binding rejection.
            return requalify ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None, postconditionSatisfied: false,
                "projected_generated_case_binding_requalified") : null;
        }

        return new SafeMigrationProviderAnalysis(
            proof == false ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false,
            proof == false ? operation.Intent is EnsureCheckConstraintIntent
                ? "structured_check_operand_type" : "generated_case_operand_type"
                : "projected_generated_case_binding_unproven");
    }

    /// <summary>Captures dynamic CASE inputs while preserving invariant unsupported contracts.</summary>
    private static bool NeedsGeneratedCaseInputCapture(
        SafeMigrationIntent intent,
        SafeMigrationProviderAnalysis analysis)
        => PostgreSqlSafeMigrationCatalogSqlBuilder.RequiresGeneratedCaseInputCapture(intent)
            && (analysis.ObservedState is SafeMigrationObservedState.Missing
                    or SafeMigrationObservedState.PrerequisiteMissing
                || IsDynamicGeneratedCaseRejection(analysis));

    /// <summary>Distinguishes a mutable live input mismatch from an invalid authored CASE definition.</summary>
    private static bool IsDynamicGeneratedCaseRejection(SafeMigrationProviderAnalysis analysis)
        => analysis.ObservedState == SafeMigrationObservedState.Unsupported && !analysis.IsInvariantUnsupported
            && analysis.Code is "generated_case_operand_type" or "structured_check_operand_type";
}
