namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Prevents native function-role loss when ordinary EF defaults enter safe migration capture.</summary>
public sealed class MySqlCapturedDefaultIdentityIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses an independent database on the assembly-owned engine.</summary>
    /// <param name="fixture">The selected MySQL or MariaDB engine.</param>
    public MySqlCapturedDefaultIdentityIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Unknown quoted function roles fail before DDL; supported defaults retain native values and replay.
    /// </summary>
    /// <param name="quoted">Whether source uses a native quoted function name that cannot be captured portably.</param>
    /// <param name="script">Whether to execute the generated script rather than provider commands.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CapturedFunctionRolesAreNotSilentlyReinterpreted(
        bool quoted,
        bool script
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `captured_defaults` (`id` int PRIMARY KEY);",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var source = quoted
            ? "`ABS`(1)"
            : "ABS(1)";

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(name: "value",
                table: "captured_defaults",
                type: "int",
                nullable: false,
                defaultValueSql: source),
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var runner = context.GetService<ISafeMigrationRunner>();
        var report = await runner.AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("captured-default-roles"),
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

                await ExecuteMigrationScriptAsync(connectionString,
                    string.Join(Environment.NewLine, commands.Select(static command => command.CommandText)),
                    CancellationToken.None);
            }
            else
            {
                await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
            }

            await ExecuteSqlAsync(connectionString,
                "INSERT INTO `captured_defaults` (`id`) VALUES (42);",
                CancellationToken.None);

            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        });

        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='captured_defaults' AND COLUMN_NAME='value';",
            CancellationToken.None);

        var values = quoted
            ? 0
            : await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM `captured_defaults` WHERE `id`=42 AND `value`=1;",
                CancellationToken.None);

        // Assert
        Assert.Equal(quoted
                ? SafeMigrationReportStatus.Blocked
                : SafeMigrationReportStatus.Ready,
            report.Status);

        Assert.Equal(quoted
                ? SafeMigrationObservedState.Unsupported
                : SafeMigrationObservedState.Missing,
            Assert.Single(report.Assessments)
                .ObservedState);

        if (quoted)
        {
            if (script)
            {
                // WHY: A standalone SQL script has native error transport,
                // not the managed migration-command wrapper's outer exception.
                var nativeFailure = Assert.IsType<MySqlException>(failure);
                Assert.Equal(1062, nativeFailure.Number);
                Assert.Contains("doka_sm_unsupported", nativeFailure.Message, StringComparison.Ordinal);
            }
            else
            {
                AssertOwnedGuardFailure(failure);
            }

            Assert.Equal(0, columns);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(1, columns);
            Assert.Equal(1, values);
        }
    }
}
