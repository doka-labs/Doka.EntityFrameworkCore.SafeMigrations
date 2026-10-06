namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlGuardCommandPlanTests
{
    /// <summary>Checks catalog scan count stays bounded as composite indexes gain key parts.</summary>
    [Fact]
    public void WideIndexDoesNotAddCatalogScansPerKeyPart()
    {
        // Arrange
        using var context = CreateContext();
        var oneKey = new MigrationBuilder(context.Database.ProviderName!);
        oneKey.CreateIndexIfNotExists("ix_items_keys", "items", ["a"]);
        var sixKeys = new MigrationBuilder(context.Database.ProviderName!);
        sixKeys.CreateIndexIfNotExists("ix_items_keys", "items", ["a", "b", "c", "d", "e", "f"]);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var oneKeySql = Assert.Single(generator.Generate(oneKey.Operations, context.Model)).CommandText;
        var sixKeySql = Assert.Single(generator.Generate(sixKeys.Operations, context.Model)).CommandText;
        var oneKeyScans = Count(oneKeySql, "INFORMATION_SCHEMA.STATISTICS");
        var sixKeyScans = Count(sixKeySql, "INFORMATION_SCHEMA.STATISTICS");

        // Assert
        Assert.True(oneKeyScans > 0);
        Assert.Equal(oneKeyScans, sixKeyScans);
    }

    /// <summary>Checks catalog scan count stays bounded as a table definition gains columns.</summary>
    /// <remarks>
    /// WHY: Counting one view is not enough. An earlier version of this guard counted only
    /// INFORMATION_SCHEMA.COLUMNS and stayed green while the collation contract issued one
    /// INFORMATION_SCHEMA.TABLES subquery per column, which is the same fan-out in another view.
    /// </remarks>
    [Fact]
    public void WideTableDoesNotAddCatalogScansPerColumn()
    {
        // Arrange
        using var context = CreateContext();
        var oneColumn = new MigrationBuilder(context.Database.ProviderName!);
        oneColumn.EnsureTable(
            new ExpectedTableDefinition("items", [Column("a0")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var eightColumns = new MigrationBuilder(context.Database.ProviderName!);
        eightColumns.EnsureTable(
            new ExpectedTableDefinition(
                "items",
                [
                    Column("a0"), Column("a1"), Column("a2"), Column("a3"),
                    Column("a4"), Column("a5"), Column("a6"), Column("a7"),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var oneColumnSql = Assert.Single(generator.Generate(oneColumn.Operations, context.Model)).CommandText;
        var eightColumnSql = Assert.Single(generator.Generate(eightColumns.Operations, context.Model)).CommandText;
        var oneColumnScans = Count(oneColumnSql, "FROM INFORMATION_SCHEMA.");
        var eightColumnScans = Count(eightColumnSql, "FROM INFORMATION_SCHEMA.");

        // Assert
        Assert.True(oneColumnScans > 0);
        Assert.Equal(oneColumnScans, eightColumnScans);
    }

    /// <summary>Checks column verification keeps the table name a catalog lookup value.</summary>
    /// <remarks>
    /// WHY: MariaDB fills INFORMATION_SCHEMA by opening table definitions and only restricts that
    /// work to one table when the name is a constant in the WHERE clause. Moving the name into a
    /// join condition against a derived expected set makes it open every table in the schema per
    /// join: in a paired run on MariaDB 11.8, analyzing 100 expected tables took 2,218 ms in that
    /// form against 167 ms in the counting form, and 3,611 ms against 132 ms with 1,000 further
    /// tables in the schema. MySQL 8.4 stayed within one percent either way, so the regression is
    /// invisible there.
    /// </remarks>
    [Fact]
    public void ColumnVerificationKeepsTheTableNameAsCatalogLookupValue()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition("items", [Column("a0"), Column("a1"), Column("a2")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var sql = Assert.Single(generator.Generate(builder.Operations, context.Model)).CommandText;

        // Assert
        Assert.DoesNotContain("LEFT JOIN INFORMATION_SCHEMA.COLUMNS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN INFORMATION_SCHEMA.COLUMNS c ON", sql, StringComparison.Ordinal);
        Assert.Contains(
            "(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_SCHEMA = DATABASE() "
            + "AND c.TABLE_NAME = 'items' AND c.ORDINAL_POSITION <= 3 "
            + "AND (c.COLLATION_NAME IS NULL OR c.COLLATION_NAME <=> "
            + "(SELECT t.TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES t "
            + "WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = 'items')) "
            + "AND (CASE c.ORDINAL_POSITION WHEN 1 THEN",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(") = 3", sql, StringComparison.Ordinal);
    }

    /// <summary>Checks analysis and verification preserve the parameterized constant catalog lookup.</summary>
    /// <param name="postflight">Whether the postcondition or classification expression is rendered.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParameterizedColumnVerificationKeepsTheTableNameAsCatalogLookupValue(
        bool postflight
    )
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition("items", [Column("a0"), Column("a1"), Column("a2")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var generator = context.GetService<IMigrationsSqlGenerator>();
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        using var lease = capture.Begin(builder.Operations.Cast<SafeMigrationOperation>().ToArray());
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command,
            context.GetService<IRelationalTypeMappingSource>());

        // Act
        _ = generator.Generate(builder.Operations, context.Model);
        var plan = lease.Complete().Single();
        var sql = postflight
            ? plan.RenderPostcondition(parameterizer.Add)
            : plan.RenderStateExpression(parameterizer.Add);

        var tableParameter = command.Parameters.Cast<DbParameter>().Single(parameter => Equals(parameter.Value, "items"));

        // Assert
        Assert.Contains(
            "(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_SCHEMA = DATABASE() "
            + $"AND c.TABLE_NAME = {tableParameter.ParameterName} AND c.ORDINAL_POSITION <= 3 "
            + "AND (c.COLLATION_NAME IS NULL OR c.COLLATION_NAME <=> "
            + "(SELECT t.TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES t "
            + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {tableParameter.ParameterName})) "
            + "AND (CASE c.ORDINAL_POSITION WHEN 1 THEN",
            sql,
            StringComparison.Ordinal);
        Assert.Equal(1, Count(sql, "TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES"));
        Assert.DoesNotContain("LEFT JOIN INFORMATION_SCHEMA.COLUMNS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN INFORMATION_SCHEMA.COLUMNS c ON", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'items'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSqlGenerator_RejectsKnownIndexBeforeColumnDropWithoutEnsureTable()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_items_legacy", "items", ["legacy"]);
        builder.DropColumnIfExists("legacy", "items");

        // Act
        var exception = Record.Exception(() => context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("Drop the index explicitly", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSqlGenerator_AcceptsExplicitIndexDropBeforeColumnDropWithoutEnsureTable()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_items_legacy", "items", ["legacy"]);
        builder.DropIndexIfExists("ix_items_legacy", "items");
        builder.DropColumnIfExists("legacy", "items");

        // Act
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        // Assert
        Assert.Equal(3, commands.Count);
    }

    [Fact]
    public void DatabaseQualifiedOperationGuardsIdentityBeforeCatalogAccessAndPreservesQualifiedDdl()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=application;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int"),
                "application"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([operation], context.Model));

        var payloads = DecodeHexPayloads(command.CommandText);
        var identityGuard = command.CommandText.IndexOf("BINARY DATABASE() = BINARY", StringComparison.Ordinal);
        var prerequisite = command.CommandText.IndexOf("@doka_sm_prerequisite_ok", StringComparison.Ordinal);

        Assert.True(identityGuard >= 0);
        Assert.True(prerequisite > identityGuard);
        Assert.Contains(
            payloads,
            payload => payload.StartsWith(
                "ALTER TABLE `application`.`items` ADD ",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnqualifiedOperationDoesNotAddDatabaseIdentityGuard()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=application;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([operation], context.Model));

        Assert.DoesNotContain("BINARY DATABASE() = BINARY", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamQualifierGuardsEveryEarlierUnqualifiedOperation()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "users",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddUniqueConstraintIfNotExists(
            "uq_users_id",
            "users",
            ["id"],
            schema: "foreign");

        // Act
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        // Assert
        Assert.Equal(2, commands.Count);
        Assert.All(
            commands,
            command =>
            {
                Assert.Contains("BINARY DATABASE() = BINARY", command.CommandText, StringComparison.Ordinal);
                Assert.Contains("BINARY 'foreign'", command.CommandText, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void DistinctStreamQualifiersGuardEveryOperationWithAnImpossibleConjunction()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "users",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddUniqueConstraintIfNotExists(
            "uq_users_id",
            "users",
            ["id"],
            schema: "first");
        builder.CreateIndexIfNotExists(
            "ix_users_id",
            "users",
            ["id"],
            schema: "second");

        // Act
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        // Assert
        Assert.Equal(3, commands.Count);
        Assert.All(
            commands,
            command =>
            {
                Assert.Contains("BINARY 'first'", command.CommandText, StringComparison.Ordinal);
                Assert.Contains("BINARY 'second'", command.CommandText, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void DataReadingSingleBaselineOperation_HasExactBoundedScopedCommandShape()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.Model);

        var command = Assert.Single(commands);

        Assert.Equal(
            "MySqlScopedMigrationCommand",
            command.GetType()
                .Name);
        Assert.Equal(3, Count(command.CommandText, "PREPARE doka_sm_statement FROM"));
        Assert.Equal(2, Count(command.CommandText, "EXECUTE doka_sm_statement"));
        Assert.Equal(2, Count(command.CommandText, "DEALLOCATE PREPARE doka_sm_statement"));
        Assert.Contains("@doka_sm_prerequisite_ok", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`",
            command.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogOnlySingleBaselineOperation_AvoidsLazyStateCommands()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.Model);

        var command = Assert.Single(commands);

        Assert.Equal(
            "MySqlScopedMigrationCommand",
            command.GetType()
                .Name);
        Assert.Equal(2, Count(command.CommandText, "PREPARE doka_sm_statement FROM"));
        Assert.Equal(1, Count(command.CommandText, "EXECUTE doka_sm_statement"));
        Assert.Equal(1, Count(command.CommandText, "DEALLOCATE PREPARE doka_sm_statement"));
        Assert.DoesNotContain("SET @doka_sm_prerequisite_ok = COALESCE", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentedBaselineOperation_UsesProviderValidatedSqlModeFragments()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition(
                    "value",
                    typeof(string),
                    isNullable: true,
                    storeType: "varchar(40)",
                    comment: "mode\\safe")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([operation], context.Model));

        var captureIndex = command.CommandText.IndexOf("@__doka_previous_sql_mode", StringComparison.Ordinal);
        var bodyIndex = command.CommandText.IndexOf(
            "PREPARE doka_sm_statement FROM @doka_sm_sql",
            StringComparison.Ordinal);

        var cleanupIndex = command.CommandText.LastIndexOf("SET SESSION sql_mode", StringComparison.OrdinalIgnoreCase);
        var guardCleanupIndex = command.CommandText.LastIndexOf(
            "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`",
            StringComparison.Ordinal);

        Assert.Equal(
            "MySqlScopedMigrationCommand",
            command.GetType()
                .Name);
        Assert.True(captureIndex >= 0);
        Assert.True(bodyIndex > captureIndex);
        Assert.True(cleanupIndex > bodyIndex);
        Assert.True(guardCleanupIndex > cleanupIndex);
    }

    [Fact]
    public void RepairableEnsureColumn_EmbedsDistinctProviderApplyAndRepairDdl()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition(
                    "value",
                    typeof(string),
                    isNullable: false,
                    storeType: "varchar(40)",
                    maxLength: 40,
                    comment: "canonical",
                    defaultValue: SafeMigrationDefaultValue.Literal("canonical"))),
            SafeMigrationPolicy.RepairIfSafe);

        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([operation], context.Model));

        var payloads = DecodeHexPayloads(command.CommandText);

        Assert.Equal(6, Count(command.CommandText, "PREPARE doka_sm_statement FROM"));
        Assert.Equal(5, Count(command.CommandText, "EXECUTE doka_sm_statement"));
        Assert.Equal(5, Count(command.CommandText, "DEALLOCATE PREPARE doka_sm_statement"));
        Assert.Contains("WHEN @doka_sm_action = 'apply'", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("WHEN @doka_sm_action = 'repair'", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN @doka_sm_state IS NULL", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            payloads,
            payload => payload.Contains(
                "THEN ('missing') ELSE NULL END INTO @doka_sm_state",
                StringComparison.Ordinal));
        Assert.Contains(
            payloads,
            payload => payload.StartsWith("ALTER TABLE `items` ADD ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(payloads, payload => payload.Contains("MODIFY COLUMN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepairableRequiredColumn_MaterializesDataProbeOnlyBehindPreparedPrerequisiteGate()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "int")),
            SafeMigrationPolicy.RepairIfSafe);

        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([operation], context.Model));

        var payloads = DecodeHexPayloads(command.CommandText);
        const string dataProbe = "EXISTS (SELECT 1 FROM `items` LIMIT 1)";

        Assert.DoesNotContain(dataProbe, command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            payloads,
            payload => payload.Contains(dataProbe, StringComparison.Ordinal)
                && payload.EndsWith("INTO @doka_sm_state", StringComparison.Ordinal));
        Assert.Equal(5, Count(command.CommandText, "PREPARE doka_sm_statement FROM"));
        Assert.Equal(4, Count(command.CommandText, "EXECUTE doka_sm_statement"));
        Assert.Equal(4, Count(command.CommandText, "DEALLOCATE PREPARE doka_sm_statement"));
    }

    [Fact]
    public void VarcharNarrowing_RequiresStrictSessionModeAndNeverRendersIgnore()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition(
                    "value",
                    typeof(string),
                    isNullable: true,
                    storeType: "varchar(5)",
                    maxLength: 5)),
            SafeMigrationPolicy.RepairIfSafe);

        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([operation], context.Model));

        var payloads = DecodeHexPayloads(command.CommandText);

        Assert.Contains("STRICT_TRANS_TABLES", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("STRICT_ALL_TABLES", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            "SET @doka_sm_repair_ok = CASE WHEN @doka_sm_state = 'different' THEN COALESCE((",
            command.CommandText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER IGNORE TABLE", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            payloads,
            payload => payload.Contains("ALTER IGNORE TABLE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModelManagedUpdateUsesTypedCompareAndSetBehindTheGuard()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        using var context = new DbContext(options.Options);
        var operation = new SafeMigrationOperation(
            new UpdateModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                new object?[,] { { 1 } },
                ["name"],
                ["varchar(64)"],
                new object?[,] { { "administrator" } },
                new object?[,] { { "owner" } },
                schema: null,
                uniqueKeys: null),
            SafeMigrationPolicy.ThrowIfDifferent);

        var command = Assert.Single(
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        var payloads = DecodeHexPayloads(command.CommandText);

        Assert.Contains("WHEN 'transition_ready' THEN 'apply'", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(
            payloads,
            payload => payload.StartsWith("UPDATE `roles` AS doka_actual JOIN", StringComparison.Ordinal)
                && payload.Contains("SET doka_actual.`name` = doka_expected.`n0`", StringComparison.Ordinal)
                && payload.Contains("doka_actual.`name` <=> doka_expected.`o0`", StringComparison.Ordinal)
                && payload.Contains("NOT (doka_actual.`name` <=> doka_expected.`n0`)", StringComparison.Ordinal));
    }

    private static int Count(
        string value,
        string search
    )
    {
        var count = 0;
        var offset = 0;

        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    /// <summary>Checks the relation binding redirects classification to a catalog snapshot.</summary>
    /// <param name="redirect">Whether a snapshot binding is supplied.</param>
    /// <remarks>
    /// WHY: The binding is what makes a snapshot possible without restating a single facet. This
    /// pins both directions: no binding leaves the live views in place, and a binding moves every
    /// relation while the facet predicates around them stay byte-identical.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogRelationBindingRedirectsEveryRelation(
        bool redirect
    )
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition("items", [Column("a0"), Column("a1")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var generator = context.GetService<IMigrationsSqlGenerator>();
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        using var lease = capture.Begin(builder.Operations.Cast<SafeMigrationOperation>().ToArray());
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command,
            context.GetService<IRelationalTypeMappingSource>());

        // Act
        _ = generator.Generate(builder.Operations, context.Model);
        var plan = lease.Complete().Single();
        var bound = redirect
            ? plan with { CatalogRelations = static view => $"`snap_{view.Length}`" }
            : plan;

        var sql = bound.RenderStateExpression(parameterizer.Add);

        // Assert
        if (redirect)
        {
            Assert.DoesNotContain("INFORMATION_SCHEMA.", sql, StringComparison.Ordinal);
            Assert.Contains("`snap_26`", sql, StringComparison.Ordinal);
            Assert.Contains("`snap_25`", sql, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("INFORMATION_SCHEMA.COLUMNS", sql, StringComparison.Ordinal);
            Assert.Contains("INFORMATION_SCHEMA.TABLES", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("`snap_", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>Checks the runtime generation path keeps reading the live catalog.</summary>
    /// <remarks>
    /// WHY: Only batched analysis can amortise a snapshot; a scoped command classifies one
    /// operation. The redirection must therefore never reach generated migration SQL, which also
    /// keeps that path free of the scan the binding performs.
    /// </remarks>
    [Fact]
    public void GeneratedCommandsReadTheLiveCatalog()
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition("items", [Column("a0"), Column("a1")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.CreateIndexIfNotExists("ix_items_a0", "items", ["a0"]);

        // Act
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        // Assert
        Assert.NotEmpty(commands);
        Assert.All(
            commands,
            command =>
            {
                Assert.Contains("INFORMATION_SCHEMA.", command.CommandText, StringComparison.Ordinal);
                Assert.DoesNotContain("__doka_sm_cat_", command.CommandText, StringComparison.Ordinal);
            });
    }

    private static ExpectedColumnDefinition Column(
        string name
    ) => new(name, typeof(int), isNullable: false, storeType: "int");

    private static DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DbContext>();
        options.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        ((DbContextOptionsBuilder)options).UseMySqlSafeMigrations();

        return new DbContext(options.Options);
    }

    private static List<string> DecodeHexPayloads(
        string sql
    )
    {
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";

        var result = new List<string>();
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var valueStart = offset + prefix.Length;
            var valueEnd = sql.IndexOf(suffix, valueStart, StringComparison.Ordinal);
            if (valueEnd < 0)
            {
                throw new InvalidOperationException("A generated hexadecimal SQL payload is unterminated.");
            }

            result.Add(Encoding.UTF8.GetString(Convert.FromHexString(sql[valueStart..valueEnd])));
            offset = valueEnd + suffix.Length;
        }

        return result;
    }
}
