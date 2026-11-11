namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Prevents captured defaults from rebinding a quoted native function to an unrelated builtin.</summary>
public sealed class PostgreSqlCapturedDefaultIdentityIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the shared native server while retaining independent test database ownership.</summary>
    /// <param name="fixture">The PostgreSQL server fixture.</param>
    public PostgreSqlCapturedDefaultIdentityIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Unsupported quoted callable roles reject before DDL in runtime and generated scripts.</summary>
    /// <param name="quoted">Whether source selects a distinct case-sensitive quoted function.</param>
    /// <param name="script">Whether to execute generated script text rather than migration commands.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CapturedDefaultNeverSilentlyRebindsQuotedFunction(
        bool quoted,
        bool script
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE FUNCTION \"ABS\"(integer) RETURNS integer LANGUAGE SQL IMMUTABLE AS 'SELECT 99'; "
            + "CREATE TABLE captured_default_identity(id integer);",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(name: "v",
                table: "captured_default_identity",
                type: "integer",
                nullable: false,
                defaultValueSql: quoted
                    ? "\"ABS\"(1)"
                    : "ABS(1)"),
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                builder.Operations,
                new SafeMigrationRunOptions("captured-default-callable-identity"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            if (script)
            {
                var commands = context
                    .GetService<IMigrationsSqlGenerator>()
                    .Generate(builder.Operations,
                        context.Model,
                        MigrationsSqlGenerationOptions.Script);

                await ExecuteSqlAsync(connectionString,
                    string.Join(Environment.NewLine, commands.Select(static command => command.CommandText)),
                    CancellationToken.None);
            }
            else
            {
                await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
            }
        });

        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns "
            + "WHERE table_schema=current_schema() AND table_name='captured_default_identity' AND column_name='v';",
            CancellationToken.None);

        var quotedValue = await ScalarIntAsync(connectionString, "SELECT \"ABS\"(1);", CancellationToken.None);
        int? persisted = null;
        if (!quoted
            && failure is null)
        {
            await ExecuteSqlAsync(connectionString,
                "INSERT INTO captured_default_identity(id) VALUES (42);",
                CancellationToken.None);

            persisted = await ScalarIntAsync(connectionString,
                "SELECT v FROM captured_default_identity WHERE id=42;",
                CancellationToken.None);
        }

        // Assert
        Assert.Equal(99, quotedValue);
        Assert.Equal(quoted
                ? SafeMigrationReportStatus.Blocked
                : SafeMigrationReportStatus.Ready,
            report.Status);

        Assert.Equal(quoted
                ? SafeMigrationObservedState.Unsupported
                : SafeMigrationObservedState.Missing,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(quoted
                ? 0
                : 1,
            columns);

        Assert.Equal(quoted
                ? null
                : 1,
            persisted);

        if (quoted)
        {
            Assert.NotNull(failure);
            Assert.Contains("doka_sm_unsupported", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(failure);
        }
    }
}
