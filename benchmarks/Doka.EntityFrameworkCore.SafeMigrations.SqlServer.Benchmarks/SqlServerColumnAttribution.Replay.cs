namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

internal static partial class SqlServerColumnAttribution
{
    /// <summary>Confirms a genuine safe repair preserves all data before measuring its matching replay.</summary>
    private static void PrepareRepairReplay(
        SqlConnection connection,
        DbContext context,
        ISafeMigrationProviderAnalyzer analyzer,
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<MigrationCommand> commands,
        Scenario scenario,
        string output
    )
    {
        var before = Snapshot(connection, scenario);
        var analyses = analyzer.AnalyzeAsync(context, operations).GetAwaiter().GetResult();
        if (analyses.Count != 1
            || analyses[0].ObservedState != SafeMigrationObservedState.Different
            || analyses[0].RepairCapability != SafeMigrationRepairCapability.Safe)
        {
            throw new InvalidOperationException("The replay fixture did not begin with a supported safe repair.");
        }

        WriteJson(Path.Combine(output, "initial-repair-analysis.json"), analyses);
        ExecuteGenerated(connection, commands, scenario);
        var after = Snapshot(connection, scenario);
        if (before.AsSpan(0, 64).SequenceEqual(after.AsSpan(0, 64)) is false)
        {
            throw new InvalidOperationException("The initial safe repair changed fixture row data.");
        }

        ValidateAnalyses(analyzer.AnalyzeAsync(context, operations).GetAwaiter().GetResult(), scenario);
        var fingerprints = new
        {
            Before = before,
            After = after,
        };

        WriteJson(Path.Combine(output, "initial-repair-fingerprints.json"), fingerprints);
    }
}
