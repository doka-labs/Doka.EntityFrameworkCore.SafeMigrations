namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlModelManagedDataSqlTests
{
    [Fact]
    public void ModelManagedDeleteUsesExactSourceAndDependencyGuards()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseNpgsql("Host=127.0.0.1;Port=1;Username=test;Password=test;Database=test");
        ((DbContextOptionsBuilder)options).UsePostgreSqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new DeleteModelManagedDataIntent(
                "roles",
                ["id"],
                ["integer"],
                new object?[,] { { 1 } },
                ["id", "name"],
                ["integer", "character varying(64)"],
                new object?[,] { { 1, "administrator" } },
                schema: "app",
                foreignKeys:
                [
                    new ExpectedModelManagedDataForeignKeyDefinition(
                        "user_roles",
                        ["role_id"],
                        ["id"],
                        "app"),
                ]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var command = Assert.Single(
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        Assert.Contains("WHEN 'transition_ready' THEN 'apply'", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            "DELETE FROM app.roles AS doka_actual",
            command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "doka_actual.name IS NOT DISTINCT FROM doka_expected.o1",
            command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("JOIN app.user_roles AS doka_dependent", command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("MESSAGE = 'doka_sm_data_blocked'", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelManagedEnsureDoesNotRenderAnOverwritePath()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseNpgsql("Host=127.0.0.1;Port=1;Username=test;Password=test;Database=test");
        ((DbContextOptionsBuilder)options).UsePostgreSqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureModelManagedDataIntent(
                "roles",
                ["id"],
                ["integer"],
                ["id", "name"],
                ["integer", "text"],
                new object?[,] { { 1, "administrator" } },
                schema: null,
                uniqueKeys: null),
            SafeMigrationPolicy.ThrowIfDifferent);

        var command = Assert.Single(
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        Assert.Contains("INSERT INTO roles", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("WHERE NOT EXISTS", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("ON CONFLICT", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE SET", command.CommandText, StringComparison.OrdinalIgnoreCase);
    }
}
