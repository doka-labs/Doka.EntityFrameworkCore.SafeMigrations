namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task SchemaOperations_AreIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.EnsureSchemaExists("module");
        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, create.Operations);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_namespace WHERE nspname = 'module';"));

        var drop = new MigrationBuilder(context.Database.ProviderName!);
        drop.DropSchemaIfExists("module");
        await ExecuteOperationsAsync(context, drop.Operations);
        await ExecuteOperationsAsync(context, drop.Operations);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_namespace WHERE nspname = 'module';"));
    }
}
