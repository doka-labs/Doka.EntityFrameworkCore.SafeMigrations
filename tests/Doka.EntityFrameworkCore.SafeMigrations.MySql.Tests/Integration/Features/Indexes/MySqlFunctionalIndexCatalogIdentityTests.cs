namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>
/// Qualifies native functional-key displays without relaxing literal identity or creation capabilities.
/// </summary>
public sealed class MySqlFunctionalIndexCatalogIdentityTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the shared engine and an independent database lease per case.</summary>
    /// <param name="fixture">The configured native engine.</param>
    public MySqlFunctionalIndexCatalogIdentityTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Native literal encoding and structural spelling share the existing exact-value comparison.</summary>
    /// <param name="actual">The independently authored native literal payload.</param>
    /// <param name="expected">The structured expected literal payload.</param>
    /// <param name="matching">Whether payloads are byte-identical.</param>
    /// <param name="hexLiteral">Whether native DDL independently encodes the value as UTF-8 bytes.</param>
    [Theory]
    [InlineData("ACTIVE", "ACTIVE", true, false)]
    [InlineData("ACTIVE", "active", false, false)]
    [InlineData("A''B", "A'B", true, false)]
    [InlineData("A''B", "a'B", false, false)]
    [InlineData("", "", true, false)]
    [InlineData("\u00e4", "\u00e4", true, false)]
    [InlineData("\u00e4", "a", false, false)]
    [InlineData("tail\\", "tail\\", true, true)]
    [InlineData("tail\\", "Tail\\", false, true)]
    [InlineData("before\\'after", "before\\'after", true, true)]
    [InlineData("before\\'after", "before'after", false, true)]
    [InlineData("line\nbreak", "line\nbreak", true, true)]
    [InlineData("line\nbreak", "line\rbreak", false, true)]
    [InlineData("line\nbreak", "line\\nbreak", false, true)]
    [InlineData("line\\nbreak", "line\\nbreak", true, true)]
    [InlineData("line\rbreak", "line\rbreak", true, true)]
    [InlineData("nul\0byte", "nul\0byte", true, true)]
    [InlineData("nul\0byte", "nulbyte", false, true)]
    [InlineData("nul\0byte", "nul\\0byte", false, true)]
    [InlineData("nul\\0byte", "nul\\0byte", true, true)]
    [InlineData("tab\tvalue", "tab\tvalue", true, true)]
    [InlineData("tab\tvalue", "tab value", false, true)]
    [InlineData("back\bspace", "back\bspace", true, true)]
    [InlineData("back\bspace", "backspace", false, true)]
    [InlineData("ctrl\u001avalue", "ctrl\u001avalue", true, true)]
    [InlineData("ctrl\u001avalue", "ctrlZvalue", false, true)]
    [InlineData("ctrl\u0001value", "ctrl\u0001value", true, true)]
    [InlineData("ctrl\u0001value", "ctrl\u0002value", false, true)]
    public async Task FunctionalKeyNativeLiteralSpellingPreservesExactValues(
        string actual,
        string expected,
        bool matching,
        bool hexLiteral
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            """
            CREATE TABLE `functional_catalog` (`id` int PRIMARY KEY, `value` varchar(30)
            NULL) ENGINE=InnoDB; INSERT INTO `functional_catalog` VALUES (42,
            'retained');
            """,
            CancellationToken.None);

        if (!Fixture.IsMariaDb)
        {
            // WHY: Encode mode-dependent values independently from the product renderer;
            // raw quoted backslashes would make the fixture depend on the session SQL mode.
            var literal = hexLiteral
                ? $"_utf8mb4 X'{Convert.ToHexString(Encoding.UTF8.GetBytes(actual))}'"
                : $"'{actual}'";

            var nativeSql = "CREATE INDEX `ix_functional_value` ON `functional_catalog` "
                + $"((CONCAT(`value`, {literal})));";

            await ExecuteSqlAsync(connectionString, nativeSql, CancellationToken.None);
        }

        await using var context = CreateContext(connectionString);
        var nativeDisplay = Fixture.IsMariaDb
            ? "unsupported"
            : await ScalarStringAsync(connectionString,
                """
                SELECT HEX(EXPRESSION) FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA=DATABASE()
                AND TABLE_NAME='functional_catalog'
                AND INDEX_NAME='ix_functional_value';
                """,
                CancellationToken.None);

        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.EnsureIndex(new ExpectedIndexDefinition("ix_functional_value",
                "functional_catalog",
                [
                    new ExpectedIndexKeyDefinition(structuredExpression: SafeMigrationSql.Function("CONCAT",
                        SafeMigrationSql.Identifier("value"),
                        SafeMigrationSql.Literal(expected))),
                ]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var runner = context.GetService<ISafeMigrationRunner>();
        var report = await runner.AnalyzeAsync(context,
            operations.Operations,
            new SafeMigrationRunOptions("functional-native-identity"),
            CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            operations.Operations,
            CancellationToken.None));

        var replay = await runner.AnalyzeAsync(context,
            operations.Operations,
            new SafeMigrationRunOptions("functional-native-replay"),
            CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `functional_catalog` WHERE `id`=42 AND `value`='retained';",
            CancellationToken.None);

        // Assert
        var expectedState = (Fixture.IsMariaDb, matching) switch
        {
            (true, _) => SafeMigrationObservedState.Unsupported,
            (false, true) => SafeMigrationObservedState.Matching,
            (false, false) => SafeMigrationObservedState.Different,
        };

        Assert.True(expectedState
            == Assert.Single(report.Assessments)
                .ObservedState,
            $"Expected {expectedState}; native catalog UTF-8 bytes: {nativeDisplay}");

        Assert.Equal(expectedState,
            Assert.Single(replay.Assessments)
                .ObservedState);

        if (!Fixture.IsMariaDb && matching)
        {
            Assert.Null(failure);
        }
        else
        {
            AssertOwnedGuardFailure(failure);
        }

        Assert.Equal(1, rows);
    }
}
