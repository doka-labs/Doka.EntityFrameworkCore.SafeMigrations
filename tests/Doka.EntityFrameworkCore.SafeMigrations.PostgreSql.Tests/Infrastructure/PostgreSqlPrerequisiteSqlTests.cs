namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks prerequisite-only rendering without constructing discarded classifier parameters.</summary>
public sealed class PostgreSqlPrerequisiteSqlTests
{
    /// <summary>
    /// Supported, constant, annotation-rejected, and unsupported contracts retain identical prerequisites.
    /// </summary>
    /// <param name="scenario">The prerequisite contract to construct.</param>
    /// <param name="expectedParameters">The exact number of prerequisite parameters.</param>
    [Theory]
    [InlineData("column", 1)]
    [InlineData("table", 0)]
    [InlineData("annotation", 0)]
    [InlineData("prefix", 0)]
    public void PrerequisiteOnly_PreservesFullPlanContractWithoutDiscardedParameters(
        string scenario,
        int expectedParameters
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=127.0.0.1;Port=1;Database=prerequisite_test;Username=test;Password=test",
            registerSafeMigrations: false);

        using var command = new NpgsqlCommand();
        var parameters = new PostgreSqlCatalogQueryParameters(command);
        var mappingSource = context.GetService<IRelationalTypeMappingSource>();
        var sqlHelper = context.GetService<ISqlGenerationHelper>();
        var literalBuilder = new PostgreSqlSafeMigrationCatalogSqlBuilder(mappingSource, sqlHelper);
        var parameterBuilder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            mappingSource, sqlHelper, parameters.AddString);

        var migration = new MigrationBuilder(context.Database.ProviderName!);
        if (scenario == "table")
        {
            migration.EnsureTable(
                new ExpectedTableDefinition(
                    "prerequisite_parent", [new ExpectedColumnDefinition("id", typeof(int), false, "integer")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else if (scenario == "prefix")
        {
            migration.EnsureIndex(
                new ExpectedIndexDefinition(
                    "prefix_index", "prerequisite_parent", [new ExpectedIndexKeyDefinition("value", prefixLength: 4)]),
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            migration.EnsureColumn(
                "prerequisite_parent", new ExpectedColumnDefinition("value", typeof(int), true, "integer"),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        var operation = (SafeMigrationOperation)migration.Operations[0];
        if (scenario == "annotation")
        {
            operation.AddAnnotation("Unknown:ProviderContract", true);
        }

        // Act
        var fullPlan = literalBuilder.Build(operation);
        var literalPrerequisite = literalBuilder.BuildPrerequisiteExpression(operation);
        var parameterPrerequisite = parameterBuilder.BuildPrerequisiteExpression(operation);

        // Assert
        Assert.Equal(fullPlan.PrerequisiteExpression, literalPrerequisite);
        Assert.Equal(expectedParameters, parameters.Count);
        Assert.Equal(expectedParameters, command.Parameters.Count);
        Assert.Equal(scenario != "column", parameterPrerequisite == "TRUE");
        Assert.Equal(scenario is "annotation" or "prefix", fullPlan.IsStaticallyUnsupported);
        if (scenario == "column")
        {
            Assert.Contains("@doka_sm_p0", parameterPrerequisite, StringComparison.Ordinal);
            Assert.Equal("prerequisite_parent", command.Parameters[0].Value);
        }
    }
}
