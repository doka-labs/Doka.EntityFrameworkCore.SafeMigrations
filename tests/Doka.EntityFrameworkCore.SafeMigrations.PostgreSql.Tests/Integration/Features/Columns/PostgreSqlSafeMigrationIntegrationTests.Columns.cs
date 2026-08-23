namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task NullCollation_MeansProviderInferredDefaultAndNeverWildcard()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE collation_defaults (inherited text NULL, explicit text COLLATE \"C\" NULL);");
        await using var context = CreateContext(connectionString);
        var inherited = new MigrationBuilder(context.Database.ProviderName!);
        inherited.EnsureColumn(
            "collation_defaults",
            new ExpectedColumnDefinition("inherited", typeof(string), true, "text"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var explicitDrift = new MigrationBuilder(context.Database.ProviderName!);
        explicitDrift.EnsureColumn(
            "collation_defaults",
            new ExpectedColumnDefinition("explicit", typeof(string), true, "text"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var inheritedReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, inherited.Operations, new SafeMigrationRunOptions("inherited-collation"));
        var driftReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, explicitDrift.Operations, new SafeMigrationRunOptions("explicit-collation"));

        Assert.Equal(
            SafeMigrationObservedState.Matching,
            Assert.Single(inheritedReport.Assessments)
                .ObservedState);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(driftReport.Assessments)
                .ObservedState);
    }

    [Fact]
    public async Task UnsafeNotNullAdd_FailsBeforeTargetDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE orders (id integer NOT NULL); INSERT INTO orders VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("sequence", "orders", type: "integer", nullable: false);

        var exception =
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal("P1003", exception.SqlState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'orders' "
                + "AND column_name = 'sequence';"));
    }

    [Fact]
    public async Task RepairIfSafe_RequiresTheLiveColumnToMatchTheDeclaredOldDefinition()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE repair_guard ("
            + "safe_value character varying(40) NULL, "
            + "drifted_value character varying(30) NULL);");
        await using var context = CreateContext(connectionString);
        var declaredOld = new ExpectedColumnDefinition(
            "safe_value",
            typeof(string),
            isNullable: true,
            storeType: "character varying(40)",
            maxLength: 40);

        var target = new ExpectedColumnDefinition(
            "safe_value",
            typeof(string),
            isNullable: true,
            storeType: "character varying(40)",
            maxLength: 40,
            comment: "approved repair");

        var safeRepair = new MigrationBuilder(context.Database.ProviderName!);
        safeRepair.AlterColumnIfDifferent("repair_guard", target, declaredOld, SafeMigrationPolicy.RepairIfSafe);

        await ExecuteOperationsAsync(context, safeRepair.Operations);
        await ExecuteOperationsAsync(context, safeRepair.Operations);

        Assert.Equal(
            "approved repair",
            await ScalarStringAsync(
                connectionString,
                "SELECT pg_catalog.col_description(c.oid, a.attnum) "
                + "FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid "
                + "WHERE n.nspname = current_schema() AND c.relname = 'repair_guard' "
                + "AND a.attname = 'safe_value';"));

        var driftedOld = new ExpectedColumnDefinition(
            "drifted_value",
            typeof(string),
            isNullable: true,
            storeType: "character varying(40)",
            maxLength: 40);

        var driftedTarget = new ExpectedColumnDefinition(
            "drifted_value",
            typeof(string),
            isNullable: true,
            storeType: "character varying(40)",
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
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(
                context,
                blockedRepair.Operations));

        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal(
            30,
            await ScalarIntAsync(
                connectionString,
                "SELECT character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'repair_guard' "
                + "AND column_name = 'drifted_value';"));
    }

    [Fact]
    public async Task UnmappedClrType_IsClassifiedUnsupportedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE unsupported_values (id integer NOT NULL);");
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
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'unsupported_values' "
                + "AND column_name = 'unmapped_value';"));
    }

    [Fact]
    public async Task ComputedCollationAndCommentFacets_ConvergeAndDetectSingleFieldDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE advanced_columns (a integer NOT NULL, b integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "display_name",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("C"),
                comment: "quote ' slash \\ umlaut \u00fc"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "stored_sum",
                typeof(int),
                true,
                "integer",
                isStored: true,
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Add, "b")),
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
                "character varying(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("C"),
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

    [Fact]
    public async Task VirtualGeneratedColumn_IsClassifiedUnsupportedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE virtual_values (a integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "virtual_values",
            new ExpectedColumnDefinition(
                "computed_value",
                typeof(int),
                true,
                "integer",
                isStored: false,
                computedExpression: SqlColumnAndInt("a", SafeMigrationSqlBinaryOperator.Add, 1)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(
            SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments)
                .ObservedState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'virtual_values' "
                + "AND column_name = 'computed_value';"));
    }

    [Fact]
    public async Task LiteralDefaultMatrix_ConvergesAndIsIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE default_values (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var definitions = new[]
        {
            new ExpectedColumnDefinition(
                "boolean_value",
                typeof(bool),
                false,
                "boolean",
                defaultValue: SafeMigrationDefaultValue.Literal(true)),
            new ExpectedColumnDefinition(
                "byte_value",
                typeof(byte),
                false,
                "smallint",
                defaultValue: SafeMigrationDefaultValue.Literal((byte)7)),
            new ExpectedColumnDefinition(
                "sbyte_value",
                typeof(sbyte),
                false,
                "smallint",
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
                "integer",
                defaultValue: SafeMigrationDefaultValue.Literal((ushort)12)),
            new ExpectedColumnDefinition(
                "integer_value",
                typeof(int),
                false,
                "integer",
                defaultValue: SafeMigrationDefaultValue.Literal(42)),
            new ExpectedColumnDefinition(
                "uint_value",
                typeof(uint),
                false,
                "bigint",
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
                "numeric(20,0)",
                precision: 20,
                scale: 0,
                defaultValue: SafeMigrationDefaultValue.Literal(4200UL)),
            new ExpectedColumnDefinition(
                "decimal_value",
                typeof(decimal),
                false,
                "numeric(10,2)",
                precision: 10,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "float_value",
                typeof(float),
                false,
                "real",
                defaultValue: SafeMigrationDefaultValue.Literal(1.5f)),
            new ExpectedColumnDefinition(
                "double_value",
                typeof(double),
                false,
                "double precision",
                defaultValue: SafeMigrationDefaultValue.Literal(1.75d)),
            new ExpectedColumnDefinition(
                "string_value",
                typeof(string),
                false,
                "character varying(80)",
                maxLength: 80,
                defaultValue: SafeMigrationDefaultValue.Literal("O'Reilly")),
            new ExpectedColumnDefinition(
                "char_value",
                typeof(char),
                false,
                "character(1)",
                maxLength: 1,
                isFixedLength: true,
                defaultValue: SafeMigrationDefaultValue.Literal('x')),
            new ExpectedColumnDefinition(
                "binary_value",
                typeof(byte[]),
                false,
                "bytea",
                defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 1, 2, 3 })),
            new ExpectedColumnDefinition(
                "guid_value",
                typeof(Guid),
                false,
                "uuid",
                defaultValue:
                SafeMigrationDefaultValue.Literal(Guid.Parse("0198bfe2-5573-7000-8000-000000000001"))),
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
                "time without time zone",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Literal(new TimeOnly(12, 34, 56))),
            new ExpectedColumnDefinition(
                "datetime_value",
                typeof(DateTime),
                false,
                "timestamp without time zone",
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
                "datetime_offset_value",
                typeof(DateTimeOffset),
                false,
                "timestamp with time zone",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Literal(
                    new DateTimeOffset(
                        2026,
                        8,
                        17,
                        12,
                        34,
                        56,
                        TimeSpan.Zero))),
            new ExpectedColumnDefinition(
                "duration_value",
                typeof(TimeSpan),
                false,
                "interval",
                defaultValue: SafeMigrationDefaultValue.Literal(TimeSpan.FromMinutes(90))),
            new ExpectedColumnDefinition(
                "enum_value",
                typeof(DayOfWeek),
                false,
                "integer",
                defaultValue: SafeMigrationDefaultValue.Literal(DayOfWeek.Wednesday)),
            new ExpectedColumnDefinition(
                "literal_null_value",
                typeof(string),
                true,
                "character varying(20)",
                maxLength: 20,
                defaultValue: SafeMigrationDefaultValue.Literal(null)),
            new ExpectedColumnDefinition(
                "no_default_value",
                typeof(string),
                true,
                "character varying(20)",
                maxLength: 20),
            new ExpectedColumnDefinition(
                "sql_default_value",
                typeof(DateTimeOffset),
                false,
                "timestamp with time zone",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Sql(
                    SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp))),
        };

        var coveredLiteralTypes = definitions
            .Where(static definition => definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal)
            .Select(static definition => definition.DefaultValue.LiteralValue)
            .Where(static value => value is not null)
            .Select(static value => value!.GetType())
            .Distinct()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var definition in definitions)
        {
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.EnsureColumn("default_values", definition, SafeMigrationPolicy.ThrowIfDifferent);
            var preflight = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("default-matrix"));

            var assessment = Assert.Single(preflight.Assessments);
            Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
            try
            {
                await ExecuteOperationsAsync(context, builder.Operations);
                await ExecuteOperationsAsync(context, builder.Operations);
            }
            catch (Exception exception)
            {
                var catalog = await DescribeColumnAsync(connectionString, definition.Name);
                throw new InvalidOperationException(
                    $"Literal default failed for '{definition.Name}'. Catalog: {catalog}",
                    exception);
            }
        }

        Assert.Equal(SafeMigrationLiteralContract.CreateSupportedNonNullTypes(), coveredLiteralTypes);
        Assert.Equal(
            definitions.Length + 1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'default_values';"));
    }

    private static async Task<string> DescribeColumnAsync(
        string connectionString,
        string column
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.format_type(a.atttypid, a.atttypmod), "
            + "pg_catalog.pg_get_expr(d.adbin, d.adrelid), a.attnotnull, a.attgenerated "
            + "FROM pg_catalog.pg_attribute a "
            + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + "LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum "
            + "WHERE n.nspname = current_schema() AND c.relname = 'default_values' "
            + "AND a.attname = @column;";
        command.Parameters.AddWithValue("column", column);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
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
}
