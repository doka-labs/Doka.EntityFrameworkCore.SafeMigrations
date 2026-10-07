namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Protects the named five-marker contract across exact-size and direct-append rendering.</summary>
public sealed class PostgreSqlPlaceholderReplacementContractTests
{
    /// <summary>Resolves every marker explicitly, independent of occurrence order and repeated use.</summary>
    /// <param name="order">The forward, reverse, or repeated interleaved marker order.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AllMarkers_PreserveExactStateAndRepairRendering(
        int order
    )
    {
        // Arrange
        var indexes = order switch
        {
            0 => new[] { 0, 1, 2, 3, 4 },
            1 => [4, 3, 2, 1, 0],
            2 => [3, 0, 4, 2, 1, 4, 0, 3, 1, 2],
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };

        var markers = Markers();
        var values = new[]
        {
            "cached_data_blocked", "fresh_transition", "doka_nullability_blocked",
            "doka_nullability_repair_eligible", "verified_matching",
        };

        var template = "prefix[" + string.Join(" | ", indexes.Select(index => markers[index])) + "]suffix";
        var expected = "prefix[" + string.Join(" | ", indexes.Select(index => values[index])) + "]suffix";
        var plan = CreatePlan(template);
        var stateBuilder = new StringBuilder("before:");
        var repairBuilder = new StringBuilder("before:");

        // Act
        var state = plan.RenderStateExpression(values[0], values[1]);
        var repair = plan.RenderRepairPrecondition(values[0], values[1]);
        plan.AppendStateExpression(stateBuilder, values[0], values[1], values[4]);
        plan.AppendRepairPrecondition(repairBuilder, values[0], values[1]);

        // Assert
        Assert.Equal(expected, state);
        Assert.Equal(expected, repair);
        Assert.Equal("before:" + expected, stateBuilder.ToString());
        Assert.Equal("before:" + expected, repairBuilder.ToString());
    }

    /// <summary>Leaves all five markers untouched inside valid escaped names and SQL string values.</summary>
    [Fact]
    public void AllMarkers_PreserveQuotedOccurrencesBesideStructuralOccurrences()
    {
        // Arrange
        var markers = Markers();
        var quoted = string.Join(" | ", markers.Select(static marker =>
            "'escaped ''" + marker + "' || \"escaped\"\"" + marker + "\"")) + " | ";

        var template = quoted + string.Join(" | ", markers);
        var expected = quoted + "cached_data_blocked | fresh_transition | doka_nullability_blocked"
            + " | doka_nullability_repair_eligible | verified_matching";

        var plan = CreatePlan(template);
        var appended = new StringBuilder();

        // Act
        var rendered = plan.RenderStateExpression("cached_data_blocked", "fresh_transition");
        plan.AppendStateExpression(appended, "cached_data_blocked", "fresh_transition", "verified_matching");

        // Assert
        Assert.Equal(expected, rendered);
        Assert.Equal(expected, appended.ToString());
    }

    /// <summary>Rejects malformed quoted templates instead of treating embedded markers as structural proofs.</summary>
    /// <param name="quote">The unterminated SQL string or identifier delimiter.</param>
    [Theory]
    [InlineData("'")]
    [InlineData("\"")]
    public void UnterminatedQuotedMarker_IsRejectedByBothRenderingPaths(
        string quote
    )
    {
        // Arrange
        var plan = CreatePlan(quote + PostgreSqlSafeMigrationRuntimePlan.MatchingPlaceholder);
        var appended = new StringBuilder();

        // Act
        var materializedError = Record.Exception(() => plan.RenderStateExpression("blocked", "transition"));
        var appendedError = Record.Exception(() => plan.AppendStateExpression(
            appended, "blocked", "transition", "verified_matching"));

        // Assert
        Assert.IsType<InvalidOperationException>(materializedError);
        Assert.IsType<InvalidOperationException>(appendedError);
    }

    /// <summary>Rejects marker kinds outside the scanner's explicit contract rather than returning matching.</summary>
    /// <param name="kind">An unsupported marker-kind value.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void UnknownMarkerKind_IsRejected(
        int kind
    )
    {
        // Arrange
        // WHY: Invalid kinds cannot be authored through the scanner. Exercise
        // the private contract directly so a future catch-all mapping cannot
        // silently reuse target-matching evidence for a new marker.
        var contract = typeof(PostgreSqlSafeMigrationRuntimePlan).GetNestedType(
            "PlaceholderReplacements", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The named PostgreSQL proof contract was not found.");

        var values = Activator.CreateInstance(contract, "data", "transition", "nulls", "repair", "matching")
            ?? throw new InvalidOperationException("The PostgreSQL proof contract could not be created.");

        var resolve = contract.GetMethod("ForKind", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The explicit PostgreSQL proof-kind resolver was not found.");

        // Act
        var error = Record.Exception(() => resolve.Invoke(values, [kind]));

        // Assert
        Assert.IsType<InvalidOperationException>(Assert.IsType<TargetInvocationException>(error).InnerException);
    }

    /// <summary>Reuses the original string when only quoted marker text is present.</summary>
    [Fact]
    public void NoStructuralMarkers_ReusesTemplateWithoutReplacementAllocation()
    {
        // Arrange
        var template = "'" + string.Join(" | ", Markers()) + "'";
        var plan = CreatePlan(template);
        var appended = new StringBuilder();

        // Act
        var rendered = plan.RenderStateExpression("blocked", "transition");
        plan.AppendStateExpression(appended, "blocked", "transition", "verified_matching");

        // Assert
        Assert.Same(template, rendered);
        Assert.Equal(template, appended.ToString());
    }

    /// <summary>Retains allocation-free appends when the destination capacity and proof values are prepared.</summary>
    [Fact]
    public void PreparedAppend_AllFiveMarkersAllocateNoIntermediateStrings()
    {
        // Arrange
        const int iterations = 100;
        var template = string.Join(" | ", Markers());
        var plan = CreatePlan(template);
        const string expected = "cached_data_blocked | fresh_transition | doka_nullability_blocked"
            + " | doka_nullability_repair_eligible | verified_matching";

        var appended = new StringBuilder(expected.Length);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            plan.AppendStateExpression(appended, "cached_data_blocked", "fresh_transition", "verified_matching");
            appended.Clear();
            _ = plan.RenderStateExpression("cached_data_blocked", "fresh_transition");
        }

        var appendedCharacters = 0;
        var materializedCharacters = 0;

        // Act
        var beforeAppend = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            plan.AppendStateExpression(appended, "cached_data_blocked", "fresh_transition", "verified_matching");
            appendedCharacters += appended.Length;
            appended.Clear();
        }

        var appendAllocations = GC.GetAllocatedBytesForCurrentThread() - beforeAppend;
        var beforeMaterialization = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            materializedCharacters += plan.RenderStateExpression("cached_data_blocked", "fresh_transition").Length;
        }

        var materializationAllocations = GC.GetAllocatedBytesForCurrentThread() - beforeMaterialization;

        // Assert
        Assert.Equal(expected.Length * iterations, appendedCharacters);
        Assert.Equal(appendedCharacters, materializedCharacters);
        Assert.Equal(0, appendAllocations);
        Assert.True(materializationAllocations >= (long)expected.Length * sizeof(char) * iterations);
    }

    /// <summary>Creates distinct target and eligibility expressions without connecting to a database.</summary>
    /// <param name="template">The controlled template shared by state and repair rendering.</param>
    /// <returns>A plan with the fresh NULL-proof path enabled.</returns>
    private static PostgreSqlSafeMigrationRuntimePlan CreatePlan(
        string template
    ) => new(template, "verified_matching", SafeMigrationRepairCapability.Safe, template)
    {
        NullabilityDataProbe = new PostgreSqlSafeMigrationNullabilityDataProbe(
            "complete_contract", "repair_eligible", "row_probe", "proof_rows"),
    };

    /// <summary>Lists every structural marker in the scanner's explicit kind order.</summary>
    /// <returns>The complete five-marker contract.</returns>
    private static string[] Markers() =>
    [
        PostgreSqlSafeMigrationRuntimePlan.DataProbePlaceholder,
        PostgreSqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
        PostgreSqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
        PostgreSqlSafeMigrationRuntimePlan.NullabilityRepairInvariantPlaceholder,
        PostgreSqlSafeMigrationRuntimePlan.MatchingPlaceholder,
    ];
}
