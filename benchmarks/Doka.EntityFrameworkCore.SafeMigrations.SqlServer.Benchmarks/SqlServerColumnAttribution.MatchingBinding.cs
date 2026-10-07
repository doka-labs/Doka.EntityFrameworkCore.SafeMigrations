namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

internal static partial class SqlServerColumnAttribution
{
    /// <summary>Checks fresh classification and preceding gates in direct and delayed batches.</summary>
    private static void ProbeMatchingBinding(
        SqlConnection connection,
        string output
    )
    {
        const string poisonState = "(SELECT TOP (1) CONVERT(nvarchar(32), value) "
            + "FROM dbo.attribution_state_poison_absent)";
        const string poisonRepair = "CASE WHEN EXISTS "
            + "(SELECT 1 FROM dbo.attribution_repair_poison_absent) THEN 1 ELSE 0 END";

        MatchingProbe[] probes =
        [
            new("matching_skips_state_and_repair", poisonState, "1", "1", "1", null, 0),
            new("nonmatching_binds_state", poisonState, "0", "1", "1", null, 208),
            new("null_matching_binds_state", poisonState, "CONVERT(int, NULL)", "1", "1", null, 208),
            new("prerequisite_precedes_matching", poisonState, "1", "0", "1", null, 51004),
            new("evaluation_precedes_matching", poisonState, "1", "1", "0", null, 51002),
            new("physical_precedes_matching", poisonState, "1", "1", "1", "0", 51002),
            new("different_binds_repair", "N'different'", "0", "1", "1", null, 208),
            new("unsupported_skips_repair", "N'unsupported'", "0", "1", "1", null, 51002),
            new("data_blocked_skips_repair", "N'data_blocked'", "0", "1", "1", null, 51003),
            new("classified_matching_skips_repair", "N'matching'", "0", "1", "1", null, 0),
        ];

        // WHY: The diagnostic also compares the abandoned shortcut candidate.
        // Reflection adapts only that historical surface, never product proofs.
        var matchingProperty = typeof(SqlServerSafeMigrationRuntimePlan).GetProperty("RuntimeMatchingPrecondition");
        var results = new List<object>();
        var contractsSatisfied = true;
        foreach (var delayed in new[] { false, true })
        {
            foreach (var probe in probes)
            {
                var operation = new SafeMigrationOperation(new EnsureColumnIntent("attribution_items",
                    new ExpectedColumnDefinition("value_0", typeof(string), true, "nvarchar(128)"), "dbo"),
                    SafeMigrationPolicy.RepairIfSafe);

                var plan = new SqlServerSafeMigrationRuntimePlan(probe.State, "1",
                    SafeMigrationRepairCapability.Safe, poisonRepair)
                {
                    RequiresDelayedBinding = delayed,
                    PrerequisiteExpression = probe.Prerequisite,
                    StateEvaluationGuardExpression = probe.Evaluation,
                    PhysicalTableSupportExpression = probe.Physical,
                };

                matchingProperty?.SetValue(plan, probe.Matching);
                var sql = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []);
                // WHY: The outer dynamic guard doubles its body's SQL quotes.
                // Detect the rendered batch, not the unescaped inner template.
                var conditionalRepair = sql.Contains("IF @doka_state = N''different''", StringComparison.Ordinal);
                // WHY: A baseline without conditional repair still binds the
                // poison repair expression. Record that legacy behavior while
                // enforcing prerequisite and permission refusal in every arm.
                var expectedError = !conditionalRepair
                    && probe.State is "N'unsupported'" or "N'data_blocked'" or "N'matching'"
                        ? 208
                        : matchingProperty is null && probe.Name == "matching_skips_state_and_repair"
                            ? 208
                            : probe.ErrorNumber;

                var name = probe.Name + (delayed ? "_delayed" : "_direct");
                File.WriteAllText(Path.Combine(output, "matching-binding-" + name + ".sql"), sql);
                var errorNumber = 0;
                try
                {
                    Execute(connection, sql);
                }
                catch (SqlException exception) when (exception.Number is 208 or 51001 or 51002 or 51003 or 51004)
                {
                    errorNumber = exception.Number;
                }

                results.Add(new
                {
                    Name = name,
                    RuntimeMatchingSurfaceAvailable = matchingProperty is not null,
                    ConditionalRepairEvaluationAvailable = conditionalRepair,
                    ExpectedErrorNumber = expectedError,
                    ActualErrorNumber = errorNumber,
                    ContractSatisfied = errorNumber == expectedError,
                });
                contractsSatisfied &= errorNumber == expectedError;
            }
        }

        WriteJson(Path.Combine(output, "matching-binding-probes.json"), results);
        if (!contractsSatisfied)
        {
            throw new InvalidOperationException("A runtime classifier violated a live binding contract.");
        }
    }

    /// <summary>Defines one scalar matching/gate combination and the required live binding outcome.</summary>
    private sealed record MatchingProbe(
        string Name,
        string State,
        string Matching,
        string Prerequisite,
        string Evaluation,
        string? Physical,
        int ErrorNumber
    );
}
