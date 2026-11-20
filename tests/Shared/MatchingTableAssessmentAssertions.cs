namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Checks the complete ordered EnsureTable contract used by the three live catalog-noise fixtures.</summary>
internal static class MatchingTableAssessmentAssertions
{
    /// <summary>
    /// Rejects incomplete, reordered or changed assessments before a per-item success check can pass vacuously.
    /// </summary>
    /// <param name="assessments">The actual final report assessments from the live runner.</param>
    /// <param name="expectedTableCount">
    /// The independently authored fixture size, not the result sequence length.
    /// </param>
    /// <param name="tablePrefix">The deterministic fixture prefix whose ordinal suffix identifies each table.</param>
    internal static void AssertComplete(
        IReadOnlyList<SafeMigrationAssessment> assessments,
        int expectedTableCount,
        string tablePrefix
    )
    {
        Assert.True(expectedTableCount > 0);
        Assert.Equal(expectedTableCount, assessments.Count);

        // WHY: Fixture ordinal, table identity and decision are one ordered contract, not independent sets.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < expectedTableCount; ordinal++)
        {
            var assessment = assessments[ordinal];
            var expectedName = tablePrefix + ordinal.ToString("D4", CultureInfo.InvariantCulture);
            Assert.Equal(ordinal, assessment.Ordinal);
            Assert.Equal(typeof(SafeMigrationOperation).FullName, assessment.OperationType);
            Assert.True(assessment.IsSafeOperation);
            Assert.Equal(SafeMigrationOperationKind.EnsureTable, assessment.OperationKind);
            Assert.Equal(expectedName, assessment.ObjectName);
            Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
            Assert.True(assessment.PostconditionSatisfied);
        }
    }
}
