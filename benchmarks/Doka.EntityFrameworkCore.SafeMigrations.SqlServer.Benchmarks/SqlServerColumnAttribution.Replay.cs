namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

public partial class SqlServerColumnAttribution
{
    /// <summary>Confirms a genuine safe repair preserves all data before measuring its matching replay.</summary>
    /// <param name="connection">The owned fixture connection.</param>
    /// <param name="context">The real provider context.</param>
    /// <param name="analyzer">The real asynchronous provider analyzer.</param>
    /// <param name="operations">The prepared repair operations.</param>
    /// <param name="commands">The exact synchronous runtime commands under measurement.</param>
    /// <param name="scenario">The exact repair and replay oracle.</param>
    /// <param name="output">The owned evidence directory.</param>
    /// <param name="cancellationToken">The token controlling analysis and evidence operations.</param>
    /// <returns>The asynchronous repair and replay control verification.</returns>
    private static async Task PrepareRepairReplayAsync(
        SqlConnection connection,
        DbContext context,
        ISafeMigrationProviderAnalyzer analyzer,
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<MigrationCommand> commands,
        Scenario scenario,
        string output,
        CancellationToken cancellationToken
    )
    {
        var before = await SnapshotAsync(connection, scenario, cancellationToken);
        var analyses = await analyzer.AnalyzeAsync(context, operations, cancellationToken);

        if (analyses.Count != 1
            || analyses[0].ObservedState != SafeMigrationObservedState.Different
            || analyses[0].RepairCapability != SafeMigrationRepairCapability.Safe)
        {
            throw new InvalidOperationException("The replay fixture did not begin with a supported safe repair.");
        }

        await WriteJsonAsync(Path.Combine(output, "initial-repair-analysis.json"), analyses, cancellationToken);

        // WHY: The replay control must execute the exact synchronous path measured by ExecuteRuntime.
        ExecuteGenerated(connection, commands, scenario);
        var after = await SnapshotAsync(connection, scenario, cancellationToken);
        if (!before
            .AsSpan(0, 64)
            .SequenceEqual(after.AsSpan(0, 64)))
        {
            throw new InvalidOperationException("The initial safe repair changed fixture row data.");
        }

        ValidateAnalyses(
            await analyzer.AnalyzeAsync(context, operations, cancellationToken),
            scenario);

        var fingerprints = new
        {
            Before = before,
            After = after,
        };

        await WriteJsonAsync(Path.Combine(output, "initial-repair-fingerprints.json"), fingerprints, cancellationToken);
    }
}
