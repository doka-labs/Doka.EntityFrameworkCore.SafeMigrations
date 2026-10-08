namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises direct and ordered lossless repair guards against the live SQLite rebuild path.</summary>
public sealed class SqliteSafeMigrationLosslessAlterIntegrationTests : SqliteIntegrationTestBase,
    IClassFixture<SqliteLosslessAlterServiceFixture>
{
    private readonly IServiceProvider _internalServices;

    /// <summary>Shares provider services without sharing test connections or transaction state.</summary>
    /// <param name="services">The class-lifetime provider fixture.</param>
    public SqliteSafeMigrationLosslessAlterIntegrationTests(SqliteLosslessAlterServiceFixture services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _internalServices = services.Provider;
    }

    /// <summary>Rejects a direct rebuild that would round a persisted Int64 value through REAL affinity.</summary>
    [Fact]
    public async Task IntegerToRealRepairDoesNotRoundPersistedValues()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER NOT NULL, "
            + "CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + "INSERT INTO lossless_alter_records VALUES (1, 9007199254740993);");

        await using var context = new SqliteLosslessAlterTestContext<double>(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(
            "lossless_alter_records",
            new ExpectedColumnDefinition("Value", typeof(double), isNullable: false, storeType: "REAL"),
            new ExpectedColumnDefinition("Value", typeof(long), isNullable: false, storeType: "INTEGER"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-lossless-integer-real"));

        var executionError = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT printf('%lld', Value) FROM lossless_alter_records WHERE Id = 1;";
        var persistedValue = await command.ExecuteScalarAsync(CancellationToken.None);
        var originalColumn = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('lossless_alter_records') "
            + "WHERE name = 'Value' AND type = 'INTEGER';");

        // Assert
        Assert.Equal("9007199254740993", persistedValue);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.IsType<InvalidOperationException>(executionError);
        Assert.Equal(1, originalColumn);
    }

    /// <summary>Type and affinity changes never substitute a successful schema rebuild for a value proof.</summary>
    /// <param name="sourceStoreType">The persisted source affinity.</param>
    /// <param name="sourceClrType">The authored source CLR domain.</param>
    /// <param name="targetStoreType">The requested target affinity.</param>
    /// <param name="targetClrType">The requested target CLR domain.</param>
    /// <param name="valueSql">The isolated boundary or representation-sensitive value.</param>
    [Theory]
    [InlineData("TEXT", typeof(string), "INTEGER", typeof(long), "'00042'")]
    [InlineData("TEXT", typeof(string), "REAL", typeof(double), "'9007199254740993'")]
    [InlineData("INTEGER", typeof(long), "TEXT", typeof(string), "9223372036854775807")]
    [InlineData("REAL", typeof(double), "TEXT", typeof(string), "0.12345678901234567")]
    [InlineData("BLOB", typeof(byte[]), "TEXT", typeof(string), "X'3030303432'")]
    [InlineData("INTEGER", typeof(long), "REAL", typeof(double), "-9007199254740993")]
    [InlineData("INTEGER", typeof(long), "REAL", typeof(double), "0")]
    public async Task AffinityChangesRetainSourceStorageAndRepresentation(
        string sourceStoreType,
        Type sourceClrType,
        string targetStoreType,
        Type targetClrType,
        string valueSql)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            $"CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value {sourceStoreType} NOT NULL, "
            + "CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + $"INSERT INTO lossless_alter_records VALUES (1, {valueSql});");

        await using DbContext context = targetClrType == typeof(long)
            ? new SqliteLosslessAlterTestContext<long>(connection, _internalServices)
            : targetClrType == typeof(double)
                ? new SqliteLosslessAlterTestContext<double>(connection, _internalServices)
                : new SqliteLosslessAlterTestContext<string>(connection, _internalServices);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(
            "lossless_alter_records",
            new ExpectedColumnDefinition("Value", targetClrType, isNullable: false, storeType: targetStoreType),
            new ExpectedColumnDefinition("Value", sourceClrType, isNullable: false, storeType: sourceStoreType),
            SafeMigrationPolicy.RepairIfSafe);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT typeof(Value) || ':' || quote(Value) FROM lossless_alter_records;";
        var originalValue = await command.ExecuteScalarAsync(CancellationToken.None);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-lossless-affinity"));

        var executionError = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var persistedValue = await command.ExecuteScalarAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.IsType<InvalidOperationException>(executionError);
        Assert.Equal(originalValue, persistedValue);
    }

    /// <summary>Same-type nullability tightening and relaxing preserve signed Int64 boundaries exactly.</summary>
    /// <param name="targetNullable">Whether the repair relaxes rather than tightens nullability.</param>
    /// <param name="value">The copied boundary value.</param>
    [Theory]
    [InlineData(false, long.MinValue)]
    [InlineData(false, long.MaxValue)]
    [InlineData(false, 9007199254740993L)]
    [InlineData(true, long.MinValue)]
    [InlineData(true, long.MaxValue)]
    public async Task SameShapeNullabilityRepairsPreserveInt64Boundaries(bool targetNullable, long value)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER "
            + (targetNullable ? "NOT NULL" : "NULL")
            + ", CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + $"INSERT INTO lossless_alter_records VALUES (1, {value.ToString(CultureInfo.InvariantCulture)});");

        await using DbContext context = targetNullable
            ? new SqliteLosslessAlterTestContext<long?>(connection, _internalServices)
            : new SqliteLosslessAlterTestContext<long>(connection, _internalServices);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(
            "lossless_alter_records",
            new ExpectedColumnDefinition("Value", typeof(long), targetNullable, storeType: "INTEGER"),
            new ExpectedColumnDefinition("Value", typeof(long), !targetNullable, storeType: "INTEGER"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-lossless-nullability"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM lossless_alter_records WHERE Id = 1;";
        var persistedValue = await command.ExecuteScalarAsync(CancellationToken.None);
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-lossless-nullability-postflight"));

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[0].Action);
        Assert.Equal(value, persistedValue);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(postflight.Assessments[0].PostconditionSatisfied);
    }

    /// <summary>Wrong old definitions and NULL-bearing source rows cannot authorize a rebuild.</summary>
    /// <param name="sourceCondition">The independently invalid source or value proof.</param>
    [Theory]
    [InlineData("wrong-old")]
    [InlineData("null-value")]
    [InlineData("no-old")]
    public async Task MissingSourceOrRowProofRetainsRowsAndSchema(string sourceCondition)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER NULL, "
            + "CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + "INSERT INTO lossless_alter_records VALUES (1, "
            + (sourceCondition == "null-value" ? "NULL" : "9007199254740993") + ");");

        await using var context = new SqliteLosslessAlterTestContext<long>(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(
            "lossless_alter_records",
            new ExpectedColumnDefinition("Value", typeof(long), isNullable: false, storeType: "INTEGER"),
            sourceCondition == "no-old" ? null : new ExpectedColumnDefinition("Value", typeof(long),
                isNullable: true, storeType: sourceCondition == "wrong-old" ? "BIGINT" : "INTEGER"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-lossless-source-proof"));

        var executionError = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var nullableColumn = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('lossless_alter_records') "
            + "WHERE name = 'Value' AND \"notnull\" = 0;");

        var retainedRows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM lossless_alter_records;");

        // Assert
        Assert.Equal(sourceCondition == "null-value" ? SafeMigrationAction.RejectDataBlocked
            : SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.IsType<InvalidOperationException>(executionError);
        Assert.Equal(1, nullableColumn);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>A new default applies to future rows without changing existing Int64 values.</summary>
    /// <param name="value">The existing boundary value retained across the rebuild.</param>
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(9007199254740993L)]
    public async Task SameShapeDefaultRepairPreservesRowsAndReplays(long value)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER NOT NULL, "
            + "CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + $"INSERT INTO lossless_alter_records VALUES (1, {value.ToString(CultureInfo.InvariantCulture)});");

        await using var context = new SqliteDefaultAlterTestContext(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(
            "lossless_alter_records",
            new ExpectedColumnDefinition("Value", typeof(long), false, storeType: "INTEGER",
                defaultValue: SafeMigrationDefaultValue.Literal(7L)),
            new ExpectedColumnDefinition("Value", typeof(long), false, storeType: "INTEGER"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-lossless-default"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-lossless-default-replay"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connection, "INSERT INTO lossless_alter_records (Id) VALUES (2);");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM lossless_alter_records WHERE Id = 1;";
        var persistedValue = await command.ExecuteScalarAsync(CancellationToken.None);
        var defaultRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM lossless_alter_records WHERE Id = 2 AND Value = 7;");

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, replay.Assessments[0].Action);
        Assert.Equal(value, persistedValue);
        Assert.Equal(1, defaultRows);
    }

    /// <summary>Accepted creation and matching-table evidence cannot erase provider-owned generation changes.</summary>
    /// <param name="tableExists">Whether the source shape is live or projected from an accepted creation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectedGenerationChangesDoNotAuthorizeRepairs(bool tableExists)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        if (tableExists)
        {
            await ExecuteSqlAsync(connection,
                "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER NOT NULL, "
                + "CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
                + "INSERT INTO lossless_alter_records VALUES (1, 9007199254740993);");
        }

        var ddlCounter = new SqliteLosslessAlterDdlCounter();
        await using var context = new SqliteAutoincrementAlterTestContext(connection, _internalServices, ddlCounter);
        var modelGeneration = context.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables
            .Single(table => table.Name == "lossless_alter_records").Columns
            .Single(column => column.Name == "Id").FindAnnotation("Sqlite:Autoincrement")?.Value;

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "lossless_alter_records",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                Value = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_lossless_alter_records", value => value.Id));

        var annotationSource = new AddColumnOperation();
        annotationSource.AddAnnotation("Sqlite:Autoincrement", true);
        var target = new ExpectedColumnDefinition("Id", typeof(int), false, storeType: "INTEGER")
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(annotationSource),
        };

        builder.AlterColumnIfDifferent("lossless_alter_records", target,
            new ExpectedColumnDefinition("Id", typeof(int), false, storeType: "INTEGER"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-lossless-projected-generation"));

        var executionError = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var tableCount = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'lossless_alter_records' AND type = 'table';");

        var autoincrementTables = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'lossless_alter_records' "
            + "AND instr(sql, 'AUTOINCREMENT') > 0;");

        // Assert
        Assert.Equal(true, modelGeneration);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        var rejection = Assert.IsType<InvalidOperationException>(executionError);
        Assert.Contains("SQLite SafeMigrations blocked AlterColumn", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("decision=alter_not_approved", rejection.Message, StringComparison.Ordinal);
        Assert.Equal(0, ddlCounter.Count);
        Assert.Equal(tableExists ? 1 : 0, tableCount);
        Assert.Equal(0, autoincrementTables);
    }

    /// <summary>The rejection observer actually counts accepted baseline DDL, rather than always returning zero.</summary>
    [Fact]
    public async Task BaselineDdlCounterObservesAcceptedTableCreation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var ddlCounter = new SqliteLosslessAlterDdlCounter();
        await using var context = new SqliteAutoincrementAlterTestContext(connection, _internalServices, ddlCounter);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "lossless_alter_records",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                Value = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_lossless_alter_records", value => value.Id));

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var createdTable = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'lossless_alter_records' "
            + "AND instr(sql, 'AUTOINCREMENT') > 0;");

        // Assert
        Assert.True(ddlCounter.Count > 0);
        Assert.Equal(1, createdTable);
    }

    /// <summary>Generated expression changes cannot discard copied values or replace stored computations.</summary>
    /// <param name="change">Whether generation is introduced, changed, or removed.</param>
    /// <param name="stored">Whether generated values are STORED rather than VIRTUAL.</param>
    [Theory]
    [InlineData("add", false)]
    [InlineData("add", true)]
    [InlineData("change", false)]
    [InlineData("change", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    public async Task GeneratedChangesDoNotAuthorizeDirectRepairs(string change, bool stored)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var sourceGenerated = change != "add";
        var targetGenerated = change != "remove";
        await ExecuteSqlAsync(connection,
            "CREATE TABLE lossless_alter_records (Id INTEGER NOT NULL, Value INTEGER NOT NULL"
            + (sourceGenerated ? " GENERATED ALWAYS AS (Id + 1) " + (stored ? "STORED" : "VIRTUAL") : string.Empty)
            + ", CONSTRAINT pk_lossless_alter_records PRIMARY KEY (Id)); "
            + (sourceGenerated
                ? "INSERT INTO lossless_alter_records (Id) VALUES (1);"
                : "INSERT INTO lossless_alter_records VALUES (1, 9007199254740993);"));

        await using var context = new SqliteLosslessAlterTestContext<long>(connection, _internalServices);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(
            "lossless_alter_records",
            new ExpectedColumnDefinition("Value", typeof(long), false, storeType: "INTEGER",
                computedColumnSql: targetGenerated ? "Id + 2" : null,
                isStored: targetGenerated ? stored : null),
            new ExpectedColumnDefinition("Value", typeof(long), false, storeType: "INTEGER",
                computedColumnSql: sourceGenerated ? "Id + 1" : null,
                isStored: sourceGenerated ? stored : null),
            SafeMigrationPolicy.RepairIfSafe);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'lossless_alter_records';";
        var originalSchema = await command.ExecuteScalarAsync(CancellationToken.None);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-lossless-generated-change"));

        var executionError = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var retainedSchema = await command.ExecuteScalarAsync(CancellationToken.None);
        command.CommandText = "SELECT Value FROM lossless_alter_records WHERE Id = 1;";
        var persistedValue = await command.ExecuteScalarAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.IsType<InvalidOperationException>(executionError);
        Assert.Equal(originalSchema, retainedSchema);
        Assert.Equal(sourceGenerated ? 2L : 9007199254740993L, persistedValue);
    }
}
