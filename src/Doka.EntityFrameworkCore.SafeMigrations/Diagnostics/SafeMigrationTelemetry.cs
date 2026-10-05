namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationTelemetry
{
    /// <summary>Identifies optional scopes for fixed analysis stages.</summary>
    internal const string AnalysisStageActivityName = "safe_migrations.analysis.stage";

    public static readonly ActivitySource ActivitySource = new(SafeMigrationDiagnostics.ActivitySourceName);

    private static readonly Meter s_meter = new(SafeMigrationDiagnostics.MeterName);

    private static readonly Counter<long> s_runCount = s_meter.CreateCounter<long>(
        SafeMigrationDiagnostics.RunCountMetricName);

    private static readonly Histogram<double> s_runDuration = s_meter.CreateHistogram<double>(
        SafeMigrationDiagnostics.RunDurationMetricName,
        "ms");

    private static readonly Histogram<long> s_operationCount = s_meter.CreateHistogram<long>(
        SafeMigrationDiagnostics.OperationCountMetricName,
        "{operation}");

    private static readonly Counter<long> s_failureCount = s_meter.CreateCounter<long>(
        SafeMigrationDiagnostics.RunFailureCountMetricName);

    /// <summary>Starts an optional, privacy-safe scope for one fixed analysis phase.</summary>
    /// <param name="stage">A fixed provider-neutral phase or catalog-batch identifier.</param>
    /// <param name="operationCount">The number of operations considered by this scope.</param>
    /// <returns>The sampled activity, or null when no listener requests the scope.</returns>
    /// <exception cref="ArgumentException">The stage is not a recognized, fixed identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The operation count is negative.</exception>
    internal static Activity? StartAnalysisStage(
        string stage,
        int operationCount
    )
    {
        // WHY: A fixed vocabulary keeps tracing low-cardinality and prevents
        // callers from accidentally emitting object names, SQL, or data values.
        if (stage is not ("provider-classification" or "ordered-projection" or "unexpected-inventory"
            or "provider-baseline" or "catalog-classification" or "catalog-batch"))
        {
            throw new ArgumentException("The analysis stage is not a recognized fixed identifier.", nameof(stage));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(operationCount);

        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        var tags = new TagList
        {
            { "safe_migrations.analysis.stage", stage },
            { "safe_migrations.operation_count", operationCount },
        };

        // WHY: Start callbacks run before StartActivity returns. Supplying tags
        // during creation makes a stalled phase identifiable before it completes.
        return ActivitySource.StartActivity(AnalysisStageActivityName, ActivityKind.Internal,
            parentContext: default, tags: tags);
    }

    public static void Record(
        SafeMigrationReportMode mode,
        SafeMigrationReportStatus status,
        string providerId,
        string engineFamily,
        int operationCount,
        TimeSpan duration
    )
    {
        var tags = new TagList
        {
            { "db.system.name", engineFamily },
            { "safe_migrations.provider", providerId },
            { "safe_migrations.mode", ModeCode(mode) },
            { "safe_migrations.status", StatusCode(status) },
        };

        s_runCount.Add(1, tags);
        s_runDuration.Record(duration.TotalMilliseconds, tags);
        s_operationCount.Record(operationCount, tags);
    }

    public static void RecordFailure(
        SafeMigrationReportMode mode,
        string failureCode
    )
    {
        var tags = new TagList
        {
            { "safe_migrations.mode", ModeCode(mode) },
            { "safe_migrations.failure_code", failureCode },
        };

        s_failureCount.Add(1, tags);
    }

    public static string FailureCode(
        Exception exception
    )
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            SafeMigrationModelMismatchException => "model_contract_mismatch",
            DbException => "provider_command_failed",
            ArgumentException => "input_contract_invalid",
            InvalidOperationException => "runtime_contract_invalid",
            _ => "unexpected_failure",
        };
    }

    public static string ModeCode(
        SafeMigrationReportMode mode
    ) => mode switch
    {
        SafeMigrationReportMode.Preflight => "preflight",
        SafeMigrationReportMode.Postflight => "postflight",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string StatusCode(
        SafeMigrationReportStatus status
    ) => status switch
    {
        SafeMigrationReportStatus.NoOperations => "no_operations",
        SafeMigrationReportStatus.Ready => "ready",
        SafeMigrationReportStatus.ReadyWithProviderOperations => "ready_with_provider_operations",
        SafeMigrationReportStatus.Blocked => "blocked",
        SafeMigrationReportStatus.RuntimeValidationRequired => "runtime_validation_required",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
