namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies event-aware SQL Server cascade topology without a live server.</summary>
public sealed class SqlServerForeignKeyTopologyTests
{
    /// <summary>Allows a single cascading branch and unrelated action trees.</summary>
    [Theory]
    [InlineData(ReferentialAction.Cascade)]
    [InlineData(ReferentialAction.SetNull)]
    [InlineData(ReferentialAction.SetDefault)]
    public void SingleCascadeAndUnrelatedBranchesAreValid(
        ReferentialAction action
    )
    {
        // Arrange
        SqlServerProjectedDependencyGraph.ForeignKeyEdge[] graph =
        [
            new(2, 1, action, ReferentialAction.NoAction),
            new(4, 3, ReferentialAction.Cascade, ReferentialAction.NoAction),
        ];

        // Act
        var valid = SqlServerProjectedDependencyGraph.HasValidTopology(graph);

        // Assert
        Assert.True(valid);
    }

    /// <summary>Allows a self-reference with no cascading action.</summary>
    [Fact]
    public void NonCascadingSelfReferenceIsValid()
    {
        // Arrange
        SqlServerProjectedDependencyGraph.ForeignKeyEdge[] graph =
        [
            new(1, 1, ReferentialAction.NoAction, ReferentialAction.NoAction),
        ];

        // Act
        var valid = SqlServerProjectedDependencyGraph.HasValidTopology(graph);

        // Assert
        Assert.True(valid);
    }

    /// <summary>Rejects duplicate physical-table paths for DELETE and UPDATE separately.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoConstraintsToSameDependentRejectMultiplePaths(
        bool delete
    )
    {
        // Arrange
        var action = delete ? ReferentialAction.Cascade : ReferentialAction.NoAction;
        var update = delete ? ReferentialAction.NoAction : ReferentialAction.Cascade;
        SqlServerProjectedDependencyGraph.ForeignKeyEdge[] graph =
        [
            new(2, 1, action, update),
            new(2, 1, action, update),
        ];

        // Act
        var valid = SqlServerProjectedDependencyGraph.HasValidTopology(graph);

        // Assert
        Assert.False(valid);
    }

    /// <summary>Rejects a diamond even when its paths have different lengths.</summary>
    [Fact]
    public void DiamondWithUnequalPathLengthsIsInvalid()
    {
        // Arrange
        SqlServerProjectedDependencyGraph.ForeignKeyEdge[] graph =
        [
            new(2, 1, ReferentialAction.Cascade, ReferentialAction.NoAction),
            new(3, 1, ReferentialAction.Cascade, ReferentialAction.NoAction),
            new(4, 2, ReferentialAction.Cascade, ReferentialAction.NoAction),
            new(5, 3, ReferentialAction.Cascade, ReferentialAction.NoAction),
            new(4, 5, ReferentialAction.Cascade, ReferentialAction.NoAction),
        ];

        // Act
        var valid = SqlServerProjectedDependencyGraph.HasValidTopology(graph);

        // Assert
        Assert.False(valid);
    }

    /// <summary>Rejects cascading cycles, including a self-cascade.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CascadingCycleIsInvalid(
        bool self
    )
    {
        // Arrange
        SqlServerProjectedDependencyGraph.ForeignKeyEdge[] graph = self
            ? [new(1, 1, ReferentialAction.Cascade, ReferentialAction.NoAction)]
            : [new(2, 1, ReferentialAction.Cascade, ReferentialAction.NoAction),
                new(1, 2, ReferentialAction.Cascade, ReferentialAction.NoAction)];

        // Act
        var valid = SqlServerProjectedDependencyGraph.HasValidTopology(graph);

        // Assert
        Assert.False(valid);
    }

    /// <summary>Follows DELETE SET NULL into downstream UPDATE cascades.</summary>
    [Fact]
    public void DeleteSetNullAndUpdateCascadeCannotCreateSecondDeletePath()
    {
        // Arrange
        SqlServerProjectedDependencyGraph.ForeignKeyEdge[] graph =
        [
            new(2, 1, ReferentialAction.SetNull, ReferentialAction.NoAction),
            new(3, 2, ReferentialAction.NoAction, ReferentialAction.Cascade),
            new(3, 1, ReferentialAction.Cascade, ReferentialAction.NoAction),
        ];

        // Act
        var valid = SqlServerProjectedDependencyGraph.HasValidTopology(graph);

        // Assert
        Assert.False(valid);
    }

    /// <summary>A matching physical FK skips the runtime catalog graph traversal.</summary>
    [Fact]
    public void RuntimeGraphWalkIsGatedByCandidateAbsence()
    {
        // Arrange
        var foreignKey = new ExpectedForeignKeyDefinition("FK_child_parent", "child", ["ParentId"], "parent", ["Id"],
            onDelete: ReferentialAction.Cascade);

        // Act
        var sql = SqlServerForeignKeySafety.BuildCatalogPreamble([foreignKey], static value => "N'" + value + "'");

        // Assert
        Assert.NotNull(sql);
        Assert.True(sql.IndexOf("IF (NOT EXISTS", StringComparison.Ordinal)
            < sql.IndexOf("DECLARE @doka_fk_edges", StringComparison.Ordinal));
        Assert.Contains("COUNT(*) > 1", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("WITH RECURSIVE", sql, StringComparison.Ordinal);
    }

    /// <summary>Physically safe no-action FKs need no cascade graph preamble.</summary>
    [Fact]
    public void NoActionDoesNotAllocateRuntimeTopologySql()
    {
        // Arrange
        var foreignKey = new ExpectedForeignKeyDefinition("FK_child_parent", "child", ["ParentId"], "parent", ["Id"]);

        // Act
        var sql = SqlServerForeignKeySafety.BuildCatalogPreamble([foreignKey], static value => value);

        // Assert
        Assert.Null(sql);
    }
}
