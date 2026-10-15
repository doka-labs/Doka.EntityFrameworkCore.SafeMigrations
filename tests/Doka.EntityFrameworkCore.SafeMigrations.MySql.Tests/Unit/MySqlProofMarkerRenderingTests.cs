namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies one closed, quote-aware proof contract across every runtime-plan rendering path.</summary>
public sealed class MySqlProofMarkerRenderingTests
{
    private const string BinaryMarker = "__DOKA_SM_BINARY_DEFAULT_0__";

    /// <summary>Enumerates every ordinary or prepared expression consumer and each supported proof binding.</summary>
    /// <returns>Consumer names, independently completed bindings and their exact substitutions.</returns>
    public static IEnumerable<object?[]> BinaryRenderCases()
    {
        string[] consumers =
        [
            "prerequisite", "qualification", "state", "state-resolved", "guard", "guard-failure",
            "classification", "classification-resolved", "diagnostics", "matched-object", "row-evidence",
            "dependency-counts", "postcondition", "repair", "repair-resolved", "prepared-prerequisite",
            "prepared-qualification", "prepared-state", "prepared-state-resolved", "prepared-guard",
            "prepared-guard-failure", "prepared-postcondition", "prepared-execution-postcondition",
            "required-status", "prepared-required-status", "required-resume", "prepared-required-resume",
            "prepared-repair", "prepared-repair-resolved", "prepared-transition", "prepared-data-required",
            "prepared-data-blocked", "prepared-mutation", "prepared-nullable", "prepared-null-repair",
            "prepared-null-blocked",
        ];

        (string? Binding, string Expected)[] bindings =
        [
            (null, "FALSE"), ("FALSE", "FALSE"), ("TRUE", "TRUE"), ("@completed_binary", "@completed_binary"),
        ];

        foreach (var consumer in consumers)
        {
            foreach (var binding in bindings)
            {
                yield return [consumer, binding.Binding, binding.Expected];
            }
        }
    }

    /// <summary>Every expression consumer binds native default evidence without leaking a structural token.</summary>
    /// <param name="consumer">The documented runtime-plan entry point exercised by the dispatch helper.</param>
    /// <param name="binding">The completed fact, or null when no native proof was materialized.</param>
    /// <param name="expected">The exact fail-closed or independently completed substitution.</param>
    [Theory]
    [MemberData(nameof(BinaryRenderCases))]
    public void OrdinaryAndPreparedConsumersBindTheSameProof(
        string consumer,
        string? binding,
        string expected
    )
    {
        // Arrange
        var plan = CreatePlan(BinaryMarker, binding);
        var expectedSql = consumer switch
        {
            "required-resume" => "(" + expected + ") = 'staged'",
            "prepared-required-resume" => "((" + expected + ")) = 'staged'",
            "prepared-data-blocked" => "EXISTS(SELECT 1 FROM `proof_items` WHERE " + expected
                + " IS NOT NULL AND CHAR_LENGTH(" + expected + ") > 8 LIMIT 1)",
            _ => expected,
        };

        // Act
        var rendered = Render(plan, consumer);

        // Assert
        Assert.Equal(expectedSql, rendered);
        Assert.DoesNotContain(BinaryMarker, rendered, StringComparison.Ordinal);
    }

    /// <summary>Failed-guard diagnostics never borrow even a bound default fact from later execution.</summary>
    /// <param name="binding">An absent result, completed Boolean, or runtime result variable.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("FALSE")]
    [InlineData("TRUE")]
    [InlineData("@completed_binary")]
    public void GuardDiagnosticsReturnNullBeforeBinaryProofsAreAvailable(string? binding)
    {
        // Arrange
        var parameter = MySqlCatalogSqlTemplate.Marker(0);
        var plan = CreatePlan(BinaryMarker + " AND " + parameter, binding) with
        {
            ParameterValues = [new MySqlCatalogParameterValue("must-not-be-rendered", null)],
        };

        var renderCalls = 0;

        // Act
        var rendered = plan.RenderStateEvaluationGuardClassificationCodeExpression(_ =>
        {
            renderCalls++;

            return "'unexpected'";
        });

        // Assert
        Assert.Equal("NULL", rendered);
        Assert.Equal(0, renderCalls);
    }

    /// <summary>Quoted binary-marker values do not acquire a dependency on later proof materialization.</summary>
    [Fact]
    public void GuardDiagnosticsPreserveQuotedMarkersAndBoundRequiredStatus()
    {
        // Arrange
        var expression = "CASE WHEN " + MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder
            + " = 'staged' THEN '" + BinaryMarker + "' ELSE `name``" + BinaryMarker + "` END";
        var plan = CreatePlan(expression, "TRUE") with
        {
            RequiredColumnAddPlan = new MySqlSafeMigrationRequiredColumnAddPlan("owned", "'fresh'", false)
            {
                StatusBinding = "'staged'",
            },
        };

        // Act
        var rendered = plan.RenderStateEvaluationGuardClassificationCodeExpression(UnexpectedValue);

        // Assert
        Assert.Equal("CASE WHEN 'staged' = 'staged' THEN '" + BinaryMarker
            + "' ELSE `name``" + BinaryMarker + "` END", rendered);
    }

    /// <summary>Old transition facts, required-add status and binary facts remain independent proof domains.</summary>
    [Fact]
    public void MixedProofFamiliesRetainTheirOwnCompletedBindings()
    {
        // Arrange
        var expression = string.Join(" AND ", BinaryMarker,
            MySqlSafeMigrationRuntimePlan.DataProbePlaceholder,
            MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
            MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
            MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder,
            MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder);

        var plan = CreatePlan(expression, "TRUE") with
        {
            DataProbe = new MySqlSafeMigrationDataProbe(
                "proof_items", "payload", 8, "`proof_items`", "`payload`", "fresh_transition", "needs_rows"),
            NullabilityDataProbe = new MySqlSafeMigrationNullabilityDataProbe(
                "nullable_column", "safe_column", "null_rows"),
            RequiredColumnAddPlan = new MySqlSafeMigrationRequiredColumnAddPlan("owned", "'fresh'", false)
            {
                StatusBinding = "'fresh'",
            },
        };

        // Act
        var rendered = plan.RenderPreparedStateExpression([], "@completed_rows", "@completed_transition");

        // Assert
        Assert.Equal("TRUE AND @completed_rows AND @completed_transition AND @doka_sm_nullability_blocked"
            + " AND @doka_sm_column_repair_eligible AND 'fresh'", rendered);
        Assert.DoesNotContain("__DOKA_SM_", rendered, StringComparison.Ordinal);
    }

    /// <summary>Raw status classifiers cannot recursively ask for their own bound or unbound status.</summary>
    /// <param name="prepared">Whether the prepared status rendering entry point is used.</param>
    /// <param name="bound">Whether a previously completed status exists.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StatusRecursionIsRejectedAfterBindingOtherProofs(bool prepared, bool bound)
    {
        // Arrange
        var expression = BinaryMarker + " AND " + MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder;
        var plan = CreatePlan(expression, "TRUE");
        plan = plan with
        {
            RequiredColumnAddPlan = plan.RequiredColumnAddPlan! with
            {
                StatusBinding = bound ? "'complete'" : null,
            },
        };

        // Act
        var exception = Record.Exception(() =>
        {
            _ = prepared ? plan.RenderPreparedRequiredColumnAddStatusExpression([])
                : plan.RenderRequiredColumnAddStatusExpression(UnexpectedValue);
        });

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("cannot depend on its own status marker", invalid.Message, StringComparison.Ordinal);
    }

    /// <summary>A structural binary marker cannot survive without an owning proof contract.</summary>
    /// <param name="prepared">Whether the prepared entry point is used.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingBinaryProofContractFailsClosed(bool prepared)
    {
        // Arrange
        var plan = CreatePlan(BinaryMarker, "TRUE") with { BinaryDefaultProofs = [], };

        // Act
        var exception = Record.Exception(() =>
        {
            _ = prepared ? plan.RenderPreparedPostcondition([]) : plan.RenderPostcondition(UnexpectedValue);
        });

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Malformed, unknown and non-owned ordinal spellings never become executable catalog SQL.</summary>
    /// <param name="token">The unquoted malformed or unowned structural token.</param>
    [Theory]
    [InlineData("__DOKA_SM_BINARY_DEFAULT_")]
    [InlineData("__DOKA_SM_BINARY_DEFAULT___")]
    [InlineData("__DOKA_SM_BINARY_DEFAULT_-1__")]
    [InlineData("__DOKA_SM_BINARY_DEFAULT_0_")]
    [InlineData("__DOKA_SM_BINARY_DEFAULT_0x__")]
    [InlineData("__DOKA_SM_BINARY_DEFAULT_2147483648__")]
    [InlineData("__DOKA_SM_BINARY_DEFAULT_999999999999999999999999999999__")]
    [InlineData("__DOKA_SM_BINARY_DEFAULT_1__")]
    [InlineData("__DOKA_SM_BINARY_DEFAULT_00__")]
    public void InvalidBinaryTokensAreRejected(string token)
    {
        // Arrange
        var plan = CreatePlan("TRUE AND " + token, "TRUE");

        // Act
        var exception = Record.Exception(() => plan.RenderPreparedPostcondition([]));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>The structural position must match the exact token of its owning immutable descriptor.</summary>
    [Fact]
    public void MismatchedProofDescriptorIsRejected()
    {
        // Arrange
        var plan = CreatePlan(BinaryMarker, "TRUE") with
        {
            BinaryDefaultProofs = [new MySqlSafeMigrationBinaryDefaultProof(
                "__DOKA_SM_BINARY_DEFAULT_1__", "fresh_catalog", "SELECT TRUE", "TRUE")],
        };

        // Act
        var exception = Record.Exception(() => plan.RenderPostcondition(UnexpectedValue));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Incomplete quote contexts cannot conceal structural tokens before or after a replacement.</summary>
    /// <param name="quote">The independent SQL literal or identifier delimiter.</param>
    /// <param name="before">Whether the unterminated quote precedes the structural token.</param>
    /// <param name="prepared">Whether the prepared render path is used.</param>
    [Theory]
    [InlineData("'", false, false)]
    [InlineData("'", true, false)]
    [InlineData("'", false, true)]
    [InlineData("'", true, true)]
    [InlineData("\"", false, false)]
    [InlineData("\"", true, false)]
    [InlineData("\"", false, true)]
    [InlineData("\"", true, true)]
    [InlineData("`", false, false)]
    [InlineData("`", true, false)]
    [InlineData("`", false, true)]
    [InlineData("`", true, true)]
    public void UnterminatedQuotedTokensAreRejected(string quote, bool before, bool prepared)
    {
        // Arrange
        var expression = before ? quote + "unclosed " + BinaryMarker : BinaryMarker + " AND " + quote + "unclosed";
        var plan = CreatePlan(expression, "TRUE");

        // Act
        var exception = Record.Exception(() =>
        {
            _ = prepared ? plan.RenderPreparedPostcondition([]) : plan.RenderPostcondition(UnexpectedValue);
        });

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Quoted values, escaped identifiers and rendered parameter payloads remain opaque.</summary>
    /// <param name="prepared">Whether catalog values have already been rendered.</param>
    /// <param name="hasContract">Whether a proof contract exists despite containing only quoted marker values.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void QuotedMarkersAndParameterPayloadsArePreserved(bool prepared, bool hasContract)
    {
        // Arrange
        var parameter = MySqlCatalogSqlTemplate.Marker(0);
        var expression = "'one''" + BinaryMarker + "' AND \"two\"\"" + BinaryMarker + "\" AND `three``"
            + BinaryMarker + "` AND " + parameter;
        var value = "'payload " + BinaryMarker + "'";
        var expected = expression.Replace(parameter, value, StringComparison.Ordinal);
        var plan = CreatePlan(expression, "TRUE") with
        {
            ParameterValues = [new MySqlCatalogParameterValue("payload " + BinaryMarker, null)],
            BinaryDefaultProofs = hasContract
                ? [new MySqlSafeMigrationBinaryDefaultProof(BinaryMarker, "fresh_catalog", "SELECT TRUE", "TRUE")]
                : [],
        };

        // Act
        var rendered = prepared ? plan.RenderPreparedPostcondition([value]) : plan.RenderPostcondition(_ => value);

        // Assert
        Assert.Equal(expected, rendered);
    }

    /// <summary>Binding an ordinary marker-free template preserves its original string reference.</summary>
    [Fact]
    public void MarkerFreeBindingUsesReferencePreservingFastPath()
    {
        // Arrange
        var expression = new string("catalog_matches AND physical_shape".AsSpan());
        var proof = new MySqlSafeMigrationBinaryDefaultProof(BinaryMarker, "fresh_catalog", "SELECT TRUE", "TRUE");

        // Act
        var bound = MySqlSafeMigrationBinaryDefaultProof.Bind(expression, [proof]);
        var unowned = MySqlSafeMigrationBinaryDefaultProof.Bind(expression, []);
        var prepared = CreatePlan(expression, "TRUE").RenderPreparedPostcondition([]);

        // Assert
        Assert.Same(expression, bound);
        Assert.Same(expression, unowned);
        Assert.Same(expression, prepared);
    }

    /// <summary>Eligibility stays fresh catalog SQL and never borrows the result of the proof it qualifies.</summary>
    /// <param name="prepared">Whether literal values are already prepared.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EligibilityRenderingIsIndependentOfBinaryResultsAndCatalogSnapshots(bool prepared)
    {
        // Arrange
        var parameter = MySqlCatalogSqlTemplate.Marker(0);
        var eligibility = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE COLUMN_NAME = " + parameter
            + " AND COLUMN_COMMENT = '" + BinaryMarker + "')";
        var proof = new MySqlSafeMigrationBinaryDefaultProof(BinaryMarker, eligibility, "SELECT TRUE", "TRUE");
        var plan = CreatePlan("TRUE", "TRUE") with
        {
            ParameterValues = [new MySqlCatalogParameterValue("payload", null)],
            BinaryDefaultProofs = [proof],
            CatalogRelations = static _ => throw new InvalidOperationException("Fresh eligibility used a snapshot."),
        };

        // Act
        var rendered = prepared
            ? MySqlSafeMigrationRuntimePlan.RenderPreparedBinaryDefaultEligibilityExpression(proof, ["'payload'"])
            : plan.RenderBinaryDefaultEligibilityExpression(proof, static _ => "'payload'");

        // Assert
        Assert.Equal(eligibility.Replace(parameter, "'payload'", StringComparison.Ordinal), rendered);
        Assert.Contains("INFORMATION_SCHEMA.COLUMNS", rendered, StringComparison.Ordinal);
        Assert.Contains("'" + BinaryMarker + "'", rendered, StringComparison.Ordinal);
    }

    /// <summary>Real binary defaults and no-default required additions produce mutually exclusive protocols.</summary>
    [Fact]
    public void CapturedProducerPlansKeepBinaryAndRequiredAddProtocolsExclusive()
    {
        // Arrange
        // WHY: The explicit test-owned provider avoids accumulating engine
        // variants in EF's process-wide application service-provider cache.
        using var services = new ServiceCollection()
            .AddEntityFrameworkDokaMySql()
            .AddEntityFrameworkDokaMySqlSafeMigrations()
            .BuildServiceProvider();

        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseInternalServiceProvider(services);
        options.UseMySql("Server=127.0.0.1;Port=1;User ID=test;Database=proof_marker;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        options.UseMySqlSafeMigrations();
        using var context = new SafeMigrationDbContext(options.Options);
        var binary = new ExpectedColumnDefinition("payload", typeof(byte[]), false, "varbinary(8)",
            defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 0, 10, }));

        var required = new ExpectedColumnDefinition("payload", typeof(byte[]), false, "varbinary(8)");
        var binaryOperation = new SafeMigrationOperation(new EnsureColumnIntent("proof_items", binary),
            SafeMigrationPolicy.ThrowIfDifferent);

        var requiredOperation = new SafeMigrationOperation(new EnsureColumnIntent("proof_items", required),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var binaryPlan = Capture(context, binaryOperation);
        var requiredPlan = Capture(context, requiredOperation);

        // Assert
        Assert.Single(binaryPlan.BinaryDefaultProofs);
        Assert.Null(binaryPlan.RequiredColumnAddPlan);
        Assert.Empty(requiredPlan.BinaryDefaultProofs);
        Assert.NotNull(requiredPlan.RequiredColumnAddPlan);
        Assert.Contains(BinaryMarker, binaryPlan.StateExpression, StringComparison.Ordinal);
        Assert.Contains(MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder,
            requiredPlan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Releases each capture lease before another producer protocol is inspected.</summary>
    private static MySqlSafeMigrationRuntimePlan Capture(DbContext context, SafeMigrationOperation operation)
    {
        using var lease = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        return lease.Complete().Single();
    }

    /// <summary>Creates a synthetic plan whose independent expression surfaces carry the same template.</summary>
    private static MySqlSafeMigrationRuntimePlan CreatePlan(string expression, string? binding) =>
        new(expression, expression, SafeMigrationRepairCapability.Safe, expression)
        {
            PrerequisiteExpression = expression,
            CurrentDatabaseQualificationExpression = expression,
            StateEvaluationGuardExpression = expression,
            StateEvaluationGuardFailureExpression = expression,
            ClassificationCodeExpression = expression,
            DiagnosticEvidenceExpression = expression,
            MatchedObjectNameExpression = expression,
            ModelManagedRowEvidenceExpression = expression,
            ModelManagedDependencyCountsExpression = expression,
            ExecutionPostcondition = expression,
            MutationSql = expression,
            BinaryDefaultProofs = [new MySqlSafeMigrationBinaryDefaultProof(
                BinaryMarker, "fresh_catalog", "SELECT TRUE", binding)],
            RequiredColumnAddPlan = new MySqlSafeMigrationRequiredColumnAddPlan("owned", expression, false),
            DataProbe = new MySqlSafeMigrationDataProbe(
                "proof_items", "payload", 8, "`proof_items`", expression, expression, expression),
            NullabilityDataProbe = new MySqlSafeMigrationNullabilityDataProbe(expression, expression, expression),
        };

    /// <summary>Dispatches documented consumer names without duplicating their common proof assertions.</summary>
    private static string Render(MySqlSafeMigrationRuntimePlan plan, string consumer) => consumer switch
    {
        "prerequisite" => plan.RenderPrerequisiteExpression(UnexpectedValue),
        "qualification" => plan.RenderCurrentDatabaseQualificationExpression(UnexpectedValue),
        "state" => plan.RenderStateExpression(UnexpectedValue),
        "state-resolved" => plan.RenderStateExpression(UnexpectedValue, false, true),
        "guard" => plan.RenderStateEvaluationGuardExpression(UnexpectedValue),
        "guard-failure" => plan.RenderStateEvaluationGuardFailureExpression(UnexpectedValue),
        "classification" => plan.RenderClassificationCodeExpression(UnexpectedValue),
        "classification-resolved" => plan.RenderClassificationCodeExpression(UnexpectedValue, false),
        "diagnostics" => plan.RenderDiagnosticEvidenceExpression(UnexpectedValue),
        "matched-object" => plan.RenderMatchedObjectNameExpression(UnexpectedValue),
        "row-evidence" => plan.RenderModelManagedRowEvidenceExpression(UnexpectedValue),
        "dependency-counts" => plan.RenderModelManagedDependencyCountsExpression(UnexpectedValue),
        "postcondition" => plan.RenderPostcondition(UnexpectedValue),
        "repair" => plan.RenderRepairPrecondition(UnexpectedValue),
        "repair-resolved" => plan.RenderRepairPrecondition(UnexpectedValue, false, true),
        "prepared-prerequisite" => plan.RenderPreparedPrerequisiteExpression([]),
        "prepared-qualification" => plan.RenderPreparedCurrentDatabaseQualificationExpression([]),
        "prepared-state" => plan.RenderPreparedStateExpression([]),
        "prepared-state-resolved" => plan.RenderPreparedStateExpression([], "FALSE", "TRUE"),
        "prepared-guard" => plan.RenderPreparedStateEvaluationGuardExpression([]),
        "prepared-guard-failure" => plan.RenderPreparedStateEvaluationGuardFailureExpression([]),
        "prepared-postcondition" => plan.RenderPreparedPostcondition([]),
        "prepared-execution-postcondition" => plan.RenderPreparedExecutionPostcondition([]),
        "required-status" => plan.RenderRequiredColumnAddStatusExpression(UnexpectedValue),
        "prepared-required-status" => plan.RenderPreparedRequiredColumnAddStatusExpression([]),
        "required-resume" => plan.RenderRequiredColumnAddResumeExpression(UnexpectedValue),
        "prepared-required-resume" => plan.RenderPreparedRequiredColumnAddResumeExpression([]),
        "prepared-repair" => plan.RenderPreparedRepairPrecondition([]),
        "prepared-repair-resolved" => plan.RenderPreparedRepairPrecondition([], "FALSE", "TRUE"),
        "prepared-transition" => plan.RenderPreparedTransitionInvariantExpression([]),
        "prepared-data-required" => plan.RenderPreparedDataProbeRequiredExpression([]),
        "prepared-data-blocked" => plan.RenderPreparedDataProbeBlockedExpression([]),
        "prepared-mutation" => plan.RenderPreparedMutationSql([]),
        "prepared-nullable" => plan.RenderPreparedNullabilityColumnExpression([]),
        "prepared-null-repair" => plan.RenderPreparedNullabilityRepairInvariantExpression([]),
        "prepared-null-blocked" => plan.RenderPreparedNullabilityDataProbeBlockedExpression([]),
        _ => throw new ArgumentOutOfRangeException(nameof(consumer), consumer, "Unknown runtime-plan consumer."),
    };

    /// <summary>Rejects accidental parameter materialization in a synthetic value-free template.</summary>
    private static string UnexpectedValue(MySqlCatalogParameterValue value) =>
        throw new InvalidOperationException("The marker-only fixture has no catalog values.");
}
