namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Leaves provider-owned schema classification intact for both ensure and removal contracts.</summary>
    /// <param name="drop">Whether the separately evaluated operation removes rather than ensures a schema.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemaProjection_PreservesTheProviderClassification(
        bool drop
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        SafeMigrationIntent intent = drop
            ? new DropSchemaIntent("app")
            : new EnsureSchemaIntent("app");

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: true,
            "provider_schema_state");

        // Act
        var projected = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            projected.ObservedState,
            operation.Policy,
            projected.RepairCapability);

        projection.Observe(operation, projected, projected, decision);

        // Assert
        Assert.Same(live, projected);
    }

    /// <summary>
    /// Normalizes only the current database qualifier so a following unqualified member sees the same accepted
    /// owner.
    /// </summary>
    [Fact]
    public void CurrentDatabaseQualifierSharesProjectedIdentityWithUnqualifiedOperations()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new CurrentDatabaseIdentityNormalizer("application"));

        var id = Column("id");
        var qualifiedTable = new ExpectedTableDefinition(
            "items",
            [id],
            schema: "application");

        ObserveAccepted(
            projection,
            new EnsureTableIntent(qualifiedTable, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationObservedState.Missing);

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureColumnIntent("items", id),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal("projected_matching", analysis.Code);
    }

    /// <summary>
    /// Keeps the native foreign-database rejection ahead of otherwise matching current-database facts.
    /// </summary>
    [Fact]
    public void ForeignDatabaseRejectionCannotReuseCurrentDatabaseProjection()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new CurrentDatabaseIdentityNormalizer("application"));

        var id = Column("id");
        var currentTable = new ExpectedTableDefinition("items", [id]);

        ObserveAccepted(
            projection,
            new EnsureTableIntent(currentTable, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationObservedState.Missing);

        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "database_qualifier_mismatch");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureColumnIntent("items", id, "archive"),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Same(live, analysis);
    }

    private sealed class CurrentDatabaseIdentityNormalizer : ISafeMigrationProviderObjectIdentityNormalizer
    {
        private readonly string _database;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        /// <param name="database">The supplied database for this fixture.</param>
        public CurrentDatabaseIdentityNormalizer(
            string database
        )
        {
            _database = database;
        }

        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        public string NormalizeIdentifier(
            string identifier
        ) => identifier;

        public string? NormalizeSchema(
            string? schema
        ) => StringComparer.Ordinal.Equals(schema, _database)
            ? null
            : schema;

        public bool IsObjectIdentityMismatch(
            SafeMigrationProviderAnalysis analysis
        ) => analysis.ObservedState == SafeMigrationObservedState.Unsupported
            && StringComparer.Ordinal.Equals(analysis.Code, "database_qualifier_mismatch");
    }
}
