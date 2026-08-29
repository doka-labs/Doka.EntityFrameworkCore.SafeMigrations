namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Default provider-neutral preflight and postflight runner.</summary>
public sealed class SafeMigrationRunner : ISafeMigrationRunner
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
        CancellationToken cancellationToken = default
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
            .OrderBy(static entry => entry.Key)
            .ToArray();

        var targetMigrationId = options.TargetMigrationId
            ?? migrations.LastOrDefault().Key;

        if (targetMigrationId is not null
            && !migrations.Any(entry => StringComparer.Ordinal.Equals(entry.Key, targetMigrationId)))
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

        var operations = new List<MigrationOperation>();

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
        }

        return await AnalyzeAsync(
            context,
            operations,
            new SafeMigrationRunOptions(options.InstanceId, targetMigrationId, options.ExpectedModelFingerprint),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SafeMigrationRunReport> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken = default
    ) => RunAsync(context, operations, SafeMigrationReportMode.Preflight, options, cancellationToken);

    /// <inheritdoc />
    public Task<SafeMigrationRunReport> VerifyAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken = default
    ) => RunAsync(context, operations, SafeMigrationReportMode.Postflight, options, cancellationToken);

    private async Task<SafeMigrationRunReport> RunAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationReportMode mode,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(options);

        cancellationToken.ThrowIfCancellationRequested();

        _providerAnalyzer.ValidateContext(context);

        // Validate the canonical Core model before trusting any catalog result
        // produced for an instance-specific derived DbContext.
        var providerContract = context.Database.ProviderName
            ?? throw new InvalidOperationException("The DbContext does not expose an EF Core provider name.");

        var fingerprint = ValidateCanonicalMigrationModelAndCreateFingerprint(context, providerContract);

        SafeMigrationModelFingerprint.ValidateExpected(fingerprint, options.ExpectedModelFingerprint);

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
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

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

            await using var analysisScope = await _providerAnalyzer.AcquireAnalysisScopeAsync(
                context,
                cancellationToken);

            var report = await AnalyzeOperationsAsync(
                context,
                operations,
                mode,
                options,
                fingerprint,
                contractFingerprint,
                generatedAtUtc,
                environment,
                cancellationToken);

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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failureCode = SafeMigrationTelemetry.FailureCode(exception);
            activity?.SetStatus(ActivityStatusCode.Error, failureCode);
            activity?.SetTag("safe_migrations.failure_code", failureCode);
            activity?.SetTag(
                "safe_migrations.runbook",
                SafeMigrationDiagnostics.RunbookBaseUrl + "failure-codes.md#" + failureCode);

            SafeMigrationTelemetry.RecordFailure(mode, failureCode);

            throw;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
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
                    SafeMigrationModelFingerprint.Create(runtimeDesignTimeModel, providerContract));
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
        CancellationToken cancellationToken
    )
    {
        var assessments = new List<SafeMigrationAssessment>(operations.Count);
        var blocked = false;
        var hasProviderOperations = false;
        var projection = mode == SafeMigrationReportMode.Preflight ? new SafeMigrationPreflightProjection() : null;
        var safeOperations = operations
            .OfType<SafeMigrationOperation>()
            .ToArray();

        // Providers classify the safe subset in one batch. The projection then
        // advances sequentially without rereading the database, which preflight cannot mutate.
        var liveAnalyses = await _providerAnalyzer.AnalyzeAsync(context, safeOperations, cancellationToken);
        if (liveAnalyses.Count != safeOperations.Length)
        {
            throw new InvalidOperationException("The SafeMigrations analyzer returned an inconsistent result count.");
        }

        var safeOperationOrdinal = 0;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operation = operations[ordinal];
            if (operation is not SafeMigrationOperation safeOperation)
            {
                hasProviderOperations = true;
                assessments.Add(
                    new SafeMigrationAssessment(
                        ordinal,
                        operation.GetType().FullName
                        ?? operation.GetType().Name,
                        isSafeOperation: false,
                        operationKind: null,
                        objectName: null,
                        observedState: null,
                        action: null,
                        postconditionSatisfied: null,
                        "provider_owned_not_analyzed"));

                continue;
            }

            var liveAnalysis = liveAnalyses[safeOperationOrdinal++];
            var analysis = projection?.Project(safeOperation, liveAnalysis) ?? liveAnalysis;
            var decision = SafeMigrationDecisionPlanner.Plan(
                safeOperation.Intent.Kind,
                analysis.ObservedState,
                safeOperation.Policy,
                analysis.RepairCapability);

            var operationBlocked = mode == SafeMigrationReportMode.Preflight
                ? decision.Action is SafeMigrationAction.RejectDifferent
                    or SafeMigrationAction.RejectUnsupported
                    or SafeMigrationAction.RejectDataBlocked
                    or SafeMigrationAction.RejectPrerequisiteMissing
                : !analysis.PostconditionSatisfied;

            blocked |= operationBlocked;
            projection?.Observe(safeOperation, analysis, decision);
            assessments.Add(
                new SafeMigrationAssessment(
                    ordinal,
                    typeof(SafeMigrationOperation).FullName!,
                    isSafeOperation: true,
                    safeOperation.Intent.Kind,
                    safeOperation.Intent.ObjectName,
                    analysis.ObservedState,
                    decision.Action,
                    analysis.PostconditionSatisfied,
                    operationBlocked
                        ? mode == SafeMigrationReportMode.Postflight
                            ? "postcondition_failed"
                            : analysis.ObservedState == SafeMigrationObservedState.Unsupported
                                ? analysis.Code
                                : decision.Code
                        : analysis.Code));
        }

        var status = operations.Count == 0
            ? SafeMigrationReportStatus.NoOperations
            : blocked
                ? SafeMigrationReportStatus.Blocked
                : hasProviderOperations
                    ? SafeMigrationReportStatus.ReadyWithProviderOperations
                    : SafeMigrationReportStatus.Ready;

        var unexpectedObjects = await _providerAnalyzer.FindUnexpectedObjectsAsync(
            context,
            operations,
            cancellationToken);

        return new SafeMigrationRunReport(
            mode,
            status,
            generatedAtUtc,
            options.InstanceId,
            environment,
            options.TargetMigrationId,
            modelFingerprint,
            contractFingerprint,
            assessments,
            unexpectedObjects);
    }
}
