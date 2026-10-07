namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

internal static partial class SqlServerColumnAttribution
{
    /// <summary>Measures guard rendering without catalog-builder, EF baseline or integer-proof construction.</summary>
    private static void MeasureGuardGeneration(
        string output
    )
    {
        var results = new List<object>();
        foreach (var control in CreateGuardGenerationControls())
        {
            var sql = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(control.Operation, control.Plan, [], []);
            File.WriteAllText(Path.Combine(output, "guard-generation-" + control.Name + ".sql"), sql);
            var samples = new List<Sample>(Samples);
            for (var index = 0; index < Samples; index++)
            {
                string? measuredSql = null;
                samples.Add(Measure(() =>
                {
                    measuredSql = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(
                        control.Operation, control.Plan, [], []);

                    return measuredSql.Length;
                }, threadLocal: true));

                if (measuredSql != sql)
                {
                    throw new InvalidOperationException("Synthetic guard generation was not deterministic.");
                }
            }

            results.Add(new
            {
                control.Name,
                Scope = "guard_generation_microcontrol_not_full_generator_attribution",
                SyntheticInput = true,
                ExecutesGeneratedSql = false,
                Policy = control.Operation.Policy.ToString(),
                RepairCapability = control.Plan.RepairCapability.ToString(),
                control.Plan.RequiresDelayedBinding,
                InputStateCharacters = control.Plan.StateExpression.Length,
                InputRepairCharacters = control.Plan.RepairPrecondition.Length,
                InputPostconditionCharacters = control.Plan.Postcondition.Length,
                GeneratedSqlCharacters = sql.Length,
                GeneratedSqlUtf8Bytes = Encoding.UTF8.GetByteCount(sql),
                SysColumnsSubqueryOccurrences = CountOccurrences(sql, "FROM sys.columns"),
                GenerationSamples = samples,
                Interpretation = "Fixed common plan inputs bypass catalog construction and integer admission. "
                    + "Results measure combined guard helpers, not individual causes or full-generator improvements.",
            });
        }

        WriteJson(Path.Combine(output, "guard-generation-controls.json"), results);
    }

    /// <summary>Creates quoted direct and delayed inputs using only the baseline runtime-plan surface.</summary>
    private static IEnumerable<GuardGenerationControl> CreateGuardGenerationControls()
    {
        // WHY: Fixed inputs isolate rendering from newly added catalog proofs and keep baseline DLLs executable.
        // They are deliberately never submitted to the engine and do not claim a valid live column contract.
        var predicate = string.Join(" AND ", Enumerable.Range(0, 64).Select(index =>
            "EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[attribution_items]') "
            + "AND name = N'value_" + index.ToString(CultureInfo.InvariantCulture) + "' AND max_length = 256)"));

        var postcondition = "CASE WHEN " + predicate + " THEN 1 ELSE 0 END";
        var catalogState = "CASE WHEN " + predicate + " THEN N'matching' ELSE N'different' END";
        var rowState = "CASE WHEN " + predicate + " THEN N'matching' "
            + "WHEN EXISTS (SELECT 1 FROM dbo.attribution_items WHERE value_0 IS NULL) "
            + "THEN N'data_blocked' ELSE N'different' END";

        var repair = "CASE WHEN " + predicate
            + " AND NOT EXISTS (SELECT 1 FROM dbo.attribution_items WHERE value_0 IS NULL) THEN 1 ELSE 0 END";

        var target = new ExpectedColumnDefinition("value_0", typeof(string), true, "nvarchar(128)", maxLength: 128);
        var intent = new EnsureColumnIntent("attribution_items", target, "dbo");
        var strict = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var repairing = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var direct = new SqlServerSafeMigrationRuntimePlan(catalogState, postcondition,
            SafeMigrationRepairCapability.None, "0");

        yield return new GuardGenerationControl("direct_catalog_64", strict, direct);

        var delayed = new SqlServerSafeMigrationRuntimePlan(rowState, postcondition,
            SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
        };

        yield return new GuardGenerationControl("delayed_classifier_64", strict, delayed);

        var delayedRepair = new SqlServerSafeMigrationRuntimePlan(rowState, postcondition,
            SafeMigrationRepairCapability.Safe, repair)
        {
            RequiresDelayedBinding = true,
        };

        yield return new GuardGenerationControl("delayed_repair_64", repairing, delayedRepair);
    }

    /// <summary>Retains a preconstructed input so factories and fixtures remain outside allocation samples.</summary>
    private sealed record GuardGenerationControl(
        string Name,
        SafeMigrationOperation Operation,
        SqlServerSafeMigrationRuntimePlan Plan
    );
}
