namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Separates required transition metadata from optional analysis and exact-target replay metadata.</summary>
public sealed class PostgreSqlIntegerWideningCatalogIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the assembly-owned native engine and test-local database ownership.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public PostgreSqlIntegerWideningCatalogIntegrationTests(PostgreSqlContainerFixture fixture) : base(fixture) { }

    /// <summary>Classifies unreadable required catalogs before PostgreSQL can bind the dependency query.</summary>
    /// <param name="catalog">The independently revoked required metadata catalog.</param>
    /// <param name="targetAlreadyWidened">Whether only an exact target no-op remains.</param>
    /// <param name="nullable">Whether equality avoids required-column constraint metadata.</param>
    [Theory]
    [InlineData("pg_depend", false, false)]
    [InlineData("pg_inherits", false, false)]
    [InlineData("pg_am", false, false)]
    [InlineData("pg_opclass", false, false)]
    [InlineData("pg_constraint", false, false)]
    [InlineData("pg_index", false, false)]
    [InlineData("pg_depend", true, false)]
    [InlineData("pg_inherits", true, false)]
    [InlineData("pg_am", true, false)]
    [InlineData("pg_opclass", true, false)]
    [InlineData("pg_constraint", true, false)]
    [InlineData("pg_index", true, false)]
    [InlineData("pg_constraint", true, true)]
    public async Task WideningRequiresVisibleDependencyCatalogs(
        string catalog,
        bool targetAlreadyWidened,
        bool nullable)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var role = "integer_catalog_owner_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(connectionString, "CREATE ROLE " + role + "; "
            + "GRANT USAGE, CREATE ON SCHEMA public TO " + role + "; "
            + "CREATE TABLE catalog_integer_rows (value " + (targetAlreadyWidened ? "bigint" : "integer")
            + (nullable ? " NULL); " : " NOT NULL); ")
            + "INSERT INTO catalog_integer_rows VALUES (7); "
            + "ALTER TABLE catalog_integer_rows OWNER TO " + role + "; "
            + "REVOKE SELECT ON pg_catalog." + catalog + " FROM PUBLIC;");

        try
        {
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync(CancellationToken.None);
            await context.Database.ExecuteSqlAsync($"SELECT pg_catalog.set_config('role', {role}, false);");
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.AlterColumnIfDifferent("catalog_integer_rows",
                new ExpectedColumnDefinition("value", typeof(long), nullable, "bigint"),
                new ExpectedColumnDefinition("value", typeof(int), nullable, "integer"),
                SafeMigrationPolicy.RepairIfSafe);
            var targetCanBeCertified = targetAlreadyWidened && (catalog != "pg_constraint" || nullable);

            // Act
            var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
                context, builder.Operations, new SafeMigrationRunOptions("integer-catalog-visibility"));
            var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
            var postflight = targetCanBeCertified
                ? await context.GetService<ISafeMigrationRunner>().VerifyAsync(
                    context, builder.Operations, new SafeMigrationRunOptions("integer-catalog-replay"))
                : null;

            // Assert
            Assert.Equal(targetCanBeCertified ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectUnsupported,
                Assert.Single(report.Assessments).Action);
            Assert.Equal(targetCanBeCertified ? "classified_matching" : "integer_widening_unproven",
                report.Assessments[0].AnalysisCode);
            if (targetCanBeCertified)
            {
                Assert.Null(failure);
                Assert.True(Assert.Single(postflight!.Assessments).PostconditionSatisfied);
            }
            else
            {
                Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
            }

            Assert.Equal(targetAlreadyWidened ? "bigint" : "integer", await ScalarStringAsync(connectionString,
                "SELECT data_type FROM information_schema.columns WHERE table_schema = 'public' "
                + "AND table_name = 'catalog_integer_rows' AND column_name = 'value';"));
            Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT value FROM catalog_integer_rows;"));
        }
        finally
        {
            await ExecuteSqlAsync(connectionString, "ALTER TABLE catalog_integer_rows OWNER TO postgres; "
                + "REVOKE USAGE, CREATE ON SCHEMA public FROM " + role + "; DROP ROLE " + role + ";");
        }
    }

    /// <summary>Does not add optional analysis-role catalog privileges to the existing native DROP contract.</summary>
    /// <param name="catalog">The independently revoked optional index-role catalog.</param>
    [Theory]
    [InlineData("pg_am")]
    [InlineData("pg_opclass")]
    public async Task StandaloneIndexDropDoesNotRequireOptionalRoleCatalogs(string catalog)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var role = "index_role_owner_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(connectionString, "CREATE ROLE " + role + "; "
            + "GRANT USAGE, CREATE ON SCHEMA public TO " + role + "; "
            + "CREATE TABLE optional_index_rows(value integer NOT NULL); "
            + "CREATE INDEX ix_optional_role ON optional_index_rows(value); "
            + "ALTER TABLE optional_index_rows OWNER TO " + role + "; "
            + "REVOKE SELECT ON pg_catalog." + catalog + " FROM PUBLIC;");

        try
        {
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync(CancellationToken.None);
            await context.Database.ExecuteSqlAsync($"SELECT pg_catalog.set_config('role', {role}, false);");
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.DropIndexIfExists("ix_optional_role", "optional_index_rows");

            // Act
            var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
                context, builder.Operations, new SafeMigrationRunOptions("optional-index-role-catalog"));
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);

            // Assert
            Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
            Assert.Equal(0, await ScalarIntAsync(connectionString,
                "SELECT count(*) FROM pg_catalog.pg_class WHERE relname = 'ix_optional_role';"));
        }
        finally
        {
            await ExecuteSqlAsync(connectionString, "ALTER TABLE optional_index_rows OWNER TO postgres; "
                + "REVOKE USAGE, CREATE ON SCHEMA public FROM " + role + "; DROP ROLE " + role + ";");
        }
    }

}
