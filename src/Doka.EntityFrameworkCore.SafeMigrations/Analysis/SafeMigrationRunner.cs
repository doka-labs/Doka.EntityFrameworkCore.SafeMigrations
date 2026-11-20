namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Default provider-neutral preflight and postflight runner.</summary>
[PublicAPI]
public sealed partial class SafeMigrationRunner : ISafeMigrationRunner
{
    private readonly ISafeMigrationProviderAnalyzer _providerAnalyzer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the runner with one provider analyzer.</summary>
    /// <param name="providerAnalyzer">The provider-specific read-only catalog analyzer.</param>
    /// <param name="timeProvider">The time source, or null to use the system time provider.</param>
    public SafeMigrationRunner(
        ISafeMigrationProviderAnalyzer providerAnalyzer,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(providerAnalyzer);

        _providerAnalyzer = providerAnalyzer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<SafeMigrationRunReport> AnalyzePendingMigrationsAsync(
        DbContext context,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        _providerAnalyzer.ValidateContext(context);

        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var historyRepository = context.GetService<IHistoryRepository>();
        var applied = (await historyRepository.GetAppliedMigrationsAsync(cancellationToken))
            .Select(static row => row.MigrationId)
            .ToHashSet(StringComparer.Ordinal);

        var migrations = migrationsAssembly
            .Migrations

            // WHY: Discovery, target boundaries and EF migration IDs must share ordinal identity, not UI culture.
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)

            // WHY: Retain the ordered array snapshot used for both target selection and stream reconstruction.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var targetMigrationId = options.TargetMigrationId
            ?? migrations.LastOrDefault()
                .Key;

        if (targetMigrationId is not null
            && migrations.All(entry => !StringComparer.Ordinal.Equals(entry.Key, targetMigrationId)))
        {
            throw new ArgumentException(
                "The target migration is not present in the configured migrations assembly.",
                nameof(options));
        }

        if (targetMigrationId is not null
            && applied.Any(migrationId => StringComparer.Ordinal.Compare(migrationId, targetMigrationId) > 0))
        {
            throw new InvalidOperationException("SafeMigrations preflight supports only forward migration targets.");
        }

        List<MigrationOperation> operations = [];
        List<IModel?> targetModels = [];
        List<string> migrationIds = [];

        // Reconstruct the pending Up-operation stream in the migration-ID order
        // used for target selection throughout this method.
        foreach (var migrationEntry in migrations)
        {
            if (targetMigrationId is not null
                && StringComparer.Ordinal.Compare(migrationEntry.Key, targetMigrationId) > 0)
            {
                break;
            }

            if (applied.Contains(migrationEntry.Key))
            {
                continue;
            }

            var migration = migrationsAssembly.CreateMigration(
                migrationEntry.Value,
                context.Database.ProviderName ?? string.Empty);

            operations.AddRange(migration.UpOperations);
            targetModels.AddRange(Enumerable.Repeat(migration.TargetModel, migration.UpOperations.Count));
            migrationIds.AddRange(Enumerable.Repeat(migrationEntry.Key, migration.UpOperations.Count));
        }

        return await RunAsync(
            context,
            operations,
            SafeMigrationReportMode.Preflight,
            new SafeMigrationRunOptions(options.InstanceId, targetMigrationId, options.ExpectedModelFingerprint),
            targetModels,
            migrationIds,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SafeMigrationRunReport> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken
    ) => RunAsync(
        context,
        operations,
        SafeMigrationReportMode.Preflight,
        options,
        null,
        null,
        cancellationToken);

    /// <inheritdoc />
    public Task<SafeMigrationRunReport> VerifyAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken
    ) => RunAsync(
        context,
        operations,
        SafeMigrationReportMode.Postflight,
        options,
        null,
        null,
        cancellationToken);

    private async Task<SafeMigrationRunReport> RunAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationReportMode mode,
        SafeMigrationRunOptions options,
        List<IModel?>? targetModels,
        List<string>? migrationIds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(options);

        if (targetModels is not null
            && targetModels.Count != operations.Count)
        {
            throw new ArgumentException(
                "The target-model sequence must align with the migration-operation sequence.",
                nameof(targetModels));
        }

        if (migrationIds is not null
            && migrationIds.Count != operations.Count)
        {
            throw new ArgumentException(
                "The migration-ID sequence must align with the migration-operation sequence.",
                nameof(migrationIds));
        }

        cancellationToken.ThrowIfCancellationRequested();

        _providerAnalyzer.ValidateContext(context);

        // WHY: A published migration keeps the meaning it had when it was
        // authored. SafeMigrations classifies its own operations and leaves every
        // other operation to the provider; only scaffolding shapes new source.
        // WHY: Only pending-migration discovery supplies actual authored IDs.
        // Standalone Analyze/Verify retain the strict one-batch contract.
        SafeMigrationModelManagedDataContractValidator.Validate(operations, migrationIds);

        // Validate the canonical Core model before trusting any catalog result
        // produced for an instance-specific derived DbContext.
        var providerContract = context.Database.ProviderName
            ?? throw new InvalidOperationException("The DbContext does not expose an EF Core provider name.");

        var fingerprint = ValidateCanonicalMigrationModelAndCreateFingerprint(context, providerContract);

        SafeMigrationModelFingerprint.ValidateExpected(
            fingerprint,
            options.ExpectedModelFingerprint,
            context.GetType());

        var contractFingerprint = SafeMigrationContractFingerprint.Create(operations);
        var generatedAtUtc = _timeProvider.GetUtcNow();
        var startedAt = Stopwatch.GetTimestamp();

        using var activity = SafeMigrationTelemetry.ActivitySource.StartActivity(
            SafeMigrationDiagnostics.RunActivityName,
            ActivityKind.Internal,
            parentContext: default);

        activity?.SetTag("safe_migrations.mode", SafeMigrationTelemetry.ModeCode(mode));
        activity?.SetTag("safe_migrations.operation_count", operations.Count);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        Exception? originalFailure = null;

        try
        {
            var environment = await _providerAnalyzer.GetEnvironmentAsync(context, cancellationToken);
            if (!StringComparer.Ordinal.Equals(environment.ProviderId, _providerAnalyzer.ProviderId))
            {
                throw new InvalidOperationException(
                    "The SafeMigrations analyzer returned an inconsistent provider identifier.");
            }

            activity?.SetTag("db.system.name", environment.EngineFamily);
            activity?.SetTag("safe_migrations.provider", environment.ProviderId);

            var analysisScope = await _providerAnalyzer.AcquireAnalysisScopeAsync(
                context,
                cancellationToken);

            SafeMigrationRunReport report;
            Exception? analysisFailure = null;

            try
            {
                report = await AnalyzeOperationsAsync(
                    context,
                    operations,
                    mode,
                    options,
                    fingerprint,
                    contractFingerprint,
                    generatedAtUtc,
                    environment,
                    targetModels,
                    migrationIds,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                analysisFailure = exception;

                throw;
            }
            finally
            {
                await SafeMigrationAnalysisCleanup.DisposeAsync(
                    analysisScope,
                    analysisFailure,
                    "SafeMigrations.AnalysisScopeDisposeException");
            }

            activity?.SetStatus(ActivityStatusCode.Ok);

            SafeMigrationTelemetry.Record(
                mode,
                report.Status,
                environment.ProviderId,
                environment.EngineFamily,
                operations.Count,
                Stopwatch.GetElapsedTime(startedAt));

            return report;
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            if (exception is not OperationCanceledException)
            {
                var failureCode = SafeMigrationTelemetry.FailureCode(exception);
                activity?.SetStatus(ActivityStatusCode.Error, failureCode);
                activity?.SetTag("safe_migrations.failure_code", failureCode);
                activity?.SetTag(
                    "safe_migrations.runbook",
                    SafeMigrationDiagnostics.RunbookBaseUrl + "failure-codes.md#" + failureCode);

                SafeMigrationTelemetry.RecordFailure(mode, failureCode);
            }

            throw;
        }
        finally
        {
            if (openedHere)
            {
                await SafeMigrationAnalysisCleanup.CloseAsync(connection, originalFailure);
            }
        }
    }

    internal static string ValidateCanonicalMigrationModelAndCreateFingerprint(
        DbContext context,
        string providerContract
    )
    {
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var snapshot = migrationsAssembly.ModelSnapshot;
        var runtimeDesignTimeModel = context.GetService<IDesignTimeModel>()
            .Model;

        if (snapshot is not null)
        {
            var snapshotModel = context
                .GetService<IModelRuntimeInitializer>()
                .Initialize(snapshot.Model, designTime: true);

            var modelDiffer = context.GetService<IMigrationsModelDiffer>();

            if (modelDiffer.HasDifferences(
                snapshotModel.GetRelationalModel(),
                runtimeDesignTimeModel.GetRelationalModel()))
            {
                throw new SafeMigrationModelMismatchException(
                    SafeMigrationModelFingerprint.Create(snapshotModel, providerContract),
                    SafeMigrationModelFingerprint.Create(runtimeDesignTimeModel, providerContract),
                    context.GetType());
            }
        }

        return SafeMigrationModelFingerprint.Create(runtimeDesignTimeModel, providerContract);
    }

    private async Task<SafeMigrationRunReport> AnalyzeOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationReportMode mode,
        SafeMigrationRunOptions options,
        string modelFingerprint,
        string contractFingerprint,
        DateTimeOffset generatedAtUtc,
        SafeMigrationProviderEnvironment environment,
        List<IModel?>? targetModels,
        List<string>? migrationIds,
        CancellationToken cancellationToken
    )
    {
        var assessments = new List<SafeMigrationAssessment>(operations.Count);

        // WHY: Keep the compact provider batch independent from full report and migration ordinals.
        // ReSharper disable once UseCollectionExpression
        var safeOperations = operations
            .OfType<SafeMigrationOperation>()
            .ToArray();

        var classification = await ClassifyOperationStreamAsync(
            context,
            operations,
            safeOperations,
            mode,
            targetModels,
            cancellationToken);

        var reduction = ReduceOrderedAssessments(
            operations,
            mode,
            migrationIds,
            classification,
            assessments,
            cancellationToken);

        return await CreateRunReportAsync(
            context,
            operations,
            mode,
            options,
            modelFingerprint,
            contractFingerprint,
            generatedAtUtc,
            environment,
            reduction,
            cancellationToken);
    }
}
