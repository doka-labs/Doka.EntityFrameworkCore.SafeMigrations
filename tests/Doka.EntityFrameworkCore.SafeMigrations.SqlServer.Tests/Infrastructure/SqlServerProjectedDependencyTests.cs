namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies accepted-operation dependency transitions independently of the live catalog.</summary>
public sealed class SqlServerProjectedDependencyTests
{
    /// <summary>An accepted inline FK prevents dropping its new principal table.</summary>
    [Fact]
    public void InlineForeignKeyPreventsLaterParentDrop()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        var child = new ExpectedTableDefinition("child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            foreignKeys: [new ExpectedForeignKeyDefinition(
                "FK_child_parent", "child", ["ParentId"], "parent", ["Id"])]);

        Accept(graph, new EnsureTableIntent(child, SafeMigrationTableMode.StrictDefinition));
        var drop = new SafeMigrationOperation(new DropTableIntent("parent"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(drop, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal("projected_incoming_foreign_key", result.Code);
    }

    /// <summary>An accepted dependent drop removes the incoming principal dependency.</summary>
    [Fact]
    public void ChildDropPermitsLaterParentDrop()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        Accept(graph, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_child_parent", "child", ["ParentId"], "parent", ["Id"])));
        Accept(graph, new DropTableIntent("child"), SafeMigrationObservedState.Matching);
        var drop = new SafeMigrationOperation(new DropTableIntent("parent"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(drop, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState);
    }

    /// <summary>Only an incoming-FK-specific immutable conflict can be discharged after an accepted removal.</summary>
    [Theory]
    [InlineData("incoming_foreign_key_dependency", SafeMigrationObservedState.Matching)]
    [InlineData("classified_different", SafeMigrationObservedState.Different)]
    public void ChildDropReconcilesOnlyIncomingForeignKeyConflict(
        string code,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        Accept(graph, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_child_parent", "child", ["ParentId"], "parent", ["Id"])));
        Accept(graph, new DropTableIntent("child"), SafeMigrationObservedState.Matching);
        var drop = new SafeMigrationOperation(new DropTableIntent("parent"), SafeMigrationPolicy.ThrowIfDifferent);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, code);

        // Act
        var result = graph.ValidateProjectedOperation(drop, live, columns);

        // Assert
        Assert.Equal(expected, result.ObservedState);
    }

    /// <summary>A surviving incoming FK cannot be discharged by the immutable conflict classifier.</summary>
    [Fact]
    public void IncomingForeignKeyConflictRetainsSurvivingEdge()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        Accept(graph, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_child_parent", "child", ["ParentId"], "parent", ["Id"])));
        var drop = new SafeMigrationOperation(new DropTableIntent("parent"), SafeMigrationPolicy.ThrowIfDifferent);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "incoming_foreign_key_dependency");

        // Act
        var result = graph.ValidateProjectedOperation(drop, live, columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal("projected_incoming_foreign_key", result.Code);
    }

    /// <summary>The authored parent definition cannot preserve a key removed by an accepted operation.</summary>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(true, SafeMigrationObservedState.Missing)]
    public void InlineForeignKeyRequiresSurvivingProjectedCandidateKey(
        bool alternateKey,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        var parent = new ExpectedTableDefinition("parent",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")],
            primaryKey: new ExpectedPrimaryKeyDefinition("PK_parent", "parent", ["Id"]),
            uniqueConstraints: alternateKey
                ? [new ExpectedUniqueConstraintDefinition("UQ_parent", "parent", ["Id"])] : []);

        Accept(graph, new EnsureTableIntent(parent, SafeMigrationTableMode.StrictDefinition));
        Accept(graph, new DropPrimaryKeyIntent("PK_parent", "parent"), SafeMigrationObservedState.Matching);
        Accept(graph, new DropTableIntent("child"), SafeMigrationObservedState.Matching);
        var child = new ExpectedTableDefinition("child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            foreignKeys: [new ExpectedForeignKeyDefinition(
                "FK_child_parent", "child", ["ParentId"], "parent", ["Id"])]);

        var ensure = new SafeMigrationOperation(new EnsureTableIntent(child, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(ensure, Live(SafeMigrationObservedState.Missing), columns);

        // Assert
        Assert.Equal(expected, result.ObservedState);
    }

    /// <summary>Opaque provider effects invalidate inline dependency proof as well as standalone FK proof.</summary>
    [Fact]
    public void OpaqueOperationCannotReuseInlineDependencyEvidence()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        var parent = new ExpectedTableDefinition("parent",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")],
            primaryKey: new ExpectedPrimaryKeyDefinition("PK_parent", "parent", ["Id"]));

        Accept(graph, new EnsureTableIntent(parent, SafeMigrationTableMode.StrictDefinition));
        graph.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1;" });
        var child = new ExpectedTableDefinition("child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            foreignKeys: [new ExpectedForeignKeyDefinition(
                "FK_child_parent", "child", ["ParentId"], "parent", ["Id"])]);

        var ensure = new SafeMigrationOperation(new EnsureTableIntent(child, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(ensure, Live(SafeMigrationObservedState.Missing), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.True(result.IsOpaqueProjectionUnknown);
    }

    /// <summary>An accepted FK rename target remains the same physical dependent table.</summary>
    [Fact]
    public void TableRenameRetainsIncomingDependency()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        Accept(graph, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_child_parent", "child", ["ParentId"], "parent", ["Id"])));
        Accept(graph, new RenameTableIntent("parent", "renamed_parent"), SafeMigrationObservedState.Matching);
        var drop = new SafeMigrationOperation(
            new DropTableIntent("renamed_parent"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(drop, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
    }

    /// <summary>A drop removes a previously accepted cascade before a replacement is assessed.</summary>
    [Fact]
    public void DroppedCascadeDoesNotCreateFalseReplacementPath()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        Accept(graph, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_first", "child", ["ParentId"], "parent", ["Id"], onDelete: ReferentialAction.Cascade)));
        Accept(graph, new DropForeignKeyIntent("FK_first", "child"), SafeMigrationObservedState.Matching);
        var replacement = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_second", "child", ["ParentId"], "parent", ["Id"], onDelete: ReferentialAction.Cascade)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(replacement,
            new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationRepairCapability.None, false, "foreign_key_cascade_topology"), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState);
    }

    /// <summary>A rejected operation cannot introduce an edge into a subsequent valid assessment.</summary>
    [Fact]
    public void RejectedForeignKeyDoesNotAuthorizeOrBlockLaterCandidate()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_rejected", "child", ["ParentId"], "parent", ["Id"], onDelete: ReferentialAction.Cascade)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var rejected = Live(SafeMigrationObservedState.Different);

        graph.ObserveAcceptedOperation(operation, rejected, rejected,
            SafeMigrationDecisionPlanner.Plan(
                operation.Intent.Kind, rejected.ObservedState, operation.Policy, rejected.RepairCapability));
        var later = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_later", "child", ["ParentId"], "parent", ["Id"], onDelete: ReferentialAction.Cascade)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(later, Live(SafeMigrationObservedState.Missing), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState);
    }

    /// <summary>The provider's physical-width rejection cannot be bypassed by new parent/key projection.</summary>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(true, SafeMigrationObservedState.Missing)]
    public void InlineWidthProofIsRequiredAfterProjectedParentCreation(
        bool supported,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph((_, _) => supported);
        var columns = new EmptyColumns();
        var parent = new ExpectedTableDefinition("parent",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")],
            primaryKey: new ExpectedPrimaryKeyDefinition("PK_parent", "parent", ["Id"]));

        Accept(graph, new EnsureTableIntent(parent, SafeMigrationTableMode.StrictDefinition));
        Accept(graph, new DropTableIntent("child"), SafeMigrationObservedState.Matching);
        var child = new ExpectedTableDefinition("child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            foreignKeys: [new ExpectedForeignKeyDefinition(
                "FK_child_parent", "child", ["ParentId"], "parent", ["Id"])]);

        var ensure = new SafeMigrationOperation(new EnsureTableIntent(child, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(ensure, Live(SafeMigrationObservedState.Missing), columns);

        // Assert
        Assert.Equal(expected, result.ObservedState);
    }

    /// <summary>Accepted schema removal overrides stale matching existence for a later ensure.</summary>
    [Fact]
    public void DroppedSchemaMakesLaterEnsureMissing()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        Accept(graph, new DropSchemaIntent("application"), SafeMigrationObservedState.Matching);
        var ensure = new SafeMigrationOperation(
            new EnsureSchemaIntent("application"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(ensure, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState);
    }

    /// <summary>A table introduced after an accepted schema ensure blocks a stale-live-missing schema drop.</summary>
    [Fact]
    public void NewlyPopulatedSchemaCannotBeDropped()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        Accept(graph, new EnsureSchemaIntent("application"));
        Accept(graph, new EnsureTableIntent(new ExpectedTableDefinition("items",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], schema: "application"),
            SafeMigrationTableMode.StrictDefinition));
        var drop = new SafeMigrationOperation(
            new DropSchemaIntent("application"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(drop, Live(SafeMigrationObservedState.Missing), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal("projected_schema_not_empty", result.Code);
    }

    /// <summary>Opaque mutations cannot preserve trusted schema presence or emptiness.</summary>
    [Fact]
    public void OpaqueOperationInvalidatesSchemaEvidence()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        var columns = new EmptyColumns();
        graph.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1;" });
        var drop = new SafeMigrationOperation(
            new DropSchemaIntent("application"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = graph.ValidateProjectedOperation(drop, Live(SafeMigrationObservedState.Matching), columns);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.True(result.IsOpaqueProjectionUnknown);
    }

    private static void Accept(
        SqlServerProjectedDependencyGraph graph,
        SafeMigrationIntent intent,
        SafeMigrationObservedState state = SafeMigrationObservedState.Missing
    )
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var analysis = Live(state);

        graph.ObserveAcceptedOperation(operation, analysis, analysis,
            SafeMigrationDecisionPlanner.Plan(
                intent.Kind, state, operation.Policy, SafeMigrationRepairCapability.None));
    }

    private static SafeMigrationProviderAnalysis Live(
        SafeMigrationObservedState state
    )
        => new(
            state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching, "classified_test");

    private sealed class EmptyColumns : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? definition
        )
        {
            definition = null;

            return false;
        }
    }
}
