namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>SQL NULL defaults retain exact source and target identity, including expression defaults.</summary>
    /// <param name="expressionDefault">Whether the original NULL default uses expression syntax.</param>
    /// <param name="literalNull">Whether the expected contract explicitly specifies a NULL literal.</param>
    /// <param name="alreadyTarget">Whether the column already has the target width.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task NullDefaultIdentity_RecognizesSqlNullAndReplays(
        bool expressionDefault,
        bool literalNull,
        bool alreadyTarget
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var defaultSql = expressionDefault ? "(NULL)" : "NULL";
        var storeType = alreadyTarget ? "varchar(40)" : "varchar(20)";
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `null_defaults` (`value` {storeType} NULL DEFAULT {defaultSql}) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `null_defaults` VALUES ('kept'), (NULL);");

        var operation = NullDefaultTransition(literalNull);
        await using var context = CreateContext(connectionString);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var before = await analyzer.AnalyzeAsync(context, [operation]);
        var catalog = await ScalarStringAsync(connectionString,
            "SELECT CONCAT(COLUMN_DEFAULT IS NULL, '|', COALESCE(HEX(COLUMN_DEFAULT), '-'), '|', EXTRA) "
            + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'null_defaults' AND COLUMN_NAME = 'value';");

        await ExecuteOperationsAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var after = await analyzer.AnalyzeAsync(context, [operation]);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `null_defaults` WHERE `value` IS NULL OR BINARY `value` = BINARY 'kept';");

        // Assert
        Assert.Equal(alreadyTarget ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            Assert.Single(before).ObservedState);
        if (!alreadyTarget)
        {
            Assert.Equal(SafeMigrationRepairCapability.Safe, before[0].RepairCapability);
        }

        if (!Fixture.IsMariaDb)
        {
            Assert.Equal(expressionDefault ? "0|4E554C4C|DEFAULT_GENERATED" : "1|-|", catalog);
        }

        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(after).ObservedState);
        Assert.True(after[0].PostconditionSatisfied);
        Assert.Equal(2, rows);
    }

    /// <summary>The text NULL cannot impersonate either SQL NULL contract at the source or target.</summary>
    /// <param name="expressionDefault">Whether the text value uses expression-default syntax.</param>
    /// <param name="literalNull">Whether the expected contract explicitly specifies a NULL literal.</param>
    /// <param name="alreadyTarget">Whether the column already has the target width.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task NullDefaultIdentity_RejectsTextWithoutMutation(
        bool expressionDefault,
        bool literalNull,
        bool alreadyTarget
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var defaultSql = expressionDefault ? "('NULL')" : "'NULL'";
        var width = alreadyTarget ? 40 : 20;
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `null_defaults` (`value` varchar({width}) NULL DEFAULT {defaultSql}) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `null_defaults` () VALUES ();");

        var operation = NullDefaultTransition(literalNull);
        await using var context = CreateContext(connectionString);

        // Act
        var results = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var replayFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var unchangedWidth = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'null_defaults' AND COLUMN_NAME = 'value';");

        var value = await ScalarStringAsync(connectionString, "SELECT HEX(`value`) FROM `null_defaults`;");

        // Assert
        var analysis = Assert.Single(results);
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
        Assert.False(analysis.RequiresLiveDataProof);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message, StringComparison.Ordinal);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(replayFailure).Message,
            StringComparison.Ordinal);
        Assert.Equal(width, unchangedWidth);
        Assert.Equal("4E554C4C", value);
    }

    /// <summary>A newly generated nullable column satisfies its NULL postcondition and idempotent replay.</summary>
    /// <param name="literalNull">Whether the contract explicitly specifies a NULL literal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullDefaultIdentity_NewColumnSatisfiesPostcondition(bool literalNull)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `null_defaults` (`id` int NOT NULL);");
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("null_defaults", NullDefaultColumn("varchar(40)", literalNull)),
            SafeMigrationPolicy.ThrowIfDifferent);

        await using var context = CreateContext(connectionString);

        // Act
        await ExecuteOperationsAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var results = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        await ExecuteSqlAsync(connectionString, "INSERT INTO `null_defaults` (`id`) VALUES (1);");
        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `null_defaults` WHERE `value` IS NULL;");

        // Assert
        var analysis = Assert.Single(results);
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
        Assert.Equal(1, nullRows);
    }

    /// <summary>A required column's NULL expression cannot silently satisfy an absent-default contract.</summary>
    [Fact]
    public async Task NullDefaultIdentity_RequiredColumnPreservesAbsentDefaultContract()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("null_defaults", new ExpectedColumnDefinition("value", typeof(string), false,
                "varchar(40)")),
            SafeMigrationPolicy.ThrowIfDifferent);

        await using var context = CreateContext(connectionString);
        IReadOnlyList<SafeMigrationProviderAnalysis> results = [];
        Exception? failure = null;
        Exception? replayFailure = null;

        // Act
        var creationFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "CREATE TABLE `null_defaults` (`value` varchar(40) NOT NULL DEFAULT (NULL)); "
            + "INSERT INTO `null_defaults` VALUES ('kept');"));

        if (creationFailure is null)
        {
            results = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
            replayFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        }

        var required = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'null_defaults' AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO';");

        var tables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'null_defaults';");

        // Assert
        if (Fixture.IsMariaDb)
        {
            // WHY: MariaDB rejects this required-column expression at CREATE;
            // only MySQL can expose it to the catalog identity matcher.
            Assert.Equal(1067, Assert.IsType<MySqlException>(creationFailure).Number);
            Assert.Empty(results);
            Assert.Equal(0, required);
        }
        else
        {
            Assert.Null(creationFailure);
            Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(results).ObservedState);
            Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);

            Assert.IsType<MySqlException>(replayFailure);
            Assert.Equal(1, required);
        }

        Assert.Equal(Fixture.IsMariaDb ? 0 : 1, tables);
    }

    /// <summary>The explicit NULL-literal contract still requires a nullable column.</summary>
    [Fact]
    public void NullDefaultIdentity_RequiredLiteralNullRemainsInvalid()
    {
        // Arrange
        var defaultValue = SafeMigrationDefaultValue.Literal(null);

        // Act
        var action = () => new ExpectedColumnDefinition("value", typeof(string), false, "varchar(40)",
            defaultValue: defaultValue);

        // Assert
        var failure = Assert.Throws<ArgumentException>(action);
        Assert.Equal("defaultValue", failure.ParamName);
    }

    /// <summary>Builds a width-only transition with identical source and target NULL-default contracts.</summary>
    /// <param name="literalNull">Whether both definitions explicitly request a NULL literal.</param>
    /// <returns>The reviewed widening operation used by the source and target identity probes.</returns>
    private static SafeMigrationOperation NullDefaultTransition(bool literalNull) => new(
        new AlterColumnIntent("null_defaults", NullDefaultColumn("varchar(40)", literalNull),
            NullDefaultColumn("varchar(20)", literalNull)),
        SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Builds a nullable string column with an absent or explicit NULL default.</summary>
    /// <param name="storeType">The exact store type, including its width.</param>
    /// <param name="literalNull">Whether the definition explicitly requests a NULL literal.</param>
    /// <returns>The exact column contract shared by runtime and analysis probes.</returns>
    private static ExpectedColumnDefinition NullDefaultColumn(
        string storeType,
        bool literalNull
    ) => new("value", typeof(string), true, storeType,
        defaultValue: literalNull ? SafeMigrationDefaultValue.Literal(null) : SafeMigrationDefaultValue.None);
}
