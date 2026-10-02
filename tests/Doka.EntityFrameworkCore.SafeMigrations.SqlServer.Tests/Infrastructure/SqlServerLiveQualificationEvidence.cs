namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Persists bounded stage markers even when a live qualification never produces its final TRX.</summary>
internal static class SqlServerLiveQualificationEvidence
{
    /// <summary>Captures privacy-safe analysis stages for one isolated workload trace.</summary>
    /// <param name="workload">The fixed, non-sensitive workload identifier.</param>
    /// <returns>A listener scope whose write failures can be checked after successful qualification.</returns>
    internal static AnalysisStageCapture CaptureAnalysisStages(string workload) => new(GetPath(workload));

    /// <summary>Appends a timestamped stage before entering the next potentially long-running operation.</summary>
    /// <param name="workload">The fixed, non-sensitive workload identifier.</param>
    /// <param name="stage">The fixed, non-sensitive stage identifier.</param>
    internal static void WriteStage(string workload, string stage)
    {
        ValidateIdentifier(stage);
        var path = GetPath(workload);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // WHY: A final test result is unavailable after a job timeout. Append before each awaited phase so
        // the existing always-upload artifact records the active phase without SQL or connection strings.
        File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}\t{stage}\n", Encoding.UTF8);
    }

    /// <summary>Resolves a workload's ignored evidence file under the repository artifact root.</summary>
    /// <param name="workload">The fixed workload identifier, never a path.</param>
    /// <returns>The absolute evidence path.</returns>
    internal static string GetPath(string workload)
    {
        ValidateIdentifier(workload);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return Path.Combine(directory.FullName, "artifacts", "performance", "live",
                    $"sqlserver-{workload}-progress.log");
            }
        }

        throw new InvalidOperationException("The qualification artifact root could not be located.");
    }

    /// <summary>Rejects paths and arbitrary payloads instead of allowing them into qualification evidence.</summary>
    private static void ValidateIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(static character => !char.IsAsciiLetterOrDigit(character)
                && character != '-'))
        {
            throw new ArgumentException("Qualification identifiers must contain only ASCII letters, digits or '-'.",
                nameof(value));
        }
    }

    /// <summary>Writes bounded activity metadata without intercepting an analysis failure.</summary>
    internal sealed class AnalysisStageCapture : IDisposable
    {
        private readonly string _path;
        private readonly Activity _trace;
        private readonly ActivityListener _listener;
        private readonly object _writeLock = new();
        private Exception? _writeFailure;

        /// <summary>Creates a private trace so concurrent workloads never share stage evidence.</summary>
        /// <param name="path">The already validated workload evidence path.</param>
        internal AnalysisStageCapture(string path)
        {
            _path = path;
            _trace = new Activity("sqlserver.live.qualification")
                .SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None)
                .Start();

            var traceId = _trace.TraceId;
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == SafeMigrationDiagnostics.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Parent.TraceId == traceId ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
                ActivityStarted = activity => WriteActivity(activity, completed: false),
                ActivityStopped = activity => WriteActivity(activity, completed: true),
            };

            ActivitySource.AddActivityListener(_listener);
        }

        /// <summary>Fails successful qualification if its stage evidence could not be persisted.</summary>
        /// <remarks>
        /// Call only after the workload assertions so evidence failures never replace the original failure.
        /// </remarks>
        internal void ThrowIfWriteFailed()
        {
            lock (_writeLock)
            {
                if (_writeFailure is not null)
                {
                    throw new InvalidOperationException("The analysis stage evidence could not be persisted.",
                        _writeFailure);
                }
            }
        }

        /// <summary>Detaches callbacks and restores the activity that preceded this workload.</summary>
        public void Dispose()
        {
            _listener.Dispose();
            _trace.Dispose();
        }

        /// <summary>Captures only fixed stages and numeric counters from the owned trace.</summary>
        private void WriteActivity(Activity activity, bool completed)
        {
            if (activity.TraceId != _trace.TraceId
                || activity.OperationName != SafeMigrationTelemetry.AnalysisStageActivityName
                || activity.GetTagItem("safe_migrations.analysis.stage") is not string stage)
            {
                return;
            }

            var operationCount = activity.GetTagItem("safe_migrations.operation_count") is int count ? count : 0;
            var statementCount = activity.GetTagItem("safe_migrations.catalog.statement_count") is int statements
                ? statements
                : 0;

            var batchCount = stage == "catalog-batch" && completed ? 1 : 0;
            var marker = completed ? "analysis-stage-completed" : "analysis-stage-start";
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O}\t{marker}\tstage={stage}\toperations={operationCount}"
                + $"\tduration-ms={activity.Duration.TotalMilliseconds:F3}\tbatches={batchCount}"
                + $"\tstatements={statementCount}\n");

            lock (_writeLock)
            {
                if (_writeFailure is not null)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    File.AppendAllText(_path, line, Encoding.UTF8);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // WHY: Activity callbacks execute inside the workload. Preserve its exception first,
                    // then reject missing evidence separately only if the workload itself succeeds.
                    _writeFailure = exception;
                }
            }
        }
    }
}
