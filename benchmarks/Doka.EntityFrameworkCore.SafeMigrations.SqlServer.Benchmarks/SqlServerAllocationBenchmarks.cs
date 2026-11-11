namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Reports catalog command assembly allocations without enforcing heap budgets in correctness tests.</summary>
[MemoryDiagnoser]
public class SqlServerCatalogCommandAssemblyBenchmarks
{
    private string _expectedText = string.Empty;
    private string _lastCommandText = string.Empty;
    private string[] _selections = [];

    /// <summary>Prepares and validates the 32 large catalog selections outside the measured workload.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _selections = Enumerable
            .Repeat(new string('x', 2_048), 32)
            .ToArray();

        _expectedText = string.Join("\n", _selections);
        _lastCommandText = AssembleCatalogCommand();
        ValidateResult();
    }

    /// <summary>Assembles the prepared catalog selections with their exact statement separator.</summary>
    /// <returns>The complete catalog command text.</returns>
    [Benchmark]
    public string AssembleCatalogCommand() => _lastCommandText =
        SqlServerSafeMigrationProviderAnalyzer.BuildCatalogCommandText(
            _selections,
            string.Empty,
            "\n",
            string.Empty);

    /// <summary>Validates the exact assembled text and fixed workload length outside measurement.</summary>
    [GlobalCleanup]
    public void ValidateResult()
    {
        if (_lastCommandText.Length != 32 * 2_048 + 31
            || !string.Equals(_expectedText, _lastCommandText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The catalog command assembly changed the prepared statement text.");
        }
    }
}
