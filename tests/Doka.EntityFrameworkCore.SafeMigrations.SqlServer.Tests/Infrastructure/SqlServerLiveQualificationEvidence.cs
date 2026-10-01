namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Persists bounded stage markers even when a live qualification never produces its final TRX.</summary>
internal static class SqlServerLiveQualificationEvidence
{
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
}
