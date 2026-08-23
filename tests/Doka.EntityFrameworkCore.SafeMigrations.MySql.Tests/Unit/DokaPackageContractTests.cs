namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class DokaPackageContractTests
{
    [Fact]
    public void ScopedCommand_SnapshotsInputsAndExposesExecutionOrder()
    {
        string[] setup = ["SET @first = 1;", "SET @second = 2;"];
        string[] cleanup = ["SET @first = NULL;", "SET @second = NULL;"];

        var command = MySqlMigrationCommandSpec.CreateScoped(setup, "DO 0;", cleanup, transactionSuppressed: true);

        setup[0] = "SELECT 1;";
        cleanup[0] = "SELECT 2;";

        Assert.True(command.TransactionSuppressed);
        Assert.Collection(
            command.Fragments,
            fragment => AssertFragment(fragment, MySqlMigrationCommandFragmentKind.Setup, "SET @first = 1;"),
            fragment => AssertFragment(fragment, MySqlMigrationCommandFragmentKind.Setup, "SET @second = 2;"),
            fragment => AssertFragment(fragment, MySqlMigrationCommandFragmentKind.Body, "DO 0;"),
            fragment => AssertFragment(fragment, MySqlMigrationCommandFragmentKind.Cleanup, "SET @second = NULL;"),
            fragment => AssertFragment(fragment, MySqlMigrationCommandFragmentKind.Cleanup, "SET @first = NULL;"));
        Assert.Equal(
            command.CommandText,
            string.Concat(command.Fragments.Select(static fragment => fragment.CommandText.ToString())));
    }

    [Fact]
    public void MySqlTemporalMappings_RenderExecutableInvariantLiterals()
    {
        using var context = CreateContext(MySqlServerVersion.MySql(new Version(8, 4, 11)));

        AssertTemporalMappings(context);
    }

    [Fact]
    public void MariaDbTemporalMappings_RenderExecutableInvariantLiterals()
    {
        using var context = CreateContext(MySqlServerVersion.MariaDb(new Version(10, 11, 18)));

        AssertTemporalMappings(context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullableToRequiredColumn_RejectsMissingApplicationBackfill(
        bool isMariaDb
    )
    {
        using var context = CreateContext(CreateServerVersion(isMariaDb));
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var operation = CreateNullableTimestampRepair();

        var exception = Assert.Throws<InvalidOperationException>(() => generator.Generate([operation], context.Model));

        Assert.Contains(nameof(AlterColumnOperation), exception.Message, StringComparison.Ordinal);
        Assert.Contains("explicit DefaultValue or DefaultValueSql", exception.Message, StringComparison.Ordinal);
        Assert.Contains("application contract", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Entries", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OccurredAt", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullableToRequiredTimestamp_RejectsClrLiteralBackfill(
        bool isMariaDb
    )
    {
        using var context = CreateContext(CreateServerVersion(isMariaDb));
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var operation = CreateNullableTimestampRepair();
        operation.DefaultValue = new DateTime(2026, 8, 21, 12, 34, 56, DateTimeKind.Unspecified);

        var exception = Assert.Throws<InvalidOperationException>(() => generator.Generate([operation], context.Model));

        Assert.Contains("requires DefaultValueSql", exception.Message, StringComparison.Ordinal);
        Assert.Contains("session time zone", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Entries", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OccurredAt", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullableToRequiredTimestamp_AcceptsExplicitSqlBackfill(
        bool isMariaDb
    )
    {
        using var context = CreateContext(CreateServerVersion(isMariaDb));
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var operation = CreateNullableTimestampRepair();
        operation.DefaultValueSql = "CURRENT_TIMESTAMP(6)";

        var sql = string.Concat(
            generator
                .Generate([operation], context.Model)
                .Select(static command => command.CommandText));

        Assert.Contains(
            "UPDATE `Entries` SET `OccurredAt` = CURRENT_TIMESTAMP(6) WHERE `OccurredAt` IS NULL;",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("MODIFY COLUMN `OccurredAt` timestamp(6) NOT NULL", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static DbContext CreateContext(
        MySqlServerVersion serverVersion
    )
    {
        var options = new DbContextOptionsBuilder<DbContext>().UseMySql(
                "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
                serverVersion)
            .Options;

        return new DbContext(options);
    }

    private static MySqlServerVersion CreateServerVersion(
        bool isMariaDb
    ) => isMariaDb
        ? MySqlServerVersion.MariaDb(new Version(10, 11, 18))
        : MySqlServerVersion.MySql(new Version(8, 4, 11));

    private static AlterColumnOperation CreateNullableTimestampRepair() => new()
    {
        Table = "Entries",
        Name = "OccurredAt",
        ClrType = typeof(DateTime),
        ColumnType = "timestamp(6)",
        IsNullable = false,
        OldColumn =
        {
            ClrType = typeof(DateTime),
            ColumnType = "timestamp(6)",
            IsNullable = true,
        },
    };

    private static void AssertTemporalMappings(
        DbContext context
    )
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        MigrationOperation[] operations =
        [
            new AddColumnOperation
            {
                Table = "Entries",
                Name = "CreatedOn",
                ClrType = typeof(DateOnly),
                ColumnType = "date",
                IsNullable = false,
                DefaultValue = new DateOnly(2026, 8, 21),
            },
            new AddColumnOperation
            {
                Table = "Entries",
                Name = "CreatedAt",
                ClrType = typeof(TimeOnly),
                ColumnType = "time(6)",
                IsNullable = false,
                DefaultValue = new TimeOnly(12, 34, 56),
            },
        ];

        var sql = string.Concat(
            generator
                .Generate(operations, context.Model)
                .Select(static command => command.CommandText));

        Assert.Contains("DEFAULT (DATE '2026-08-21')", sql, StringComparison.Ordinal);
        Assert.Contains("DEFAULT (TIME '12:34:56", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFAULT DATE ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEFAULT TIME ", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertFragment(
        MySqlMigrationCommandFragment fragment,
        MySqlMigrationCommandFragmentKind expectedKind,
        string expectedCommandText
    )
    {
        Assert.Equal(expectedKind, fragment.Kind);
        Assert.Equal(expectedCommandText, fragment.CommandText.ToString());
    }
}
