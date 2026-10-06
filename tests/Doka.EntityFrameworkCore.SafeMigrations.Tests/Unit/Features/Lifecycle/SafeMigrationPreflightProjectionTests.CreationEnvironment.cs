namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Recreated tables use captured creation limits rather than the dropped table's storage limits.</summary>
    /// <param name="kind">The physical key being validated.</param>
    /// <param name="recreate">Whether earlier accepted operations replace the physical table.</param>
    [Theory]
    [InlineData("index", false)]
    [InlineData("index", true)]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    public void ProjectedKeysSelectTheCurrentTableCreationEnvironment(
        string kind,
        bool recreate
    )
    {
        // Arrange
        var provider = new CapturingKeyEnvironmentAnalyzer();
        var projection = new SafeMigrationPreflightProjection(projectedKeyAnalyzer: provider);
        var definition = new ExpectedTableDefinition("items", [Column("value")]);
        if (recreate)
        {
            ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
            Apply(projection, new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition));
        }
        else
        {
            ObserveAccepted(projection, new EnsureColumnIntent("items", Column("value")),
                SafeMigrationObservedState.Matching);
        }

        var creation = new SafeMigrationIndexPhysicalEnvironment(767);
        var original = new SafeMigrationIndexPhysicalEnvironment(3072, NewTableEnvironment: creation);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "missing")
        {
            IndexPhysicalEnvironment = original,
        };

        SafeMigrationIntent intent = kind switch
        {
            "index" => new EnsureIndexIntent(new ExpectedIndexDefinition("ix", "items",
                [new ExpectedIndexKeyDefinition("value")])),
            "primary" => new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk", "items", ["value"])),
            _ => new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("uq", "items", ["value"])),
        };

        // Act
        projection.Project(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent), live);

        // Assert
        Assert.Same(recreate ? creation : original, provider.Environment);
        Assert.Same(original, live.IndexPhysicalEnvironment);
    }

    /// <summary>Database default changes affect only tables created after that operation.</summary>
    /// <param name="changeBeforeCreate">
    /// Whether the database default changes before creation, after it, or not at all.
    /// </param>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectedCreationCharsetTracksTheDatabaseDefaultAtCreation(bool? changeBeforeCreate)
    {
        // Arrange
        var provider = new CapturingProjectedColumnAnalyzer();
        var projection = new SafeMigrationPreflightProjection(new AlterDatabaseProjection(),
            projectedColumnAnalyzer: provider);
        if (changeBeforeCreate == true)
        {
            projection.ObserveProviderPostcondition(new AlterDatabaseOperation());
        }

        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [VarcharColumn(200)]),
            SafeMigrationTableMode.StrictDefinition));
        if (changeBeforeCreate == false)
        {
            projection.ObserveProviderPostcondition(new AlterDatabaseOperation());
        }

        var alter = new SafeMigrationOperation(new AlterColumnIntent("items", VarcharColumn(500), VarcharColumn(200)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        projection.Project(alter, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.NotNull(provider.Context);
        Assert.Equal(changeBeforeCreate != true, provider.Context.Value.CanReuseCreationCharacterSet);
    }

    private sealed class CapturingKeyEnvironmentAnalyzer : ISafeMigrationProjectedKeyAnalyzer
    {
        public bool SharesUniqueConstraintAndIndexIdentity => false;

        public SafeMigrationIndexPhysicalEnvironment? Environment { get; private set; }

        public SafeMigrationProviderAnalysis ValidateProjectedIndex(
            EnsureIndexIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => Capture(liveAnalysis, projectedAnalysis);

        public SafeMigrationProviderAnalysis ValidateProjectedPrimaryKey(
            EnsurePrimaryKeyIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => Capture(liveAnalysis, projectedAnalysis);

        public SafeMigrationProviderAnalysis ValidateProjectedUniqueConstraint(
            EnsureUniqueConstraintIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => Capture(liveAnalysis, projectedAnalysis);

        private SafeMigrationProviderAnalysis Capture(
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        )
        {
            Environment = liveAnalysis.IndexPhysicalEnvironment;

            return projectedAnalysis;
        }
    }
}
