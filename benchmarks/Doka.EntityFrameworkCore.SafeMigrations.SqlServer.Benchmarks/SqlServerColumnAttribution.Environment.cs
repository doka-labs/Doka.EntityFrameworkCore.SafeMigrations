namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

public partial class SqlServerColumnAttribution
{
    /// <summary>Rejects missing explicit native configuration before creating a diagnostic database.</summary>
    internal static void ValidateEnvironment()
    {
        _ = ReadRequiredEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_CONNECTION");
        _ = ReadRequiredEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT");
        _ = ReadRowCount();
    }

    /// <summary>Creates a unique evidence directory for one isolated BenchmarkDotNet setup.</summary>
    /// <param name="label">The nonsecret fixture or microcontrol identity.</param>
    /// <returns>The full path of the newly created evidence directory.</returns>
    internal static string CreateEvidenceDirectory(
        string label
    )
    {
        var root = Path.GetFullPath(ReadRequiredEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT"));
        var output = Path.Combine(
            root,
            label
            + "-"
            + Guid
                .NewGuid()
                .ToString("N"));

        Directory.CreateDirectory(output);

        return output;
    }

    private static string ReadRequiredEnvironmentVariable(
        string name
    )
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Native attribution requires the {name} environment variable.");
        }

        return value;
    }

    private static int ReadRowCount()
    {
        var configured = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_ROWS");
        var rows = string.IsNullOrWhiteSpace(configured)
            ? 10_000
            : int.Parse(configured, CultureInfo.InvariantCulture);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 200_000);

        return rows;
    }
}
