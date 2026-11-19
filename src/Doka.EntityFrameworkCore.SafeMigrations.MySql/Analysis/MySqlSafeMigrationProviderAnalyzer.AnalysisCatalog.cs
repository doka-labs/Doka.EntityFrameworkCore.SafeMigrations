namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Captures full-stream catalog contracts before bounded window classification begins.</summary>
    /// <param name="context">The context owning the open connection and model.</param>
    /// <param name="connection">The actual open connection retained by the outer analysis lifetime.</param>
    /// <param name="operations">The immutable complete safe-operation stream.</param>
    /// <param name="captureRenameSources">Whether this is the root stream rather than a rebound source capture.</param>
    /// <param name="inheritedBackfillConstraints">Original hazards rebound to exact source-capture operations.</param>
    /// <param name="cancellationToken">The token canceling actual metadata reads.</param>
    /// <returns>The qualified invocation catalog, retaining original reference identities.</returns>
    private async Task<AnalysisCatalog> CaptureAnalysisCatalogAsync(
        DbContext context,
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        bool captureRenameSources,
        MySqlBackfillConstraintCatalog? inheritedBackfillConstraints,
        CancellationToken cancellationToken
    )
    {
        var commandTimeout = context.Database.GetCommandTimeout();
        _currentDatabase = null;
        if (operations.Any(static operation =>
            MySqlSafeMigrationCatalogSqlBuilder.HasDatabaseQualifier(operation.Intent)))
        {
            _currentDatabase = await ReadCurrentDatabaseAsync(connection, commandTimeout, cancellationToken);
        }

        var maximumPayloadBytes = await GetMaximumPayloadBytesAsync(connection, commandTimeout, cancellationToken);
        var backfillConstraints = inheritedBackfillConstraints
            ?? MySqlBackfillConstraintCatalog.Create(operations, _currentDatabase, TableIdentifierComparer);

        var physicalKeyOperations = operations
            .Where(operation => TargetsCurrentDatabase(operation.Intent, _currentDatabase))

            // WHY: Retain fluent materialization and its existing snapshot
            // ownership rather than replacing this boundary with a spread.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var columnSessionEnvironment = await ReadProjectedColumnSessionEnvironmentAsync(
            connection,
            physicalKeyOperations,
            maximumPayloadBytes,
            commandTimeout,
            cancellationToken);

        var indexEnvironments = await ReadIndexPhysicalEnvironmentsAsync(
            connection,
            physicalKeyOperations,
            maximumPayloadBytes,
            commandTimeout,
            TableIdentifierComparer,
            cancellationToken);

        var alterPhysicalSnapshots = await ReadProjectedAlterPhysicalSnapshotsAsync(
            connection,
            physicalKeyOperations,
            indexEnvironments,
            maximumPayloadBytes,
            commandTimeout,
            cancellationToken);

        var expectedUniqueIndexes = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes(
            operations,
            _currentDatabase,
            TableIdentifierComparer);

        var expectedTableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
            operations,
            schema => MySqlTableIdentity.NormalizeDatabase(schema, _currentDatabase),
            TableIdentifierComparer,
            '`',
            false,
            StringComparer.OrdinalIgnoreCase);

        var dependencyOperations = captureRenameSources && _analysisOperationStream is { } fullStream
            ? fullStream
            : operations;

        var generatedCandidates = MySqlGeneratedColumnModelCandidates.CaptureTargets(
            dependencyOperations,
            captureRenameSources
                ? _analysisTargetModels
                : null,
            context.Model,
            context.GetService<IModelRuntimeInitializer>());

        _generatedColumnDependencyContracts = MySqlGeneratedColumnDependencyContractCatalog.Create(
            dependencyOperations,
            _currentDatabase,
            TableIdentifierComparer,
            generatedCandidates);

        if (captureRenameSources)
        {
            // WHY: Normalize only after this invocation has read its native
            // owner profile. Rebound rename probes share the analyzer but
            // must not replace the root stream's index lifetime facts.
            PrepareGeneratedIndexIdentityCapture(operations);
        }

        // WHY: MariaDB spills every INFORMATION_SCHEMA subquery of a classification statement to
        // an on-disk temporary table, so the catalog of the connected database is copied once
        // per operation window and classified against instead. MySQL keeps the equivalent work
        // in memory and rejects a second reference to a temporary table in one statement, so it
        // stays on the live views. See D-014.
        var isMariaDb = MySqlServerVersion.AutoDetect(
                connection,
                MySqlServerVersionCompatibilityMode.AllowUnsupported)
            .IsMariaDb;

        if (captureRenameSources)
        {
            _generatedColumnDependencyIsMariaDb = isMariaDb;
        }

        var primaryKeyReplacements = MySqlSafeMigrationPlanCapture.CreatePrimaryKeyReplacementTargets(
            operations,
            _currentDatabase,
            TableIdentifierComparer);

        return new AnalysisCatalog(
            connection,
            maximumPayloadBytes,
            commandTimeout,
            backfillConstraints,
            columnSessionEnvironment,
            indexEnvironments,
            alterPhysicalSnapshots,
            expectedUniqueIndexes,
            expectedTableConstraints,
            primaryKeyReplacements,
            isMariaDb);
    }

    /// <summary>
    /// Retains one invocation's qualified immutable catalog references without another context object.
    /// </summary>
    /// <param name="Connection">The actual open connection retained by the outer ownership lifetime.</param>
    /// <param name="MaximumPayloadBytes">The native packet-qualified shared payload limit.</param>
    /// <param name="CommandTimeout">The caller-owned native command timeout.</param>
    /// <param name="BackfillConstraints">Exact operation-bound backfill hazards.</param>
    /// <param name="ColumnSessionEnvironment">Qualified column-session and required-add facts.</param>
    /// <param name="IndexEnvironments">Original physical-key budgets by owner.</param>
    /// <param name="AlterPhysicalSnapshots">Original physical row and key shapes by owner.</param>
    /// <param name="ExpectedUniqueIndexes">Complete-stream unique-index declarations.</param>
    /// <param name="ExpectedTableConstraints">Complete-stream table constraint declarations.</param>
    /// <param name="PrimaryKeyReplacements">Exact operations participating in primary-key replacement.</param>
    /// <param name="IsMariaDb">Whether the qualified native server uses MariaDB catalog semantics.</param>
    private readonly record struct AnalysisCatalog(
        DbConnection Connection,
        int MaximumPayloadBytes,
        int? CommandTimeout,
        MySqlBackfillConstraintCatalog BackfillConstraints,
        ProjectedColumnSessionEnvironment? ColumnSessionEnvironment,
        IReadOnlyDictionary<string, SafeMigrationIndexPhysicalEnvironment> IndexEnvironments,
        IReadOnlyDictionary<string, MySqlProjectedAlterPhysicalSnapshot> AlterPhysicalSnapshots,
        MySqlExpectedUniqueIndexCatalog ExpectedUniqueIndexes,
        IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
            ExpectedTableConstraints,
        IReadOnlySet<SafeMigrationOperation> PrimaryKeyReplacements,
        bool IsMariaDb
    );
}
