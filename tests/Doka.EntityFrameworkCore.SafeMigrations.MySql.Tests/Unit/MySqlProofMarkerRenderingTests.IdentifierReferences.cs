namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProofMarkerRenderingTests
{
    private const string IdentifierMarker = "__DOKA_SM_IDENTIFIER_REFERENCE_0__";
    private const string IdentifierScope = "0123456789abcdef0123456789abcdef";

    /// <summary>Every ordinary, prepared and raw-status consumer owns the same native syntax proof binding.</summary>
    /// <param name="consumer">The runtime-plan expression entry point.</param>
    /// <param name="binding">The native completed result, runtime variable, or absent proof.</param>
    /// <param name="expected">The independently completed binding or fail-closed FALSE.</param>
    [Theory]
    [MemberData(nameof(BinaryRenderCases))]
    public void AllConsumersBindIdentifierProofs(string consumer, string? binding, string expected)
    {
        // Arrange
        var plan = CreateIdentifierPlan(IdentifierMarker, binding);
        var expectedSql = consumer switch
        {
            "required-resume" => "(" + expected + ") = 'staged'",
            "prepared-required-resume" => "((" + expected + ")) = 'staged'",
            "prepared-data-blocked" => "EXISTS(SELECT 1 FROM `proof_items` WHERE " + expected
                + " IS NOT NULL AND CHAR_LENGTH(" + expected + ") > 8 LIMIT 1)",
            "index-identity" => "CASE WHEN (" + expected + ") THEN 'matching' WHEN (" + expected
                + ") THEN 'different' ELSE 'missing' END",
            _ => expected,
        };

        // Act
        var rendered = Render(plan, consumer);

        // Assert
        Assert.Equal(expectedSql, rendered);
        Assert.DoesNotContain(IdentifierMarker, rendered, StringComparison.Ordinal);
    }

    /// <summary>Early guard diagnostics can use identifier parsing because acquisition precedes that guard.</summary>
    [Fact]
    public void GuardDiagnosticsUseCompletedIdentifierFactWithoutBorrowingLaterBinaryEvidence()
    {
        // Arrange
        var available = CreateIdentifierPlan(IdentifierMarker, "FALSE");
        var unavailable = available with { ClassificationCodeExpression = IdentifierMarker + " AND " + BinaryMarker };

        // Act
        var availableSql = available.RenderStateEvaluationGuardClassificationCodeExpression(UnexpectedValue);
        var unavailableSql = unavailable.RenderStateEvaluationGuardClassificationCodeExpression(UnexpectedValue);

        // Assert
        Assert.Equal("FALSE", availableSql);
        Assert.Equal("NULL", unavailableSql);
    }

    /// <summary>Missing proof contracts and malformed ordinals cannot escape any replacement funnel.</summary>
    /// <param name="token">A malformed token or an ordinal not owned by the operation.</param>
    [Theory]
    [InlineData("__DOKA_SM_IDENTIFIER_REFERENCE_")]
    [InlineData("__DOKA_SM_IDENTIFIER_REFERENCE_-1__")]
    [InlineData("__DOKA_SM_IDENTIFIER_REFERENCE_0_")]
    [InlineData("__DOKA_SM_IDENTIFIER_REFERENCE_0x__")]
    [InlineData("__DOKA_SM_IDENTIFIER_REFERENCE_00__")]
    [InlineData("__DOKA_SM_IDENTIFIER_REFERENCE_1__")]
    [InlineData("__DOKA_SM_IDENTIFIER_REFERENCE_2147483648__")]
    public void InvalidIdentifierProofTokensFailClosed(string token)
    {
        // Arrange
        var plan = CreateIdentifierPlan(token, "TRUE");

        // Act
        var exception = Record.Exception(() => plan.RenderPreparedPostcondition([]));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>A syntactically valid ordinal still requires the exact operation-owned descriptor.</summary>
    [Fact]
    public void MissingIdentifierDescriptorIsNotTreatedAsRuntimeEvidence()
    {
        // Arrange
        var plan = CreateIdentifierPlan(IdentifierMarker, "TRUE") with { IdentifierReferenceProofs = [] };

        // Act
        var exception = Record.Exception(() => plan.RenderStateExpression(UnexpectedValue));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Literal and delimited-name payloads are not structural references, even without descriptors.</summary>
    [Fact]
    public void QuotedIdentifierMarkerPayloadsRemainUnchanged()
    {
        // Arrange
        var sql = "'a''" + IdentifierMarker + "' AND `b``" + IdentifierMarker + "`";
        var plan = CreateIdentifierPlan(sql, "TRUE") with { IdentifierReferenceProofs = [] };

        // Act
        var rendered = plan.RenderPreparedPostcondition([]);

        // Assert
        Assert.Same(sql, rendered);
    }

    /// <summary>Supplies one exact reference-bound descriptor without an unrelated default proof.</summary>
    private static MySqlSafeMigrationRuntimePlan CreateIdentifierPlan(string sql, string? binding) =>
        CreatePlan(sql, null) with
        {
            BinaryDefaultProofs = [],
            IdentifierReferenceProofs = [new MySqlSafeMigrationIdentifierReferenceProof(
                IdentifierMarker, "id", IdentifierScope, binding)],
        };
}
