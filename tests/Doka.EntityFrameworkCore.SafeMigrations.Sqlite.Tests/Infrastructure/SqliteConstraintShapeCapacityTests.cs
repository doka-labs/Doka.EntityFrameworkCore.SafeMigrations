namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises exact semantic capacity through stack and pooled synchronous scratch boundaries.</summary>
public sealed class SqliteConstraintShapeCapacityTests
{
    /// <summary>Stack/pool strategy does not change allowed or required physical multiplicity.</summary>
    /// <param name="length">The authored vector size around the stack scratch boundary.</param>
    /// <param name="extraDuplicate">Whether a different allowed shape is incorrectly replaced by another A.</param>
    [Theory]
    [InlineData(255, false)]
    [InlineData(256, false)]
    [InlineData(257, false)]
    [InlineData(255, true)]
    [InlineData(256, true)]
    [InlineData(257, true)]
    public void StackAndPoolBoundariesPreserveInjectiveCapacity(int length, bool extraDuplicate)
    {
        // Arrange
        var allowed = Enumerable.Range(0, length).Select(ordinal => ordinal.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        var actual = allowed.ToArray();
        if (extraDuplicate)
        {
            actual[^1] = actual[0];
        }

        // Act
        var matching = SqliteConstraintShapeCapacity.Matches(
            actual, allowed, allowed, static (left, right) => StringComparer.Ordinal.Equals(left, right));

        // Assert
        Assert.Equal(!extraDuplicate, matching);
    }

    /// <summary>Only explicitly coalescing native UNIQUE witnesses may satisfy equivalent required aliases.</summary>
    /// <param name="coalesceRequired">Whether equivalent required shapes share one native physical witness.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredDuplicateCapacityIsExplicitlyProviderBound(bool coalesceRequired)
    {
        // Arrange
        string[] actual = ["A"];
        string[] allowed = ["A", "A"];
        string[] required = ["A", "A"];

        // Act
        var matching = SqliteConstraintShapeCapacity.Matches(actual, allowed, required,
            static (left, right) => StringComparer.Ordinal.Equals(left, right), coalesceRequired);

        // Assert
        Assert.Equal(coalesceRequired, matching);
    }

    /// <summary>Required physical slots remain distinct even when total native capacity is sufficient.</summary>
    /// <param name="coalesceRequired">Whether equivalent required aliases may share their native witness.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredWitnessReuseDependsOnCoalescingRatherThanTotalCapacity(bool coalesceRequired)
    {
        // Arrange
        string[] actual = ["A", "B"];
        string[] allowed = ["A", "B", "A"];
        string[] required = ["A", "A"];

        // WHY: Equal total sizes bypass the early capacity guard, so the
        // non-coalescing negative must exercise required-slot consumption.
        // Act
        var matching = SqliteConstraintShapeCapacity.Matches(actual, allowed, required,
            static (left, right) => StringComparer.Ordinal.Equals(left, right), coalesceRequired);

        // Assert
        Assert.Equal(coalesceRequired, matching);
    }

    /// <summary>Required UNIQUE coalescing never authorizes reuse of an allowed physical slot.</summary>
    /// <param name="coalesceRequired">Whether equivalent required aliases may share their native witness.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllowedCapacityRemainsInjectiveWhenRequiredShapesCoalesce(bool coalesceRequired)
    {
        // Arrange
        string[] actual = ["A", "A"];
        string[] allowed = ["A", "B"];
        string[] required = ["A"];

        // Act
        var matching = SqliteConstraintShapeCapacity.Matches(actual, allowed, required,
            static (left, right) => StringComparer.Ordinal.Equals(left, right), coalesceRequired);

        // Assert
        Assert.False(matching);
    }

    /// <summary>Coalescing cannot substitute a different required shape with an existing equivalent alias.</summary>
    /// <param name="coalesceRequired">Whether equivalent required aliases may share their native witness.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredCoalescingStillRequiresEveryDistinctShape(bool coalesceRequired)
    {
        // Arrange
        string[] actual = ["A", "A"];
        string[] allowed = ["A", "A", "B"];
        string[] required = ["A", "B"];

        // Act
        var matching = SqliteConstraintShapeCapacity.Matches(actual, allowed, required,
            static (left, right) => StringComparer.Ordinal.Equals(left, right), coalesceRequired);

        // Assert
        Assert.False(matching);
    }
}
