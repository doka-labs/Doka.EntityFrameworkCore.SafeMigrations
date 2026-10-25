namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlGeneratedDropIntegrationTests
{
    /// <summary>Unreadable dependency catalogs never certify absence, but an absent member remains a no-op.</summary>
    /// <param name="readable">Whether this session can read both dependency catalogs.</param>
    /// <param name="exists">Whether the requested source member exists.</param>
    /// <param name="mixed">Whether an ordinary matching operation still requires the final classifier batch.</param>
    /// <param name="catalog">The exact required catalog whose PUBLIC read permission is revoked.</param>
    [Theory]
    [InlineData(false, false, false, "pg_depend")]
    [InlineData(false, true, false, "pg_depend")]
    [InlineData(true, false, false, "pg_depend")]
    [InlineData(true, true, false, "pg_depend")]
    [InlineData(false, false, true, "pg_depend")]
    [InlineData(false, true, true, "pg_depend")]
    [InlineData(true, false, true, "pg_depend")]
    [InlineData(true, true, true, "pg_depend")]
    [InlineData(false, false, false, "pg_attrdef")]
    [InlineData(false, true, false, "pg_attrdef")]
    [InlineData(false, false, false, "pg_inherits")]
    [InlineData(false, true, false, "pg_inherits")]
    public async Task DropCatalogVisibilityIsFreshWithoutAdditionalCommonPathPhase(
        bool readable, bool exists, bool mixed, string catalog)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var role = "drop_reader_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(connectionString, "CREATE ROLE " + role + "; "
            + "GRANT USAGE, CREATE ON SCHEMA public TO " + role + "; "
            + "CREATE TABLE drop_visibility(unrelated integer" + (exists ? ", source integer" : "") + "); "
            + "INSERT INTO drop_visibility(unrelated) VALUES(9); "
            + "ALTER TABLE drop_visibility OWNER TO " + role + "; "
            + (readable ? "SELECT 1;" : "REVOKE SELECT ON pg_catalog." + catalog + " FROM PUBLIC;"));

        try
        {
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlAsync($"SELECT pg_catalog.set_config('role', {role}, false);");
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            if (mixed)
            {
                builder.AddColumnIfNotExists<int>("unrelated", "drop_visibility", type: "integer", nullable: true);
            }

            builder.DropColumnIfExists("source", "drop_visibility");
            var runner = context.GetService<ISafeMigrationRunner>();
            var commands = 0;
            using var scope = new System.Diagnostics.Activity("generated-drop-command-count").Start();
            var traceId = scope.TraceId;
            using var listener = new System.Diagnostics.ActivityListener
            {
                ShouldListenTo = static source => source.Name == "Npgsql",
                Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> options)
                    => options.TraceId == traceId ? System.Diagnostics.ActivitySamplingResult.AllData
                        : System.Diagnostics.ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId == traceId && activity.GetTagItem("db.query.text") is string)
                    {
                        Interlocked.Increment(ref commands);
                    }
                },
            };

            System.Diagnostics.ActivitySource.AddActivityListener(listener);

            // Act
            var report = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-drop-metadata-visibility"), CancellationToken.None);

            listener.Dispose();
            scope.Stop();
            var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

            // Assert
            _output.WriteLine("catalog={0}; readable={1}; target-exists={2}; mixed={3}; native-command-batches={4}",
                catalog, readable, exists, mixed, commands);
            Assert.True(commands > 0, "The native tracing instrument must observe actual command execution.");
            Assert.Equal(!exists ? SafeMigrationAction.NoOp : readable ? SafeMigrationAction.Apply
                : SafeMigrationAction.RejectUnsupported, report.Assessments[^1].Action);
            if (exists && !readable)
            {
                Assert.Equal("generated_column_drop_unproven", report.Assessments[^1].AnalysisCode);
                Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
                Assert.Equal(2, await ScalarIntAsync(connectionString,
                    "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='drop_visibility'::regclass "
                    + "AND attnum>0 AND NOT attisdropped;"));
            }
            else
            {
                Assert.Null(failure);
            }

            Assert.Equal(9, await ScalarIntAsync(connectionString, "SELECT unrelated FROM drop_visibility;"));
        }
        finally
        {
            await ExecuteSqlAsync(connectionString, "ALTER TABLE drop_visibility OWNER TO postgres; "
                + "REVOKE USAGE, CREATE ON SCHEMA public FROM " + role + "; DROP ROLE " + role + ";");
        }
    }

    /// <summary>
    /// A previous readable snapshot cannot bypass either the fresh runtime or the next analysis fence.
    /// </summary>
    [Fact]
    public async Task RevokedCatalogReadabilityExpiresAnalysisAndRuntimeCertificates()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var role = "drop_reader_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(connectionString, "CREATE ROLE " + role + "; "
            + "GRANT USAGE, CREATE ON SCHEMA public TO " + role + "; "
            + "CREATE TABLE drop_visibility(source integer, unrelated integer); "
            + "INSERT INTO drop_visibility VALUES(2,9); ALTER TABLE drop_visibility OWNER TO " + role + "; "
            + "REVOKE SELECT ON pg_catalog.pg_depend FROM PUBLIC; "
            + "GRANT SELECT ON pg_catalog.pg_depend TO " + role + ";");

        try
        {
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlAsync($"SELECT pg_catalog.set_config('role', {role}, false);");
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.DropColumnIfExists("source", "drop_visibility");
            var runner = context.GetService<ISafeMigrationRunner>();

            // Act
            var initial = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-drop-readable-before-revoke"), CancellationToken.None);

            await ExecuteSqlAsync(connectionString, "REVOKE SELECT ON pg_catalog.pg_depend FROM " + role + ";");
            var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
            var repeated = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-drop-unreadable-after-revoke"), CancellationToken.None);
            var protectedRows = await ScalarStringAsync(connectionString,
                "SELECT source::text || ':' || unrelated::text FROM drop_visibility;");

            await ExecuteSqlAsync(connectionString, "GRANT SELECT ON pg_catalog.pg_depend TO " + role + ";");
            var restored = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-drop-readable-after-regrant"), CancellationToken.None);

            await ExecuteOperationsAsync(context, builder.Operations);

            // Assert
            Assert.Equal(SafeMigrationAction.Apply, Assert.Single(initial.Assessments).Action);
            Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(repeated.Assessments).Action);
            Assert.Equal("generated_column_drop_unproven", repeated.Assessments[0].AnalysisCode);
            Assert.Equal("2:9", protectedRows);
            Assert.Equal(SafeMigrationAction.Apply, Assert.Single(restored.Assessments).Action);
            Assert.Equal(9, await ScalarIntAsync(connectionString, "SELECT unrelated FROM drop_visibility;"));
            Assert.Equal(1, await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='drop_visibility'::regclass "
                + "AND attnum>0 AND NOT attisdropped;"));
        }
        finally
        {
            await ExecuteSqlAsync(connectionString, "ALTER TABLE drop_visibility OWNER TO postgres; "
                + "REVOKE SELECT ON pg_catalog.pg_depend FROM " + role + "; "
                + "REVOKE USAGE, CREATE ON SCHEMA public FROM " + role + "; DROP ROLE " + role + ";");
        }
    }
}
