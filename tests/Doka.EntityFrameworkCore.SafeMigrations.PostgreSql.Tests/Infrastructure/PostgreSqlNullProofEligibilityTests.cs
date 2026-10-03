namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks catalog qualification, shared runtime evidence, and version-safe NULL-proof rendering.</summary>
public sealed class PostgreSqlNullProofEligibilityTests
{
    /// <summary>Materializes one NULL query and retains explicit catalog and identifier boundaries.</summary>
    /// <param name="varchar">Whether narrowing and nullability evidence coexist.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredRepair_EmitsOneCatalogQualifiedRuntimeNullQuery(
        bool varchar
    )
    {
        // Arrange
        using var context = CreateOfflineContext();
        var operation = RequiredOperation(varchar);
        var builder = CreateBuilder(context);
        var plan = builder.Build(operation, includeAnalysisEvidence: true, includeTransitionEvidence: true);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var analysis = plan.RenderStateExpression(dataBlocked: false, transitionEligible: false);
        var repair = plan.RenderRepairPrecondition(dataBlocked: false, transitionEligible: false);

        // Assert
        var command = Assert.Single(commands);
        var nullProbe = Assert.IsType<PostgreSqlSafeMigrationNullabilityDataProbe>(plan.NullabilityDataProbe)
            .BlockedExpression;
        var qualifiedTable = context.GetService<ISqlGenerationHelper>().DelimitIdentifier("proof_rows");
        Assert.Equal(1, CountOccurrences(command.CommandText, nullProbe));
        Assert.Contains("doka_nullability_blocked := COALESCE((" + nullProbe, command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("AND NOT COALESCE((EXISTS", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("AND NOT c.relhassubclass", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("not_null_constraint.convalidated", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.to_jsonb(not_null_constraint)->>'conenforced'", command.CommandText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("not_null_constraint.conenforced", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("FOR doka_evaluation_pass IN 1..2 LOOP", command.CommandText, StringComparison.Ordinal);
        Assert.Contains($"LOCK TABLE {qualifiedTable} IN ACCESS EXCLUSIVE MODE", command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("THEN (" + nullProbe + ") ELSE FALSE END", analysis, StringComparison.Ordinal);
        Assert.Contains("THEN (" + nullProbe + ") ELSE FALSE END", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("doka_nullability_blocked", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("__DOKA_SM_", analysis, StringComparison.Ordinal);
        Assert.True(command.CommandText.IndexOf(plan.StateEvaluationGuardExpression, StringComparison.Ordinal)
            < command.CommandText.IndexOf("doka_nullability_blocked := FALSE;", StringComparison.Ordinal));
        Assert.True(command.CommandText.IndexOf("'data_blocked'", StringComparison.Ordinal)
            < command.CommandText.IndexOf("THEN 'matching'", StringComparison.Ordinal));
    }

    /// <summary>Shares one physical eligibility evaluation within each fresh runtime classifier pass.</summary>
    /// <param name="varchar">Whether narrowing and nullability evidence coexist.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredRepair_MaterializesPhysicalRepairInvariantOncePerPass(
        bool varchar
    )
    {
        // Arrange
        using var context = CreateOfflineContext();
        var operation = RequiredOperation(varchar);
        var plan = CreateBuilder(context).Build(operation, includeTransitionEvidence: true);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var runtimeInvariant = plan.RenderNullabilityRepairInvariantExpression();
        var appendedInvariant = new StringBuilder();
        plan.AppendNullabilityRepairInvariantExpression(appendedInvariant);
        var inlineState = plan.RenderStateExpression(dataBlocked: false, transitionEligible: false);
        var runtimeState = plan.RenderStateExpression("length_cache", "transition_cache");
        var runtimeRepair = plan.RenderRepairPrecondition("length_cache", "transition_cache");

        // Assert
        var command = Assert.Single(commands).CommandText;
        Assert.Equal(1, CountOccurrences(command, runtimeInvariant));
        Assert.Equal(runtimeInvariant, appendedInvariant.ToString());
        Assert.Contains("doka_nullability_repair_eligible := COALESCE((" + runtimeInvariant,
            command, StringComparison.Ordinal);
        Assert.Contains("IF doka_nullability_repair_eligible AND NOT COALESCE((",
            command, StringComparison.Ordinal);
        Assert.Contains("WHEN (doka_nullability_repair_eligible) AND (", runtimeState, StringComparison.Ordinal);
        Assert.StartsWith("(doka_nullability_repair_eligible) AND NOT (", runtimeRepair, StringComparison.Ordinal);
        Assert.DoesNotContain("doka_nullability_repair_eligible", inlineState, StringComparison.Ordinal);
        Assert.DoesNotContain("__DOKA_SM_", inlineState, StringComparison.Ordinal);
        Assert.True(command.IndexOf("FOR doka_evaluation_pass IN 1..2 LOOP", StringComparison.Ordinal)
            < command.IndexOf("doka_nullability_repair_eligible := COALESCE((", StringComparison.Ordinal));
        Assert.True(command.IndexOf(plan.StateEvaluationGuardExpression, StringComparison.Ordinal)
            < command.IndexOf("doka_nullability_repair_eligible := COALESCE((", StringComparison.Ordinal));
        Assert.Contains(plan.Postcondition, command, StringComparison.Ordinal);
    }

    /// <summary>Does not create row-proof machinery for nullable targets or non-repair policies.</summary>
    /// <param name="nullable">The expected target nullability.</param>
    /// <param name="policy">The requested decision policy.</param>
    [Theory]
    [InlineData(true, SafeMigrationPolicy.RepairIfSafe)]
    [InlineData(false, SafeMigrationPolicy.ThrowIfDifferent)]
    public void InapplicableRepair_HasNoNullProof(
        bool nullable,
        SafeMigrationPolicy policy
    )
    {
        // Arrange
        using var context = CreateOfflineContext();
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("proof_rows",
            new ExpectedColumnDefinition("value", typeof(DateTime), nullable,
                storeType: "timestamp without time zone")), policy);

        // Act
        var plan = CreateBuilder(context).Build(operation, includeTransitionEvidence: true);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        // Assert
        var command = Assert.Single(commands);
        Assert.Null(plan.NullabilityDataProbe);
        Assert.DoesNotContain("doka_nullability_blocked", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain(" IS NULL LIMIT 1)", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Preserves quoted proof-token text while expanding adjacent structural markers.</summary>
    /// <param name="token">The internal token used as a legitimate name or value.</param>
    [Theory]
    [InlineData(PostgreSqlSafeMigrationRuntimePlan.DataProbePlaceholder)]
    [InlineData(PostgreSqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder)]
    [InlineData(PostgreSqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder)]
    [InlineData(PostgreSqlSafeMigrationRuntimePlan.NullabilityRepairInvariantPlaceholder)]
    public void ProofRendering_PreservesQuotedTokenText(
        string token
    )
    {
        // Arrange
        var quoted = "'doubled ''" + token + "' || \"doubled\"\"" + token + "\" || ";
        var plan = new PostgreSqlSafeMigrationRuntimePlan(
            quoted + PostgreSqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
            "TRUE", SafeMigrationRepairCapability.Safe, "TRUE")
        {
            NullabilityDataProbe = new PostgreSqlSafeMigrationNullabilityDataProbe(
                "proven_not_null", "eligible", "row_probe", "\"proof_rows\""),
        };

        // Act
        var inline = plan.RenderStateExpression();
        var runtime = plan.RenderStateExpression("length_cache", "transition_cache");
        var appended = new StringBuilder();
        plan.AppendStateExpression(appended, "length_cache", "transition_cache");

        // Assert
        Assert.Equal(quoted + "CASE WHEN (eligible) AND NOT (proven_not_null) THEN (row_probe) ELSE FALSE END",
            inline);
        Assert.Equal(quoted + "doka_nullability_blocked", runtime);
        Assert.Equal(runtime, appended.ToString());
    }

    /// <summary>Preserves quoted invariant-token text while sharing only the structural predicate.</summary>
    [Fact]
    public void RepairInvariantRendering_PreservesQuotedTokenText()
    {
        // Arrange
        var token = PostgreSqlSafeMigrationRuntimePlan.NullabilityRepairInvariantPlaceholder;
        var quoted = "'doubled ''" + token + "' || \"doubled\"\"" + token + "\" || ";
        var plan = new PostgreSqlSafeMigrationRuntimePlan(
            quoted + token, "TRUE", SafeMigrationRepairCapability.Safe, token)
        {
            NullabilityDataProbe = new PostgreSqlSafeMigrationNullabilityDataProbe(
                "proven_not_null", "eligible", "row_probe", "\"proof_rows\""),
        };

        // Act
        var inline = plan.RenderStateExpression();
        var runtime = plan.RenderStateExpression("length_cache", "transition_cache");
        var appended = new StringBuilder();
        plan.AppendStateExpression(appended, "length_cache", "transition_cache");
        var inlineRepair = plan.RenderRepairPrecondition();
        var runtimeRepair = plan.RenderRepairPrecondition("length_cache", "transition_cache");

        // Assert
        Assert.Equal(quoted + "eligible", inline);
        Assert.Equal(quoted + "doka_nullability_repair_eligible", runtime);
        Assert.Equal(runtime, appended.ToString());
        Assert.Equal("eligible", inlineRepair);
        Assert.Equal("doka_nullability_repair_eligible", runtimeRepair);
    }

    /// <summary>Ensures nullable-to-required explicit alterations share the same fresh NULL blocker.</summary>
    [Fact]
    public void AlterRequiredColumn_SharesNullEvidenceAndGuardedBinding()
    {
        // Arrange
        using var context = CreateOfflineContext();
        var operation = new SafeMigrationOperation(new AlterColumnIntent("proof_rows",
            new ExpectedColumnDefinition("value", typeof(DateTime), false, "timestamp without time zone"),
            new ExpectedColumnDefinition("value", typeof(DateTime), true, "timestamp without time zone")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var plan = CreateBuilder(context).Build(operation);
        var runtimeRepair = plan.RenderRepairPrecondition("length_cache", "transition_cache");

        // Assert
        Assert.NotNull(plan.NullabilityDataProbe);
        Assert.Contains("NOT (doka_nullability_blocked)", runtimeRepair, StringComparison.Ordinal);
        Assert.Contains("a.attname = 'value'", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Equal("'different'", plan.StateEvaluationGuardFailureExpression);
    }

    /// <summary>Transports proof freshness as catalog eligibility without emitting any live row SQL.</summary>
    /// <param name="transitionEligible">The materialized physical-transition qualification.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProofRequiredExpression_UsesCatalogEligibilityAndCurrentTransition(
        bool transitionEligible
    )
    {
        // Arrange
        var plan = new PostgreSqlSafeMigrationRuntimePlan("TRUE", "TRUE", SafeMigrationRepairCapability.Safe, "TRUE")
        {
            DataProbe = new PostgreSqlSafeMigrationDataProbe(
                "proof_rows", null, "value", 800, "proof_rows", "value", "fresh_transition", "narrowing"),
            NullabilityDataProbe = new PostgreSqlSafeMigrationNullabilityDataProbe(
                "complete_not_null_contract",
                "same_type OR " + PostgreSqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
                "EXISTS (SELECT 1 FROM proof_rows WHERE value IS NULL LIMIT 1)",
                "proof_rows"),
        };

        // Act
        var required = plan.RenderNullabilityDataProbeRequiredExpression(transitionEligible);
        var absent = (plan with { NullabilityDataProbe = null }).RenderNullabilityDataProbeRequiredExpression();

        // Assert
        Assert.Equal("(same_type OR " + (transitionEligible ? "TRUE" : "FALSE")
            + ") AND NOT (complete_not_null_contract)", required);
        Assert.DoesNotContain("SELECT", required, StringComparison.Ordinal);
        Assert.DoesNotContain("__DOKA_SM_", required, StringComparison.Ordinal);
        Assert.Equal("FALSE", absent);
    }

    /// <summary>Creates a non-connecting provider context for deterministic SQL-contract tests.</summary>
    private static SafeMigrationDbContext CreateOfflineContext() => new(
        "Host=127.0.0.1;Port=1;Database=proof_test;Username=test;Password=test");

    /// <summary>Creates the real provider catalog builder with provider rendering and type mappings.</summary>
    private static PostgreSqlSafeMigrationCatalogSqlBuilder CreateBuilder(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Creates one generic required-column repair with either length or timestamp metadata.</summary>
    private static SafeMigrationOperation RequiredOperation(
        bool varchar
    ) => new(new EnsureColumnIntent("proof_rows", new ExpectedColumnDefinition(
        "value", varchar ? typeof(string) : typeof(DateTime), false,
        storeType: varchar ? "character varying(800)" : "timestamp without time zone",
        maxLength: varchar ? 800 : null)), SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Counts exact SQL probes without matching unrelated catalog statements.</summary>
    private static int CountOccurrences(
        string text,
        string pattern
    )
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(pattern, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += pattern.Length;
        }

        return count;
    }
}
