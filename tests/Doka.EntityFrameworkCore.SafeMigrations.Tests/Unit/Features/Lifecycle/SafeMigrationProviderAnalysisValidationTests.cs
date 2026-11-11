namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies provider-analysis validation independently of runtime enum cache lifetime.</summary>
[Collection(nameof(SafeMigrationDecisionPlannerGarbageCollectionDefinition))]
public sealed class SafeMigrationProviderAnalysisValidationTests
{
    /// <summary>Enumerates the complete declared input domain outside any measured constructor.</summary>
    /// <returns>Every declared observed-state, repair-capability and impact combination.</returns>
    public static TheoryData<SafeMigrationObservedState, SafeMigrationRepairCapability, SafeMigrationOperationalImpact>
        DeclaredInputs()
    {
        var inputs = new TheoryData<SafeMigrationObservedState, SafeMigrationRepairCapability,
            SafeMigrationOperationalImpact>();

        foreach (var state in Enum.GetValues<SafeMigrationObservedState>())
        {
            foreach (var repair in Enum.GetValues<SafeMigrationRepairCapability>())
            {
                foreach (var impact in Enum.GetValues<SafeMigrationOperationalImpact>())
                {
                    inputs.Add(state, repair, impact);
                }
            }
        }

        return inputs;
    }

    /// <summary>Preserves every declared input and independent result ownership after collection.</summary>
    /// <param name="state">The declared observed state.</param>
    /// <param name="repair">The declared repair capability.</param>
    /// <param name="impact">The declared operational impact.</param>
    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    public void DeclaredInputsRemainValidAfterCollection(
        SafeMigrationObservedState state,
        SafeMigrationRepairCapability repair,
        SafeMigrationOperationalImpact impact
    )
    {
        // Arrange
        var original = new SafeMigrationProviderAnalysis(state, repair, true, "validated", impact, differences: null);
        CollectMetadata();

        // Act
        var result = new SafeMigrationProviderAnalysis(state, repair, true, "validated", impact, differences: null);

        // Assert
        Assert.NotSame(original, result);
        Assert.Equal(state, result.ObservedState);
        Assert.Equal(repair, result.RepairCapability);
        Assert.Equal(impact, result.OperationalImpact);
        Assert.True(result.PostconditionSatisfied);
        Assert.Equal("validated", result.Code);
        Assert.Empty(result.Differences);
    }

    /// <summary>Rejects undefined integers on each enum axis, including the first value above its domain.</summary>
    /// <param name="axis">The independently invalid enum argument.</param>
    /// <param name="value">An undefined integer, never an implicitly accepted future value.</param>
    [Theory]
    [InlineData("observedState", -1)]
    [InlineData("observedState", 7)]
    [InlineData("observedState", int.MinValue)]
    [InlineData("observedState", int.MaxValue)]
    [InlineData("repairCapability", -1)]
    [InlineData("repairCapability", 2)]
    [InlineData("repairCapability", int.MinValue)]
    [InlineData("repairCapability", int.MaxValue)]
    [InlineData("operationalImpact", -1)]
    [InlineData("operationalImpact", 3)]
    [InlineData("operationalImpact", int.MinValue)]
    [InlineData("operationalImpact", int.MaxValue)]
    public void UndefinedValuesAreRejectedAfterCollection(
        string axis,
        int value
    )
    {
        // Arrange
        var state = axis == "observedState"
            ? (SafeMigrationObservedState)value
            : SafeMigrationObservedState.Matching;

        var repair = axis == "repairCapability"
            ? (SafeMigrationRepairCapability)value
            : SafeMigrationRepairCapability.None;

        var impact = axis == "operationalImpact"
            ? (SafeMigrationOperationalImpact)value
            : SafeMigrationOperationalImpact.NotApplicable;

        CollectMetadata();

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SafeMigrationProviderAnalysis(state, repair, true, "validated", impact, differences: null));

        // Assert
        Assert.Equal(axis, exception.ParamName);
    }

    /// <summary>Keeps the existing first-failure order when multiple arguments are invalid.</summary>
    /// <param name="state">The observed state, valid only after the first rejection case.</param>
    /// <param name="repair">The repair capability, valid only after the second rejection case.</param>
    /// <param name="code">The diagnostic code, checked before the operational impact.</param>
    /// <param name="parameterName">The first invalid parameter under the existing contract.</param>
    [Theory]
    [InlineData(-1, -1, " ", "observedState")]
    [InlineData(1, -1, " ", "repairCapability")]
    [InlineData(1, 0, " ", "code")]
    [InlineData(1, 0, "validated", "operationalImpact")]
    public void ValidationOrderRemainsUnchanged(
        int state,
        int repair,
        string code,
        string parameterName
    )
    {
        // Arrange
        CollectMetadata();

        // Act
        var exception = Assert.ThrowsAny<ArgumentException>(() => new SafeMigrationProviderAnalysis(
            (SafeMigrationObservedState)state,
            (SafeMigrationRepairCapability)repair,
            true,
            code,
            (SafeMigrationOperationalImpact)(-1),
            differences: null));

        // Assert
        Assert.Equal(parameterName, exception.ParamName);
    }

    /// <summary>Expires weak metadata without relying on unrelated test scheduling or system load.</summary>
    private static void CollectMetadata()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
