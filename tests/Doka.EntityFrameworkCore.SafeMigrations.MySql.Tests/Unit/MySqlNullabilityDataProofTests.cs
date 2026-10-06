namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies NULL proofs are shared without leaking runtime variables into live analysis.</summary>
public sealed class MySqlNullabilityDataProofTests
{
    /// <summary>Verifies required repairs contain one guarded NULL scan shared by both decisions.</summary>
    /// <param name="varchar">Whether length and NULL proofs are both present.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredRepair_MaterializesOneNullScan(bool varchar)
    {
        // Arrange
        using var context = CreateContext();
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "proof_rows",
                new ExpectedColumnDefinition(
                    "Value",
                    varchar ? typeof(string) : typeof(int),
                    isNullable: false,
                    storeType: varchar ? "varchar(20)" : "int",
                    maxLength: varchar ? 20 : null)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var payloads = DecodePayloads(command.CommandText);

        // Assert
        const string nullScan = "EXISTS (SELECT 1 FROM `proof_rows` WHERE `Value` IS NULL LIMIT 1)";
        var proof = Assert.Single(payloads, sql => sql.Contains(nullScan, StringComparison.Ordinal));
        var state = Assert.Single(payloads, sql => sql.StartsWith("SELECT (", StringComparison.Ordinal)
            && sql.EndsWith("INTO @doka_sm_state", StringComparison.Ordinal));

        Assert.EndsWith("INTO @doka_sm_nullability_blocked", proof, StringComparison.Ordinal);
        Assert.Contains("@doka_sm_nullability_blocked", state, StringComparison.Ordinal);
        Assert.DoesNotContain(nullScan, state, StringComparison.Ordinal);
        Assert.DoesNotContain("__DOKA_SM_", state, StringComparison.Ordinal);
        Assert.Contains("c.IS_NULLABLE = 'YES'", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("@doka_sm_nullability_blocked = FALSE;", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("@doka_sm_nullability_blocked = NULL,", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("@doka_sm_column_repair_eligible", state, StringComparison.Ordinal);
        Assert.Contains(
            "SET @doka_sm_repair_ok = CASE WHEN @doka_sm_state = 'different' THEN COALESCE((",
            command.CommandText,
            StringComparison.Ordinal);

        Assert.DoesNotContain(nullScan, command.CommandText, StringComparison.Ordinal);
        Assert.True(command.CommandText.IndexOf("@doka_sm_prerequisite_ok", StringComparison.Ordinal)
            < command.CommandText.IndexOf("SET @doka_sm_column_repair_eligible", StringComparison.Ordinal));
    }

    /// <summary>Verifies no proof SQL is emitted when nullability repair cannot be requested.</summary>
    /// <param name="nullable">The target column nullability.</param>
    /// <param name="policy">The requested decision policy.</param>
    [Theory]
    [InlineData(true, SafeMigrationPolicy.RepairIfSafe)]
    [InlineData(false, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(true, SafeMigrationPolicy.ThrowIfDifferent)]
    public void InapplicableNullabilityRepair_EmitsNoNullScan(bool nullable, SafeMigrationPolicy policy)
    {
        using var context = CreateContext();
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("proof_rows", new ExpectedColumnDefinition(
                "Value", typeof(int), isNullable: nullable, storeType: "int")),
            policy);

        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        Assert.DoesNotContain(DecodePayloads(command.CommandText), sql => sql.Contains(
            "INTO @doka_sm_nullability_blocked", StringComparison.Ordinal));
        Assert.DoesNotContain("@doka_sm_nullability_blocked = FALSE;", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Verifies mixed proof markers are expanded in order and only once into final SQL.</summary>
    /// <param name="template">The marker sequence, including repeated and adjacent markers.</param>
    [Theory]
    [InlineData("__DOKA_SM_NULLABILITY_DATA_PROBE__")]
    [InlineData("__DOKA_SM_DATA_PROBE____DOKA_SM_NULLABILITY_DATA_PROBE____DOKA_SM_TRANSITION_INVARIANT__")]
    [InlineData("a__DOKA_SM_TRANSITION_INVARIANT__b__DOKA_SM_NULLABILITY_DATA_PROBE__"
        + "c__DOKA_SM_DATA_PROBE__d__DOKA_SM_NULLABILITY_DATA_PROBE__")]
    [InlineData(MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder)]
    [InlineData("TRUE")]
    public void ProofRenderer_PreservesInlineAnalysisAndCachedRuntime(string template)
    {
        var plan = new MySqlSafeMigrationRuntimePlan(template, "TRUE", SafeMigrationRepairCapability.Safe, template)
        {
            DataProbe = new MySqlSafeMigrationDataProbe(
                "rows", "Value", 20, "`rows`", "`Value`", "eligible", "narrowing"),
            NullabilityDataProbe = new MySqlSafeMigrationNullabilityDataProbe("nullable", "eligible", "null_probe"),
        };

        var analysis = plan.RenderStateExpression(static value => value.Value?.ToString() ?? "NULL");
        var runtime = plan.RenderPreparedStateExpression([], "length_cache", "eligible_cache");

        var expectedAnalysis = template.Replace(MySqlSafeMigrationRuntimePlan.DataProbePlaceholder,
                plan.DataProbe.BuildBlockedExpression(), StringComparison.Ordinal)
            .Replace(MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder, "eligible", StringComparison.Ordinal)
            .Replace(MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
                "CASE WHEN (nullable) AND (eligible) THEN (null_probe) ELSE FALSE END", StringComparison.Ordinal)
            .Replace(MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder,
                "eligible", StringComparison.Ordinal);

        var expectedRuntime = template.Replace(MySqlSafeMigrationRuntimePlan.DataProbePlaceholder,
                "length_cache", StringComparison.Ordinal)
            .Replace(MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
                "eligible_cache", StringComparison.Ordinal)
            .Replace(MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
                "@doka_sm_nullability_blocked", StringComparison.Ordinal)
            .Replace(MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder,
                "@doka_sm_column_repair_eligible", StringComparison.Ordinal);

        Assert.Equal(expectedAnalysis, analysis);
        Assert.Equal(expectedRuntime, runtime);
        Assert.DoesNotContain("@doka_sm_", analysis, StringComparison.Ordinal);
    }

    /// <summary>Verifies nullability-only plans do not require unrelated narrowing evidence.</summary>
    [Fact]
    public void NullabilityOnlyPlan_RendersWithoutLengthProbe()
    {
        var plan = new MySqlSafeMigrationRuntimePlan(MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
            "TRUE", SafeMigrationRepairCapability.Safe, MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder)
        {
            NullabilityDataProbe = new MySqlSafeMigrationNullabilityDataProbe("nullable", "eligible", "null_probe"),
        };

        var analysis = plan.RenderStateExpression(static _ => throw new InvalidOperationException());
        var runtime = plan.RenderPreparedRepairPrecondition([]);

        Assert.Equal("CASE WHEN (nullable) AND (eligible) THEN (null_probe) ELSE FALSE END", analysis);
        Assert.Equal("@doka_sm_nullability_blocked", runtime);
        Assert.Equal("nullable", plan.RenderPreparedNullabilityColumnExpression([]));
        Assert.Equal("eligible", plan.RenderPreparedNullabilityRepairInvariantExpression([]));
    }

    /// <summary>Preserves proof-token text in real identifiers, comments and literal defaults.</summary>
    /// <param name="token">A token that is structural only outside quoted SQL.</param>
    [Theory]
    [InlineData(MySqlSafeMigrationRuntimePlan.DataProbePlaceholder)]
    [InlineData(MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder)]
    [InlineData(MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder)]
    [InlineData(MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder)]
    public void ProofTokensInNamesAndLiterals_RemainUnchanged(string token)
    {
        using var context = CreateContext();
        var operation = new SafeMigrationOperation(new EnsureColumnIntent(token,
            new ExpectedColumnDefinition(token, typeof(string), isNullable: false, storeType: "varchar(80)",
                maxLength: 80, comment: "quoted '" + token, defaultValue: SafeMigrationDefaultValue.Literal(token))),
            SafeMigrationPolicy.RepairIfSafe);

        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var state = Assert.Single(DecodePayloads(command.CommandText),
            sql => sql.StartsWith("SELECT (", StringComparison.Ordinal)
            && sql.EndsWith("INTO @doka_sm_state", StringComparison.Ordinal));

        Assert.Contains("c.COLUMN_NAME = '" + token + "'", state, StringComparison.Ordinal);
        Assert.Contains("c.TABLE_NAME = '" + token + "'", state, StringComparison.Ordinal);
        Assert.Contains("quoted ''" + token, state, StringComparison.Ordinal);
        Assert.Contains("'" + token + "'", state, StringComparison.Ordinal);
        Assert.Contains("@doka_sm_nullability_blocked", state, StringComparison.Ordinal);
    }

    /// <summary>Preserves doubled quotes and backslash identifiers around structural markers.</summary>
    [Fact]
    public void ProofRenderer_SkipsQuotedTokensBeforeExpandingStructuralMarker()
    {
        const string token = MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder;
        const string quoted = "'escaped ''" + token + "' + `slash\\``" + token + "` + \"doubled\"\"" + token + "\" + ";
        var plan = new MySqlSafeMigrationRuntimePlan(quoted + token, "TRUE", SafeMigrationRepairCapability.Safe, "TRUE")
        {
            NullabilityDataProbe = new MySqlSafeMigrationNullabilityDataProbe("nullable", "eligible", "proof"),
        };

        var rendered = plan.RenderPreparedStateExpression([]);

        Assert.Equal(quoted + "@doka_sm_nullability_blocked", rendered);
    }

    /// <summary>Rejects malformed quoted templates instead of expanding unproven structural text.</summary>
    /// <param name="quote">The unterminated identifier or literal delimiter.</param>
    [Theory]
    [InlineData("'")]
    [InlineData("\"")]
    [InlineData("`")]
    public void ProofRenderer_RejectsUnterminatedQuotedToken(string quote)
    {
        var plan = new MySqlSafeMigrationRuntimePlan(
            quote + MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
            "TRUE", SafeMigrationRepairCapability.Safe, "TRUE")
        {
            NullabilityDataProbe = new MySqlSafeMigrationNullabilityDataProbe("nullable", "eligible", "proof"),
        };

        var render = () => plan.RenderPreparedStateExpression([]);

        var exception = Assert.Throws<InvalidOperationException>(render);
        Assert.Contains("unterminated quoted token", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Creates an offline provider context; generated SQL never requires a server connection.</summary>
    private static DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
                MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations()
            .Options;

        return new DbContext(options);
    }

    /// <summary>Decodes generated prepared statements to inspect real row SQL rather than encoded text.</summary>
    private static List<string> DecodePayloads(string sql)
    {
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";
        var payloads = new List<string>();
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var start = offset + prefix.Length;
            var end = sql.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("A generated SQL payload is unterminated.");
            }

            payloads.Add(Encoding.UTF8.GetString(Convert.FromHexString(sql.AsSpan(start, end - start))));
            offset = end + suffix.Length;
        }

        return payloads;
    }
}
