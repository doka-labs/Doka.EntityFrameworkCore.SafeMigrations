namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Qualifies persisted string defaults independently of table-cache and session escape state.</summary>
public sealed class MySqlPersistedStringDefaultIntegrationTests : MySqlIntegrationTestBase
{
    /// <summary>Shares the engine while retaining one isolated database lease per test.</summary>
    /// <param name="fixture">The assembly-owned native engine.</param>
    public MySqlPersistedStringDefaultIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Reopened TEXT defaults preserve actual newlines rather than their escaped spelling.</summary>
    /// <param name="noBackslashEscapes">Whether the defining session disables backslash escapes.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureColumnStructuredNewlineSurvivesPersistedDefaultReparse(bool noBackslashEscapes)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `persisted_defaults` (`id` int NOT NULL);");
        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(string), false, "text",
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("N\new")))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("persisted-newline-default"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation]);
        await context.Database.CloseConnectionAsync();
        var stored = await InsertReopenedDefaultsAsync(connectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var replay = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("persisted-newline-default-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [operation]);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(2, stored.Length);
        Assert.All(stored, static value => Assert.Equal("4E0A6577", value));
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
    }

    /// <summary>Known defaults retain exact bytes across create, add, persistence and idempotent replay.</summary>
    /// <param name="createTable">Whether the default belongs to an entirely new table.</param>
    /// <param name="structured">Whether the default uses the structured-expression rather than literal API.</param>
    /// <param name="value">The independently encoded expected string payload.</param>
    /// <param name="noBackslashEscapes">Whether the defining session disables backslash escapes.</param>
    [Theory]
    [InlineData(false, false, "", false)]
    [InlineData(false, false, "N\new", true)]
    [InlineData(false, false, "N\\new", true)]
    [InlineData(false, false, "N'ew", true)]
    [InlineData(false, true, "N\0ew", true)]
    [InlineData(false, true, "N\tew", false)]
    [InlineData(false, true, "\u20AC\u6F22\U0001F642", false)]
    [InlineData(false, true, "N\r\n\\'ew", true)]
    [InlineData(true, false, "", false)]
    [InlineData(true, false, "N\new", true)]
    [InlineData(true, false, "N\\new", true)]
    [InlineData(true, false, "N'ew", true)]
    [InlineData(true, true, "N\0ew", true)]
    [InlineData(true, true, "N\tew", false)]
    [InlineData(true, true, "\u20AC\u6F22\U0001F642", false)]
    [InlineData(true, true, "N\r\n\\'ew", true)]
    public async Task KnownStringDefaultsPreserveExactBytesAfterReopen(
        bool createTable,
        bool structured,
        string value,
        bool noBackslashEscapes)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (!createTable)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE `persisted_defaults` (`id` int NOT NULL) DEFAULT CHARSET=utf8mb4;");
        }

        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var defaultValue = structured
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value))
            : SafeMigrationDefaultValue.Literal(value);

        var operation = CreateStringDefaultOperation(createTable, defaultValue);
        var expectedHex = Convert.ToHexString(Encoding.UTF8.GetBytes(value));

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.Apply, expectedHex);
    }

    /// <summary>String leaves nested in a function preserve their distinct literal identities.</summary>
    /// <param name="createTable">Whether the complete table or only its missing column is created.</param>
    /// <param name="value">The expected selected branch, including an actual or escaped newline.</param>
    /// <param name="noBackslashEscapes">Whether the defining session disables backslash escapes.</param>
    [Theory]
    [InlineData(false, "N\\new", false)]
    [InlineData(false, "N\new", true)]
    [InlineData(true, "N\new", false)]
    [InlineData(true, "N\\new", true)]
    public async Task NestedStructuredDefaultsPreserveSelectedStringBranch(
        bool createTable,
        string value,
        bool noBackslashEscapes)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (!createTable)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE `persisted_defaults` (`id` int NOT NULL);");
        }

        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var expression = SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Literal(null),
            SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Literal(value),
                SafeMigrationSql.Literal("unselected\\fallback")));

        var operation = CreateStringDefaultOperation(createTable, SafeMigrationDefaultValue.Sql(expression));
        var expectedHex = Convert.ToHexString(Encoding.UTF8.GetBytes(value));

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.Apply, expectedHex);
    }

    /// <summary>Accepted MySQL backfills retain both repaired values and persisted expression-default bytes.</summary>
    /// <param name="noBackslashEscapes">Whether the defining session disables backslash escapes.</param>
    [MySqlPersistedDefaultTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterColumnNewlineBackfillAndReopenedDefaultAgree(bool noBackslashEscapes)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `persisted_defaults` (`id` int NOT NULL, `value` varchar(20) NULL) "
            + "DEFAULT CHARSET=utf8mb4; INSERT INTO `persisted_defaults` VALUES (7, NULL);");
        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(string), false, "text",
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("N\new"))),
            new ExpectedColumnDefinition("value", typeof(string), true, "varchar(20)")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);
        var backfilled = await ScalarStringAsync(connectionString,
            "SELECT HEX(`value`) FROM `persisted_defaults` WHERE `id` = 7;");

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.Repair, "4E0A6577");
        Assert.Equal("4E0A6577", backfilled);
    }

    /// <summary>
    /// Default-only repairs use the same stable persistence path without rewriting existing NULL values.
    /// </summary>
    /// <param name="noBackslashEscapes">Whether the defining session disables backslash escapes.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureColumnDefaultRepairPreservesNullAndReopenedDefault(bool noBackslashEscapes)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `persisted_defaults` (`id` int NOT NULL, `value` text NULL) "
            + "DEFAULT CHARSET=utf8mb4; INSERT INTO `persisted_defaults` VALUES (7, NULL);");
        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(string), true, "text",
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("N\new")))),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);
        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `persisted_defaults` WHERE `id` = 7 AND `value` IS NULL;");

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.Repair, "4E0A6577");
        Assert.Equal(1, unchanged);
    }

    /// <summary>
    /// Literal backslash sequences cannot impersonate actual control characters in an existing default.
    /// </summary>
    /// <param name="createTable">Whether comparison uses the complete table or column contract.</param>
    /// <param name="expected">The desired value whose distinct default must not be accepted.</param>
    /// <param name="actual">The independently authored default that must remain unchanged.</param>
    [Theory]
    [InlineData(false, "N\new", "N\\new")]
    [InlineData(false, "N\\new", "N\new")]
    [InlineData(true, "N\new", "N\\new")]
    [InlineData(true, "N\\new", "N\new")]
    public async Task DistinctEscapedDefaultCannotBeAcceptedAsMatching(bool createTable, string expected, string actual)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var actualHex = Convert.ToHexString(Encoding.UTF8.GetBytes(actual));
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `persisted_defaults` (`id` int NOT NULL, "
            + "`value` text COLLATE utf8mb4_unicode_ci NOT NULL "
            + $"DEFAULT (CONVERT(0x{actualHex} USING utf8mb4)));");
        await using var context = CreateContext(connectionString);
        var operation = CreateStringDefaultOperation(createTable,
            SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(expected)));

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("distinct-persisted-default"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var stored = await InsertReopenedDefaultsAsync(connectionString);

        // Assert
        var assessment = Assert.Single(preflight.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("doka_sm_different", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(2, stored.Length);
        Assert.All(stored, value => Assert.Equal(actualHex, value));
    }

    /// <summary>
    /// Explicit CLR character defaults retain their textual identity rather than a numeric or binary shape.
    /// </summary>
    /// <param name="value">The exact character, including control and quote-sensitive payloads.</param>
    /// <param name="noBackslashEscapes">Whether the defining session disables backslash escapes.</param>
    [Theory]
    [InlineData('\n', false)]
    [InlineData('\n', true)]
    [InlineData('\\', false)]
    [InlineData('\'', true)]
    public async Task StructuredCharDefaultPreservesExactCharacter(char value, bool noBackslashEscapes)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `persisted_defaults` (`id` int NOT NULL) DEFAULT CHARSET=utf8mb4;");
        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(char), false, "char(1)", maxLength: 1, isFixedLength: true,
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value)))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var expectedHex = Convert.ToHexString(Encoding.UTF8.GetBytes(value.ToString()));

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.Apply, expectedHex);
    }

    /// <summary>
    /// A mode-dependent legacy expression is not accepted merely because its current display resembles the target.
    /// </summary>
    /// <param name="noBackslashEscapes">Whether the comparison and guarded execution disable backslash escapes.</param>
    [MySqlPersistedDefaultTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictPolicyRejectsLegacyModeDependentDefaultWithoutMutation(bool noBackslashEscapes)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await CreateLegacyNewlineDefaultAsync(connectionString);
        var beforeCatalog = await ReadDefaultCatalogAsync(connectionString);
        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(string), true, "text",
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("N\new")))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("legacy-mode-dependent-default"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        await context.Database.CloseConnectionAsync();
        var stored = await InsertReopenedDefaultsAsync(connectionString);
        var afterCatalog = await ReadDefaultCatalogAsync(connectionString);
        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `persisted_defaults` WHERE `id` = 7 AND `value` IS NULL;");

        // Assert
        var assessment = Assert.Single(preflight.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("doka_sm_different", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(beforeCatalog, afterCatalog);
        Assert.Equal(2, stored.Length);
        Assert.Equal("4E0A6577", stored[0]);
        Assert.Equal("4E5C6E6577", stored[1]);
        Assert.Equal(1, unchanged);
    }

    /// <summary>
    /// Explicitly approved default-only repair replaces legacy serialization without changing existing data.
    /// </summary>
    /// <param name="noBackslashEscapes">Whether the repair session disables backslash escapes.</param>
    [MySqlPersistedDefaultTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairPolicyReplacesLegacyModeDependentDefaultAndReplays(bool noBackslashEscapes)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await CreateLegacyNewlineDefaultAsync(connectionString);
        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(string), true, "text",
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("N\new")))),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);
        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `persisted_defaults` WHERE `id` = 7 AND `value` IS NULL;");

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.Repair, "4E0A6577");
        Assert.Equal(1, unchanged);
    }

    /// <summary>Binary defaults remain typed bytes rather than being reinterpreted as UTF-8 text.</summary>
    /// <param name="noBackslashEscapes">Whether the defining session disables backslash escapes.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryDefaultRemainsOutsideStringConversion(bool noBackslashEscapes)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `persisted_defaults` (`id` int NOT NULL);");
        var settings = new MySqlConnectionStringBuilder(connectionString)
        {
            NoBackslashEscapes = noBackslashEscapes,
        };

        await using var context = CreateContext(settings.ConnectionString);
        await context.Database.OpenConnectionAsync();
        await SetEscapeModeAsync(context.Database.GetDbConnection(), noBackslashEscapes);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(byte[]), false, "varbinary(8)", maxLength: 8,
                defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 1, 10, 92, 110, 255 }))),
            SafeMigrationPolicy.ThrowIfDifferent);

        DefaultCycleResult? result = null;

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            result = await ExecuteDefaultCycleAsync(context, connectionString, operation);
        });

        var catalog = await ScalarStringAsync(connectionString,
            "SELECT CONCAT(COALESCE(COLUMN_DEFAULT, '<null>'), '|', HEX(COALESCE(COLUMN_DEFAULT, '')), '|', EXTRA) "
            + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'persisted_defaults' AND COLUMN_NAME = 'value';");

        // Assert
        Assert.True(failure is null, $"Native binary default failure: {failure}. Synthetic catalog: {catalog}");
        AssertDefaultCycle(Assert.IsType<DefaultCycleResult>(result), SafeMigrationAction.Apply, "010A5C6EFF");
    }

    /// <summary>
    /// Native literal proof preserves zero bytes while structured defaults retain their separate supported path.
    /// </summary>
    /// <param name="hex">The exact payload with a leading, internal or trailing zero byte.</param>
    /// <param name="structured">Whether the value is explicitly an expression default.</param>
    [Theory]
    [InlineData("000102", false)]
    [InlineData("010003", false)]
    [InlineData("010200", false)]
    [InlineData("000102", true)]
    [InlineData("010003", true)]
    [InlineData("010200", true)]
    public async Task BinaryZeroBytesRetainNativeLiteralAndExpressionIdentity(string hex, bool structured)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `persisted_defaults` (`id` int NOT NULL); INSERT INTO `persisted_defaults` VALUES (7);");
        await using var context = CreateContext(connectionString);
        var bytes = Convert.FromHexString(hex);
        var defaultValue = structured
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(bytes))
            : SafeMigrationDefaultValue.Literal(bytes);

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(byte[]), false, "varbinary(8)", maxLength: 8,
                defaultValue: defaultValue)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);
        var nativeDefault = structured
            ? null
            : await ScalarStringAsync(connectionString,
                "SELECT HEX(DEFAULT(`value`)) FROM `persisted_defaults` LIMIT 1;");

        var retainedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `persisted_defaults` WHERE `id` = 7;");

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.Apply, hex);
        Assert.Equal(1, retainedRows);
        if (!structured)
        {
            Assert.Equal(hex, nativeDefault);
        }
    }

    /// <summary>Native binary literals retain creation, required-add and fixed-width storage semantics.</summary>
    /// <param name="createTable">Whether the complete table or only the binary column is initially missing.</param>
    /// <param name="nullable">Whether the binary column accepts NULL.</param>
    /// <param name="populated">Whether an existing owner contains a row before ADD.</param>
    /// <param name="fixedLength">Whether native BINARY padding applies.</param>
    /// <param name="hex">The literal bytes, including empty and NUL-containing defaults.</param>
    [Theory]
    [InlineData(true, false, false, false, "")]
    [InlineData(true, true, false, false, "000102")]
    [InlineData(true, false, false, true, "01")]
    [InlineData(true, true, false, true, "010003")]
    [InlineData(false, false, false, false, "000102")]
    [InlineData(false, true, true, false, "010003")]
    [InlineData(false, false, true, true, "01")]
    [InlineData(false, true, false, true, "010003")]
    public async Task BinaryLiteralCreateAndAddPreserveNativeStorage(
        bool createTable,
        bool nullable,
        bool populated,
        bool fixedLength,
        string hex)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (!createTable)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE `persisted_defaults` (`id` int NOT NULL);"
                + (populated ? " INSERT INTO `persisted_defaults` VALUES (7);" : string.Empty));
        }

        await using var context = CreateContext(connectionString);
        var column = new ExpectedColumnDefinition("value", typeof(byte[]), nullable,
            fixedLength ? "binary(8)" : "varbinary(8)", maxLength: 8, isFixedLength: fixedLength,
            defaultValue: SafeMigrationDefaultValue.Literal(Convert.FromHexString(hex)));

        SafeMigrationIntent intent = createTable
            ? new EnsureTableIntent(new ExpectedTableDefinition("persisted_defaults",
                    [new ExpectedColumnDefinition("id", typeof(int), false, "int"), column]),
                SafeMigrationTableMode.StrictDefinition)
            : new EnsureColumnIntent("persisted_defaults", column);

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var expectedHex = fixedLength ? hex.PadRight(16, '0') : hex;

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);
        var existing = populated
            ? await ScalarStringAsync(connectionString,
                "SELECT HEX(`value`) FROM `persisted_defaults` WHERE `id` = 7;")
            : null;

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.Apply, expectedHex);
        if (populated)
        {
            Assert.Equal(expectedHex, existing);
        }
    }

    /// <summary>Default-only binary repair refreshes native proof without replacing existing NULL data.</summary>
    /// <param name="hex">The desired literal default with a leading or trailing NUL.</param>
    [Theory]
    [InlineData("000102")]
    [InlineData("010200")]
    public async Task BinaryLiteralRepairRefreshesDefaultProofAndReplays(string hex)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `persisted_defaults` (`id` int NOT NULL, `value` varbinary(8) NULL); "
            + "INSERT INTO `persisted_defaults` VALUES (7, NULL);");
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(byte[]), true, "varbinary(8)", maxLength: 8,
                defaultValue: SafeMigrationDefaultValue.Literal(Convert.FromHexString(hex)))),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);
        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `persisted_defaults` WHERE `id` = 7 AND `value` IS NULL;");

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.Repair, hex);
        Assert.Equal(1, unchanged);
    }

    /// <summary>Exact native expression metadata never binds the literal-only DEFAULT(column) intrinsic.</summary>
    /// <param name="structured">Whether the desired bytes use a structured or literal default contract.</param>
    [MySqlPersistedDefaultTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedBinaryDefaultUsesExactExpressionProofWithoutLiteralBinding(bool structured)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `persisted_defaults` (`id` int NOT NULL, "
            + "`value` varbinary(8) NOT NULL DEFAULT (0x010003));");
        await using var context = CreateContext(connectionString);
        var bytes = Convert.FromHexString("010003");
        var defaultValue = structured
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(bytes))
            : SafeMigrationDefaultValue.Literal(bytes);

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(byte[]), false, "varbinary(8)", maxLength: 8,
                defaultValue: defaultValue)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = await ExecuteDefaultCycleAsync(context, connectionString, operation);
        var generated = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'persisted_defaults' AND COLUMN_NAME = 'value' "
            + "AND FIND_IN_SET('DEFAULT_GENERATED', REPLACE(EXTRA, ' ', ',')) > 0;");

        // Assert
        AssertDefaultCycle(result, SafeMigrationAction.NoOp, "010003");
        Assert.Equal(1, generated);
    }

    /// <summary>Quoted and marker-shaped owners remain identifiers throughout native default proof.</summary>
    /// <param name="table">The independently quoted table name.</param>
    /// <param name="column">The independently quoted binary column name.</param>
    [MySqlPersistedDefaultTheory]
    [InlineData("literal`owners", "value`part")]
    [InlineData("@doka_sm_binary_default_0", "__DOKA_SM_BINARY_DEFAULT_0__")]
    public async Task NativeBinaryDefaultProofRetainsQuotedOwnerIdentity(string table, string column)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var quotedTable = QuoteIdentifier(table);
        var quotedColumn = QuoteIdentifier(column);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE {quotedTable} (`id` int NOT NULL, {quotedColumn} varbinary(8) NOT NULL DEFAULT 0x010003);");
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent(table,
            new ExpectedColumnDefinition(column, typeof(byte[]), false, "varbinary(8)", maxLength: 8,
                defaultValue: SafeMigrationDefaultValue.Literal(Convert.FromHexString("010003")))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("quoted-native-binary-default"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [operation]);
        var stored = await InsertReopenedDefaultsAsync(connectionString, table, column);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("quoted-native-binary-default-postflight"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("quoted-native-binary-default-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [operation]);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(preflight.Assessments).Action);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(2, stored.Length);
        Assert.All(stored, static value => Assert.Equal("010003", value));
    }

    /// <summary>
    /// Runtime classification overwrites stale proof and cleans its session variable on success and rejection.
    /// </summary>
    /// <param name="different">Whether the desired bytes differ from the native literal default.</param>
    [MySqlPersistedDefaultTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeBinaryDefaultProofCleansAcquiredSessionState(bool different)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `persisted_defaults` (`id` int NOT NULL, "
            + "`value` varbinary(8) NOT NULL DEFAULT 0x010003);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(byte[]), false, "varbinary(8)", maxLength: 8,
                defaultValue: SafeMigrationDefaultValue.Literal(Convert.FromHexString(different ? "01" : "010003")))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // WHY: The reserved variable is deliberately poisoned with the shorter
        // value; a stale certificate must not satisfy the rejected prefix contract.
        await context.Database.ExecuteSqlRawAsync("SET @doka_sm_binary_default_0 = 0x01;");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("native-binary-default-proof-cleanup"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var cleared = await ContextScalarIntAsync(context, "SELECT @doka_sm_binary_default_0 IS NULL;");
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'persisted_defaults' AND COLUMN_NAME = 'value' AND COLUMN_TYPE = 'varbinary(8)';");

        // Assert
        Assert.Equal(different ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.NoOp,
            Assert.Single(preflight.Assessments).Action);
        if (different)
        {
            Assert.Equal("doka_sm_different", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, cleared);
        Assert.Equal(1, columns);
    }

    /// <summary>
    /// Lossy or truncated catalog text cannot make an existing binary default impersonate different bytes.
    /// </summary>
    /// <param name="actualHex">The independently authored byte value that must remain unchanged.</param>
    /// <param name="expectedHex">The different structured binary default whose comparison must reject.</param>
    [Theory]
    [InlineData("FF", "3F")]
    [InlineData("3F", "FF")]
    [InlineData("010003", "01")]
    [InlineData("010200", "0102")]
    public async Task IncompleteBinaryCatalogCannotImpersonateDifferentBytes(string actualHex, string expectedHex)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `persisted_defaults` (`id` int NOT NULL, "
            + $"`value` varbinary(8) NOT NULL DEFAULT 0x{actualHex});");
        var beforeCatalog = await ReadDefaultCatalogAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("persisted_defaults",
            new ExpectedColumnDefinition("value", typeof(byte[]), false, "varbinary(8)", maxLength: 8,
                defaultValue: SafeMigrationDefaultValue.Sql(
                    SafeMigrationSql.Literal(Convert.FromHexString(expectedHex))))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("ambiguous-binary-default"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var stored = await InsertReopenedDefaultsAsync(connectionString);
        var afterCatalog = await ReadDefaultCatalogAsync(connectionString);

        // Assert
        var assessment = Assert.Single(preflight.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("doka_sm_different", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(beforeCatalog, afterCatalog);
        Assert.Equal(2, stored.Length);
        Assert.All(stored, value => Assert.Equal(actualHex, value));
    }

    /// <summary>Creates the independently reproduced legacy expression while fixing its defining SQL mode.</summary>
    /// <param name="connectionString">The root lease for this test's isolated database.</param>
    private static async Task CreateLegacyNewlineDefaultAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await SetEscapeModeAsync(connection, noBackslashEscapes: false);
        await using var command = connection.CreateCommand();
        // WHY: The raw native expression reproduces persisted legacy metadata independently
        // of the corrected SafeMigrations renderer; the existing NULL row must not be repaired.
        command.CommandText = "CREATE TABLE `persisted_defaults` (`id` int NOT NULL, "
            + "`value` text NULL DEFAULT (_utf8mb4 X'4E0A6577')) DEFAULT CHARSET=utf8mb4; "
            + "INSERT INTO `persisted_defaults` VALUES (7, NULL);";

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// Captures exact persisted default metadata without depending on the connection's escape interpretation.
    /// </summary>
    /// <param name="connectionString">The root lease for this test's isolated database.</param>
    /// <returns>The encoded default plus expression flags used to detect unintended mutation.</returns>
    private static Task<string> ReadDefaultCatalogAsync(string connectionString) => ScalarStringAsync(connectionString,
        "SELECT CONCAT(HEX(COALESCE(COLUMN_DEFAULT, '')), '|', EXTRA) FROM INFORMATION_SCHEMA.COLUMNS "
        + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'persisted_defaults' AND COLUMN_NAME = 'value';");

    /// <summary>
    /// Builds independently authored create and add contracts with the same complete expected default.
    /// </summary>
    /// <param name="createTable">Whether to create the table rather than its missing value column.</param>
    /// <param name="defaultValue">The exact typed or structured expression default.</param>
    /// <returns>The strict operation without relaxing repair or matching permissions.</returns>
    private static SafeMigrationOperation CreateStringDefaultOperation(
        bool createTable,
        SafeMigrationDefaultValue defaultValue)
    {
        // WHY: The oldest qualified MariaDB image defaults to latin1. Explicit
        // column collation keeps the UTF-8 fixture independent of image defaults.
        var column = new ExpectedColumnDefinition("value", typeof(string), false, "text",
            collation: new SafeMigrationCollationIdentifier("utf8mb4_unicode_ci"), defaultValue: defaultValue);

        SafeMigrationIntent intent = createTable
            ? new EnsureTableIntent(new ExpectedTableDefinition("persisted_defaults",
                    [new ExpectedColumnDefinition("id", typeof(int), false, "int"), column]),
                SafeMigrationTableMode.StrictDefinition)
            : new EnsureColumnIntent("persisted_defaults", column);

        return new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
    }

    /// <summary>
    /// Exercises one preflight, native execution, forced persistence boundary, verification and replay.
    /// </summary>
    /// <param name="context">The defining context with its explicit SQL mode.</param>
    /// <param name="connectionString">The isolated database's root lease.</param>
    /// <param name="operation">The operation whose persisted default must remain stable.</param>
    /// <returns>The independent reports and byte-level values used by assertions.</returns>
    private static async Task<DefaultCycleResult> ExecuteDefaultCycleAsync(
        SafeMigrationDbContext context,
        string connectionString,
        SafeMigrationOperation operation)
    {
        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("persisted-default"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation]);
        await context.Database.CloseConnectionAsync();
        var stored = await InsertReopenedDefaultsAsync(connectionString);
        await context.Database.OpenConnectionAsync();
        var connection = context.Database.GetDbConnection();
        var noBackslashEscapes = new MySqlConnectionStringBuilder(connection.ConnectionString).NoBackslashEscapes;
        await SetEscapeModeAsync(connection, noBackslashEscapes);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("persisted-default-postflight"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("persisted-default-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [operation]);

        return new DefaultCycleResult(preflight, postflight, replay, stored);
    }

    /// <summary>Requires exact byte identity alongside matching verification and no-op replay.</summary>
    /// <param name="result">The observed cycle rather than generated SQL alone.</param>
    /// <param name="initialAction">The independently expected create, add or repair decision.</param>
    /// <param name="expectedHex">The UTF-8 or binary bytes expected from both fresh sessions.</param>
    private static void AssertDefaultCycle(
        DefaultCycleResult result,
        SafeMigrationAction initialAction,
        string expectedHex
    )
    {
        Assert.Equal(initialAction, Assert.Single(result.Preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, result.Postflight.Status);
        Assert.True(Assert.Single(result.Postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(result.Replay.Assessments).Action);
        Assert.Equal(2, result.Stored.Length);
        Assert.All(result.Stored, value => Assert.Equal(expectedHex, value));
    }

    /// <summary>Forces persisted expression reparsing before a fresh DEFAULT insert in each escape mode.</summary>
    /// <param name="connectionString">The root lease for the test-owned database.</param>
    /// <param name="table">The exact owned table name, including escaped identifier characters.</param>
    /// <param name="column">The exact column whose persisted default is evaluated.</param>
    /// <returns>The UTF-8 bytes produced independently by ordinary and no-backslash sessions.</returns>
    private static async Task<string[]> InsertReopenedDefaultsAsync(
        string connectionString,
        string table = "persisted_defaults",
        string column = "value")
    {
        var stored = new string[2];
        var quotedTable = QuoteIdentifier(table);
        var quotedColumn = QuoteIdentifier(column);
        for (var index = 0; index < stored.Length; index++)
        {
            var noBackslashEscapes = index != 0;
            var settings = new MySqlConnectionStringBuilder(connectionString)
            {
                NoBackslashEscapes = noBackslashEscapes,
            };

            await using var connection = new MySqlConnection(settings.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await SetEscapeModeAsync(connection, noBackslashEscapes);
            await using var command = connection.CreateCommand();
            // WHY: A warm table definition can hide corrupt expression serialization.
            // FLUSH targets only this test-owned table and forces the next open to reparse it.
            command.CommandText = $"FLUSH TABLES {quotedTable}; "
                + $"INSERT INTO {quotedTable} (`id`) VALUES ({index}); "
                + $"SELECT HEX({quotedColumn}) FROM {quotedTable} WHERE `id` = {index};";
            stored[index] = Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture) ?? "<null>";
        }

        return stored;
    }

    /// <summary>Quotes the independently authored fixture names without interpreting marker-shaped content.</summary>
    /// <param name="identifier">The exact synthetic table or column name.</param>
    /// <returns>A native MySQL identifier with embedded backticks escaped.</returns>
    private static string QuoteIdentifier(string identifier) =>
        "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <summary>Sets an explicit mode instead of relying on a container or connection-pool default.</summary>
    /// <param name="connection">The already-open native connection.</param>
    /// <param name="noBackslashEscapes">Whether backslash escapes must be disabled.</param>
    private static async Task SetEscapeModeAsync(System.Data.Common.DbConnection connection, bool noBackslashEscapes)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = noBackslashEscapes
            ? "SET SESSION sql_mode = CONCAT_WS(',', @@SESSION.sql_mode, 'NO_BACKSLASH_ESCAPES');"
            : "SET SESSION sql_mode = TRIM(BOTH ',' FROM REPLACE(CONCAT(',', @@SESSION.sql_mode, ','), "
                + "',NO_BACKSLASH_ESCAPES,', ','));";

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Keeps reports associated with the exact persisted values used for the same native cycle.</summary>
    /// <param name="Preflight">The decision before applying the operation.</param>
    /// <param name="Postflight">The independent definition verification after reopening.</param>
    /// <param name="Replay">The decision after the operation has already completed.</param>
    /// <param name="Stored">The independently read default bytes in each SQL mode.</param>
    private sealed record DefaultCycleResult(
        SafeMigrationRunReport Preflight,
        SafeMigrationRunReport Postflight,
        SafeMigrationRunReport Replay,
        string[] Stored);

    /// <summary>
    /// Selects MySQL's persisted expression-default contract without changing MariaDB's separate repair capability.
    /// </summary>
    private sealed class MySqlPersistedDefaultTheoryAttribute : TheoryAttribute
    {
        /// <summary>
        /// Marks MySQL-specific legacy serialization and repair cases before their shared fixture starts.
        /// </summary>
        public MySqlPersistedDefaultTheoryAttribute()
        {
            var engine = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_ENGINE")?.Trim();
            if (!string.Equals(engine, "mysql", StringComparison.OrdinalIgnoreCase))
            {
                Skip = "This case qualifies MySQL-specific expression-default serialization or repair behavior.";
            }
        }
    }
}
