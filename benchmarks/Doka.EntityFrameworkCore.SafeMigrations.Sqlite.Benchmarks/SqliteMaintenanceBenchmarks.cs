namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Attributes model-owned rebuild capture and complete interleaved structural-window analysis.</summary>
public class SqliteMaintenanceAttribution : IDisposable
{
    private bool _disposed;
    private SqliteMaintenanceAttributionFixture _fixture = null!;

    /// <summary>Gets or sets the number of model-only relational tables.</summary>
    [Params(10, 100, 1000)]
    public int ModelTables { get; set; }

    /// <summary>Gets or sets the number of structural windows separated by opaque SQL operations.</summary>
    [Params(1, 16, 128)]
    public int Windows { get; set; }

    /// <summary>Gets or sets whether consecutive windows alternate distinct EF model references.</summary>
    [Params(false, true)]
    public bool ChangingModel { get; set; }

    /// <summary>Releases model contexts and the owned embedded database.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs the interleaved and contiguous SQLite attribution benchmark families.</summary>
    /// <param name="args">The BenchmarkDotNet command-line arguments.</param>
    /// <returns>The shared benchmark runner exit code.</returns>
    internal static int Run(
        string[] args
    ) => BenchmarkProgram.Run(
        args,
        typeof(SqliteMaintenanceAttribution),
        typeof(SqliteContiguousMaintenanceAttribution));

    /// <summary>Opens embedded SQLite and validates snapshot freshness and operation-reference controls.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _disposed = false;
        _fixture = new SqliteMaintenanceAttributionFixture(ModelTables, Windows, ChangingModel, interleaved: true);
    }

    /// <summary>Captures rebuild contracts for the complete prepared structural-window sequence.</summary>
    /// <returns>The consumed source-candidate checksum.</returns>
    [Benchmark]
    public int CaptureContracts() => _fixture.Capture();

    /// <summary>Analyzes the complete interleaved operation sequence against embedded SQLite.</summary>
    /// <returns>The complete observed operation count.</returns>
    [Benchmark]
    public int AnalyzeWindows() => _fixture.Analyze();

    /// <summary>Disposes model contexts and the owned embedded database.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases owned managed resources once while supporting generated benchmark subclasses.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (!disposing || _disposed)
        {
            return;
        }

        _fixture?.Dispose();
        _disposed = true;
    }
}

/// <summary>Attributes complete contiguous structural-window analysis independently of interleaved capture.</summary>
public class SqliteContiguousMaintenanceAttribution : IDisposable
{
    private bool _disposed;
    private SqliteMaintenanceAttributionFixture _fixture = null!;

    /// <summary>Gets or sets the number of model-only relational tables.</summary>
    [Params(10, 100, 1000)]
    public int ModelTables { get; set; }

    /// <summary>Gets or sets the number of safe operations sharing the contiguous structural window.</summary>
    [Params(1, 16, 128)]
    public int Operations { get; set; }

    /// <summary>Releases model contexts and the owned embedded database.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Prepares the contiguous workload and validates fresh capture and exact classification.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _disposed = false;
        _fixture = new SqliteMaintenanceAttributionFixture(
            ModelTables,
            Operations,
            changingModel: false,
            interleaved: false);
    }

    /// <summary>Analyzes the complete contiguous safe-operation sequence against embedded SQLite.</summary>
    /// <returns>The complete observed operation count.</returns>
    [Benchmark]
    public int AnalyzeWindow() => _fixture.Analyze();

    /// <summary>Disposes model contexts and the owned embedded database.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases owned managed resources once while supporting generated benchmark subclasses.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (!disposing || _disposed)
        {
            return;
        }

        _fixture?.Dispose();
        _disposed = true;
    }
}
