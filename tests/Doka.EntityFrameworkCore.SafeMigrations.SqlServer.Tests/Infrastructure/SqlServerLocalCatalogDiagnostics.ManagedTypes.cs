namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

internal static partial class SqlServerLocalCatalogDiagnostics
{
    /// <summary>Checks metadata refusals on the explicit diagnostic engine without bypassing native test gates.</summary>
    private static async Task VerifyManagedTypesAsync(SqlConnection root, string rootString, string output)
    {
        var database = await CreateDatabaseAsync(root);
        try
        {
            var connectionString = SqlServerContainerFixture.BuildTestConnectionString(rootString, database);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await SqlServerScalarProofRegressionTests.VerifyLocalAsync(connection);
            await ExecuteAsync(connection, "CREATE TYPE dbo.guard_alias FROM int;");
            using var context = new SafeMigrationDbContext(connectionString);
            var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
                context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

            (string Actual, string Expected, bool Matches)[] contracts =
            [
                ("int", "int", true), ("bigint", "int", false),
                ("nvarchar(40)", "nvarchar(40)", true), ("nvarchar(41)", "nvarchar(40)", false),
                ("nvarchar(max)", "nvarchar(max)", true), ("nvarchar(40)", "nvarchar(max)", false),
                ("varbinary(16)", "varbinary(16)", true), ("varbinary(17)", "varbinary(16)", false),
                ("decimal(9,3)", "decimal(9,3)", true), ("decimal(10,3)", "decimal(9,3)", false),
                ("decimal(9,2)", "decimal(9,3)", false), ("datetime2(4)", "datetime2(4)", true),
                ("datetime2(5)", "datetime2(4)", false), ("dbo.guard_alias", "int", false),
                ("missing_column", "int", false), ("missing_table", "int", false),
            ];

            var evidence = new List<string> { "scalar_binding_freshness_boundary_and_cancellation: verified" };
            foreach (var contract in contracts)
            {
                await ExecuteAsync(connection, "DROP TABLE IF EXISTS dbo.managed_types;");
                if (contract.Actual != "missing_table")
                {
                    var column = contract.Actual == "missing_column" ? "OtherValue int" : "Value " + contract.Actual;
                    await ExecuteAsync(connection,
                        "CREATE TABLE dbo.managed_types (Id int NOT NULL PRIMARY KEY, " + column + " NULL);");
                }

                var intent = new EnsureModelManagedDataIntent("managed_types", ["Id"], ["int"],
                    ["Id", "Value"], ["int", contract.Expected], new object?[,] { { 1, null } }, null, null);

                var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;
                await VerifyManagedGuardAsync(connection, guard, contract.Matches);
                evidence.Add(contract.Actual + " -> " + contract.Expected + ": " + contract.Matches);
            }

            await ExecuteAsync(connection, "CREATE TABLE dbo.identity_types (Id int IDENTITY NOT NULL PRIMARY KEY);");
            ModelManagedDataIntent[] identities =
            [
                new EnsureModelManagedDataIntent("identity_types", ["Id"], ["int"], ["Id"], ["int"],
                    new object?[,] { { 1 } }, null, null),
                new UpdateModelManagedDataIntent("identity_types", ["Id"], ["int"], new object?[,] { { 1 } },
                    ["Id"], ["int"], new object?[,] { { 1 } }, new object?[,] { { 1 } }, null, null),
                new DeleteModelManagedDataIntent("identity_types", ["Id"], ["int"], new object?[,] { { 1 } },
                    ["Id"], ["int"], new object?[,] { { 1 } }, null, []),
            ];

            foreach (var intent in identities)
            {
                var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;
                await VerifyManagedGuardAsync(connection, guard, intent is not UpdateModelManagedDataIntent);
                evidence.Add(intent.GetType().Name + ": verified");
            }

            await ExecuteAsync(connection, "CREATE SCHEMA audit;");
            await ExecuteAsync(connection, "CREATE TABLE dbo.type_principals (Id int NOT NULL PRIMARY KEY, "
                + "Code bigint NOT NULL); CREATE TABLE dbo.type_dependents (ReferenceId int NULL);");

            (string Mode, bool Matches)[] dependents =
            [
                ("matching", true), ("wrong_type", false), ("missing_column", false),
                ("missing_table", false), ("conflicting", false),
            ];

            foreach (var dependent in dependents)
            {
                await ExecuteAsync(connection, "DROP TABLE IF EXISTS audit.type_dependents;");
                if (dependent.Mode != "missing_table")
                {
                    var column = dependent.Mode == "missing_column" ? "OtherId int"
                        : dependent.Mode == "wrong_type" ? "ReferenceId bigint" : "ReferenceId int";

                    await ExecuteAsync(connection, "CREATE TABLE audit.type_dependents (" + column + " NULL);");
                }

                var foreignKeys = new List<ExpectedModelManagedDataForeignKeyDefinition>
                {
                    new("type_dependents", ["ReferenceId"], ["Id"]),
                    new("type_dependents", ["ReferenceId"], ["Id"], "audit"),
                };

                if (dependent.Mode == "conflicting")
                {
                    foreignKeys.Add(new ExpectedModelManagedDataForeignKeyDefinition(
                        "type_dependents", ["ReferenceId"], ["Code"], "dbo"));
                }

                var intent = new DeleteModelManagedDataIntent("type_principals", ["Id"], ["int"],
                    new object?[,] { { 1 } }, ["Id", "Code"], ["int", "bigint"],
                    new object?[,] { { 1, 2L } }, null, foreignKeys.ToArray());

                var guard = catalog.BuildModelManagedDataAnalysisGuard(intent).Guard;
                await VerifyManagedGuardAsync(connection, guard, dependent.Matches);
                evidence.Add("dependent " + dependent.Mode + ": " + dependent.Matches);
            }

            await File.WriteAllLinesAsync(Path.Combine(output, "managed-type-guards.log"), evidence);
        }
        finally
        {
            await DropDatabaseAsync(root, database);
        }
    }

    /// <summary>Fails on missing, NULL or contradictory physical guard evidence.</summary>
    private static async Task VerifyManagedGuardAsync(SqlConnection connection, string guard, bool expected)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (" + guard + ");";
        var actual = await command.ExecuteScalarAsync();
        Assert.NotNull(actual);
        Assert.Equal(expected ? 1 : 0, Convert.ToInt32(actual, CultureInfo.InvariantCulture));
    }
}
