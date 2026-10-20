namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Only complete accepted generated sets can prove nondependency, using actual identifier roles.</summary>
    /// <param name="shape">The complete generated-member shape.</param>
    /// <param name="expected">The bounded dependency result.</param>
    [Theory]
    [InlineData("dependent", true)]
    [InlineData("unrelated", false)]
    [InlineData("literal", false)]
    [InlineData("empty", false)]
    [InlineData("raw", null)]
    [InlineData("nested_opaque", null)]
    public void CompleteAcceptedOwnerProvidesGeneratedDependencyTriState(string shape, bool? expected)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var columns = new List<ExpectedColumnDefinition> { Column("value"), Column("other"), };
        if (shape != "empty")
        {
            columns.Add(shape == "raw"
                ? new ExpectedColumnDefinition("derived", typeof(int), true, "int", computedColumnSql: "opaque(value)")
                : GeneratedDependencyColumn(shape switch
                {
                    "dependent" => SafeMigrationSql.Identifier("VALUE"),
                    "literal" => SafeMigrationSql.Literal("value"),
                    "nested_opaque" => SafeMigrationSql.Binary(SafeMigrationSql.Identifier("other"),
                        SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Opaque("opaque(value)")),
                    _ => SafeMigrationSql.Identifier("other"),
                }));
        }

        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", columns),
            SafeMigrationTableMode.StrictDefinition));

        // Act
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.OrdinalIgnoreCase);

        // Assert
        Assert.Equal(expected, dependency);
    }

    /// <summary>Rejected, existence-only and stale owners cannot acquire complete generated-set authority.</summary>
    /// <param name="boundary">The unavailable complete-owner proof.</param>
    [Theory]
    [InlineData("rejected")]
    [InlineData("existence_different")]
    [InlineData("existence_matching")]
    [InlineData("raw")]
    [InlineData("drop_owner")]
    public void IncompleteOrExpiredOwnerCannotProveGeneratedNondependency(string boundary)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition("items", [Column("value"), Column("other")]);
        var operation = new SafeMigrationOperation(
            new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            boundary.StartsWith("existence", StringComparison.Ordinal)
                ? SafeMigrationPolicy.ExistenceOnly : SafeMigrationPolicy.ThrowIfDifferent);

        var state = boundary switch
        {
            "rejected" or "existence_different" => SafeMigrationObservedState.Different,
            "existence_matching" => SafeMigrationObservedState.Matching,
            _ => SafeMigrationObservedState.Missing,
        };

        var live = Live(state);
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);
        if (boundary == "raw")
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;", });
        }
        else if (boundary == "drop_owner")
        {
            Apply(projection, new DropTableIntent("items"));
        }

        // Act
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.OrdinalIgnoreCase);

        // Assert
        Assert.Null(dependency);
    }

    /// <summary>Rejected generated additions do not contaminate accepted complete owners.</summary>
    /// <param name="accepted">Whether the provider permits the generated addition.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedDependencyQueryObservesOnlyAcceptedEarlierOperations(bool accepted)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("value")]),
            SafeMigrationTableMode.StrictDefinition));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items",
            GeneratedDependencyColumn(SafeMigrationSql.Identifier("value"))), SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(accepted ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Unsupported);
        var analysis = accepted ? projection.Project(operation, live) : live;
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);

        // Act
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.Equal(accepted, dependency);
    }

    /// <summary>A newly recreated owner cannot inherit the removed owner's generated dependencies.</summary>
    [Fact]
    public void GeneratedDependencyQueryUsesCurrentOwnerLifetimeOnly()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items",
            [Column("value"), GeneratedDependencyColumn(SafeMigrationSql.Identifier("value"))]),
            SafeMigrationTableMode.StrictDefinition));

        Apply(projection, new DropTableIntent("items"));
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("value")]),
            SafeMigrationTableMode.StrictDefinition));

        // Act
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.False(dependency);
    }

    /// <summary>A current sparse accepted generated addition proves a dependency, never complete absence.</summary>
    /// <param name="references">Whether the accepted generated member references the requested source.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseAcceptedGeneratedMemberCanOnlyProvePositiveDependency(bool references)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("value")),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("other")),
            SafeMigrationObservedState.Matching);

        Apply(projection, new EnsureColumnIntent("items", GeneratedDependencyColumn(
            SafeMigrationSql.Identifier(references ? "value" : "other"))));

        // Act
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.Equal(references ? true : null, dependency);
    }

    /// <summary>Complete generated sets survive known member effects without proving physical layout or rows.</summary>
    /// <param name="change">The accepted complete-owner transition or uncertainty boundary.</param>
    /// <param name="expected">The surviving generated dependency proof.</param>
    [Theory]
    [InlineData("matching", false)]
    [InlineData("drop_generated", false)]
    [InlineData("drop_unrelated", true)]
    [InlineData("add_dependent", true)]
    [InlineData("add_unrelated", false)]
    [InlineData("raw", null)]
    [InlineData("provider_unknown", null)]
    [InlineData("existence", null)]
    public void AcceptedCompleteGeneratedSetSurvivesOnlyKnownMemberEffects(string change, bool? expected)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var columns = new List<ExpectedColumnDefinition> { Column("value"), Column("other"), };
        if (!change.StartsWith("add", StringComparison.Ordinal))
        {
            columns.Add(GeneratedDependencyColumn(SafeMigrationSql.Identifier(
                change.StartsWith("drop", StringComparison.Ordinal) ? "value" : "other")));
        }

        var intent = new EnsureTableIntent(new ExpectedTableDefinition("items", columns),
            SafeMigrationTableMode.StrictDefinition);

        var operation = new SafeMigrationOperation(intent, change == "existence"
            ? SafeMigrationPolicy.ExistenceOnly : SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Matching);
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(intent.Kind, analysis.ObservedState, operation.Policy,
            analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);
        switch (change)
        {
            case "drop_generated":
                Apply(projection, new DropColumnIntent("derived", "items"));
                break;
            case "drop_unrelated":
                Apply(projection, new DropColumnIntent("other", "items"));
                break;
            case "add_dependent":
            case "add_unrelated":
                Apply(projection, new EnsureColumnIntent("items", GeneratedDependencyColumn(
                    SafeMigrationSql.Identifier(change == "add_dependent" ? "value" : "other"))));
                break;
            case "raw":
                projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;", });
                break;
            case "provider_unknown":
                var provider = new AddColumnOperation { Table = "items", Name = "outside", ClrType = typeof(int), };
                provider.AddAnnotation("Test:UnknownMetadataEffect", true);
                projection.ObserveProviderPostcondition(provider);
                break;
        }

        // Act
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);
        var hasState = ((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items", null, out var state);

        // Assert
        Assert.Equal(expected, dependency);
        if (hasState)
        {
            Assert.False(state.IsNewlyCreated);
            if (change.StartsWith("drop", StringComparison.Ordinal))
            {
                Assert.True(state.HasUnknownStructure);
            }
        }
    }

    /// <summary>A fresh native empty-survivor certificate cannot erase a newer accepted generated dependency.</summary>
    /// <param name="boundary">The earlier accepted effect or rejection affecting certificate freshness.</param>
    /// <param name="expected">The safe surviving dependency proof.</param>
    [Theory]
    [InlineData("none", false)]
    [InlineData("added_generated", true)]
    [InlineData("earlier_drop", null)]
    [InlineData("owner_dropped", null)]
    [InlineData("raw", null)]
    [InlineData("rejected", null)]
    [InlineData("source_added", null)]
    public void NativeEmptyGeneratedCertificateRespectsAcceptedLifetime(string boundary, bool? expected)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("value")),
            SafeMigrationObservedState.Matching);
        if (boundary == "added_generated")
        {
            Apply(projection, new EnsureColumnIntent("items", GeneratedDependencyColumn(
                SafeMigrationSql.Identifier("value"))));
        }
        else if (boundary == "earlier_drop")
        {
            ObserveAccepted(projection, new DropColumnIntent("retired", "items"),
                SafeMigrationObservedState.Matching);
        }
        else if (boundary == "owner_dropped")
        {
            Apply(projection, new DropTableIntent("items"));
        }
        else if (boundary == "raw")
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;", });
        }
        else if (boundary == "source_added")
        {
            Apply(projection, new EnsureColumnIntent("items", Column("removed_generated")));
        }

        var operation = new SafeMigrationOperation(new DropColumnIntent("removed_generated", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(boundary switch
        {
            "rejected" => SafeMigrationObservedState.Unsupported,
            "source_added" => SafeMigrationObservedState.Missing,
            _ => SafeMigrationObservedState.Matching,
        });

        var certified = WithEmptyGeneratedSurvivors(live);
        var analysis = projection.Project(operation, certified);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, certified, analysis, decision);

        // Act
        var dependency = ((ISafeMigrationProjectedTableSource)projection).HasProjectedGeneratedColumnDependency(
            "items", null, "value", StringComparer.Ordinal);

        // Assert
        Assert.Equal(expected, dependency);
        if (boundary == "source_added")
        {
            Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
            Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        }
    }

    private static SafeMigrationProviderAnalysis WithEmptyGeneratedSurvivors(SafeMigrationProviderAnalysis source)
        => new(source.ObservedState, source.RepairCapability, source.PostconditionSatisfied, source.Code)
        {
            DropLeavesNoGeneratedColumns = true,
        };

    private static ExpectedColumnDefinition GeneratedDependencyColumn(SafeMigrationSqlExpression expression) =>
        new("derived", typeof(int), true, "int", computedExpression: expression);
}
