namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    // WHY: Column shape alone cannot prove SQL Server's single table-wide
    // identity slot. Null records mean a removal or opaque effect lacks proof.
    private readonly Dictionary<(string Schema, string Table), string?> _projectedIdentitySlots = [];
    private readonly HashSet<(string Schema, string Table)> _liveIdentitySlotConflicts = [];
    private bool _identityProjectionOpaque;

    private void ResetProjectedIdentityProofs()
    {
        _projectedIdentitySlots.Clear();
        _liveIdentitySlotConflicts.Clear();
        _identityProjectionOpaque = false;
    }

    private void CaptureIdentitySlotConflicts(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses
    )
    {
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal].Intent is EnsureColumnIntent column
                && analyses[ordinal].Code == "identity_slot_occupied")
            {
                _liveIdentitySlotConflicts.Add((column.Schema ?? "dbo", column.Table));
            }
        }
    }

    /// <summary>Qualifies the table-wide identity slot independently of row-layout proof.</summary>
    /// <param name="operation">The ordered operation under assessment.</param>
    /// <param name="analysis">The earlier provider-qualified assessment.</param>
    /// <returns>The original analysis or a fail-closed identity-slot assessment.</returns>
    internal SafeMigrationProviderAnalysis QualifyProjectedIdentityOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (analysis.IsInvariantUnsupported || analysis.IsOpaqueProjectionUnknown
            || analysis.ObservedState != SafeMigrationObservedState.Missing
            || operation.Intent is not EnsureColumnIntent column
            || !SqlServerSafeMigrationCatalogSqlBuilder.TryGetIdentity(
                column.Definition, out var identity, out _, out _) || !identity)
        {
            return analysis;
        }

        var key = (column.Schema ?? "dbo", column.Table);
        if (_projectedIdentitySlots.TryGetValue(key, out var slot))
        {
            if (slot == string.Empty)
            {
                return analysis;
            }

            return IdentitySlotFailure(slot is null ? "projected_identity_slot_unproven" : "identity_slot_occupied");
        }

        return _identityProjectionOpaque || _liveIdentitySlotConflicts.Contains(key)
            ? IdentitySlotFailure("projected_identity_slot_unproven") : analysis;
    }

    private void ObserveProjectedIdentityOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.Repair
            or SafeMigrationAction.NoOp))
        {
            return;
        }

        switch (operation.Intent)
        {
            case EnsureTableIntent table when decision.ShouldExecute
                || table.Mode == SafeMigrationTableMode.StrictDefinition
                    && analysis.ObservedState == SafeMigrationObservedState.Matching:
                var identityColumn = table.Definition.Columns.FirstOrDefault(static column =>
                    SqlServerSafeMigrationCatalogSqlBuilder.TryGetIdentity(column, out var identity, out _, out _)
                        && identity);

                _projectedIdentitySlots[(table.Definition.Schema ?? "dbo", table.Definition.Table)]
                    = identityColumn?.Name ?? string.Empty;
                break;
            case EnsureColumnIntent column when SqlServerSafeMigrationCatalogSqlBuilder.TryGetIdentity(
                column.Definition, out var identity, out _, out _) && identity
                && (decision.ShouldExecute || analysis.ObservedState == SafeMigrationObservedState.Matching):
                _projectedIdentitySlots[(column.Schema ?? "dbo", column.Table)] = column.Definition.Name;
                break;
            case DropColumnIntent column when decision.ShouldExecute:
                var key = (column.Schema ?? "dbo", column.Table);
                if (!_projectedIdentitySlots.TryGetValue(key, out var slot) || slot == column.Name)
                {
                    _projectedIdentitySlots[key] = null;
                }

                break;
            case DropTableIntent table when decision.ShouldExecute:
                _projectedIdentitySlots[(table.Schema ?? "dbo", table.Table)] = null;
                break;
            case RenameTableIntent table when decision.ShouldExecute:
                var oldKey = (table.Schema ?? "dbo", table.Name);
                var newKey = (table.NewSchema ?? table.Schema ?? "dbo", table.NewName ?? table.Name);
                _projectedIdentitySlots.TryGetValue(oldKey, out var oldSlot);
                _projectedIdentitySlots[oldKey] = null;
                _projectedIdentitySlots[newKey] = oldSlot;
                break;
            case RenameColumnIntent column when decision.ShouldExecute:
                var columnKey = (column.Schema ?? "dbo", column.Table);
                if (_projectedIdentitySlots.TryGetValue(columnKey, out var oldIdentity) && oldIdentity == column.Name)
                {
                    _projectedIdentitySlots[columnKey] = column.NewName;
                }

                break;
        }
    }

    private void InvalidateProjectedIdentityProofs()
    {
        _identityProjectionOpaque = true;
        _projectedIdentitySlots.Clear();
    }

    private static SafeMigrationProviderAnalysis IdentitySlotFailure(
        string code
    ) => new(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationRepairCapability.None, false, code);
}
