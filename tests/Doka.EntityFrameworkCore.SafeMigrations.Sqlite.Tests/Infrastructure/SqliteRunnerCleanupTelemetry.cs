namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Observes only the runner's success and failure counts during one process-wide test.</summary>
internal sealed class SqliteRunnerCleanupTelemetry : IDisposable
{
    private readonly MeterListener _listener = new();
    private int _failures;
    private int _runs;

    /// <summary>Starts observing the two established low-cardinality runner instruments.</summary>
    internal SqliteRunnerCleanupTelemetry()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SafeMigrationDiagnostics.MeterName
                && instrument.Name is SafeMigrationDiagnostics.RunCountMetricName
                    or SafeMigrationDiagnostics.RunFailureCountMetricName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
        {
            if (instrument.Name == SafeMigrationDiagnostics.RunCountMetricName)
            {
                Interlocked.Increment(ref _runs);
            }
            else
            {
                Interlocked.Increment(ref _failures);
            }
        });

        _listener.Start();
    }

    /// <summary>Gets observed completed-run measurements.</summary>
    internal int Runs => Volatile.Read(ref _runs);

    /// <summary>Gets observed noncancellation failure measurements.</summary>
    internal int Failures => Volatile.Read(ref _failures);

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();
}
