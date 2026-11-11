namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies complete temporal literal identity through native defaults, guards and repairs.</summary>
public sealed class PostgreSqlTemporalDefaultIdentityIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the assembly-owned server and a separately leased database per test.</summary>
    /// <param name="fixture">The native PostgreSQL fixture.</param>
    public PostgreSqlTemporalDefaultIdentityIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Seconds, fractions, days and signs remain part of the expected literal contract.</summary>
    /// <param name="kind">The temporal CLR and native storage family.</param>
    /// <param name="matching">Whether the existing default retains every expected value component.</param>
    [Theory]
    [InlineData("time", false)]
    [InlineData("time", true)]
    [InlineData("timestamp", false)]
    [InlineData("timestamp", true)]
    [InlineData("timestamp_tz", false)]
    [InlineData("timestamp_tz", true)]
    [InlineData("interval", false)]
    [InlineData("interval", true)]
    [InlineData("negative_interval", false)]
    [InlineData("negative_interval", true)]
    [InlineData("duration_time", false)]
    [InlineData("duration_time", true)]
    [InlineData("date", false)]
    [InlineData("date", true)]
    [InlineData("date_min", false)]
    [InlineData("date_min", true)]
    [InlineData("date_max", false)]
    [InlineData("date_max", true)]
    public async Task TemporalLiteralMatchingPreservesEveryValueComponent(
        string kind,
        bool matching
    )
    {
        // Arrange
        var scenario = TemporalCase(kind);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var actual = matching
            ? scenario.NativeValue
            : scenario.DifferentValue;

        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE temporal_identity(v {scenario.StoreType} NOT NULL DEFAULT {actual}); "
            + "INSERT INTO temporal_identity DEFAULT VALUES;",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = Build(context, scenario, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM temporal_identity;",
            CancellationToken.None);

        var nativeDefault = await ScalarStringAsync(connectionString,
            "SELECT pg_get_expr(adbin,adrelid) FROM pg_attrdef WHERE adrelid='temporal_identity'::regclass;",
            CancellationToken.None);

        var expectedSql = context
            .GetService<IRelationalTypeMappingSource>()
            .FindMapping(scenario.ClrType, scenario.StoreType)!.GenerateSqlLiteral(scenario.Value);

        // Assert
        Assert.True(report.Status
            == (matching
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked),
            $"native={nativeDefault}; expected={expectedSql}");

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
            Assert.NotNull(failure);
            Assert.Contains("doka_sm_different", failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, rows);
    }

    /// <summary>Canonical formatting follows native output settings without changing the temporal value.</summary>
    /// <param name="kind">The temporal storage family.</param>
    [Theory]
    [InlineData("time")]
    [InlineData("timestamp")]
    [InlineData("timestamp_tz")]
    [InlineData("interval")]
    [InlineData("negative_interval")]
    [InlineData("duration_time")]
    [InlineData("date")]
    [InlineData("date_min")]
    [InlineData("date_max")]
    public async Task NativeTemporalDisplaySettingsPreserveLiteralIdentity(
        string kind
    )
    {
        // Arrange
        var scenario = TemporalCase(kind);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE temporal_identity(v {scenario.StoreType} NOT NULL DEFAULT {scenario.NativeValue});",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(
            "SET DateStyle='SQL, DMY'; SET IntervalStyle='iso_8601'; SET TimeZone='Europe/Berlin';",
            CancellationToken.None);

        var builder = Build(context, scenario, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Null(failure);
    }

    /// <summary>Default-only repair preserves stored rows and replays without another change.</summary>
    /// <param name="kind">The temporal storage family.</param>
    [Theory]
    [InlineData("time")]
    [InlineData("timestamp")]
    [InlineData("timestamp_tz")]
    [InlineData("interval")]
    [InlineData("negative_interval")]
    [InlineData("duration_time")]
    [InlineData("date")]
    [InlineData("date_min")]
    [InlineData("date_max")]
    public async Task TemporalDefaultRepairPreservesRowsAndReplays(
        string kind
    )
    {
        // Arrange
        var scenario = TemporalCase(kind);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE temporal_identity(v {scenario.StoreType} NOT NULL DEFAULT {scenario.DifferentValue}); "
            + "INSERT INTO temporal_identity DEFAULT VALUES;",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = Build(context, scenario, SafeMigrationPolicy.RepairIfSafe);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "INSERT INTO temporal_identity DEFAULT VALUES;",
            CancellationToken.None);

        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var oldRows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM temporal_identity WHERE v = ({scenario.DifferentValue})::{scenario.StoreType};",
            CancellationToken.None);

        var newRows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM temporal_identity WHERE v = ({scenario.NativeValue})::{scenario.StoreType};",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(1, oldRows);
        Assert.Equal(1, newRows);
    }

    /// <summary>New typed defaults survive the native postcondition and a second application.</summary>
    /// <param name="kind">The temporal storage family.</param>
    [Theory]
    [InlineData("time")]
    [InlineData("timestamp")]
    [InlineData("timestamp_tz")]
    [InlineData("interval")]
    [InlineData("negative_interval")]
    [InlineData("duration_time")]
    [InlineData("date")]
    [InlineData("date_min")]
    [InlineData("date_max")]
    public async Task TemporalDefaultCreationSatisfiesNativePostconditionAndReplay(
        string kind
    )
    {
        // Arrange
        var scenario = TemporalCase(kind);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE temporal_identity(id integer);", CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = Build(context, scenario, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "INSERT INTO temporal_identity(id) VALUES (42);",
            CancellationToken.None);

        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var values = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM temporal_identity WHERE id=42 AND v=({scenario.NativeValue})::{scenario.StoreType};",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(1, values);
    }

    /// <summary>Every Safe DDL default path retains a negative duration in runtime and script execution.</summary>
    /// <param name="path">The private standard-operation reconstruction path.</param>
    /// <param name="script">Whether to execute generated script commands instead of runtime commands.</param>
    [Theory]
    [InlineData("table", false)]
    [InlineData("table", true)]
    [InlineData("alter", false)]
    [InlineData("alter", true)]
    [InlineData("structured", false)]
    [InlineData("structured", true)]
    public async Task NegativeDurationSafeDdlRetainsValueAcrossAllOwnedPaths(
        string path,
        bool script
    )
    {
        // Arrange
        var scenario = TemporalCase("negative_interval");
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (path != "table")
        {
            var columns = path == "alter"
                ? "v interval NOT NULL DEFAULT '0 seconds'"
                : "id integer";

            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE temporal_identity(" + columns + ");",
                CancellationToken.None);
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var definition = new ExpectedColumnDefinition("v",
            typeof(TimeSpan),
            false,
            storeType: "interval",
            defaultValue: path == "structured"
                ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(scenario.Value))
                : SafeMigrationDefaultValue.Literal(scenario.Value));

        if (path == "table")
        {
            builder.EnsureTable(new ExpectedTableDefinition("temporal_identity", [definition]),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else if (path == "alter")
        {
            builder.Operations.Add(new SafeMigrationOperation(new AlterColumnIntent("temporal_identity",
                    definition,
                    new ExpectedColumnDefinition("v",
                        typeof(TimeSpan),
                        false,
                        storeType: "interval",
                        defaultValue: SafeMigrationDefaultValue.Literal(TimeSpan.Zero))),
                SafeMigrationPolicy.RepairIfSafe));
        }
        else
        {
            builder.EnsureColumn("temporal_identity", definition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        if (script)
        {
            var commands = context
                .GetService<IMigrationsSqlGenerator>()
                .Generate(builder.Operations,
                    context.Model,
                    MigrationsSqlGenerationOptions.Script);

            var sql = string.Join("\n", commands.Select(static command => command.CommandText));
            await ExecuteSqlAsync(connectionString, sql, CancellationToken.None);
            await ExecuteSqlAsync(connectionString, sql, CancellationToken.None);
        }
        else
        {
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        }

        await ExecuteSqlAsync(connectionString,
            "INSERT INTO temporal_identity DEFAULT VALUES;",
            CancellationToken.None);

        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM temporal_identity WHERE v=INTERVAL '-176523.123456 seconds';",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(1, rows);
    }

    /// <summary>TimeSpan remains valid for native clock-time storage, including the exact upper endpoint.</summary>
    /// <param name="storeType">The supported native clock-time spelling.</param>
    /// <param name="structured">Whether the literal carries an explicit structured SQL mapping.</param>
    [Theory]
    [InlineData("time(6)", false)]
    [InlineData("time(6)", true)]
    [InlineData("time(6) without time zone", false)]
    [InlineData("time(6) without time zone", true)]
    public async Task ClockTimeDurationUpperEndpointAppliesMatchesAndReplays(
        string storeType,
        bool structured
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE temporal_identity(id integer);", CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var duration = TimeSpan.FromDays(1);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("temporal_identity",
            new ExpectedColumnDefinition("v",
                typeof(TimeSpan),
                false,
                storeType: storeType,
                defaultValue: structured
                    ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(duration, storeType))
                    : SafeMigrationDefaultValue.Literal(duration)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "INSERT INTO temporal_identity(id) VALUES (42);",
            CancellationToken.None);

        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM temporal_identity WHERE id=42 AND v=TIME '24:00:00';",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(1, rows);
    }

    /// <summary>A clock-time mapping cannot silently discard a duration's sign or calendar days.</summary>
    /// <param name="negative">Whether the unrepresentable duration is negative rather than over one day.</param>
    /// <param name="structured">Whether the literal is explicitly typed in the structured SQL tree.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClockTimeDurationOutsideNativeDomainIsRejectedBeforeDdl(
        bool negative,
        bool structured
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE temporal_identity(v time(6) NOT NULL DEFAULT '01:02:03.123456'); "
            + "INSERT INTO temporal_identity DEFAULT VALUES;",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var duration = new TimeSpan(negative
                ? 0
                : 2,
            1,
            2,
            3).Add(TimeSpan.FromTicks(1_234_560));

        if (negative)
        {
            duration = -duration;
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("temporal_identity",
            new ExpectedColumnDefinition("v",
                typeof(TimeSpan),
                false,
                storeType: "time(6) without time zone",
                defaultValue: structured
                    ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(duration, "time(6) without time zone"))
                    : SafeMigrationDefaultValue.Literal(duration)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await AnalyzeAsync(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM temporal_identity WHERE v=TIME '01:02:03.123456';",
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.NotNull(failure);
        Assert.Contains("doka_sm_unsupported", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, rows);
    }

    /// <summary>The shared seed renderer retains valid durations and classifies invalid clock values.</summary>
    /// <param name="kind">The independently specified native time or interval value family.</param>
    [Theory]
    [InlineData("clock")]
    [InlineData("endpoint")]
    [InlineData("negative_clock")]
    [InlineData("overflow_clock")]
    [InlineData("negative_interval")]
    public async Task ManagedDurationValuesRetainNativeIdentityAndReplay(
        string kind
    )
    {
        // Arrange
        var interval = kind == "negative_interval";
        var supported = kind is not ("negative_clock" or "overflow_clock");
        var storeType = interval
            ? "interval"
            : "time(6) without time zone";

        var duration = kind switch
        {
            "clock" => TimeSpan.FromHours(12),
            "endpoint" => TimeSpan.FromDays(1),
            "negative_clock" => TimeSpan.FromHours(-12),
            "overflow_clock" => TimeSpan
                .FromDays(2)
                .Add(TimeSpan.FromHours(12)),
            "negative_interval" => new TimeSpan(-2, -1, -2, -3).Subtract(TimeSpan.FromTicks(1_234_560)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var expected = kind switch
        {
            "endpoint" => "TIME '24:00:00'",
            "negative_interval" => "INTERVAL '-2 days -01:02:03.123456'",
            _ => "TIME '12:00:00'",
        };

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE temporal_identity(id integer PRIMARY KEY, v {storeType} NOT NULL);",
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(new SafeMigrationOperation(new EnsureModelManagedDataIntent("temporal_identity",
                ["id"],
                ["integer"],
                ["id", "v"],
                ["integer", storeType],
                new object?[,] { { 42, duration } },
                schema: null,
                uniqueKeys: null),
            SafeMigrationPolicy.ThrowIfDifferent));

        // Act
        var before = await AnalyzeAsync(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        });

        var after = await AnalyzeAsync(context, builder, CancellationToken.None);
        var rows = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM temporal_identity WHERE id=42 AND v={expected};",
            CancellationToken.None);

        // Assert
        Assert.Equal(supported
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked,
            before.Status);

        Assert.Equal(supported
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Unsupported,
            Assert.Single(after.Assessments)
                .ObservedState);

        if (supported)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.NotNull(failure);
            Assert.Contains("doka_sm_unsupported", failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(supported
                ? 1
                : 0,
            rows);
    }

    /// <summary>Builds one independently specified CLR/native-value scenario without using the renderer.</summary>
    private static TemporalScenario TemporalCase(
        string kind
    ) => kind switch
    {
        "time" => new TemporalScenario(typeof(TimeOnly),
            "time(6) without time zone",
            new TimeOnly(12, 0, 0).Add(TimeSpan.FromTicks(1_234_560)),
            "'12:00:00.123456'",
            "'12:00:00'"),
        "timestamp" => new TemporalScenario(typeof(DateTime),
            "timestamp(6) without time zone",
            new DateTime(2026, 11, 10, 12, 0, 0).AddTicks(1_234_560),
            "'2026-11-10 12:00:00.123456'",
            "'2026-11-10 12:00:00'"),
        "timestamp_tz" => new TemporalScenario(typeof(DateTimeOffset),
            "timestamp(6) with time zone",
            new DateTimeOffset(2026,
                11,
                10,
                12,
                0,
                0,
                TimeSpan.Zero).AddTicks(1_234_560),
            "'2026-11-10 12:00:00.123456+00'",
            "'2026-11-10 12:00:00+00'"),
        "interval" => new TemporalScenario(typeof(TimeSpan),
            "interval",
            new TimeSpan(2, 1, 2, 3).Add(TimeSpan.FromTicks(1_234_560)),
            "'2 days 01:02:03.123456'",
            "'01:02:03.123456'"),
        "negative_interval" => new TemporalScenario(typeof(TimeSpan),
            "interval",
            new TimeSpan(-2, -1, -2, -3)
                .Subtract(TimeSpan.FromTicks(1_234_560)),
            "'-2 days -01:02:03.123456'",
            "'-01:02:03.123456'"),
        "duration_time" => new TemporalScenario(typeof(TimeSpan),
            "time(6) without time zone",
            TimeSpan
                .FromHours(12)
                .Add(TimeSpan.FromTicks(1_234_560)),
            "'12:00:00.123456'",
            "'12:00:00'"),
        "date" => new TemporalScenario(typeof(DateOnly),
            "date",
            new DateOnly(2026, 11, 10),
            "'2026-11-10'",
            "'2026-11-09'"),
        "date_min" => new TemporalScenario(typeof(DateOnly), "date", DateOnly.MinValue, "'-infinity'", "'0001-01-01'"),
        "date_max" => new TemporalScenario(typeof(DateOnly), "date", DateOnly.MaxValue, "'infinity'", "'9999-12-31'"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Creates one ensure operation with the requested policy and complete literal value.</summary>
    private static MigrationBuilder Build(
        DbContext context,
        TemporalScenario scenario,
        SafeMigrationPolicy policy
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("temporal_identity",
            new ExpectedColumnDefinition("v",
                scenario.ClrType,
                false,
                storeType: scenario.StoreType,
                defaultValue: SafeMigrationDefaultValue.Literal(scenario.Value)),
            policy);

        return builder;
    }

    /// <summary>Uses the public runner shared with application preflight.</summary>
    private static Task<SafeMigrationRunReport> AnalyzeAsync(
        DbContext context,
        MigrationBuilder builder,
        CancellationToken cancellationToken
    ) => context
        .GetService<ISafeMigrationRunner>()
        .AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("temporal-default-identity"),
            cancellationToken);

    /// <summary>Retains independently authored fixture values and their native storage contract.</summary>
    private sealed record TemporalScenario(
        Type ClrType,
        string StoreType,
        object Value,
        string NativeValue,
        string DifferentValue
    );
}
