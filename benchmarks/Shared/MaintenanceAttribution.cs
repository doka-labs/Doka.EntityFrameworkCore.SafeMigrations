namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Retains functional attribution controls separately from BenchmarkDotNet measurement reports.</summary>
internal static class MaintenanceAttribution
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes source-labeled structural evidence without timing, allocation, or statistical calculations.
    /// </summary>
    /// <param name="outputPath">The caller-owned functional evidence destination.</param>
    /// <param name="provider">The actual native or client-only profile being exercised.</param>
    /// <param name="results">The verified functional results and structural controls.</param>
    /// <returns>Zero after the complete evidence file has been persisted.</returns>
    internal static int Write(
        string outputPath,
        string provider,
        IReadOnlyList<object> results
    )
    {
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    Runtime = Environment.Version.ToString(),
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Provider = provider,
                    Evidence =
                        "Functional controls only; measurements are in native BenchmarkDotNet full JSON reports.",
                    SourceLabel = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_SOURCE")
                        ?? "unlabelled",
                    Results = results,
                },
                s_jsonOptions));

        return 0;
    }

    /// <summary>
    /// Hashes exact output outside measured methods so equality controls do not pollute measurements.
    /// </summary>
    /// <param name="value">The complete deterministic output.</param>
    /// <returns>The uppercase SHA-256 digest of its UTF-8 representation.</returns>
    internal static string Hash(
        string value
    ) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Rejects a failed functional invariant, independently of measured performance.</summary>
    /// <param name="condition">The exact functional invariant.</param>
    /// <param name="message">The violated attribution contract.</param>
    internal static void Require(
        bool condition,
        string message
    )
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
