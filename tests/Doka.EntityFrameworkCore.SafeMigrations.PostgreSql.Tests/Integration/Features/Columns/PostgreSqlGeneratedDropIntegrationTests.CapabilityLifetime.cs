namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlGeneratedDropIntegrationTests
{
    /// <summary>
    /// Accepted source presence does not prove unchanged native DROP authority or hierarchy capability.
    /// </summary>
    /// <param name="restriction">The independently observed native capability refusal.</param>
    [Theory]
    [InlineData("lock")]
    [InlineData("catalog")]
    [InlineData("hierarchy")]
    public async Task AcceptedSourceNoOpCannotEraseNativeDropCapabilityRefusal(string restriction)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var role = "drop_capability_" + Guid.NewGuid().ToString("N");
        var restrictionSql = restriction switch
        {
            "lock" => "REVOKE ALL ON drop_capability FROM " + role + ";",
            "catalog" => "REVOKE SELECT ON pg_catalog.pg_depend FROM PUBLIC;",
            "hierarchy" => "CREATE TABLE drop_capability_child() INHERITS(drop_capability);",
            _ => throw new ArgumentOutOfRangeException(nameof(restriction)),
        };

        await ExecuteSqlAsync(connectionString, "CREATE ROLE " + role + "; "
            + "GRANT USAGE, CREATE ON SCHEMA public TO " + role + "; "
            + "CREATE TABLE drop_capability(source integer, unrelated integer); "
            + "INSERT INTO drop_capability VALUES(2,9); ALTER TABLE drop_capability OWNER TO " + role + "; "
            + restrictionSql);

        try
        {
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlAsync($"SELECT pg_catalog.set_config('role', {role}, false);");
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.AddColumnIfNotExists<int>("source", "drop_capability", type: "integer", nullable: true);
            builder.DropColumnIfExists("source", "drop_capability");

            // Act
            var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-drop-native-capability-lifetime"), CancellationToken.None);

            var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [builder.Operations[^1]]));

            // Assert
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
            Assert.Equal("generated_column_drop_unproven", report.Assessments[1].AnalysisCode);
            Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
            Assert.Equal("2:9", await ScalarStringAsync(connectionString,
                "SELECT source::text || ':' || unrelated::text FROM drop_capability;"));
        }
        finally
        {
            await ExecuteSqlAsync(connectionString, "ALTER TABLE drop_capability OWNER TO postgres; "
                + "REVOKE USAGE, CREATE ON SCHEMA public FROM " + role + "; DROP ROLE " + role + ";");
        }
    }
}
