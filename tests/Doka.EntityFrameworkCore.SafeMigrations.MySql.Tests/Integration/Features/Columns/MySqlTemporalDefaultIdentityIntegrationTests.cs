namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Preserves complete duration values across native default matching and safe default-only repair.</summary>
public sealed class MySqlTemporalDefaultIdentityIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the shared engine while retaining a separately owned database per test.</summary>
    /// <param name="fixture">The configured MySQL or MariaDB engine.</param>
    public MySqlTemporalDefaultIdentityIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Native duration spelling must retain total hours, sign and the target precision.</summary>
    /// <param name="expected">The complete CLR duration in invariant constant format.</param>
    /// <param name="actual">The independently authored native default value.</param>
    /// <param name="precision">The actual TIME fractional precision.</param>
    /// <param name="matching">Whether both values have identical persisted meaning.</param>
    [Theory]
    [InlineData("2.01:02:03.123456", "49:02:03.123456", 6, true)]
    [InlineData("-2.01:02:03.123456", "-49:02:03.123456", 6, true)]
    [InlineData("2.01:02:03.123456", "01:02:03.123456", 6, false)]
    [InlineData("-2.01:02:03.123456", "01:02:03.123456", 6, false)]
    [InlineData("-2.01:02:03.123456", "-01:02:03.123456", 6, false)]
    [InlineData("-01:02:03.123456", "01:02:03.123456", 6, false)]
    [InlineData("2.01:02:03.1234567", "49:02:03", 0, true)]
    [InlineData("2.01:02:03.1234567", "49:02:03.123", 3, true)]
    [InlineData("2.01:02:03.1234567", "49:02:03.123456", 6, true)]
    [InlineData("2.01:02:03.123456", "49:02:03.123457", 6, false)]
    [InlineData("2.01:02:03", "49:02:03", 6, true)]
    [InlineData("00:00:00", "00:00:00", 6, true)]
    [InlineData("34.22:59:59", "838:59:59", 6, true)]
    public async Task DurationDefaultsPreserveFullPersistedIdentity(
        string expected,
        string actual,
        int precision,
        bool matching
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var sql = $"""
            CREATE TABLE `duration_defaults` (`id` int NOT NULL PRIMARY KEY, `value`
            time({precision}) NOT NULL DEFAULT '{actual}')
            ENGINE=InnoDB; INSERT INTO `duration_defaults` (`id`) VALUES (42);
            """;

        await ExecuteSqlAsync(connectionString, sql, CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.EnsureColumn("duration_defaults",
            DurationDefinition(TimeSpan.ParseExact(expected, "c", CultureInfo.InvariantCulture), precision),
            SafeMigrationPolicy.ThrowIfDifferent);

        var before = await ReadDefaultAsync(connectionString, CancellationToken.None);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                operations.Operations,
                new SafeMigrationRunOptions("duration-identity"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            operations.Operations,
            CancellationToken.None));

        var after = await ReadDefaultAsync(connectionString, CancellationToken.None);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `duration_defaults` WHERE `id`=42;",
            CancellationToken.None);

        // Assert
        Assert.Equal(matching
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked,
            report.Status);

        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        if (matching)
        {
            Assert.Null(failure);
        }
        else
        {
            AssertOwnedGuardFailure(failure);
        }

        Assert.Equal(before, after);
        Assert.Equal(1, rows);
    }

    /// <summary>Typed TIME expression defaults must not impersonate another fractional TimeOnly value.</summary>
    /// <param name="expected">The CLR time of day before provider precision truncation.</param>
    /// <param name="actual">The independently authored native typed TIME expression value.</param>
    /// <param name="precision">The owning column's fractional precision.</param>
    /// <param name="matching">Whether both defaults have the same provider-persisted value.</param>
    [Theory]
    [InlineData("12:00:00.123456", "12:00:00", 6, false)]
    [InlineData("12:00:00.123456", "12:00:00.123456", 6, true)]
    [InlineData("12:00:00.123000", "12:00:00.123", 6, true)]
    [InlineData("12:00:00.123456", "12:00:00.123457", 6, false)]
    [InlineData("12:00:00.1234567", "12:00:00.123456", 6, true)]
    [InlineData("12:00:00.1234567", "12:00:00.123", 3, true)]
    [InlineData("12:00:00.1234567", "12:00:00", 3, false)]
    [InlineData("12:00:00.1234567", "12:00:00", 0, true)]
    [InlineData("12:00:00", "12:00:00", 6, true)]
    [InlineData("12:00:00", "12:00:00", 3, true)]
    [InlineData("12:00:00", "12:00:00", 0, true)]
    public async Task TypedTimeExpressionDefaultsRetainFractionalValueIdentity(
        string expected,
        string actual,
        int precision,
        bool matching
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var sql = $"""
            CREATE TABLE `clock_defaults` (`id` int NOT NULL PRIMARY KEY, `value` time({precision})
            NOT NULL DEFAULT (TIME '{actual}')) ENGINE=InnoDB; INSERT
            INTO `clock_defaults` (`id`) VALUES (42);
            """;

        await ExecuteSqlAsync(connectionString, sql, CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.EnsureColumn("clock_defaults",
            new ExpectedColumnDefinition("value",
                typeof(TimeOnly),
                false,
                $"time({precision})",
                precision: precision > 0
                    ? precision
                    : null,
                defaultValue: SafeMigrationDefaultValue.Literal(
                    TimeOnly.Parse(expected, CultureInfo.InvariantCulture))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var nativeDefault = await ScalarStringAsync(connectionString,
            """
            SELECT HEX(COLUMN_DEFAULT) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA=DATABASE()
            AND TABLE_NAME='clock_defaults'
            AND COLUMN_NAME='value';
            """,
            CancellationToken.None);

        // Act
        var runner = context.GetService<ISafeMigrationRunner>();
        var report = await runner.AnalyzeAsync(context,
            operations.Operations,
            new SafeMigrationRunOptions("typed-time-identity"),
            CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            operations.Operations,
            CancellationToken.None));

        var replay = await runner.AnalyzeAsync(context,
            operations.Operations,
            new SafeMigrationRunOptions("typed-time-replay"),
            CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `clock_defaults` WHERE `id`=42;",
            CancellationToken.None);

        // Assert
        var expectedState = matching
            ? SafeMigrationObservedState.Matching
            : SafeMigrationObservedState.Different;

        Assert.True(expectedState
            == Assert.Single(report.Assessments)
                .ObservedState,
            $"Expected {expectedState}; native default UTF-8 bytes: {nativeDefault}");

        Assert.Equal(expectedState,
            Assert.Single(replay.Assessments)
                .ObservedState);

        if (matching)
        {
            Assert.Null(failure);
        }
        else
        {
            AssertOwnedGuardFailure(failure);
        }

        Assert.Equal(1, rows);
    }

    /// <summary>Native target conversion controls both new DateTime defaults and existing default identity.</summary>
    /// <param name="fraction">The exact six-digit source fraction emitted by Doka.</param>
    /// <param name="precision">The physical datetime fractional precision.</param>
    /// <param name="alternateMode">Whether to select the engine's alternate rounding or truncation mode.</param>
    /// <param name="existing">Whether the column is already present before SafeMigrations runs.</param>
    /// <param name="matching">Whether the independently authored existing default matches native conversion.</param>
    [Theory]
    [InlineData("123789", 0, false, false, true)]
    [InlineData("123789", 0, true, false, true)]
    [InlineData("123789", 0, false, true, true)]
    [InlineData("123789", 0, true, true, true)]
    [InlineData("123000", 3, false, false, true)]
    [InlineData("123000", 3, true, false, true)]
    [InlineData("123000", 3, false, true, true)]
    [InlineData("123000", 3, true, true, true)]
    [InlineData("123456", 3, false, false, true)]
    [InlineData("123456", 3, true, false, true)]
    [InlineData("123456", 3, false, true, true)]
    [InlineData("123456", 3, true, true, true)]
    [InlineData("123789", 3, false, false, true)]
    [InlineData("123789", 3, true, false, true)]
    [InlineData("123789", 3, false, true, true)]
    [InlineData("123789", 3, true, true, true)]
    [InlineData("123789", 6, false, false, true)]
    [InlineData("123789", 6, true, false, true)]
    [InlineData("123789", 6, false, true, true)]
    [InlineData("123789", 6, true, true, true)]
    [InlineData("123789", 3, false, true, false)]
    [InlineData("123789", 3, true, true, false)]
    public async Task DateTimeDefaultsFollowNativeTargetPrecisionAndSessionMode(
        string fraction,
        int precision,
        bool alternateMode,
        bool existing,
        bool matching
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var rounds = Fixture.IsMariaDb
            ? alternateMode
            : !alternateMode;

        var persistedFraction = precision switch
        {
            0 => string.Empty,
            3 => !matching
                ? ".125"
                : rounds && fraction == "123789"
                    ? ".124"
                    : ".123",
            6 => "." + fraction,
            _ => throw new UnreachableException(),
        };

        var persisted = "2020-01-01 12:00:00" + persistedFraction;
        var columnSql = existing
            ? $", `value` datetime({precision}) NOT NULL DEFAULT '{persisted}'"
            : string.Empty;

        await ExecuteSqlAsync(connectionString,
            $"""
            CREATE TABLE `datetime_defaults` (`id` int NOT NULL PRIMARY KEY{columnSql})
            ENGINE=InnoDB; INSERT INTO `datetime_defaults` (`id`) VALUES (42);
            """,
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        if (alternateMode)
        {
            await using var command = context
                .Database
                .GetDbConnection()
                .CreateCommand();

            command.CommandText = Fixture.IsMariaDb
                ? "SET SESSION sql_mode=CONCAT(@@SESSION.sql_mode, ',TIME_ROUND_FRACTIONAL');"
                : "SET SESSION sql_mode=CONCAT(@@SESSION.sql_mode, ',TIME_TRUNCATE_FRACTIONAL');";

            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var expected = DateTime.ParseExact("2020-01-01 12:00:00." + fraction,
            "yyyy-MM-dd HH:mm:ss.ffffff",
            CultureInfo.InvariantCulture);

        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.EnsureColumn("datetime_defaults",
            new ExpectedColumnDefinition("value",
                typeof(DateTime),
                false,
                $"datetime({precision})",
                precision: precision > 0
                    ? precision
                    : null,
                defaultValue: SafeMigrationDefaultValue.Literal(expected)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var runner = context.GetService<ISafeMigrationRunner>();
        var report = await runner.AnalyzeAsync(context,
            operations.Operations,
            new SafeMigrationRunOptions("datetime-native-precision"),
            CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            operations.Operations,
            CancellationToken.None));

        var replay = await runner.AnalyzeAsync(context,
            operations.Operations,
            new SafeMigrationRunOptions("datetime-native-replay"),
            CancellationToken.None);

        var value = await ScalarStringAsync(connectionString,
            "SELECT CAST(`value` AS CHAR) FROM `datetime_defaults` WHERE `id`=42;",
            CancellationToken.None);

        // Assert
        var expectedInitial = !existing
            ? SafeMigrationObservedState.Missing
            : matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different;

        Assert.Equal(expectedInitial,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(replay.Assessments)
                .ObservedState);

        if (matching)
        {
            Assert.Null(failure);
        }
        else
        {
            AssertOwnedGuardFailure(failure);
        }

        Assert.Equal(persisted, value);
    }

    /// <summary>Repair updates only the default and retains already stored values and replay behavior.</summary>
    [Fact]
    public async Task DurationDefaultRepairPreservesRowsAndReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            """
            CREATE TABLE `duration_defaults` (`id` int NOT NULL PRIMARY KEY, `value`
            time(6) NOT NULL DEFAULT '01:02:03.123456') ENGINE=InnoDB; INSERT INTO `duration_defaults`
            (`id`) VALUES (42);
            """,
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var expected = new TimeSpan(2, 1, 2, 3).Add(TimeSpan.FromTicks(1_234_560));
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.EnsureColumn("duration_defaults", DurationDefinition(expected, 6), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var runner = context.GetService<ISafeMigrationRunner>();
        var initial = await runner.AnalyzeAsync(context,
            operations.Operations,
            new SafeMigrationRunOptions("duration-repair"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "INSERT INTO `duration_defaults` (`id`) VALUES (43);",
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context,
            operations.Operations,
            new SafeMigrationRunOptions("duration-repair-replay"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `duration_defaults` WHERE `id`=42 AND `value`='01:02:03.123456';",
            CancellationToken.None);

        var repaired = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `duration_defaults` WHERE `id`=43 AND `value`='49:02:03.123456';",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair,
            Assert.Single(initial.Assessments)
                .Action);

        Assert.Equal(SafeMigrationAction.NoOp,
            Assert.Single(replay.Assessments)
                .Action);

        Assert.Equal(1, preserved);
        Assert.Equal(1, repaired);
    }

    /// <summary>Only the native zero-fraction temporal alias may omit its explicit precision.</summary>
    /// <param name="storeType">The requested temporal store type.</param>
    /// <param name="actualType">The independently declared native type.</param>
    /// <param name="matching">Whether both types denote the same fractional precision.</param>
    [Theory]
    [InlineData("time(0)", "time", true)]
    [InlineData("datetime(0)", "datetime", true)]
    [InlineData("timestamp(0)", "timestamp", true)]
    [InlineData("time(0)", "time(1)", false)]
    [InlineData("datetime(0)", "datetime(1)", false)]
    [InlineData("timestamp(0)", "timestamp(1)", false)]
    public async Task ZeroFractionTypeAliasesRetainNonzeroPrecisionDrift(
        string storeType,
        string actualType,
        bool matching
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var sql = $"CREATE TABLE `temporal_alias` (`value` {actualType} NULL) ENGINE=InnoDB;";
        await ExecuteSqlAsync(connectionString, sql, CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var clrType = storeType.StartsWith("time(", StringComparison.Ordinal)
            ? typeof(TimeSpan)
            : typeof(DateTime);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("temporal_alias",
            new ExpectedColumnDefinition("value", clrType, true, storeType),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                builder.Operations,
                new SafeMigrationRunOptions("temporal-zero-precision"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            builder.Operations,
            CancellationToken.None));

        // Assert
        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        if (matching)
        {
            Assert.Null(failure);
        }
        else
        {
            AssertOwnedGuardFailure(failure);
        }
    }

    /// <summary>Declares the same physical TIME contract for independent matching and repair probes.</summary>
    private static ExpectedColumnDefinition DurationDefinition(
        TimeSpan expected,
        int precision
    ) => new("value",
        typeof(TimeSpan),
        false,
        $"time({precision})",
        precision: precision > 0
            ? precision
            : null,
        defaultValue: SafeMigrationDefaultValue.Literal(expected));

    /// <summary>Reads the native default text without normalizing any temporal component.</summary>
    private static Task<string> ReadDefaultAsync(
        string connectionString,
        CancellationToken cancellationToken
    ) => ScalarStringAsync(connectionString,
        """
        SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_SCHEMA=DATABASE()
        AND TABLE_NAME='duration_defaults'
        AND COLUMN_NAME='value';
        """,
        cancellationToken);
}
