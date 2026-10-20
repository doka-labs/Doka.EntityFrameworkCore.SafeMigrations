namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>
/// Verifies the generated lock boundary without relying on a live server or implementation internals.
/// </summary>
public sealed class PostgreSqlModelManagedDeleteFenceSqlTests
{
    /// <summary>Requires a fresh complete classifier pass after the operation-owned delete fence.</summary>
    [Fact]
    public void DeleteFenceReclassifiesBeforeMutationWithoutLockingDependentTables()
    {
        // Arrange
        using var context = CreateDeleteFenceSqlContext();
        var operation = new SafeMigrationOperation(
            new DeleteModelManagedDataIntent(
                "roles",
                ["id"],
                ["integer"],
                new object?[,] { { 1 } },
                ["id", "name"],
                ["integer", "text"],
                new object?[,] { { 1, "source" } },
                schema: "app",
                foreignKeys:
                [
                    new ExpectedModelManagedDataForeignKeyDefinition("users", ["role_id"], ["id"], "app"),
                ]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        // Assert
        var command = Assert.Single(commands);
        var fence = command.CommandText.IndexOf("LOCK TABLE app.roles IN EXCLUSIVE MODE;", StringComparison.Ordinal);
        var mutation = command.CommandText.IndexOf("DELETE FROM app.roles", StringComparison.Ordinal);
        Assert.True(fence > 0);
        Assert.True(mutation > fence);
        Assert.Contains("FOR doka_evaluation_pass IN 1..2 LOOP", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("EXIT WHEN doka_action OPERATOR(pg_catalog.<>) 'apply' "
            + "OR doka_evaluation_pass OPERATOR(pg_catalog.=) 2;", command.CommandText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("LOCK TABLE app.users", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR UPDATE", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("SET TRANSACTION", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("COMMIT", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.to_jsonb(constraint_row)->>'conenforced'", command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("pg_catalog.current_setting('server_version_num')::integer < 180000", command.CommandText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("constraint_row.conenforced", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Leaves non-delete model-managed mutation contracts unchanged.</summary>
    /// <param name="update">Whether the tested operation updates instead of ensuring a row.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonDeleteModelManagedOperationsDoNotAcquireDeleteFence(
        bool update
    )
    {
        // Arrange
        using var context = CreateDeleteFenceSqlContext();
        ModelManagedDataIntent intent = update
            ? new UpdateModelManagedDataIntent(
                "roles", ["id"], ["integer"], new object?[,] { { 1 } },
                ["name"], ["text"], new object?[,] { { "source" } }, new object?[,] { { "target" } },
                schema: "app", uniqueKeys: null)
            : new EnsureModelManagedDataIntent(
                "roles", ["id"], ["integer"], ["id", "name"], ["integer", "text"],
                new object?[,] { { 1, "target" } }, schema: "app", uniqueKeys: null);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(
            [new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent)], context.Model);

        // Assert
        var command = Assert.Single(commands);

        Assert.DoesNotContain("LOCK TABLE", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("doka_evaluation_pass", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Does not acquire a stateful lock for statically rejected operation metadata.</summary>
    [Fact]
    public void UnsupportedDeleteDoesNotAcquireDeleteFence()
    {
        // Arrange
        using var context = CreateDeleteFenceSqlContext();
        var operation = new SafeMigrationOperation(
            new DeleteModelManagedDataIntent(
                "roles", ["id"], ["integer"], new object?[,] { { 1 } },
                ["id"], ["integer"], new object?[,] { { 1 } }, schema: "app", foreignKeys: null),
            SafeMigrationPolicy.ThrowIfDifferent);
        operation.AddAnnotation("unsupported:delete-fixture", true);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        // Assert
        var command = Assert.Single(commands);

        Assert.Contains("doka_sm_unsupported", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCK TABLE", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Creates only the provider services needed to render generated operation SQL.</summary>
    /// <returns>The disposable offline provider context.</returns>
    private static DbContext CreateDeleteFenceSqlContext()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseNpgsql("Host=127.0.0.1;Port=1;Username=test;Password=test;Database=test");
        ((DbContextOptionsBuilder)options).UsePostgreSqlSafeMigrations();

        return new DbContext(options.Options);
    }
}
