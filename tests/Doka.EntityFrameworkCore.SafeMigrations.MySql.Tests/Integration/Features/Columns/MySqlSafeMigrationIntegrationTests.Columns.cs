namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task UnsafeNotNullAdd_FailsBeforeTargetDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `orders` (`id` int NOT NULL); INSERT INTO `orders` VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("sequence", "orders", type: "int", nullable: false);

        var exception =
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orders' "
                + "AND COLUMN_NAME = 'sequence';"));
    }

    [Fact]
    public async Task RepairIfSafe_RequiresTheLiveColumnToMatchTheDeclaredOldDefinition()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `repair_guard` (" + "`safe_value` varchar(40) NULL, `drifted_value` varchar(30) NULL); ");
        await using var context = CreateContext(connectionString);
        var declaredOld = new ExpectedColumnDefinition(
            "safe_value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40);

        var target = new ExpectedColumnDefinition(
            "safe_value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "approved repair");

        var safeRepair = new MigrationBuilder(context.Database.ProviderName!);
        safeRepair.AlterColumnIfDifferent("repair_guard", target, declaredOld, SafeMigrationPolicy.RepairIfSafe);

        await ExecuteOperationsAsync(context, safeRepair.Operations);
        await ExecuteOperationsAsync(context, safeRepair.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'repair_guard' "
                + "AND COLUMN_NAME = 'safe_value' AND COLUMN_COMMENT = 'approved repair';"));

        var driftedOld = new ExpectedColumnDefinition(
            "drifted_value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40);

        var driftedTarget = new ExpectedColumnDefinition(
            "drifted_value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "must not land");

        var blockedRepair = new MigrationBuilder(context.Database.ProviderName!);
        blockedRepair.AlterColumnIfDifferent(
            "repair_guard",
            driftedTarget,
            driftedOld,
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, blockedRepair.Operations, new SafeMigrationRunOptions("test-instance"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        var exception =
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, blockedRepair.Operations));

        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'repair_guard' "
                + "AND COLUMN_NAME = 'drifted_value' AND CHARACTER_MAXIMUM_LENGTH = 30 "
                + "AND COLUMN_COMMENT = '';"));
    }

    [Fact]
    public async Task LiteralDefaultMatrix_ConvergesAndIsIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `default_values` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var definitions = new[]
        {
            new ExpectedColumnDefinition(
                "boolean_value",
                typeof(bool),
                false,
                "tinyint(1)",
                defaultValue: SafeMigrationDefaultValue.Literal(true)),
            new ExpectedColumnDefinition(
                "byte_value",
                typeof(byte),
                false,
                "tinyint unsigned",
                defaultValue: SafeMigrationDefaultValue.Literal((byte)7)),
            new ExpectedColumnDefinition(
                "sbyte_value",
                typeof(sbyte),
                false,
                "tinyint",
                defaultValue: SafeMigrationDefaultValue.Literal((sbyte)-7)),
            new ExpectedColumnDefinition(
                "short_value",
                typeof(short),
                false,
                "smallint",
                defaultValue: SafeMigrationDefaultValue.Literal((short)-12)),
            new ExpectedColumnDefinition(
                "ushort_value",
                typeof(ushort),
                false,
                "smallint unsigned",
                defaultValue: SafeMigrationDefaultValue.Literal((ushort)12)),
            new ExpectedColumnDefinition(
                "integer_value",
                typeof(int),
                false,
                "int",
                defaultValue: SafeMigrationDefaultValue.Literal(42)),
            new ExpectedColumnDefinition(
                "uint_value",
                typeof(uint),
                false,
                "int unsigned",
                defaultValue: SafeMigrationDefaultValue.Literal(42U)),
            new ExpectedColumnDefinition(
                "long_value",
                typeof(long),
                false,
                "bigint",
                defaultValue: SafeMigrationDefaultValue.Literal(4200L)),
            new ExpectedColumnDefinition(
                "ulong_value",
                typeof(ulong),
                false,
                "bigint unsigned",
                defaultValue: SafeMigrationDefaultValue.Literal(4200UL)),
            new ExpectedColumnDefinition(
                "decimal_value",
                typeof(decimal),
                false,
                "decimal(10,2)",
                precision: 10,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "float_value",
                typeof(float),
                false,
                "float",
                defaultValue: SafeMigrationDefaultValue.Literal(1.5f)),
            new ExpectedColumnDefinition(
                "double_value",
                typeof(double),
                false,
                "double",
                defaultValue: SafeMigrationDefaultValue.Literal(1.75d)),
            new ExpectedColumnDefinition(
                "string_value",
                typeof(string),
                false,
                "varchar(80)",
                maxLength: 80,
                defaultValue: SafeMigrationDefaultValue.Literal("O'Reilly")),
            new ExpectedColumnDefinition(
                "char_value",
                typeof(char),
                false,
                "char(1)",
                maxLength: 1,
                isFixedLength: true,
                defaultValue: SafeMigrationDefaultValue.Literal('x')),
            new ExpectedColumnDefinition(
                "binary_value",
                typeof(byte[]),
                false,
                "varbinary(4)",
                maxLength: 4,
                defaultValue: SafeMigrationDefaultValue.Literal(
                    new byte[]
                    {
                        1,
                        2,
                        3
                    })),
            new ExpectedColumnDefinition(
                "enum_value",
                typeof(DayOfWeek),
                false,
                "int",
                defaultValue: SafeMigrationDefaultValue.Literal(DayOfWeek.Wednesday)),
            new ExpectedColumnDefinition(
                "date_value",
                typeof(DateOnly),
                false,
                "date",
                defaultValue: SafeMigrationDefaultValue.Literal(new DateOnly(2026, 8, 17))),
            new ExpectedColumnDefinition(
                "time_value",
                typeof(TimeOnly),
                false,
                "time(6)",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Literal(new TimeOnly(12, 34, 56))),
            new ExpectedColumnDefinition(
                "datetime_value",
                typeof(DateTime),
                false,
                "datetime(6)",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Literal(
                    new DateTime(
                        2026,
                        8,
                        17,
                        12,
                        34,
                        56,
                        DateTimeKind.Unspecified))),
            new ExpectedColumnDefinition(
                "duration_value",
                typeof(TimeSpan),
                false,
                "time(6)",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Literal(TimeSpan.FromMinutes(90))),
            new ExpectedColumnDefinition(
                "literal_null_value",
                typeof(string),
                true,
                "varchar(20)",
                maxLength: 20,
                defaultValue: SafeMigrationDefaultValue.Literal(null)),
            new ExpectedColumnDefinition("no_default_value", typeof(string), true, "varchar(20)", maxLength: 20),
            new ExpectedColumnDefinition(
                "sql_default_value",
                typeof(DateTime),
                false,
                "datetime(6)",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Sql("CURRENT_TIMESTAMP(6)")),
        };

        var expectedColumnCount = 1;
        var unsupportedColumns = new List<string>();

        foreach (var definition in definitions)
        {
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.EnsureColumn("default_values", definition, SafeMigrationPolicy.ThrowIfDifferent);
            var preflight = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("default-matrix"));

            var assessment = Assert.Single(preflight.Assessments);
            if (assessment.ObservedState == SafeMigrationObservedState.Unsupported)
            {
                unsupportedColumns.Add(definition.Name);

                var unsupported =
                    await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, builder.Operations));

                Assert.Contains("doka_sm_unsupported", unsupported.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(
                    0,
                    await ScalarIntAsync(
                        connectionString,
                        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                        + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'default_values' "
                        + $"AND COLUMN_NAME = '{definition.Name}';"));
                continue;
            }

            Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
            try
            {
                await ExecuteOperationsAsync(context, builder.Operations);
                await ExecuteOperationsAsync(context, builder.Operations);
                expectedColumnCount++;
            }
            catch (Exception exception)
            {
                var catalog = await DescribeColumnAsync(connectionString, definition.Name);
                throw new InvalidOperationException(
                    $"Literal default failed for '{definition.Name}'. Catalog: {catalog}",
                    exception);
            }
        }

        Assert.Equal(
            Fixture.IsMariaDb
                ? []
                :
                [
                    "date_value",
                    "time_value"
                ],
            unsupportedColumns);
        Assert.Equal(
            expectedColumnCount,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'default_values';"));
    }

    private static async Task<string> DescribeColumnAsync(
        string connectionString,
        string column
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COLUMN_TYPE, COLUMN_DEFAULT, IS_NULLABLE, EXTRA, "
            + "HEX(COLUMN_DEFAULT) "
            + "FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'default_values' "
            + "AND COLUMN_NAME = @column;";
        command.Parameters.AddWithValue("column", column);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return "missing";
        }

        return string.Join(
            " | ",
            Enumerable
                .Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index)
                    ? "<null>"
                    : reader
                        .GetValue(index)
                        .ToString()));
    }

    private static async Task<string> DescribeCheckConstraintAsync(
        string connectionString,
        string constraint
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CHECK_CLAUSE, HEX(CHECK_CLAUSE) "
            + "FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = @constraint;";
        command.Parameters.AddWithValue("constraint", constraint);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return "missing";
        }

        return $"{reader.GetString(0)} | {reader.GetString(1)}";
    }

    [Fact]
    public async Task UnmappedClrType_IsClassifiedUnsupportedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `unsupported_values` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "unsupported_values",
            new ExpectedColumnDefinition("unmapped_value", typeof(UnmappedValue), true),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments)
                .ObservedState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'unsupported_values' "
                + "AND COLUMN_NAME = 'unmapped_value';"));
    }

    [Fact]
    public async Task BinaryGuidDefault_IsUnsupportedBeforeTargetDdlWhenCatalogIsLossy()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `unsupported_binary_default` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "unsupported_binary_default",
            new ExpectedColumnDefinition(
                "binary_guid",
                typeof(Guid),
                false,
                "binary(16)",
                maxLength: 16,
                isFixedLength: true,
                defaultValue: SafeMigrationDefaultValue.Literal(Guid.Parse("0198bfe2-5573-7000-8000-000000000001"))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments)
                .ObservedState);
        var exception =
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = 'unsupported_binary_default' "
                + "AND COLUMN_NAME = 'binary_guid';"));
    }

    [Fact]
    public async Task ComputedCollationAndCommentFacets_ConvergeAndDetectSingleFieldDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `advanced_columns` (`a` int NOT NULL, `b` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "display_name",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: "utf8mb4_bin",
                comment: "quote ' slash \\ umlaut \u00fc"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "virtual_sum",
                typeof(int),
                true,
                "int",
                computedColumnSql: "a + b",
                isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "stored_sum",
                typeof(int),
                true,
                "int",
                computedColumnSql: "a + b",
                isStored: true),
            SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var mismatch = new MigrationBuilder(context.Database.ProviderName!);
        mismatch.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "display_name",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: "utf8mb4_bin",
                comment: "different"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, mismatch.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);
    }
}
