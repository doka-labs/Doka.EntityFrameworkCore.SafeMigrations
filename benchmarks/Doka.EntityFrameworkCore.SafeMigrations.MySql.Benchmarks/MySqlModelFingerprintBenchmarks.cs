namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks;

/// <summary>Measures MySQL model fingerprinting with ten properties per configured entity.</summary>
public class MySqlModelFingerprintBenchmarks : IDisposable
{
    private DbContext _context = null!;
    private IModel _model = null!;

    /// <summary>Gets or sets the number of entities included in the fingerprint model.</summary>
    [Params(10, 100, 1000)]
    public int EntityCount { get; set; }

    /// <summary>Releases the owned fingerprint model context.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds the design-time fingerprint model outside the measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        // Distinct marker types keep EF model caching from reusing a different entity-count model.
        _context = EntityCount switch
        {
            10 => new MySqlFingerprintContext<Fingerprint10>(10),
            100 => new MySqlFingerprintContext<Fingerprint100>(100),
            1000 => new MySqlFingerprintContext<Fingerprint1000>(1000),
            _ => throw new InvalidOperationException("The fingerprint entity count is not a retained benchmark case."),
        };

        _model = _context.GetService<IDesignTimeModel>()
            .Model;
    }

    /// <summary>Creates the relational model fingerprint for the configured entity count.</summary>
    /// <returns>The fingerprint length.</returns>
    [Benchmark]
    public int CreateFingerprint() => SafeMigrationModelFingerprint.Create(_model, "doka_mysql")
        .Length;

    /// <summary>Disposes the fingerprint model context after measurement.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources owned by this benchmark fixture.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _context.Dispose();
        }
    }
}
