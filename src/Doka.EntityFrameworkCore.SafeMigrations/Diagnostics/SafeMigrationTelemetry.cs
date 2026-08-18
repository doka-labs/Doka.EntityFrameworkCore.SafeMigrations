namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationTelemetry
{
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
            { "db.system", engineFamily },
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
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
