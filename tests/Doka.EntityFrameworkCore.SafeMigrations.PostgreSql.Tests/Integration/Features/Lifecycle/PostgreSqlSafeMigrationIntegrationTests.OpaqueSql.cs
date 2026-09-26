namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task DataPreparationSql_DefersPreflightAndAllowsGuardedUniqueIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE mixed_records (id integer NOT NULL PRIMARY KEY, code text NOT NULL); "
            + "INSERT INTO mixed_records VALUES (1, 'duplicate'), (2, 'duplicate');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("UPDATE mixed_records SET code = code || id WHERE code = 'duplicate';");
        builder.CreateIndexIfNotExists("ux_mixed_records_code", "mixed_records", ["code"], unique: true);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("postgresql-opaque-sql"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = current_schema() "
            + "AND tablename = 'mixed_records' AND indexname = 'ux_mixed_records_code';");

        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Null(preflight.Assessments[1].DeferredOrigin?.MigrationId);
        Assert.Equal(1, indexCount);
    }

    [Fact]
    public async Task DataRegressionSql_DefersPreflightButRuntimeGuardRejectsUniqueIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE mixed_records (id integer NOT NULL PRIMARY KEY, code text NOT NULL); "
            + "INSERT INTO mixed_records VALUES (1, 'first'), (2, 'second');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("UPDATE mixed_records SET code = 'duplicate';");
        builder.CreateIndexIfNotExists("ux_mixed_records_code", "mixed_records", ["code"], unique: true);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("postgresql-opaque-sql-reject"));

        var exception = await Record.ExceptionAsync(
            () => ExecuteOperationsAsync(context, builder.Operations));

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM pg_indexes WHERE schemaname = current_schema() "
            + "AND tablename = 'mixed_records' AND indexname = 'ux_mixed_records_code';");

        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.IsType<PostgresException>(exception);
        Assert.Equal(0, indexCount);
    }

    [Fact]
    public async Task OpaqueSql_DoesNotDeferUnsupportedOperationAnnotation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.CreateIndexIfNotExists("ix_unapproved", "missing_table", ["code"])
            .Annotation("Unapproved:Test", true);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("postgresql-opaque-unsupported-annotation"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[1].Action);
        Assert.Equal("operation_annotation", preflight.Assessments[1].AnalysisCode);
        Assert.Null(preflight.Assessments[1].DeferredOrigin);
    }
}
