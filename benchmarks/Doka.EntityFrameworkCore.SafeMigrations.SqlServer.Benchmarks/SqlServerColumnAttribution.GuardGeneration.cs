namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Measures synthetic guard rendering independently of catalog and integer-proof construction.</summary>
[MemoryDiagnoser]
public class SqlServerGuardGenerationBenchmarks
{
    private GuardGenerationControl? _control;
    private string _sql = string.Empty;
    private string? _lastSql;

    /// <summary>Gets or sets the fixed direct, delayed-classifier or delayed-repair microcontrol.</summary>
    [Params("direct_catalog_64", "delayed_classifier_64", "delayed_repair_64")]
    public string ControlName { get; set; } = "direct_catalog_64";

    private GuardGenerationControl Control
        => _control ?? throw new InvalidOperationException("Setup has not completed.");

    /// <summary>Constructs the fixed inputs and records SQL shape outside all measured work.</summary>
    [GlobalSetup]
    public void Setup()
    {
        SqlServerColumnAttribution.ValidateEnvironment();
        _control = CreateGuardGenerationControls().Single(control => control.Name == ControlName);
        _sql = Generate();
        var output = SqlServerColumnAttribution.CreateEvidenceDirectory("guard-" + ControlName);
        File.WriteAllText(Path.Combine(output, "guard-generation.sql"), _sql);
        SqlServerColumnAttribution.WriteJson(Path.Combine(output, "guard-generation-control.json"), new
        {
            Control.Name,
            Scope = "guard_generation_microcontrol_not_full_generator_attribution",
            SyntheticInput = true,
            ExecutesGeneratedSql = false,
            Policy = Control.Operation.Policy.ToString(),
            RepairCapability = Control.Plan.RepairCapability.ToString(),
            Control.Plan.RequiresDelayedBinding,
            InputStateCharacters = Control.Plan.StateExpression.Length,
            InputRepairCharacters = Control.Plan.RepairPrecondition.Length,
            InputPostconditionCharacters = Control.Plan.Postcondition.Length,
            GeneratedSqlCharacters = _sql.Length,
            GeneratedSqlUtf8Bytes = Encoding.UTF8.GetByteCount(_sql),
            SysColumnsSubqueryOccurrences = SqlServerColumnAttribution.CountOccurrences(_sql, "FROM sys.columns"),
            Interpretation = "Fixed common plan inputs bypass catalog construction and integer admission. "
                + "Results measure combined guard helpers, not individual causes or full-generator improvements.",
        });
    }

    /// <summary>Renders one guard using the same preconstructed immutable control inputs.</summary>
    /// <returns>The complete guarded SQL text.</returns>
    [Benchmark]
    public string Generate()
        => _lastSql = SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(Control.Operation, Control.Plan, [], []);

    /// <summary>Checks each iteration's generated SQL without including validation in the timing.</summary>
    [IterationCleanup]
    public void VerifyDeterministicSql()
    {
        if (_lastSql != _sql)
        {
            throw new InvalidOperationException("Synthetic guard generation was not deterministic.");
        }
    }

    /// <summary>Requires the final generated SQL to match the setup oracle.</summary>
    [GlobalCleanup]
    public void Cleanup()
        => VerifyDeterministicSql();

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

    /// <summary>Retains a preconstructed input so factories and fixtures remain outside the measurement.</summary>
    private sealed record GuardGenerationControl(
        string Name,
        SafeMigrationOperation Operation,
        SqlServerSafeMigrationRuntimePlan Plan
    );
}
