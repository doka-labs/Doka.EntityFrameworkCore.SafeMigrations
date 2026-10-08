namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private PostgreSqlTriggerRisk _eventTriggerRisk;
    private PostgreSqlTriggerRisk _dmlTriggerRisk;
    private int? _triggerOriginOrdinal;
    private string? _triggerOriginCode;
    private bool _triggerOriginIsDml;
    private int? _projectedOperationOrdinal;

    /// <inheritdoc />
    public void SetCurrentOperationOrdinal(int operationOrdinal) => _projectedOperationOrdinal = operationOrdinal;

    /// <inheritdoc />
    public bool PreservesExistingTableState(MigrationOperation operation)
        => ProviderOperationPreservesTableState(operation);

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis? ValidateCapturedProjection(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    ) => QualifyProjectedTriggerEffects(operation, liveAnalysis);

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis? ValidateOpaqueProviderPostcondition(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    ) => QualifyProjectedTriggerEffects(operation, liveAnalysis);

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis ValidateProjectedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projectedAnalysis,
        ISafeMigrationProjectedColumnSource columns
    ) => QualifyProjectedTriggerEffects(operation, projectedAnalysis) ?? projectedAnalysis;

    /// <inheritdoc />
    public void ObserveAcceptedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (!decision.ShouldExecute)
        {
            return;
        }

        if (operation.Intent is ModelManagedDataIntent)
        {
            InvalidateProjectedDmlTriggerEffects();

            return;
        }

        if (SafeColumnOperationMayEmitDml(operation, decision))
        {
            InvalidateProjectedDmlTriggerEffects();
        }

        if (operation.Intent is RenameTableIntent table
                && (table.NewName is null || table.Name == table.NewName)
                && (table.NewSchema is null || table.Schema == table.NewSchema)
            || operation.Intent is RenameIndexIntent index && index.Name == index.NewName)
        {
            return;
        }

        InvalidateProjectedEventTriggerEffects();
    }

    /// <inheritdoc />
    public void ObserveProviderOperation(MigrationOperation operation)
    {
        if (ProviderOperationPreservesTableState(operation))
        {
            return;
        }

        if (operation is InsertDataOperation or UpdateDataOperation or DeleteDataOperation)
        {
            InvalidateProjectedDmlTriggerEffects();

            return;
        }

        if (operation is AlterColumnOperation alter && ColumnAlterMayEmitDml(alter))
        {
            InvalidateProjectedDmlTriggerEffects();
        }

        if (operation is CreateTableOperation or AlterTableOperation or DropTableOperation
            or RenameTableOperation or AddColumnOperation or AlterColumnOperation or DropColumnOperation
            or RenameColumnOperation or CreateIndexOperation or DropIndexOperation or RenameIndexOperation
            or AddPrimaryKeyOperation or DropPrimaryKeyOperation or AddUniqueConstraintOperation
            or DropUniqueConstraintOperation or AddCheckConstraintOperation or DropCheckConstraintOperation
            or AddForeignKeyOperation or DropForeignKeyOperation or EnsureSchemaOperation or DropSchemaOperation
            or AlterDatabaseOperation or CreateSequenceOperation or AlterSequenceOperation or RestartSequenceOperation
            or RenameSequenceOperation or DropSequenceOperation)
        {
            InvalidateProjectedEventTriggerEffects();
        }
    }

    /// <summary>Recognizes only Npgsql baselines which preserve every captured table-scoped fact.</summary>
    /// <param name="operation">The ordinary provider operation in the ordered stream.</param>
    /// <returns>Whether the complete operation emits no table DDL or row write.</returns>
    private static bool ProviderOperationPreservesTableState(MigrationOperation operation)
    {
        // WHY: Npgsql 10 omits these zero-row table writes, unchanged renames and public-schema ensure.
        // Empty INSERT may still bump an identity sequence; sequence counters are not
        // table, column, constraint, index or row facts and cannot invoke DML/event triggers.
        // An unchanged column rename is not included: its baseline still emits ALTER TABLE.
        return operation is InsertDataOperation { Values.Length: 0 }
            or UpdateDataOperation { KeyValues.Length: 0 }
            or DeleteDataOperation { KeyValues.Length: 0 }
            or EnsureSchemaOperation { Name: "public" }
            || operation is RenameTableOperation table
                && (table.NewName is null || table.Name == table.NewName)
                && (table.NewSchema is null || table.Schema == table.NewSchema)
            || operation is RenameIndexOperation index && index.Name == index.NewName
            || operation is RenameSequenceOperation sequence
                && (sequence.NewName is null || sequence.Name == sequence.NewName)
                && (sequence.NewSchema is null || sequence.Schema == sequence.NewSchema);
    }

    /// <summary>Recognizes UPDATE emitted inside an accepted safe column baseline.</summary>
    /// <param name="operation">The safe operation whose executable baseline will run.</param>
    /// <param name="decision">The accepted decision, distinguishing ensure-add from ensure-repair.</param>
    /// <returns>Whether Npgsql can invoke user DML triggers before completing the column DDL.</returns>
    private static bool SafeColumnOperationMayEmitDml(
        SafeMigrationOperation operation,
        SafeMigrationDecision decision
    ) => operation.Intent switch
    {
        // WHY: The ensure-repair factory declares a synthetic nullable source even
        // for a NOT NULL live column. Npgsql emits UPDATE WHERE IS NULL whenever the
        // required target has a rendered default; zero affected rows still fire
        // statement-level triggers. A missing-column ADD has no such UPDATE.
        EnsureColumnIntent column => decision.Action == SafeMigrationAction.Repair
            && !column.Definition.IsNullable
            && column.Definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None,
        AlterColumnIntent column => column.OldDefinition?.IsNullable == true
            && !column.Definition.IsNullable
            && column.Definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None,
        _ => false,
    };

    /// <summary>Recognizes the ordinary Npgsql nullable-to-required backfill branch before its DDL.</summary>
    /// <param name="operation">The original provider column alteration.</param>
    /// <returns>Whether the baseline can execute a table UPDATE.</returns>
    private static bool ColumnAlterMayEmitDml(AlterColumnOperation operation)
        // WHY: Npgsql handles computed-column replacement before its backfill branch
        // and ignores system-column operations altogether. Neither emits a table UPDATE.
        => operation.OldColumn.IsNullable && !operation.IsNullable
            && (operation.DefaultValue is not null || operation.DefaultValueSql is not null)
            && operation.ComputedColumnSql == operation.OldColumn.ComputedColumnSql
            && operation.IsStored == operation.OldColumn.IsStored
            && operation.Name is not ("tableoid" or "xmin" or "cmin" or "xmax" or "cmax" or "ctid");

    /// <summary>Clears invocation-local trigger evidence and ordered DML/DDL provenance.</summary>
    private void ResetProjectedEventTriggerEffects()
    {
        _eventTriggerRisk = PostgreSqlTriggerRisk.None;
        _dmlTriggerRisk = PostgreSqlTriggerRisk.None;
        _triggerOriginOrdinal = null;
        _triggerOriginCode = null;
        _triggerOriginIsDml = false;
        _projectedOperationOrdinal = null;
    }

    /// <summary>Captures activation as two flags without retaining functions, names, owners, or user data.</summary>
    /// <param name="connection">The connection inside the active read-only analysis scope.</param>
    /// <param name="commandTimeout">The context's command timeout.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task CaptureProjectedEventTriggerEffectsAsync(
        DbConnection connection,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using var visibility = connection.CreateCommand();
        ApplyCommandTimeout(visibility, commandTimeout);
        visibility.CommandText = "SELECT (CASE WHEN "
            + "COALESCE(pg_catalog.current_setting('event_triggers', true), 'on')='off' "
            + "THEN 0 WHEN pg_catalog.has_table_privilege('pg_catalog.pg_event_trigger', 'SELECT') THEN 1 ELSE 2 END) "
            + "+ CASE WHEN pg_catalog.has_table_privilege('pg_catalog.pg_trigger', 'SELECT') THEN 4 ELSE 8 END;";

        // WHY: This bounded scalar encodes event visibility in the low two bits and
        // DML visibility in the next two. Two once-per-analysis reads serve both trigger
        // families; no per-operation query or retained owner/function inventory is needed.
        if (await visibility.ExecuteScalarAsync(cancellationToken) is not int access
            || access is not (4 or 5 or 6 or 8 or 9 or 10))
        {
            throw new InvalidOperationException(
                "The PostgreSQL trigger visibility query returned invalid evidence.");
        }

        var eventAccess = access & 3;
        var canReadDmlTriggers = (access & 4) != 0;
        if (eventAccess == 2)
        {
            _eventTriggerRisk = PostgreSqlTriggerRisk.VisibilityUnproven;
        }

        if (!canReadDmlTriggers)
        {
            _dmlTriggerRisk = PostgreSqlTriggerRisk.VisibilityUnproven;
        }

        if (eventAccess != 1 && !canReadDmlTriggers)
        {
            // WHY: Revoked catalog privilege never proves that triggers are absent.
            // Check privilege separately: PostgreSQL checks SELECT permissions before CASE branches.
            return;
        }

        await using var active = connection.CreateCommand();
        ApplyCommandTimeout(active, commandTimeout);
        // WHY: Construct only catalog references the caller can bind. Conditional SQL
        // evaluation cannot protect a SELECT against a catalog permission denial.
        var activeEventTrigger = eventAccess == 1
            ? "EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger e "
                + "WHERE e.evtevent IN ('ddl_command_start','ddl_command_end','sql_drop','table_rewrite') "
                + "AND (e.evtenabled='A' OR e.evtenabled='R' "
                + "AND pg_catalog.current_setting('session_replication_role')='replica' OR e.evtenabled='O' "
                + "AND pg_catalog.current_setting('session_replication_role') IN ('origin','local')))"
            : "FALSE";

        var activeDmlTrigger = canReadDmlTriggers
            ? "EXISTS (SELECT 1 FROM pg_catalog.pg_trigger t WHERE NOT t.tgisinternal "
                + "AND (t.tgenabled='A' OR t.tgenabled='R' "
                + "AND pg_catalog.current_setting('session_replication_role')='replica' OR t.tgenabled='O' "
                + "AND pg_catalog.current_setting('session_replication_role') IN ('origin','local')))"
            : "FALSE";

        active.CommandText = "SELECT (" + activeEventTrigger + ")::integer + 2*(" + activeDmlTrigger + ")::integer;";

        if (await active.ExecuteScalarAsync(cancellationToken) is not int activation || activation is < 0 or > 3)
        {
            throw new InvalidOperationException(
                "The PostgreSQL trigger activation query returned invalid evidence.");
        }

        if ((activation & 1) != 0)
        {
            _eventTriggerRisk = PostgreSqlTriggerRisk.EnabledTrigger;
        }

        if ((activation & 2) != 0)
        {
            _dmlTriggerRisk = PostgreSqlTriggerRisk.EnabledTrigger;
        }
    }

    /// <summary>Records the exact preceding executable DDL responsible for unknown rows and structure.</summary>
    private void InvalidateProjectedEventTriggerEffects()
    {
        if (_eventTriggerRisk != PostgreSqlTriggerRisk.None)
        {
            // WHY: Event-trigger functions can run arbitrary DML and DDL, including on unrelated
            // tables. Neither a matching postcondition for the initiating object nor an empty
            // projected CREATE proves that captured rows, columns, or dependencies survived.
            _triggerOriginOrdinal = _projectedOperationOrdinal;
            _triggerOriginIsDml = false;
            _triggerOriginCode = _eventTriggerRisk == PostgreSqlTriggerRisk.EnabledTrigger
                ? "projected_event_trigger_state_unknown" : "projected_event_trigger_visibility_unknown";
        }
    }

    /// <summary>Invalidates structural proofs after DML which can reach an arbitrary user trigger.</summary>
    private void InvalidateProjectedDmlTriggerEffects()
    {
        if (_dmlTriggerRisk != PostgreSqlTriggerRisk.None)
        {
            // WHY: A direct owner filter misses FK cascades, partition routing and
            // recursive trigger writes. User functions can execute unrelated DDL;
            // a global presence flag is conservative without parsing arbitrary bodies.
            // Internal FK triggers alone have known effects and do not set this flag.
            _triggerOriginOrdinal = _projectedOperationOrdinal;
            _triggerOriginIsDml = true;
            _triggerOriginCode = _dmlTriggerRisk == PostgreSqlTriggerRisk.EnabledTrigger
                ? "projected_dml_trigger_structure_unknown" : "projected_dml_trigger_visibility_unknown";
        }
    }

    /// <summary>Defers stale captured truth without inventing a missing prerequisite or a raw-SQL origin.</summary>
    /// <param name="operation">The operation whose original rows or metadata may no longer be current.</param>
    /// <param name="analysis">The analysis that still describes the original read-only snapshot.</param>
    /// <returns>A fresh runtime boundary, or null when no prior trigger-risk operation invalidated evidence.</returns>
    private SafeMigrationProviderAnalysis? QualifyProjectedTriggerEffects(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (_triggerOriginOrdinal is not { } ordinal)
        {
            return null;
        }

        if (analysis.IsInvariantUnsupported || analysis.ObservedState == SafeMigrationObservedState.Unsupported)
        {
            // WHY: Unknown trigger effects cannot authorize unsupported contracts or metadata grants.
            return analysis;
        }

        return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false,
            _triggerOriginIsDml && operation.Intent is ModelManagedDataIntent
                ? "projected_model_managed_data_state_unknown" : _triggerOriginCode!)
        {
            ProviderDeferredOriginOrdinal = ordinal,
        };
    }
}

/// <summary>Separates a certified trigger absence from activation or inaccessible catalog evidence.</summary>
internal enum PostgreSqlTriggerRisk
{
    /// <summary>No trigger in this family fires in the current session.</summary>
    None,
    /// <summary>An enabled user trigger can change captured rows or structure.</summary>
    EnabledTrigger,
    /// <summary>The caller cannot inspect the trigger catalog to prove absence.</summary>
    VisibilityUnproven,
}
