namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Proves the live catalog assertion accepts its real fixture contract and rejects vacuous evidence.</summary>
public sealed class MatchingTableAssessmentAssertionsTests
{
    /// <summary>One hundred independently identified matching table assessments satisfy the complete contract.</summary>
    [Fact]
    public void CompleteOrderedMatchingTablesAreAccepted()
    {
        // Arrange
        var assessments = CreateAssessments();

        // Act
        var failure = Record.Exception(() =>
            MatchingTableAssessmentAssertions.AssertComplete(assessments, 100, "expected_perf_"));

        // Assert
        Assert.Null(failure);
    }

    /// <summary>Malformed cardinality, position, identity or decision cannot satisfy the live fixture assertion.</summary>
    /// <param name="fault">The independently introduced result defect.</param>
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("swapped")]
    [InlineData("duplicate_ordinal")]
    [InlineData("wrong_name")]
    [InlineData("wrong_action")]
    public void IncompleteOrChangedEvidenceIsRejected(
        string fault
    )
    {
        // Arrange
        var assessments = CreateAssessments();
        if (fault == "empty")
        {
            assessments = [];
        }
        else if (fault == "truncated")
        {
            assessments = assessments[..^1];
        }
        else if (fault == "swapped")
        {
            (assessments[0], assessments[1]) = (assessments[1], assessments[0]);
        }
        else
        {
            assessments[1] = CreateAssessment(
                fault == "duplicate_ordinal"
                    ? 0
                    : 1,
                fault == "wrong_name"
                    ? "other_table"
                    : "expected_perf_0001",
                fault == "wrong_action"
                    ? SafeMigrationAction.Apply
                    : SafeMigrationAction.NoOp);
        }

        // Act
        var failure = Record.Exception(() =>
            MatchingTableAssessmentAssertions.AssertComplete(assessments, 100, "expected_perf_"));

        // Assert
        Assert.IsAssignableFrom<XunitException>(failure);
    }

    /// <summary>Builds fixture evidence independently from the assertion's enumeration.</summary>
    private static SafeMigrationAssessment[] CreateAssessments() => Enumerable
        .Range(0, 100)
        .Select(ordinal => CreateAssessment(
            ordinal,
            "expected_perf_" + ordinal.ToString("D4", CultureInfo.InvariantCulture),
            SafeMigrationAction.NoOp))

        // WHY: Retain explicit fluent materialization and its independently owned result snapshot.
        // ReSharper disable once UseCollectionExpression
        .ToArray();

    /// <summary>Creates one exact public assessment so each negative case changes only its selected axis.</summary>
    private static SafeMigrationAssessment CreateAssessment(
        int ordinal,
        string name,
        SafeMigrationAction action
    ) => new(
        ordinal,
        typeof(SafeMigrationOperation).FullName!,
        true,
        SafeMigrationOperationKind.EnsureTable,
        name,
        SafeMigrationObservedState.Matching,
        action,
        true,
        "matching");
}
