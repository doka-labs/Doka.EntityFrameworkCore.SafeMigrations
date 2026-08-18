namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Default provider-neutral preflight and postflight runner.</summary>
public sealed class SafeMigrationRunner : ISafeMigrationRunner
{
    private readonly ISafeMigrationProviderAnalyzer _providerAnalyzer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the runner with one provider analyzer.</summary>
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

        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var historyRepository = context.GetService<IHistoryRepository>();
        var applied = (await historyRepository.GetAppliedMigrationsAsync(cancellationToken))
            .Select(static row => row.MigrationId)
            .ToHashSet(StringComparer.Ordinal);

        var migrations = migrationsAssembly
            .Migrations.OrderBy(static entry => entry.Key)
            .ToArray();

        var targetMigrationId = options.TargetMigrationId
            ?? migrations.LastOrDefault()
                .Key;

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
        ValidateCanonicalMigrationModel(context);
        var fingerprint = SafeMigrationModelFingerprint.Create(context.Model);

        SafeMigrationModelFingerprint.ValidateExpected(fingerprint, options.ExpectedModelFingerprint);

        var contractFingerprint = SafeMigrationContractFingerprint.Create(operations);
        var generatedAtUtc = _timeProvider.GetUtcNow();
        var startedAt = Stopwatch.GetTimestamp();

        using var activity = SafeMigrationTelemetry.ActivitySource.StartActivity(
            SafeMigrationDiagnostics.RunActivityName,
            ActivityKind.Internal);

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

            activity?.SetTag("db.system", environment.EngineFamily);
            activity?.SetTag("safe_migrations.provider", environment.ProviderId);

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

    private static void ValidateCanonicalMigrationModel(
        DbContext context
    )
    {
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var snapshot = migrationsAssembly.ModelSnapshot
            ?? FindCompatibleBaseContextSnapshot(migrationsAssembly.Assembly, context.GetType());

        if (snapshot is null)
        {
            return;
        }

        var snapshotModel = context
            .GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot.Model, designTime: true);

        var runtimeDesignTimeModel = context.GetService<IDesignTimeModel>()
            .Model;
        var modelDiffer = context.GetService<IMigrationsModelDiffer>();

        if (modelDiffer.HasDifferences(snapshotModel.GetRelationalModel(), runtimeDesignTimeModel.GetRelationalModel()))
        {
            throw new SafeMigrationModelMismatchException(
                SafeMigrationModelFingerprint.Create(snapshotModel),
                SafeMigrationModelFingerprint.Create(runtimeDesignTimeModel));
        }
    }

    private static ModelSnapshot? FindCompatibleBaseContextSnapshot(
        System.Reflection.Assembly assembly,
        Type runtimeContextType
    )
    {
        var candidates = assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ModelSnapshot).IsAssignableFrom(type))
            .Select(type => new
            {
                Type = type,
                Attribute = type
                    .GetCustomAttributes(typeof(DbContextAttribute), inherit: false)
                    .Cast<DbContextAttribute>()
                    .SingleOrDefault(),
            })
            .Where(candidate => candidate.Attribute is not null
                && candidate.Attribute.ContextType.IsAssignableFrom(runtimeContextType))
            .OrderBy(candidate => InheritanceDistance(runtimeContextType, candidate.Attribute!.ContextType))
            .ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        var bestDistance = InheritanceDistance(runtimeContextType, candidates[0].Attribute!.ContextType);
        if (candidates
            .Skip(1)
            .Any(candidate =>
                InheritanceDistance(runtimeContextType, candidate.Attribute!.ContextType) == bestDistance))
        {
            throw new InvalidOperationException(
                "Multiple migration model snapshots match the runtime DbContext hierarchy.");
        }

        return Activator.CreateInstance(candidates[0].Type, nonPublic: true) as ModelSnapshot
            ?? throw new InvalidOperationException("The canonical migration model snapshot could not be constructed.");
    }

    private static int InheritanceDistance(
        Type runtimeContextType,
        Type candidateContextType
    )
    {
        var distance = 0;
        for (var current = runtimeContextType; current is not null; current = current.BaseType)
        {
            if (current == candidateContextType)
            {
                return distance;
            }

            distance++;
        }

        return int.MaxValue;
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
                        operation.GetType()
                            .FullName
                        ?? operation.GetType()
                            .Name,
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
                        ? mode == SafeMigrationReportMode.Postflight ? "postcondition_failed" : decision.Code
                        : analysis.Code));
        }

        var status = operations.Count == 0 ? SafeMigrationReportStatus.NoOperations :
            blocked ? SafeMigrationReportStatus.Blocked :
            hasProviderOperations ? SafeMigrationReportStatus.ReadyWithProviderOperations :
            SafeMigrationReportStatus.Ready;

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
