namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises the set-based managed-data metadata guard against isolated live catalogs.</summary>
[Collection(SqlServerSharedContainer.Name)]
public sealed class SqlServerManagedTypeGuardIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Creates the suite with one isolated database per test.</summary>
    /// <param name="fixture">The shared engine container.</param>
    public SqlServerManagedTypeGuardIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Only complete builtin physical type evidence satisfies a managed-data guard.</summary>
    /// <param name="actualType">The actual column type or a missing-object fixture sentinel.</param>
    /// <param name="expectedType">The captured type contract.</param>
    /// <param name="matches">Whether the physical catalog proves the entire contract.</param>
    [SqlServerLiveTheory]
    [InlineData("int", "int", true)]
    [InlineData("bigint", "int", false)]
    [InlineData("nvarchar(40)", "nvarchar(40)", true)]
    [InlineData("nvarchar(41)", "nvarchar(40)", false)]
    [InlineData("nvarchar(max)", "nvarchar(max)", true)]
    [InlineData("nvarchar(40)", "nvarchar(max)", false)]
    [InlineData("varbinary(16)", "varbinary(16)", true)]
    [InlineData("varbinary(17)", "varbinary(16)", false)]
    [InlineData("decimal(9,3)", "decimal(9,3)", true)]
    [InlineData("decimal(10,3)", "decimal(9,3)", false)]
    [InlineData("decimal(9,2)", "decimal(9,3)", false)]
    [InlineData("datetime2(4)", "datetime2(4)", true)]
    [InlineData("datetime2(5)", "datetime2(4)", false)]
    [InlineData("alias", "int", false)]
    [InlineData("missing_column", "int", false)]
    [InlineData("missing_table", "int", false)]
    public async Task TableTypeGuard_RequiresCompleteBuiltinColumnEvidence(
        string actualType,
        string expectedType,
        bool matches
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        if (actualType != "missing_table")
        {
            if (actualType == "alias")
            {
                await ExecuteSqlAsync(connectionString, "CREATE TYPE dbo.guard_alias FROM int;");
            }

            var setup = actualType == "alias"
                ? "CREATE TABLE dbo.managed_types (Id int NOT NULL PRIMARY KEY, Value dbo.guard_alias NULL);"
                : actualType == "missing_column"
                    ? "CREATE TABLE dbo.managed_types (Id int NOT NULL PRIMARY KEY, OtherValue int NULL);"
                    : "CREATE TABLE dbo.managed_types (Id int NOT NULL PRIMARY KEY, Value " + actualType + " NULL);";

            await ExecuteSqlAsync(connectionString, setup);
        }

        await using var context = CreateContext(connectionString);
        var catalog = CreateCatalog(context);
        var intent = new EnsureModelManagedDataIntent("managed_types", ["Id"], ["int"],
            ["Id", "Value"], ["int", expectedType], new object?[,] { { 1, null } }, null, null);

        var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;

        // Act
        var result = await ScalarIntAsync(connectionString, "SELECT " + guard + ";");

        // Assert
        Assert.Equal(matches ? 1 : 0, result);
    }

    /// <summary>
    /// Every dependent schema and repeated foreign-key type requirement participates in deletion safety.
    /// </summary>
    /// <param name="mode">The actual dependent metadata or conflicting captured contract.</param>
    /// <param name="matches">Whether all physical type requirements hold.</param>
    [SqlServerLiveTheory]
    [InlineData("matching", true)]
    [InlineData("wrong_type", false)]
    [InlineData("missing_column", false)]
    [InlineData("missing_table", false)]
    [InlineData("conflicting", false)]
    public async Task DeleteTypeGuard_RetainsAllDependentTableRequirements(
        string mode,
        bool matches
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA audit;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.type_principals (Id int NOT NULL PRIMARY KEY, Code bigint NOT NULL); "
            + "CREATE TABLE dbo.type_dependents (ReferenceId int NULL);");
        if (mode != "missing_table")
        {
            var definition = mode == "missing_column" ? "OtherId int"
                : mode == "wrong_type" ? "ReferenceId bigint" : "ReferenceId int";

            await ExecuteSqlAsync(connectionString, "CREATE TABLE audit.type_dependents (" + definition + " NULL);");
        }

        await using var context = CreateContext(connectionString);
        var catalog = CreateCatalog(context);
        var foreignKeys = new List<ExpectedModelManagedDataForeignKeyDefinition>
        {
            new("type_dependents", ["ReferenceId"], ["Id"]),
            new("type_dependents", ["ReferenceId"], ["Id"], "audit"),
        };

        if (mode == "conflicting")
        {
            foreignKeys.Add(new ExpectedModelManagedDataForeignKeyDefinition(
                "type_dependents", ["ReferenceId"], ["Code"], "dbo"));
        }

        var intent = new DeleteModelManagedDataIntent("type_principals", ["Id"], ["int"],
            new object?[,] { { 1 } }, ["Id", "Code"], ["int", "bigint"],
            new object?[,] { { 1, 2L } }, null, foreignKeys.ToArray());

        var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;

        // Act
        var result = await ScalarIntAsync(connectionString, "SELECT " + guard + ";");

        // Assert
        Assert.Equal(matches ? 1 : 0, result);
    }

    /// <summary>
    /// Metadata consolidation permits explicit identity INSERT and DELETE but forbids identity UPDATE.
    /// </summary>
    /// <param name="kind">The captured operation kind.</param>
    /// <param name="allowed">Whether identity columns are legal targets for that operation.</param>
    [SqlServerLiveTheory]
    [InlineData("ensure", true)]
    [InlineData("update", false)]
    [InlineData("delete", true)]
    public async Task WritableGuard_PreservesIdentityOperationBoundaries(
        string kind,
        bool allowed
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_types (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var catalog = CreateCatalog(context);
        var intent = CreateIdentityIntent(kind);
        var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;

        // Act
        var result = await ScalarIntAsync(connectionString, "SELECT " + guard + ";");

        // Assert
        Assert.Equal(allowed ? 1 : 0, result);
    }

    /// <summary>Model-managed deletion remains legal for an identity-keyed source row.</summary>
    [SqlServerLiveFact]
    public async Task IdentitySource_ManagedDeleteAppliesWithoutWritableIdentityRequirement()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_types (Id int IDENTITY(1,1) NOT NULL PRIMARY KEY); "
            + "INSERT INTO dbo.identity_types DEFAULT VALUES;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DeleteModelManagedDataFromModel("identity_types", ["Id"], ["int"],
            new object?[,] { { 1 } }, ["Id"], ["int"], new object?[,] { { 1 } });

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var remaining = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.identity_types;");

        // Assert
        Assert.Equal(0, remaining);
    }

    /// <summary>Uses the runtime provider services for type parsing and identifier rendering.</summary>
    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(
        DbContext context
    )
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>
    /// Captures an identity column without varying the physical type contract between operation kinds.
    /// </summary>
    private static ModelManagedDataIntent CreateIdentityIntent(
        string kind
    )
        => kind switch
        {
            "ensure" => new EnsureModelManagedDataIntent("identity_types", ["Id"], ["int"],
                ["Id"], ["int"], new object?[,] { { 1 } }, null, null),
            "update" => new UpdateModelManagedDataIntent("identity_types", ["Id"], ["int"],
                new object?[,] { { 1 } }, ["Id"], ["int"],
                new object?[,] { { 1 } }, new object?[,] { { 1 } }, null, null),
            "delete" => new DeleteModelManagedDataIntent("identity_types", ["Id"], ["int"],
                new object?[,] { { 1 } }, ["Id"], ["int"], new object?[,] { { 1 } }, null, []),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
