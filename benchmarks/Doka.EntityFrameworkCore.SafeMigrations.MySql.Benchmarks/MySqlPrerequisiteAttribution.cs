namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Attributes local prerequisite extraction independently of provider catalog-plan generation.</summary>
public class MySqlPrerequisiteAttribution
{
    private SafeMigrationIntent _intent = null!;

    /// <summary>Gets or sets the immutable expression and source-role workload.</summary>
    [Params(
        "no_expression",
        "one_identifier",
        "duplicate_identifiers",
        "deep_identifier",
        "many_index_columns",
        "old_and_target",
        "check")]
    public string Workload { get; set; } = "no_expression";

    /// <summary>Checks ordered source roles and the null-input negative control before timing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var definition = MySqlMaintenanceAttribution
            .Prerequisites()
            .Single(item => item.Id == Workload);

        _intent = definition.Intent;
        var observed = SafeMigrationPrerequisiteColumns.Local(_intent);
        MaintenanceAttribution.Require(
            observed.SequenceEqual(definition.Expected, StringComparer.Ordinal),
            "The prerequisite attribution fixture has incorrect source roles.");

        var rejectedNull = false;
        try
        {
            _ = SafeMigrationPrerequisiteColumns.Local(null!);
        }
        catch (ArgumentNullException)
        {
            rejectedNull = true;
        }

        MaintenanceAttribution.Require(rejectedNull, "Prerequisite extraction accepted a null operation.");
        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            _ = MaintenanceAttribution.Write(
                Path.Combine(output, $"prerequisites-{Workload}.json"),
                "Functional/source evidence; Core prerequisite extraction; not timing evidence",
                [
                    new
                    {
                        Id = "PERF-01",
                        Workload,
                        Columns = observed,
                    },
                ]);
        }
    }

    /// <summary>Extracts prerequisite columns once from the prebuilt immutable intent.</summary>
    /// <returns>The ordered local prerequisite count, consumed by BenchmarkDotNet.</returns>
    [Benchmark]
    public int ExtractColumns() => SafeMigrationPrerequisiteColumns.Local(_intent).Count;
}
