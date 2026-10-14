namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>
    /// Fresh projected ENUM additions respect captured double-quoted literal syntax before target DDL.
    /// </summary>
    /// <param name="ansiQuotes">Whether double quotes are identifiers rather than string-member delimiters.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredDefaultProjectedEnumAddUsesCapturedDoubleQuoteMode(bool ansiQuotes)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES"
            + (ansiQuotes ? ",ANSI_QUOTES" : "") + "'; SET SESSION default_storage_engine = InnoDB;");
        var column = new SafeMigrationOperation(new EnsureColumnIntent("required_projected_items",
                new ExpectedColumnDefinition("value", typeof(string), false, @"enum(""Alpha"")")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var originalFingerprint = SafeMigrationContractFingerprint.Create([column]);
        var originalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);
        var operations = RequiredDefaultsProjectedOwnerOperations(column);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("projected-required-enum-quote-mode"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, operations, CancellationToken.None));

        var finalMode = await RequiredDefaultsEnumCommentSessionModeAsync(context);
        var state = await ReadAtomicRequiredAddStateAsync(connectionString, column);
        var physicalType = await ScalarStringAsync(connectionString,
            "SELECT COLUMN_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_projected_items' AND COLUMN_NAME = 'value';");

        // Assert
        Assert.Equal(ansiQuotes ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(ansiQuotes ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Apply,
            report.Assessments[1].Action);
        if (ansiQuotes)
        {
            Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(failure).Message,
                StringComparison.Ordinal);
            Assert.Equal(new AtomicRequiredAddState(0, 0, 0, 0), state);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 0), state);
            Assert.Equal("enum('Alpha')", physicalType);
        }

        Assert.Equal(originalMode, finalMode);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([column]));
    }

    /// <summary>
    /// Fresh owners cannot bypass strict mode or transactional storage for a no-default required add.
    /// </summary>
    /// <param name="environment">
    /// The strict InnoDB control, permissive session, or nontransactional default engine.
    /// </param>
    [Theory]
    [InlineData("strict-innodb")]
    [InlineData("permissive")]
    [InlineData("non-innodb")]
    public async Task RequiredDefaultProjectedNoDefaultAddUsesCapturedEnvironment(string environment)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var strictSession = environment != "permissive";
        var engine = environment == "non-innodb" ? "MyISAM" : "InnoDB";
        await AtomicRequiredAddSessionSqlAsync(context,
            "SET SESSION sql_mode = '" + (strictSession ? "STRICT_ALL_TABLES" : "") + "'; "
            + "SET SESSION default_storage_engine = " + engine + ";");
        var column = AtomicRequiredAddOperation(guid: false, table: "required_projected_items");
        var operations = RequiredDefaultsProjectedOwnerOperations(column);
        var allowed = environment == "strict-innodb";

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("projected-required-add-environment"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, operations, CancellationToken.None));

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, column);
        var actualEngine = await ScalarStringAsync(connectionString,
            "SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_projected_items';");

        // Assert
        Assert.Equal(allowed ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(allowed ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[1].Action);
        if (allowed)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(failure).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(engine, actualEngine);
        Assert.Equal(allowed ? new AtomicRequiredAddState(1, 0, 0, 0) : new AtomicRequiredAddState(0, 0, 0, 0), state);
    }

    /// <summary>Fresh MariaDB owners cannot certify a no-default add with session CHECK protection disabled.</summary>
    [AtomicAddMariaDbFact]
    public async Task RequiredDefaultProjectedNoDefaultAddRejectsDisabledMariaCheckProtection()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context,
            "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; SET SESSION default_storage_engine = InnoDB; "
            + "SET SESSION check_constraint_checks = OFF;");
        var column = AtomicRequiredAddOperation(guid: false, table: "required_projected_items");
        var operations = RequiredDefaultsProjectedOwnerOperations(column);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("projected-required-add-check-off"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, operations, CancellationToken.None));

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, column);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal(new AtomicRequiredAddState(0, 0, 0, 0), state);
    }

    /// <summary>
    /// A dropped matching TIMESTAMP target cannot borrow its former completion for an unavailable required add.
    /// </summary>
    [Fact]
    public async Task RequiredDefaultProjectedTimestampDropThenEnsureRejectsUnavailableMissingAdd()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        // WHY: Creating the original TIMESTAMP with explicit semantics avoids
        // an implicit CURRENT_TIMESTAMP default impersonating the no-default target.
        await AtomicRequiredAddSessionSqlAsync(context,
            "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; SET SESSION explicit_defaults_for_timestamp = ON; "
            + "CREATE TABLE `required_projected_time_items` (`key` int NOT NULL PRIMARY KEY, "
            + "`value` timestamp(6) NOT NULL) ENGINE=InnoDB; "
            + "INSERT INTO `required_projected_time_items` VALUES (1, '2000-01-02 03:04:05');");
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("required_projected_time_items",
                new ExpectedColumnDefinition("value", typeof(DateTime), false, "timestamp(6)")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("value", "required_projected_time_items");
        builder.Operations.Add(ensure);

        // Act
        var originalContract = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_projected_time_items' AND COLUMN_NAME = 'value' "
            + "AND IS_NULLABLE = 'NO' AND COLUMN_DEFAULT IS NULL;");

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("projected-required-timestamp-readd"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var targetColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_projected_time_items' AND COLUMN_NAME = 'value';");

        var retainedKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `required_projected_time_items` WHERE `key` = 1;");

        // Assert
        Assert.Equal(1, originalContract);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, targetColumns);
        Assert.Equal(1, retainedKeys);
    }

    /// <summary>Defines a freshly created key-only owner followed by its untouched required-column intent.</summary>
    /// <param name="column">The immutable pending no-default required-column operation.</param>
    /// <returns>The ordered create-owner and required-add stream.</returns>
    private static List<MigrationOperation> RequiredDefaultsProjectedOwnerOperations(SafeMigrationOperation column)
    {
        var intent = Assert.IsType<EnsureColumnIntent>(column.Intent);
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.EnsureTable(new ExpectedTableDefinition(intent.Table,
                [new ExpectedColumnDefinition("key", typeof(int), false, "int")],
                primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", intent.Table, ["key"])),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.Operations.Add(column);

        return builder.Operations;
    }
}
