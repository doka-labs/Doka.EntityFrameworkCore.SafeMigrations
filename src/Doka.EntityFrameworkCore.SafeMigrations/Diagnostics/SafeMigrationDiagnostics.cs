namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Publishes stable OpenTelemetry-compatible diagnostic names.</summary>
public static class SafeMigrationDiagnostics
{
    /// <summary>Gets the ActivitySource name.</summary>
    public const string ActivitySourceName = "Doka.EntityFrameworkCore.SafeMigrations";

    /// <summary>Gets the Meter name.</summary>
    public const string MeterName = "Doka.EntityFrameworkCore.SafeMigrations";

    /// <summary>Gets the preflight and postflight activity name.</summary>
    public const string RunActivityName = "safe_migrations.run";

    /// <summary>Gets the completed-run counter name.</summary>
    public const string RunCountMetricName = "safe_migrations.run.count";

    /// <summary>Gets the run-duration histogram name.</summary>
    public const string RunDurationMetricName = "safe_migrations.run.duration";

    /// <summary>Gets the operation-count histogram name.</summary>
    public const string OperationCountMetricName = "safe_migrations.operation.count";

    /// <summary>Gets the failed-run counter name.</summary>
    public const string RunFailureCountMetricName = "safe_migrations.run.failure.count";

    /// <summary>Gets the stable runbook base URL.</summary>
    public const string RunbookBaseUrl =
        "https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/blob/main/docs/runbooks/";
}
