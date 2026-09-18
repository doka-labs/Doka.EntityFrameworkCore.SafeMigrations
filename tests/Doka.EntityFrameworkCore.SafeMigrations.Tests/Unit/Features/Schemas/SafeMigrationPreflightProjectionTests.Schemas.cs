namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    [Fact]
    public void SchemaProjection_PreservesTheProviderClassification()
    {
        SafeMigrationIntent[] intents = [new EnsureSchemaIntent("app"), new DropSchemaIntent("app"),];

        var projection = new SafeMigrationPreflightProjection();

        foreach (var intent in intents)
        {
            var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
            var live = new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None,
                postconditionSatisfied: true,
                "provider_schema_state");

            var projected = projection.Project(operation, live);
            var decision = SafeMigrationDecisionPlanner.Plan(
                intent.Kind,
                projected.ObservedState,
                operation.Policy,
                projected.RepairCapability);

            projection.Observe(operation, projected, projected, decision);

            Assert.Same(live, projected);
        }
    }

    [Fact]
    public void CurrentDatabaseQualifierSharesProjectedIdentityWithUnqualifiedOperations()
    {
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

        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureColumnIntent("items", id),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Missing));

        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal("projected_matching", analysis.Code);
    }

    [Fact]
    public void ForeignDatabaseRejectionCannotReuseCurrentDatabaseProjection()
    {
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

        var analysis = projection.Project(
            new SafeMigrationOperation(
                new EnsureColumnIntent("items", id, "archive"),
                SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Same(live, analysis);
    }

    private sealed class CurrentDatabaseIdentityNormalizer(
        string database
    ) : ISafeMigrationProviderObjectIdentityNormalizer
    {
        public string? NormalizeSchema(
            string? schema
        ) => StringComparer.Ordinal.Equals(schema, database)
            ? null
            : schema;

        public bool IsObjectIdentityMismatch(
            SafeMigrationProviderAnalysis analysis
        ) => analysis.ObservedState == SafeMigrationObservedState.Unsupported
            && StringComparer.Ordinal.Equals(analysis.Code, "database_qualifier_mismatch");
    }
}
