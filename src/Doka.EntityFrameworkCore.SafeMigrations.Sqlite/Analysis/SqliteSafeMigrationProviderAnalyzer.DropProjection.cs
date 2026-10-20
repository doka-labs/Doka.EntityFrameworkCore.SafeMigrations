namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    private SqliteCatalogSnapshot? _dropProjectionSnapshot;
    private Dictionary<SafeMigrationOperation, SafeMigrationProviderAnalysis>? _dropProjectionAnalyses;
    private Dictionary<string, HashSet<string>>? _acceptedColumnDrops;
    private bool _dropProjectionExpired;
    private int _currentOperationOrdinal = -1;
    private int? _rebuildRowMutationOrdinal;

    /// <summary>Starts one catalog-bound window shared by optional preflight and mandatory runtime guards.</summary>
    /// <param name="snapshot">The immutable native catalog captured for this invocation.</param>
    internal void ResetDropOnlyProjection(SqliteCatalogSnapshot snapshot)
    {
        _dropProjectionSnapshot = snapshot;
        _dropProjectionAnalyses = null;
        _acceptedColumnDrops = null;
        _dropProjectionExpired = false;
        _currentOperationOrdinal = -1;
        _rebuildRowMutationOrdinal = null;
    }

    /// <inheritdoc />
    public void SetCurrentOperationOrdinal(int operationOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(operationOrdinal);

        _currentOperationOrdinal = operationOrdinal;
    }

    /// <summary>Captures immutable native facts without treating existing tables as newly created or empty.</summary>
    private void CaptureDropOnlyProjection(SafeMigrationOperation operation, SafeMigrationProviderAnalysis analysis)
    {
        if ((operation.Intent is EnsureColumnIntent { Definition.ComputedExpression: not null }
                && analysis.ObservedState == SafeMigrationObservedState.Matching)
            || (operation.Intent is EnsureIndexIntent index && IsSimpleIndex(index.Definition)
                && analysis.ObservedState == SafeMigrationObservedState.Missing))
        {
            CaptureDropProjectionEvidence(operation, analysis);
        }
    }

    /// <summary>
    /// Reuses one reference-keyed capture for conditional ownership and row refusals or native baselines.
    /// </summary>
    private void CaptureDropProjectionEvidence(SafeMigrationOperation operation, SafeMigrationProviderAnalysis analysis)
    {
        _dropProjectionAnalyses ??= new Dictionary<SafeMigrationOperation, SafeMigrationProviderAnalysis>(
            ReferenceEqualityComparer.Instance);

        _dropProjectionAnalyses[operation] = analysis;
    }

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis ValidateProjectedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projectedAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        // WHY: Column presence is not a rebuild certificate. Preserve independent
        // ownership and row refusals when exact accepted presence replaces native
        // state, without charging a genuinely absent no-op for a rebuild.
        if (SqliteSafeMigrationOperationClassifier.RequiresTableRebuild(operation.Intent)
            && _dropProjectionAnalyses?.TryGetValue(operation, out var rebuildBlocker) == true
            && rebuildBlocker.ObservedState is SafeMigrationObservedState.Unsupported
                or SafeMigrationObservedState.DataBlocked
            && (SafeMigrationDecisionPlanner.PlanAction(operation.Intent.Kind, projectedAnalysis.ObservedState,
                    operation.Policy, projectedAnalysis.RepairCapability)
                    is SafeMigrationAction.Apply or SafeMigrationAction.Repair
                || (rebuildBlocker.ObservedState == SafeMigrationObservedState.DataBlocked
                    && projectedAnalysis.ObservedState == SafeMigrationObservedState.DataBlocked
                    && _rebuildRowMutationOrdinal is not null))
            && !(columns is ISafeMigrationProjectedTableSource tables
                && SqliteSafeMigrationOperationClassifier.TableName(operation.Intent) is { } rebuildOwner
                && tables.TryGetProjectedTableState(rebuildOwner, null, out var rebuildState)
                && rebuildState.IsNewlyCreated))
        {
            if (rebuildBlocker.ObservedState == SafeMigrationObservedState.DataBlocked
                && _rebuildRowMutationOrdinal is { } mutationOrdinal)
            {
                // WHY: A typed repair, backfill or cascading DROP may change the
                // rows, unlike unrelated metadata. Neither the old refusal nor
                // optimistic source presence proves the current relationship.
                return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                    SafeMigrationRepairCapability.None, postconditionSatisfied: false, "projected_data_state_unknown")
                {
                    ProviderDeferredOriginOrdinal = mutationOrdinal >= 0 ? mutationOrdinal : null,
                };
            }

            return rebuildBlocker;
        }

        if (operation.Intent is DropColumnIntent drop)
        {
            return ValidateGeneratedColumnDrop(operation, drop, projectedAnalysis, columns);
        }

        if (_dropProjectionExpired || projectedAnalysis.Code != "projected_structure_state_unknown"
            || _dropProjectionAnalyses is not { } capturedAnalyses
            || !capturedAnalyses.TryGetValue(operation, out var captured)
            || SqliteSafeMigrationOperationClassifier.TableName(operation.Intent) is not { } owner
            || _acceptedColumnDrops is not { } acceptedDrops
            || !acceptedDrops.TryGetValue(owner, out var dropped)
            || _dropProjectionSnapshot is not { } snapshot || !snapshot.Tables.TryGetValue(owner, out var table))
        {
            return projectedAnalysis;
        }

        // WHY: Accepted DROP analysis already certified the complete native
        // rebuild artifact contract. Only untouched intrinsic columns survive;
        // recursive generated operands and missing nonunique index keys must
        // retain their original physical identities through every such rebuild.
        var unchanged = operation.Intent switch
        {
            EnsureColumnIntent value when captured.ObservedState == SafeMigrationObservedState.Matching
                => HasRetainedColumnSource(table, dropped, value.Definition.Name, null),
            EnsureIndexIntent value when captured.ObservedState == SafeMigrationObservedState.Missing
                && IsSimpleIndex(value.Definition) => HasRetainedIndexSources(table, dropped, value.Definition),
            _ => false,
        };

        if (!unchanged)
        {
            return projectedAnalysis;
        }

        // WHY: Native source survival does not erase an earlier accepted index
        // identity. Core owns exact, alias and tombstone precedence; a captured
        // Missing result must not overwrite a known same-name different index.
        return operation.Intent is EnsureIndexIntent index
            ? columns.GetProjectedIndexIdentityAnalysis(index.Definition, captured) ?? captured
            : captured;
    }

    /// <summary>Combines native surviving expressions with only accepted, same-lifetime prefix removals.</summary>
    private SafeMigrationProviderAnalysis ValidateGeneratedColumnDrop(
        SafeMigrationOperation operation,
        DropColumnIntent drop,
        SafeMigrationProviderAnalysis projectedAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (projectedAnalysis.ObservedState == SafeMigrationObservedState.Missing)
        {
            return projectedAnalysis;
        }

        if (projectedAnalysis.ObservedState == SafeMigrationObservedState.Unsupported
            && projectedAnalysis.Code is not ("generated_column_drop_dependency" or "generated_column_drop_unproven"))
        {
            return projectedAnalysis;
        }

        var projectedTables = columns as ISafeMigrationProjectedTableSource;
        var dependency = projectedTables?.HasProjectedGeneratedColumnDependency(
            drop.Table, drop.Schema, drop.Name, s_identifierComparer);

        if (dependency == true)
        {
            return Unsupported("generated_column_drop_dependency");
        }

        if (dependency == false
            && projectedTables is not null
            && projectedTables.TryGetProjectedTableState(drop.Table, drop.Schema, out var state)
            && state.IsNewlyCreated)
        {
            // WHY: A recreated owner has a different physical lifetime. Its
            // accepted complete generated set, not the old native table SQL,
            // determines whether the new member may be removed.
            return projectedAnalysis;
        }

        if (_dropProjectionSnapshot is not { } snapshot
            || !snapshot.Tables.TryGetValue(drop.Table, out var table))
        {
            return projectedAnalysis;
        }

        if (_dropProjectionExpired)
        {
            // WHY: Accepted source presence can replace the native Unsupported
            // assessment in neutral projection. An expired dependency window
            // must not let that shortcut silently authorize the same DROP.
            return projectedAnalysis.ObservedState == SafeMigrationObservedState.Matching
                && AnalyzeGeneratedColumnDrop(table, drop.Name, dropped: null) is not null
                    ? Unsupported("generated_column_drop_unproven")
                    : projectedAnalysis;
        }

        HashSet<string>? dropped = null;
        _ = _acceptedColumnDrops?.TryGetValue(drop.Table, out dropped);
        var blocker = AnalyzeGeneratedColumnDrop(table, drop.Name, dropped);
        if (blocker is not null)
        {
            return blocker;
        }

        // WHY: The native dependency can disappear after an accepted earlier
        // DROP, but all other rebuild guards must retain their captured result.
        return projectedAnalysis.Code is "generated_column_drop_dependency" or "generated_column_drop_unproven"
            && _dropProjectionAnalyses?.TryGetValue(operation, out var baseline) == true
                ? baseline
                : projectedAnalysis;
    }

    /// <summary>Reads identifier roles from the complete captured native generated-column set.</summary>
    private static SafeMigrationProviderAnalysis? AnalyzeGeneratedColumnDrop(
        SqliteTableSnapshot table,
        string column,
        HashSet<string>? dropped
    )
    {
        var unproven = false;
        foreach (var candidate in table.Columns.Values)
        {
            if (s_identifierComparer.Equals(candidate.Name, column) || dropped?.Contains(candidate.Name) == true
                || (candidate.GeneratedSql is null && candidate.HiddenKind is not (2 or 3)))
            {
                continue;
            }

            if (candidate.GeneratedSql is not { } sql
                || !SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression)
                || !SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression))
            {
                unproven = true;
                continue;
            }

            if (SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
                    expression, column, s_identifierComparer))
            {
                return Unsupported("generated_column_drop_dependency");
            }
        }

        return unproven ? Unsupported("generated_column_drop_unproven") : null;
    }

    /// <inheritdoc />
    public void ObserveAcceptedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.Repair))
        {
            return;
        }

        if (operation.Intent is ModelManagedDataIntent or DropTableIntent or AlterColumnIntent
            || analysis.RepairMutatesData)
        {
            // WHY: Row effects have a separate lifetime from generated-source
            // metadata. A DROP can cascade, while a repair may backfill values
            // and fire triggers outside the directly authored owner.
            _rebuildRowMutationOrdinal = _currentOperationOrdinal;
        }

        if (!_dropProjectionExpired && operation.Intent is DropColumnIntent drop
            && analysis.ObservedState == SafeMigrationObservedState.Matching)
        {
            _acceptedColumnDrops ??= new Dictionary<string, HashSet<string>>(s_identifierComparer);
            if (!_acceptedColumnDrops.TryGetValue(drop.Table, out var columns))
            {
                columns = new HashSet<string>(s_identifierComparer);
                _acceptedColumnDrops.Add(drop.Table, columns);
            }

            _ = columns.Add(drop.Name);

            return;
        }

        if (operation.Intent is EnsureIndexIntent index && IsSimpleIndex(index.Definition))
        {
            // WHY: A simple index creates no column or row mutation. Subsequent
            // index observations still consult Core's accepted identity before
            // using the immutable native Missing certificate.
            return;
        }

        _dropProjectionExpired = true;
    }

    /// <summary>Separates row-independent native index creation from expressions, filters and uniqueness.</summary>
    private static bool IsSimpleIndex(ExpectedIndexDefinition definition)
    {
        if (definition.Unique || definition.Filter is not null || definition.StructuredFilter is not null)
        {
            return false;
        }

        foreach (var key in definition.Keys)
        {
            if (key.Column is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks native operand identities without allocating a per-key predicate closure.</summary>
    private static bool HasRetainedIndexSources(
        SqliteTableSnapshot table,
        HashSet<string> dropped,
        ExpectedIndexDefinition definition
    )
    {
        foreach (var key in definition.Keys)
        {
            if (key.Column is not { } column || !HasRetainedColumnSource(table, dropped, column, null))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public void ObserveProviderOperation(MigrationOperation operation)
    {
        // WHY: Ordinary provider commands have no accepted safe rebuild proof.
        // Even a later DROP cannot revive catalog facts invalidated by raw SQL.
        _dropProjectionExpired = true;
        if (operation is DropTableOperation or AlterColumnOperation
            || !SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(operation))
        {
            // WHY: Typed DML and unknown provider commands need live row proof.
            // Known row-independent structural commands must not erase a proven
            // orphan merely because they invalidate unrelated layout metadata.
            _rebuildRowMutationOrdinal = _currentOperationOrdinal;
        }
    }

    /// <summary>Rejects both directly dropped columns and transitive generated dependencies.</summary>
    private static bool HasRetainedColumnSource(
        SqliteTableSnapshot table,
        HashSet<string> dropped,
        string column,
        HashSet<string>? visited
    )
    {
        if (dropped.Contains(column) || !table.Columns.TryGetValue(column, out var source))
        {
            return false;
        }

        if (source.GeneratedSql is not { } sql)
        {
            return true;
        }

        visited ??= new HashSet<string>(s_identifierComparer);
        if (!visited.Add(column))
        {
            return true;
        }

        if (!SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression))
        {
            return false;
        }

        var references = new HashSet<string>(s_identifierComparer);
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression, references);
        foreach (var reference in references)
        {
            if (!HasRetainedColumnSource(table, dropped, reference, visited))
            {
                return false;
            }
        }

        return true;
    }
}
