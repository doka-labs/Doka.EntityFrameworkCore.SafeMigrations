namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <inheritdoc />
    bool ISafeMigrationProjectedKeyAnalyzer.CanValidateAfterColumnDrops => true;

    /// <summary>Requalifies a replacement only from independent rows and accepted member lifetimes.</summary>
    /// <param name="intent">The complete authored replacement key.</param>
    /// <param name="columns">The current invocation's accepted sparse member facts.</param>
    /// <param name="live">The immutable native row and physical observation.</param>
    /// <param name="projected">The deliberately unproven Core replacement state.</param>
    /// <returns>A qualified result, or the original uncertainty when no current proof exists.</returns>
    private static SafeMigrationProviderAnalysis QualifyReplacementPrimaryKey(
        EnsurePrimaryKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis live,
        SafeMigrationProviderAnalysis projected
    )
    {
        if (!IsUnprovenProjectedKey(projected)
            || columns is not ISafeMigrationProjectedTableSource tables
            || !tables.TryGetProjectedTableState(intent.Definition.Table, intent.Definition.Schema, out var state)
            || !state.PrimaryKeyWasDropped
            || state.IsNewlyCreated
            || state.HasDataMutation)
        {
            return projected;
        }

        if (live.ObservedState == SafeMigrationObservedState.Unsupported
            && live.Code != "primary_key_exceeds_physical_limit")
        {
            return live;
        }

        var valuesPreserved = true;
        int? generatedOrigin = null;
        foreach (var name in intent.Definition.Columns)
        {
            if (columns.PreservesLiveColumnValues(intent.Definition.Table, intent.Definition.Schema, name))
            {
                continue;
            }

            valuesPreserved = false;
            if (!columns.TryGetProjectedColumn(intent.Definition.Table, intent.Definition.Schema, name, out _))
            {
                // WHY: Native width metadata may still describe a dropped
                // member. It does not prove that the replacement member exists.
                return projected;
            }

            generatedOrigin ??= columns.GetColumnValueMutationOrigin(
                intent.Definition.Table,
                intent.Definition.Schema,
                name);
        }

        if (valuesPreserved)
        {
            if (live.ObservedState == SafeMigrationObservedState.DataBlocked)
            {
                return live;
            }

            if (live.Code == "primary_key_replacement_data_blocked")
            {
                return new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.DataBlocked,
                    SafeMigrationRepairCapability.None,
                    false,
                    live.Code);
            }

            if (live.Code == "key_replacement_data_safe"
                || live.ObservedState is SafeMigrationObservedState.Missing or SafeMigrationObservedState.Matching)
            {
                return ProjectedKeyMissing();
            }
        }

        // WHY: UUID/default-generated values are not in the immutable catalog
        // snapshot. Inspect their actual ordered values with the mandatory
        // runtime key guard instead of claiming prospective uniqueness.
        return DeferProjectedKey(generatedOrigin ?? state.ColumnDropOriginOrdinal, projected);
    }

    /// <summary>Retains changed native index identities until the mandatory ordered guard observes them.</summary>
    /// <param name="table">The physical table owner.</param>
    /// <param name="schema">The normalized provider schema.</param>
    /// <param name="columns">The accepted sparse member source.</param>
    /// <param name="live">The independently captured original result.</param>
    /// <param name="projected">The unknown-after-DROP marker, never a fabricated Missing.</param>
    /// <param name="valuesPreserved">
    /// Whether original row comparisons retain every target member's value domain.
    /// </param>
    /// <returns>The original result or actual-origin runtime validation.</returns>
    private static SafeMigrationProviderAnalysis QualifyKeyAfterColumnDrop(
        string table,
        string? schema,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis live,
        SafeMigrationProviderAnalysis projected,
        bool valuesPreserved
    )
    {
        if (projected.Code != "projected_key_after_column_drop_unproven"
            || columns is not ISafeMigrationProjectedTableSource tables
            || !tables.TryGetProjectedTableState(table, schema, out var state)
            || state.IsNewlyCreated
            || state.HasDataMutation)
        {
            return projected;
        }

        // WHY: Both callers retain immutable Unsupported results and qualify
        // current physical feasibility first. An old width rejection must not
        // override that newer proof while the key identity remains deferred.
        if (valuesPreserved && live.ObservedState == SafeMigrationObservedState.DataBlocked)
        {
            return live;
        }

        if (valuesPreserved
            && live.Code is "index_replacement_data_blocked" or "unique_constraint_replacement_data_blocked")
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.DataBlocked,
                SafeMigrationRepairCapability.None,
                false,
                live.Code);
        }

        // WHY: DROP may shrink a composite index into a semantic alias or
        // remove it. Historical Matching and absence cannot bind its final
        // physical name; the runtime guard must resolve that namespace again.
        return DeferProjectedKey(state.ColumnDropOriginOrdinal, projected);
    }

    /// <summary>Requires a surviving accepted member before consulting original physical width metadata.</summary>
    /// <param name="table">The current physical owner.</param>
    /// <param name="schema">The normalized provider schema.</param>
    /// <param name="column">The ordered target key member.</param>
    /// <param name="source">The synchronous current-column source.</param>
    /// <param name="valuesPreserved">
    /// Accumulates whether every target comparison retains its original value domain.
    /// </param>
    /// <returns>Whether a current member, rather than a stale dropped-column width, is proven.</returns>
    private static bool HasCurrentProjectedKeyMember(
        string table,
        string? schema,
        string column,
        ISafeMigrationProjectedColumnSource source,
        ref bool valuesPreserved
    )
    {
        if (source.PreservesLiveColumnValues(table, schema, column))
        {
            return true;
        }

        valuesPreserved = false;

        return source.TryGetProjectedColumn(table, schema, column, out _);
    }

    /// <summary>Recognizes only the Core markers whose replacement prerequisites still need qualification.</summary>
    private static bool IsUnprovenProjectedKey(
        SafeMigrationProviderAnalysis analysis
    ) => analysis.Code is "projected_primary_key_replacement_unproven" or "projected_key_after_column_drop_unproven";

    /// <summary>Returns absence only after native row proof and current member qualification.</summary>
    private static SafeMigrationProviderAnalysis ProjectedKeyMissing() => new(
        SafeMigrationObservedState.Missing,
        SafeMigrationRepairCapability.None,
        false,
        "projected_missing");

    /// <summary>Attaches an actual preceding accepted mutation without inventing an object postcondition.</summary>
    /// <param name="origin">The complete-stream ordinal of the accepted prerequisite mutation.</param>
    /// <param name="projected">The original unproven state when no provenance is available.</param>
    /// <returns>Explicit runtime validation, or unchanged uncertainty when the origin is unavailable.</returns>
    private static SafeMigrationProviderAnalysis DeferProjectedKey(
        int? origin,
        SafeMigrationProviderAnalysis projected
    ) => origin is >= 0
        ? new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None,
            false,
            "projected_key_values_require_runtime")
        {
            ProviderDeferredOriginOrdinal = origin,
        }
        : projected;
}
