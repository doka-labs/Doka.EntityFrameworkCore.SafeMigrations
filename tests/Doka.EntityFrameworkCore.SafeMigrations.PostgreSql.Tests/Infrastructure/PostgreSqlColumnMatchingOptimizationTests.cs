namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Protects single-resolution collation SQL and the qualified matching-first runtime boundary.</summary>
public sealed class PostgreSqlColumnMatchingOptimizationTests
{
    /// <summary>Resolves an explicit collation once while retaining absence, schema and visibility semantics.</summary>
    /// <param name="schema">The explicit schema, or null for search-path visibility.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("pg_catalog")]
    public void ExplicitCollation_UsesOneNullSafeOidResolution(
        string? schema
    )
    {
        // Arrange
        using var context = CreateContext();
        var definition = new ExpectedColumnDefinition("value", typeof(string), true, "text",
            collation: new SafeMigrationCollationIdentifier("C", schema));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("proof_rows", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateBuilder(context).Build(operation, includeAnalysisEvidence: true);

        // Assert
        Assert.Equal(1, Occurrences(plan.Postcondition, "SELECT coll.oid FROM pg_catalog.pg_collation coll"));
        Assert.Contains("COALESCE(a.attcollation = (SELECT coll.oid", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains(schema is null ? "pg_catalog.pg_collation_is_visible(coll.oid) LIMIT 1"
                : "ns.nspname = 'pg_catalog'", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("NOT (COALESCE(a.attcollation =", plan.DiagnosticEvidenceExpression!, StringComparison.Ordinal);
    }

    /// <summary>The default-collation path retains type-owned identity without adding a resolver.</summary>
    [Fact]
    public void ImplicitCollation_RetainsTypeOwnedCatalogComparison()
    {
        // Arrange
        using var context = CreateContext();
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("proof_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, "text")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateBuilder(context).Build(operation);

        // Assert
        Assert.Contains("a.attcollation = t.typcollation", plan.Postcondition, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_catalog.pg_collation", plan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>Bounds explicit collation resolver growth to one lookup for each expected table column.</summary>
    /// <param name="count">The table width used by the attribution fixtures.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    public void TableCollations_ResolveEachExpectedOidOnce(
        int count
    )
    {
        // Arrange
        using var context = CreateContext();
        var columns = Enumerable.Range(0, count)
            .Select(index => new ExpectedColumnDefinition(
                $"value_{index}", typeof(string), true, "text",
                collation: new SafeMigrationCollationIdentifier("C", index % 2 == 0 ? null : "pg_catalog")))
            .ToArray();

        var operation = new SafeMigrationOperation(new EnsureTableIntent(
            new ExpectedTableDefinition("proof_rows", columns), SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateBuilder(context).Build(operation);

        // Assert
        Assert.Equal(count, Occurrences(plan.Postcondition, "SELECT coll.oid FROM pg_catalog.pg_collation coll"));
        Assert.Contains("NOT COALESCE(CASE expected.attnum", plan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>Never equates parent-only nullability with a complete target row-relation proof.</summary>
    /// <param name="varchar">Whether both narrowing and NULL qualification apply.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingRepair_ShortcutsOnlyAfterBindingAndCompleteNotNullProof(
        bool varchar
    )
    {
        // Arrange
        using var context = CreateContext();
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("proof_rows",
            new ExpectedColumnDefinition("value", varchar ? typeof(string) : typeof(DateTime), false,
                varchar ? "character varying(32)" : "timestamp without time zone")),
            SafeMigrationPolicy.RepairIfSafe);

        var plan = CreateBuilder(context).Build(operation, includeTransitionEvidence: true);

        // Act
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model))
            .CommandText;

        var targetEvaluation = "doka_target_matches := COALESCE((" + plan.Postcondition + "), FALSE);";
        var completeProof = "doka_complete_not_null := COALESCE(("
            + plan.NullabilityDataProbe!.NotNullContractExpression + "), FALSE);";

        const string shortcut = "IF doka_target_matches AND doka_complete_not_null THEN";

        // Assert
        Assert.Contains(shortcut, sql, StringComparison.Ordinal);
        Assert.Contains(targetEvaluation, sql, StringComparison.Ordinal);
        Assert.Contains(completeProof, sql, StringComparison.Ordinal);
        Assert.False(plan.NullabilityDataProbe.MatchingRequiresSourceContractProof);
        Assert.Equal(1, Occurrences(sql, plan.NullabilityDataProbe.NotNullContractExpression));
        Assert.True(sql.IndexOf(plan.PrerequisiteExpression, StringComparison.Ordinal)
            < sql.IndexOf(shortcut, StringComparison.Ordinal));
        Assert.True(sql.IndexOf(plan.StateEvaluationGuardExpression, StringComparison.Ordinal)
            < sql.IndexOf(shortcut, StringComparison.Ordinal));
        Assert.True(sql.IndexOf(shortcut, StringComparison.Ordinal)
            < sql.IndexOf("doka_nullability_repair_eligible := COALESCE((", StringComparison.Ordinal));
        Assert.Contains("doka_state := 'matching';", sql, StringComparison.Ordinal);
        Assert.Contains("AND NOT c.relhassubclass", completeProof, StringComparison.Ordinal);
        Assert.Contains("FOR doka_evaluation_pass IN 1..2 LOOP", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN doka_target_matches THEN 'matching'", sql, StringComparison.Ordinal);
    }

    /// <summary>Retains the exact old contract as explicit authority for Alter's descendant NULL matching.</summary>
    [Fact]
    public void AlterNullabilityProbe_PreservesExactSourceAuthority()
    {
        // Arrange
        using var context = CreateContext();
        var target = new ExpectedColumnDefinition("value", typeof(DateTime), false, "timestamp without time zone");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("proof_rows", target,
            new ExpectedColumnDefinition("value", typeof(DateTime), true, "timestamp without time zone")),
            SafeMigrationPolicy.RepairIfSafe);

        var plan = CreateBuilder(context).Build(operation, includeTransitionEvidence: true);

        // Act
        var inline = plan.RenderStateExpression();
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model))
            .CommandText;

        // Assert
        Assert.True(plan.NullabilityDataProbe!.MatchingRequiresSourceContractProof);
        Assert.Contains($"WHEN ({plan.Postcondition}) AND (({plan.NullabilityDataProbe.NotNullContractExpression})",
            inline, StringComparison.Ordinal);
        Assert.Contains("WHEN (doka_target_matches AND (doka_complete_not_null "
            + "OR doka_nullability_repair_eligible)) THEN 'matching'", sql, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(sql, plan.NullabilityDataProbe.NotNullContractExpression));
    }

    /// <summary>Does not add extra matching checks to plans without live repair proofs.</summary>
    [Fact]
    public void UnprobedPlan_DoesNotEmitMatchingShortcut()
    {
        // Arrange
        using var context = CreateContext();
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("proof_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, "text")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model))
            .CommandText;

        // Assert
        Assert.DoesNotContain("doka_state := 'matching';", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR doka_evaluation_pass", sql, StringComparison.Ordinal);
    }

    /// <summary>Substitutes controlled matching markers without rewriting authored values or names.</summary>
    /// <param name="sourceAuthority">Whether the matching predicate also needs Alter's old contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingMarker_PreservesQuotedNamesLiteralsAndCommentPayloads(
        bool sourceAuthority
    )
    {
        // Arrange
        var token = PostgreSqlSafeMigrationRuntimePlan.MatchingPlaceholder;
        var quoted = "'a doubled ''" + token + " /* comment */' || \"a doubled\"\"" + token + "\" || ";
        var plan = new PostgreSqlSafeMigrationRuntimePlan(quoted + token, "fresh_target",
            SafeMigrationRepairCapability.Safe, "TRUE")
        {
            NullabilityDataProbe = new PostgreSqlSafeMigrationNullabilityDataProbe(
                "complete_contract", "eligible", "row_probe", "proof_rows")
            {
                MatchingRequiresSourceContractProof = sourceAuthority,
            },
        };

        // Act
        var inline = plan.RenderStateExpression(dataBlocked: false, transitionEligible: false);
        var runtime = new StringBuilder();
        plan.AppendStateExpression(runtime, "blocked", "transition", "fresh_matching");

        // Assert
        Assert.Equal(quoted + (sourceAuthority
            ? "(fresh_target) AND ((complete_contract) OR (eligible))" : "fresh_target"), inline);
        Assert.Equal(quoted + "fresh_matching", runtime.ToString());
    }

    /// <summary>Includes the separately materialized target predicate in dollar-tag collision checks.</summary>
    [Fact]
    public void MatchingMarker_TargetOnlyDollarTagCollisionUsesFreshDelimiter()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("proof_rows",
            new ExpectedColumnDefinition("value", typeof(DateTime), false, "timestamp without time zone")),
            SafeMigrationPolicy.RepairIfSafe);

        var plan = new PostgreSqlSafeMigrationRuntimePlan(
            "CASE WHEN " + PostgreSqlSafeMigrationRuntimePlan.MatchingPlaceholder
                + " THEN 'matching' ELSE 'different' END",
            "'$doka_safe_migration$' IS NOT NULL", SafeMigrationRepairCapability.Safe, "FALSE")
        {
            ExecutionPostcondition = "TRUE",
            NullabilityDataProbe = new PostgreSqlSafeMigrationNullabilityDataProbe(
                "TRUE", "FALSE", "FALSE", "proof_rows"),
        };

        var method = typeof(PostgreSqlSafeMigrationsSqlGenerator).GetMethod("BuildGuardedSql",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The PostgreSQL guard renderer was not found.");

        // Act
        var sql = (string)method.Invoke(null,
            [operation, plan, Array.Empty<MigrationCommand>(), Array.Empty<MigrationCommand>()])!;

        // Assert
        Assert.StartsWith("DO $doka_safe_migration_1$", sql, StringComparison.Ordinal);
        Assert.Contains("doka_target_matches := COALESCE((" + plan.Postcondition, sql, StringComparison.Ordinal);
    }

    /// <summary>Creates an offline context that uses the actual provider services.</summary>
    /// <returns>A context that composes the real provider without a database connection.</returns>
    private static SafeMigrationDbContext CreateContext() => new(
        "Host=127.0.0.1;Port=1;Database=matching_test;Username=test;Password=test");

    /// <summary>Creates the real catalog builder without connecting.</summary>
    /// <param name="context">The context supplying the real provider services.</param>
    /// <returns>The provider catalog SQL builder.</returns>
    private static PostgreSqlSafeMigrationCatalogSqlBuilder CreateBuilder(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Counts exact resolver statements independently of surrounding joins.</summary>
    /// <param name="text">The complete emitted SQL text.</param>
    /// <param name="value">The exact resolver statement to count.</param>
    /// <returns>The ordinal occurrence count.</returns>
    private static int Occurrences(
        string text,
        string value
    )
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
