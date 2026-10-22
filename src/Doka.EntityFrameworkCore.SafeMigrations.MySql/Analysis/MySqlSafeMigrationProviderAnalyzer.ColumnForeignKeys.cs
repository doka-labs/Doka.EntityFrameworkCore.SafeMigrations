namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    private Dictionary<SafeMigrationOperation, IReadOnlySet<string>>? _columnSetNullDependencies;
    private HashSet<SafeMigrationOperation>? _columnPrimaryKeyDependencies;

    /// <summary>Captures bounded SET NULL and PRIMARY dependencies for source-proven column transitions.</summary>
    /// <param name="context">The active provider context.</param>
    /// <param name="operations">The immutable analyzed operations.</param>
    /// <param name="analyses">Their independently captured source and row proofs.</param>
    /// <param name="maximumPayloadBytes">The bounded catalog transport payload.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    private async Task CaptureColumnDependenciesAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        _columnSetNullDependencies = null;
        _columnPrimaryKeyDependencies = null;
        var (requests, aliases) = CreateColumnDependencyRequests(operations, analyses, _currentDatabase);
        if (requests.Count == 0)
        {
            return;
        }

        HashSet<SafeMigrationOperation>? primary = null;
        Dictionary<SafeMigrationOperation, IReadOnlySet<string>>? setNull = null;
        await ReadColumnDependenciesAsync(
            context.Database.GetDbConnection(), requests, maximumPayloadBytes, commandTimeout,
            (ordinal, hasPrimaryKey, names) =>
            {
                AddColumnDependencyFact(requests[ordinal].Operation, hasPrimaryKey, names, ref primary, ref setNull);
                if (aliases?.TryGetValue(ordinal, out var consumers) == true)
                {
                    foreach (var consumer in consumers)
                    {
                        AddColumnDependencyFact(consumer, hasPrimaryKey, names, ref primary, ref setNull);
                    }
                }
            },
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // WHY: No partial certificate becomes visible if transport, validation
        // or cancellation fails. Completed FK sets are shared without mutation
        // between independently source-bound operation-reference consumers.
        _columnPrimaryKeyDependencies = primary;
        _columnSetNullDependencies = setNull;
    }

    /// <inheritdoc />
    SafeMigrationProviderAnalysis? ISafeMigrationProjectedDependencyAnalyzer.ValidateCapturedProjection(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (ValidateCapturedGeneratedColumn(operation, liveAnalysis, columns) is { } generated)
        {
            return generated;
        }

        if (!TryGetColumnDependencyTarget(operation.Intent, out var table, out var schema, out var column)
            || liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported)
        {
            return null;
        }

        var certifiedSurvivingSource = columns is ISafeMigrationProjectedTableSource dropTables
            && dropTables.TryGetProjectedTableState(table, schema, out var dropState)
            && dropState is { IsNewlyCreated: false, HasDataMutation: false, ColumnDropOriginOrdinal: >= 0 }
            && columns.PreservesLiveColumnValues(table, schema, column.Name);

        if (column.IsNullable)
        {
            var primaryMembership = columns.IsProjectedPrimaryKeyColumn(
                table, schema, column.Name, StringComparer.OrdinalIgnoreCase);

            if (primaryMembership == true)
            {
                return NullableColumnPrimaryKeyDependency();
            }

            if (primaryMembership == false)
            {
                // WHY: An accepted different key or key removal supersedes
                // original membership. Unknown absence must instead retain
                // the captured source check below, not authorize a repair.
                return null;
            }

            if (_foreignKeyReplacementRowsInvalidated
                || _columnPrimaryKeyDependencies?.Contains(operation) != true
                || (!certifiedSurvivingSource
                    && _invalidatedForeignKeyTables?.Contains((NormalizeProofSchema(schema), table)) == true))
            {
                return null;
            }

            if (columns is ISafeMigrationProjectedTableSource primaryTables
                && primaryTables.TryGetProjectedTableState(table, schema, out var primaryState)
                && (primaryState.IsNewlyCreated || (primaryState.HasUnknownStructure && !certifiedSurvivingSource)
                    || primaryState.HasDataMutation || primaryState.PrimaryKeyWasDropped))
            {
                return null;
            }

            // WHY: Original PRIMARY membership is not discharged by a column
            // repair or FK removal. Only the accepted current Core graph may
            // prove its ordered drop; recreation clears that absence marker.
            return NullableColumnPrimaryKeyDependency();
        }

        if (columns.HasProjectedSetNullForeignKey(
                table, schema, column.Name, StringComparer.OrdinalIgnoreCase))
        {
            return RequiredColumnSetNullDependency();
        }

        if (_foreignKeyReplacementRowsInvalidated
            || _columnSetNullDependencies is null
            || !_columnSetNullDependencies.TryGetValue(operation, out var dependencies)
            || (!certifiedSurvivingSource
                && _invalidatedForeignKeyTables?.Contains((NormalizeProofSchema(schema), table)) == true)
            || (columns is ISafeMigrationProjectedTableSource tables
                && tables.TryGetProjectedTableState(table, schema, out var state)
                && (state.IsNewlyCreated || (state.HasUnknownStructure && !certifiedSurvivingSource))))
        {
            return null;
        }

        foreach (var name in dependencies)
        {
            if (_acceptedForeignKeyRemovals?.Contains((NormalizeProofSchema(schema), table, name)) != true)
            {
                // WHY: NULL-free rows do not make a SET NULL action compatible
                // with NOT NULL. Only removal of every exact physical dependency
                // discharges this blocker; runtime checks the catalog again.
                return RequiredColumnSetNullDependency();
            }
        }

        return null;
    }

    /// <summary>Shares dependency checks between explicit alters and existence-based column repairs.</summary>
    /// <param name="intent">The immutable column operation.</param>
    /// <param name="table">The physical owner of the column target.</param>
    /// <param name="schema">The authored current-database qualifier.</param>
    /// <param name="column">The exact nullable or required target declaration.</param>
    /// <returns>True when the operation has a column target with physical dependency rules.</returns>
    private static bool TryGetColumnDependencyTarget(
        SafeMigrationIntent intent,
        [NotNullWhen(true)] out string? table,
        out string? schema,
        [NotNullWhen(true)] out ExpectedColumnDefinition? column
    )
    {
        // WHY: Ensure can repair an existing column just as Alter can. The
        // dependency is a property of the target, not of the authoring API.
        (table, schema, column) = intent switch
        {
            AlterColumnIntent alter
                => (alter.Table, alter.Schema, alter.Definition),
            EnsureColumnIntent ensure
                => (ensure.Table, ensure.Schema, ensure.Definition),
            _ => (null, null, null),
        };

        return column is not null;
    }

    /// <summary>Reports a structural dependency refusal without discarding the authored target.</summary>
    /// <returns>The explicit NOT NULL compatibility rejection.</returns>
    private static SafeMigrationProviderAnalysis RequiredColumnSetNullDependency()
        => new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None,
            postconditionSatisfied: false, "column_set_null_dependency");

    /// <summary>Refuses nullable-column DDL while a current physical primary key requires NOT NULL.</summary>
    /// <returns>The explicit PRIMARY compatibility rejection.</returns>
    private static SafeMigrationProviderAnalysis NullableColumnPrimaryKeyDependency()
        => new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None,
            postconditionSatisfied: false, "column_primary_key_dependency");
}
