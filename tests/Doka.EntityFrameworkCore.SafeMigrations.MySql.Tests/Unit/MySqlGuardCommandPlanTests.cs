namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlGuardCommandPlanTests
{
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

        Assert.Equal(5, Count(command.CommandText, "PREPARE doka_sm_statement FROM"));
        Assert.Equal(4, Count(command.CommandText, "EXECUTE doka_sm_statement"));
        Assert.Equal(4, Count(command.CommandText, "DEALLOCATE PREPARE doka_sm_statement"));
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
        Assert.Equal(4, Count(command.CommandText, "PREPARE doka_sm_statement FROM"));
        Assert.Equal(3, Count(command.CommandText, "EXECUTE doka_sm_statement"));
        Assert.Equal(3, Count(command.CommandText, "DEALLOCATE PREPARE doka_sm_statement"));
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

        Assert.Contains(
            payloads,
            payload => payload.Contains("STRICT_TRANS_TABLES", StringComparison.Ordinal)
                && payload.Contains("STRICT_ALL_TABLES", StringComparison.Ordinal));
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
