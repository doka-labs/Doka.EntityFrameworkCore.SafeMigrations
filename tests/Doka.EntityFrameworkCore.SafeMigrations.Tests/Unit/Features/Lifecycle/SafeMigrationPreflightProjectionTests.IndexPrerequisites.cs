namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>A complete projected table cannot supply an absent index dependency.</summary>
    /// <param name="dependency">The key, include, expression or predicate dependency under test.</param>
    [Theory]
    [InlineData("key")]
    [InlineData("include")]
    [InlineData("expression")]
    [InlineData("filter")]
    public void CreatedTableDoesNotProveAbsentIndexDependency(
        string dependency
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("nodes", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition));

        var definition = IndexWithDependency(dependency);
        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action);
    }

    /// <summary>A complete projected table supplies present index dependencies.</summary>
    /// <param name="dependency">The key, include, expression or predicate dependency under test.</param>
    [Theory]
    [InlineData("key")]
    [InlineData("include")]
    [InlineData("expression")]
    [InlineData("filter")]
    public void CreatedTableProvesPresentIndexDependency(
        string dependency
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("nodes", [Column("id"), Column("predicate")]),
                SafeMigrationTableMode.StrictDefinition));

        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(IndexWithDependency(dependency)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Provider-recognized raw predicates require ordered column evidence.</summary>
    /// <param name="created">Whether the earlier table operation creates a new table.</param>
    /// <param name="present">Whether that table contains the predicate column.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ProviderIndexDependenciesRequireOrderedColumnProof(
        bool created,
        bool present
    )
    {
        // Arrange
        var source = new IndexPrerequisiteSource();
        var projection = new SafeMigrationPreflightProjection(indexPrerequisiteSource: source);
        var columns = present
            ? new[] { Column("id"), Column("predicate"), }
            : [Column("id")];

        ObserveAccepted(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("nodes", columns),
                SafeMigrationTableMode.StrictDefinition),
            created ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching);

        var operation = RawFilterIndex();

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.True(source.WasQueried);
        Assert.Equal(
            present ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing,
            analysis.ObservedState);
    }

    /// <summary>An earlier column addition discharges a provider-recognized predicate prerequisite.</summary>
    /// <param name="created">Whether the earlier table operation creates a new table.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderIndexDependencyCanBeAddedEarlier(
        bool created
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(indexPrerequisiteSource: new IndexPrerequisiteSource());
        ObserveAccepted(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("nodes", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition),
            created ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching);
        ObserveAccepted(
            projection,
            new EnsureColumnIntent("nodes", new ExpectedColumnDefinition("predicate", typeof(int), true, "int")),
            SafeMigrationObservedState.Missing);

        var operation = RawFilterIndex();

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>A dropped predicate column cannot retain stale accepted dependency evidence.</summary>
    [Fact]
    public void ProviderIndexDependencyDoesNotSurviveColumnDrop()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(indexPrerequisiteSource: new IndexPrerequisiteSource());
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("nodes", [Column("id"), Column("predicate")]),
                SafeMigrationTableMode.StrictDefinition));
        ObserveAccepted(projection, new DropColumnIntent("predicate", "nodes"), SafeMigrationObservedState.Matching);

        var operation = RawFilterIndex();

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>Invariant provider rejection cannot be discharged by a recognized dependency source.</summary>
    [Fact]
    public void ProviderIndexDependenciesDoNotOverrideInvariantRejection()
    {
        // Arrange
        var source = new IndexPrerequisiteSource();
        var projection = new SafeMigrationPreflightProjection(indexPrerequisiteSource: source);
        var operation = RawFilterIndex();
        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "opaque_sql_expression")
        {
            IsInvariantUnsupported = true,
        };

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Same(live, analysis);
        Assert.False(source.WasQueried);
    }

    private static SafeMigrationOperation RawFilterIndex() => new(
        new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_nodes",
                "nodes",
                [new ExpectedIndexKeyDefinition("id")],
                filter: "predicate IS NULL")),
        SafeMigrationPolicy.ThrowIfDifferent);

    private sealed class IndexPrerequisiteSource : ISafeMigrationIndexPrerequisiteSource
    {
        public bool WasQueried { get; private set; }

        public IReadOnlyList<string> GetIndexPrerequisiteColumns(
            EnsureIndexIntent intent
        )
        {
            WasQueried = true;

            return ["id", "predicate"];
        }
    }

    private static ExpectedIndexDefinition IndexWithDependency(
        string dependency
    ) => dependency switch
    {
        "key" => new ExpectedIndexDefinition("ix_nodes", "nodes", [new ExpectedIndexKeyDefinition("predicate")]),
        "include" => new ExpectedIndexDefinition(
            "ix_nodes",
            "nodes",
            [new ExpectedIndexKeyDefinition("id")],
            includedColumns: ["predicate"]),
        "expression" => new ExpectedIndexDefinition(
            "ix_nodes",
            "nodes",
            [new ExpectedIndexKeyDefinition(structuredExpression: SafeMigrationSql.Identifier("predicate"))]),
        "filter" => new ExpectedIndexDefinition(
            "ix_nodes",
            "nodes",
            [new ExpectedIndexKeyDefinition("id")],
            structuredFilter: SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("predicate"))),
        _ => throw new ArgumentException("Unknown dependency case.", nameof(dependency)),
    };
}
