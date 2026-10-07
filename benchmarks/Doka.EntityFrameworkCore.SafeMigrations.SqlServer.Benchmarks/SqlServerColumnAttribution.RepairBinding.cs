namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

internal static partial class SqlServerColumnAttribution
{
    /// <summary>Probes actual dynamic binding rather than inferring guard behavior from generated text.</summary>
    private static void ProbeRepairBinding(
        SqlConnection connection,
        string output
    )
    {
        (string Name, string State, SafeMigrationPolicy Policy, SafeMigrationRepairCapability Capability,
            bool ShouldBind)[] probes =
        [
            ("matching", "matching", SafeMigrationPolicy.RepairIfSafe, SafeMigrationRepairCapability.Safe, false),
            ("eligible_different", "different", SafeMigrationPolicy.RepairIfSafe,
                SafeMigrationRepairCapability.Safe, true),
            ("nonrepair_policy", "different", SafeMigrationPolicy.ThrowIfDifferent,
                SafeMigrationRepairCapability.Safe, false),
            ("no_repair_capability", "different", SafeMigrationPolicy.RepairIfSafe,
                SafeMigrationRepairCapability.None, false),
        ];

        // WHY: Use an eligible control to detect the generation strategy once.
        // Ineligible plans intentionally omit both the expression and its IF,
        // so their own SQL cannot distinguish that omission from the baseline.
        var control = new SafeMigrationOperation(new EnsureColumnIntent("attribution_items",
            new ExpectedColumnDefinition("value_0", typeof(string), true, "nvarchar(128)")),
            SafeMigrationPolicy.RepairIfSafe);

        var controlPlan = new SqlServerSafeMigrationRuntimePlan("N'matching'", "1",
            SafeMigrationRepairCapability.Safe, "1");

        // WHY: The isolated batch quotes the entire guard as dynamic SQL.
        var conditionalRepair = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(control, controlPlan, [], [])
            .Contains("IF @doka_state = N''different''", StringComparison.Ordinal);

        var results = new List<object>();
        var contractsSatisfied = true;
        foreach (var probe in probes)
        {
            var operation = new SafeMigrationOperation(new EnsureColumnIntent("attribution_items",
                new ExpectedColumnDefinition("value_0", typeof(string), true, "nvarchar(128)")), probe.Policy);

            // WHY: The nonexistent object must bind only when a Different state is eligible for repair.
            var plan = new SqlServerSafeMigrationRuntimePlan($"N'{probe.State}'", "1", probe.Capability,
                "CASE WHEN EXISTS (SELECT 1 FROM dbo.attribution_poison_absent) THEN 1 ELSE 0 END")
            {
                RequiresDelayedBinding = true,
            };

            var sql = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []);
            File.WriteAllText(Path.Combine(output, "repair-binding-" + probe.Name + ".sql"), sql);
            // WHY: Baseline and final products share this harness. Infer only
            // the rendered control-flow shape, not the existence of an unrelated
            // optional matching field, and assert every live result in both arms.
            var expectedBinding = probe.ShouldBind || !conditionalRepair;
            var expectedError = expectedBinding
                ? 208
                : probe.State == "different"
                    ? 51001
                    : 0;

            var errorNumber = 0;
            try
            {
                Execute(connection, sql);
            }
            catch (SqlException exception) when (exception.Number is 208 or 51001)
            {
                errorNumber = exception.Number;
            }

            results.Add(new
            {
                probe.Name,
                ExpectedBinding = expectedBinding,
                ConditionalRepairEvaluationAvailable = conditionalRepair,
                ActualBinding = errorNumber == 208,
                ExpectedErrorNumber = expectedError,
                ErrorNumber = errorNumber,
                ContractSatisfied = errorNumber == expectedError,
            });
            contractsSatisfied &= errorNumber == expectedError;
        }

        WriteJson(Path.Combine(output, "repair-binding-probes.json"), results);
        if (!contractsSatisfied)
        {
            throw new InvalidOperationException("The candidate repair guard violated a live binding contract.");
        }
    }
}
