namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private Dictionary<TableKey, int>? _certifiedColumnDropOrigins;

    /// <summary>Retains a real accepted DROP origin without claiming its unknown dependent-key effects.</summary>
    private void ObserveIndependentColumnDrop(
        DropColumnIntent intent
    )
    {
        var key = new TableKey(intent.Table, intent.Schema);
        if (CurrentOperationOrdinal < 0
            || _projectedColumnAnalyzer?.PreservesIntrinsicColumnSources(intent) != true
            || _hasOpaqueProviderPostcondition
            || _hasOpaqueSqlPostcondition
            || _providerDataMutationVersion != 0
            || HasUnanalyzedDataChanges(intent.Table, intent.Schema)
            || (_prerequisites.TryGetValue(key, out var prerequisites) && prerequisites.NewlyCreated)
            || (IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
                && _certifiedColumnDropOrigins?.ContainsKey(key) != true))
        {
            _certifiedColumnDropOrigins?.Remove(key);

            return;
        }

        _certifiedColumnDropOrigins ??= new Dictionary<TableKey, int>(_tables.Comparer);
        _certifiedColumnDropOrigins.TryAdd(key, CurrentOperationOrdinal);
        _ = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);
    }

    /// <summary>Expires narrow DROP provenance on changes whose dependent effects were not certified.</summary>
    private void UpdateIndependentColumnDropLineage(
        SafeMigrationIntent intent,
        SafeMigrationDecision decision,
        bool preservesColumnProofs
    )
    {
        if (_certifiedColumnDropOrigins is not { Count: > 0 }
            || decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.Repair))
        {
            return;
        }

        var owner = intent switch
        {
            EnsureTableIntent value => new TableKey(value.Definition.Table, value.Definition.Schema),
            DropTableIntent value => new TableKey(value.Table, value.Schema),
            RenameTableIntent value => new TableKey(value.Name, value.Schema),
            EnsureColumnIntent value => new TableKey(value.Table, value.Schema),
            AlterColumnIntent value => new TableKey(value.Table, value.Schema),
            DropColumnIntent value => new TableKey(value.Table, value.Schema),
            RenameColumnIntent value => new TableKey(value.Table, value.Schema),
            EnsureIndexIntent value => new TableKey(value.Definition.Table, value.Definition.Schema),
            DropIndexIntent value => new TableKey(value.Table, value.Schema),
            RenameIndexIntent value => new TableKey(value.Table, value.Schema),
            EnsurePrimaryKeyIntent value => new TableKey(value.Definition.Table, value.Definition.Schema),
            DropPrimaryKeyIntent value => new TableKey(value.Table, value.Schema),
            EnsureUniqueConstraintIntent value => new TableKey(value.Definition.Table, value.Definition.Schema),
            DropUniqueConstraintIntent value => new TableKey(value.Table, value.Schema),
            EnsureCheckConstraintIntent value => new TableKey(value.Definition.Table, value.Definition.Schema),
            DropCheckConstraintIntent value => new TableKey(value.Table, value.Schema),
            EnsureForeignKeyIntent value => new TableKey(value.Definition.Table, value.Definition.Schema),
            DropForeignKeyIntent value => new TableKey(value.Table, value.Schema),
            _ => (TableKey?)null,
        };

        if (owner is not { } key)
        {
            return;
        }

        // WHY: Adding a separate member does not establish its prospective values,
        // but also does not explain away the earlier certified removal's uncertainty.
        var preservesOrigin = intent is DropColumnIntent
            || (intent is EnsureColumnIntent && decision.Action == SafeMigrationAction.Apply)
            || (intent is EnsureColumnIntent or AlterColumnIntent && preservesColumnProofs)
            || intent is EnsurePrimaryKeyIntent
            || _projectedColumnAnalyzer?.PreservesIntrinsicColumnSources(intent) == true;

        if (!preservesOrigin)
        {
            _certifiedColumnDropOrigins.Remove(key);
        }

        if (intent is RenameTableIntent rename)
        {
            _certifiedColumnDropOrigins.Remove(
                new TableKey(rename.NewName ?? rename.Name, rename.NewSchema ?? rename.Schema));
        }
    }

    /// <summary>Admits only opted-in provider requalification after an uninterrupted certified DROP cause.</summary>
    private bool CanValidateKeyAfterColumnDrop(
        string table,
        string? schema
    ) => _projectedKeyAnalyzer?.CanValidateAfterColumnDrops == true
        && _certifiedColumnDropOrigins?.ContainsKey(new TableKey(table, schema)) == true
        && !_hasOpaqueProviderPostcondition
        && !_hasOpaqueSqlPostcondition
        && _providerDataMutationVersion == 0
        && !HasUnanalyzedDataChanges(table, schema);

    /// <summary>Keeps accepted missing or unknown member facts ahead of a provider's speculative key proof.</summary>
    private bool HasUnavailableKeyMember(
        string table,
        string? schema,
        IReadOnlyList<string> columns
    )
    {
        foreach (var t in columns)
        {
            if (IsProjectedColumnMissing(table, schema, t)
                || IsProjectedColumnUnknown(table, schema, t))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Separates an explainable DROP boundary from a fabricated actionable key postcondition.</summary>
    private static SafeMigrationProviderAnalysis KeyAfterColumnDropUnproven() => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "projected_key_after_column_drop_unproven");

    /// <inheritdoc />
    bool ISafeMigrationProjectedColumnSource.PreservesLiveColumnValues(
        string table,
        string? schema,
        string column
    )
    {
        var key = new TableKey(table, schema);
        var hasState = TryGetProjectedColumnState(table, schema, column, out var state);
        if ((_projectedStructurallyModifiedTables.Contains(key)
                && _certifiedColumnDropOrigins?.ContainsKey(key) != true
                && !HasColumnPreservingHistory(key)
                && _projectedIndexDropOnlyTables?.Contains(key) != true
                && (!hasState || !state.PreservesLiveValueDomain))
            || _hasOpaqueProviderPostcondition
            || _hasOpaqueSqlPostcondition
            || _providerDataMutationVersion != 0
            || HasUnanalyzedDataChanges(table, schema)
            || _projectedMissingTables.Contains(key)
            || (_prerequisites.TryGetValue(key, out var prerequisites) && prerequisites.NewlyCreated)
            || IsProjectedColumnMissing(table, schema, column)
            || IsProjectedColumnUnknown(table, schema, column))
        {
            return false;
        }

        var wasChanged = _projectedChangedColumns.TryGetValue(key, out var changes) && changes.Contains(column);

        // WHY: Completing an owned staging member preserves that exact member's
        // values, not the owner's full physical layout or another member's values.
        return hasState ? state.Definition is not null && (!wasChanged || state.PreservesLiveValueDomain) : !wasChanged;
    }

    /// <inheritdoc />
    int? ISafeMigrationProjectedColumnSource.GetColumnValueMutationOrigin(
        string table,
        string? schema,
        string column
    )
    {
        var key = new TableKey(table, schema);
        if (_hasOpaqueProviderPostcondition
            || _hasOpaqueSqlPostcondition
            || _providerDataMutationVersion != 0
            || HasUnanalyzedDataChanges(table, schema)
            || _projectedMissingTables.Contains(key)
            || (_prerequisites.TryGetValue(key, out var prerequisites) && prerequisites.NewlyCreated)
            || !TryGetProjectedColumnState(table, schema, column, out var state)
            || state.Definition is null
            || state.ValueMutationOriginOrdinal < 0)
        {
            return null;
        }

        // WHY: One compact ordinal travels with its exact member state; no
        // per-ADD side graph or nullable field is needed for large streams.

        return state.ValueMutationOriginOrdinal;
    }
}
