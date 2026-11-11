namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks native temporal identity in generated columns, CHECKs and indexes.</summary>
public sealed class PostgreSqlTemporalExpressionIdentityIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses one shared native server and a separately leased database per test.</summary>
    /// <param name="fixture">The PostgreSQL fixture.</param>
    public PostgreSqlTemporalExpressionIdentityIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Enumerates valid typed and inferred native temporal literal mappings.</summary>
    /// <returns>The native family, explicit typing and owning expression consumer.</returns>
    public static TheoryData<string, bool, string> Cases()
    {
        var cases = new TheoryData<string, bool, string>();
        foreach (var (kind, typed, consumer) in NativeCases())
        {
            cases.Add(kind, typed, consumer);
        }

        return cases;
    }

    /// <summary>Adds both exact and changed-value catalog contracts for every admitted expression consumer.</summary>
    /// <returns>The case plus whether its authored value matches the independently created native object.</returns>
    public static TheoryData<string, bool, string, bool> MatchingCases()
    {
        var cases = new TheoryData<string, bool, string, bool>();
        foreach (var (kind, typed, consumer) in NativeCases())
        {
            cases.Add(kind, typed, consumer, false);
            cases.Add(kind, typed, consumer, true);
        }

        return cases;
    }

    /// <summary>Enumerates unsupported scalar-to-native mappings independently from SQL CAST expressions.</summary>
    /// <returns>The source/target pair and each affected mutation consumer.</returns>
    public static TheoryData<string, string> UnsupportedMappingCases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var kind in new[]
            {
                "span_date",
                "date_interval",
                "scalar_array",
                "date_array",
                "clock_array",
            })
        {
            foreach (var consumer in new[]
                {
                    "literal_default",
                    "expression_default",
                    "generated",
                    "check",
                    "index",
                    "seed",
                })
            {
                cases.Add(kind, consumer);
            }
        }

        return cases;
    }

    /// <summary>Shares typed native inputs without reinterpreting xUnit's object-array output.</summary>
    private static IEnumerable<(string Kind, bool Typed, string Consumer)> NativeCases()
    {
        foreach (var kind in new[]
            {
                "interval",
                "negative_interval",
                "interval_precision",
                "clock",
                "clock_precision",
                "endpoint",
                "timestamp",
                "timestamp_utc",
                "timestamp_plain",
                "timetz",
                "date",
                "infinity",
            })
        {
            foreach (var typed in new[] { false, true })
            {
                if (!typed
                    && kind is "endpoint"
                        or "interval_precision"
                        or "clock_precision"
                        or "timestamp_plain"
                        or "timetz")
                {
                    continue;
                }

                foreach (var consumer in new[] { "generated", "check", "index" })
                {
                    yield return (kind, typed, consumer);
                }
            }
        }
    }

    /// <summary>Native spelling preserves fractions, signs, days and infinities.</summary>
    /// <param name="kind">The temporal native value family.</param>
    /// <param name="typed">Whether the literal explicitly carries the native store type.</param>
    /// <param name="consumer">The catalog expression consumer.</param>
    /// <param name="matching">Whether the expected literal has the native fixture's exact value.</param>
    [Theory]
    [MemberData(nameof(MatchingCases))]
    public async Task ExistingNativeTemporalExpressionRetainsValueIdentity(
        string kind,
        bool typed,
        string consumer,
        bool matching
    )
    {
        // Arrange
        var scenario = Scenario(kind);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, CreateNative(scenario, consumer), CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = Build(context,
            scenario,
            consumer,
            typed,
            matching
                ? scenario.Value
                : scenario.Other);

        // Act
        var report = await Analyze(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM temporal_expression WHERE id=42;",
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
            Assert.NotNull(failure);
            Assert.Contains("doka_sm_different", failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, rows);
    }

    /// <summary>Execution, native postconditions and replay agree for newly created temporal expressions.</summary>
    /// <param name="kind">The temporal native value family.</param>
    /// <param name="typed">Whether the literal explicitly carries the native store type.</param>
    /// <param name="consumer">The generated-column, CHECK or expression-index consumer.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TemporalExpressionCreationSatisfiesPostconditionAndReplay(
        string kind,
        bool typed,
        string consumer
    )
    {
        // Arrange
        var scenario = Scenario(kind);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"""
            CREATE TABLE temporal_expression(id integer, source {scenario.StoreType}); INSERT INTO
            temporal_expression VALUES(42,
            {scenario.Native});
            """,
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = Build(context, scenario, consumer, typed, scenario.Value);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var report = await Analyze(context, builder, CancellationToken.None);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM temporal_expression WHERE id=42"
            + (consumer == "generated"
                ? $" AND v={scenario.Native};"
                : ";"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(1, rows);
    }

    /// <summary>A native CHECK may retain a constant's precision conversion instead of folding it.</summary>
    /// <param name="kind">The temporal value with an explicit native precision.</param>
    [Theory]
    [InlineData("clock")]
    [InlineData("endpoint")]
    [InlineData("timestamp")]
    public async Task ExplicitNativeCheckPrecisionRetainsExactConstantAndCast(
        string kind
    )
    {
        // Arrange
        var scenario = Scenario(kind);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"""
            CREATE TABLE temporal_expression(id integer, source {scenario.StoreType}); INSERT INTO
            temporal_expression VALUES(42,
            {scenario.Native}); ALTER TABLE temporal_expression ADD CONSTRAINT ck_temporal
            CHECK(source=({scenario.Native})::{scenario.StoreType});
            """,
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = Build(context, scenario, "check", true, scenario.Value);

        // Act
        var report = await Analyze(context, builder, CancellationToken.None);
        var native = await ScalarStringAsync(connectionString,
            "SELECT pg_get_expr(conbin,conrelid) FROM pg_constraint WHERE conname='ck_temporal';",
            CancellationToken.None);

        // Assert
        Assert.True(report.Status == SafeMigrationReportStatus.Ready, native);
    }

    /// <summary>Invalid inferred timestamp values fail closed before native execution.</summary>
    /// <param name="consumer">The literal default, structured or seed consumer.</param>
    [Theory]
    [InlineData("literal_default")]
    [InlineData("expression_default")]
    [InlineData("generated")]
    [InlineData("check")]
    [InlineData("index")]
    [InlineData("seed")]
    public async Task InvalidNativeTimestampInputIsUnsupportedBeforeMutation(
        string consumer
    )
    {
        // Arrange
        var value = new DateTime(2026,
            11,
            10,
            12,
            0,
            0,
            DateTimeKind.Unspecified);

        var scenario = new TemporalScenario(typeof(DateTime),
            "timestamp(6) with time zone",
            value,
            value,
            "TIMESTAMPTZ '2026-11-10 12:00:00+00'");

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            """
            CREATE TABLE temporal_expression(id integer PRIMARY KEY, source timestamp(6) with time zone);
            INSERT INTO temporal_expression VALUES(42,
            TIMESTAMPTZ '2026-11-10 12:00:00+00');
            """,
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = Build(context, scenario, consumer, false, value);
        if (consumer is "literal_default" or "expression_default")
        {
            builder.Operations.Clear();
            builder.EnsureColumn("temporal_expression",
                new ExpectedColumnDefinition("v",
                    typeof(DateTime),
                    true,
                    storeType: scenario.StoreType,
                    defaultValue: consumer == "literal_default"
                        ? SafeMigrationDefaultValue.Literal(value)
                        : SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value))),
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else if (consumer == "seed")
        {
            builder.Operations.Clear();
            builder.Operations.Add(new SafeMigrationOperation(new EnsureModelManagedDataIntent("temporal_expression",
                    ["id"],
                    ["integer"],
                    ["id", "source"],
                    ["integer", scenario.StoreType],
                    new object?[,] { { 42, value } },
                    schema: null,
                    uniqueKeys: null),
                SafeMigrationPolicy.ThrowIfDifferent));
        }

        // Act
        var report = await Analyze(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM temporal_expression WHERE id=42 AND source=TIMESTAMPTZ '2026-11-10 12:00:00+00';",
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

    /// <summary>An uncertified temporal Literal store mapping never becomes executable fallback CAST SQL.</summary>
    /// <param name="kind">The independently unsupported scalar/store-type pair.</param>
    /// <param name="consumer">The default, generated, CHECK, index or seed consumer.</param>
    [Theory]
    [MemberData(nameof(UnsupportedMappingCases))]
    public async Task UnsupportedTemporalLiteralMappingIsRejectedBeforeQueryEvaluation(
        string kind,
        string consumer
    )
    {
        // Arrange
        object value = kind switch
        {
            "span_date" => TimeSpan.FromHours(2),
            "date_array" => new DateOnly(2026, 11, 10),
            "clock_array" => new TimeOnly(12, 0),
            _ => new DateTime(2026,
                11,
                10,
                12,
                0,
                0,
                DateTimeKind.Utc),
        };

        var storeType = kind switch
        {
            "span_date" => "date", "date_interval" => "interval", "date_array" => "date[]",
            "clock_array" => "time[]", _ => "timestamp[]",
        };

        var targetType = kind switch
        {
            "span_date" => typeof(DateOnly), "date_interval" => typeof(TimeSpan),
            "date_array" => typeof(DateOnly[]), "clock_array" => typeof(TimeOnly[]), _ => typeof(DateTime[]),
        };

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"""
            CREATE TABLE temporal_expression(id integer PRIMARY KEY, source {storeType});
            INSERT INTO temporal_expression(id) VALUES(42);
            """,
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = Build(context,
            new TemporalScenario(targetType, storeType, value, value, "NULL"),
            consumer,
            true,
            value);

        if (consumer is "literal_default" or "expression_default")
        {
            builder.Operations.Clear();
            builder.EnsureColumn("temporal_expression",
                new ExpectedColumnDefinition("v",
                    consumer == "literal_default"
                        ? value.GetType()
                        : targetType,
                    true,
                    storeType: storeType,
                    defaultValue: consumer == "literal_default"
                        ? SafeMigrationDefaultValue.Literal(value)
                        : SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value, storeType))),
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else if (consumer == "seed")
        {
            builder.Operations.Clear();
            builder.Operations.Add(new SafeMigrationOperation(new EnsureModelManagedDataIntent("temporal_expression",
                    ["id"],
                    ["integer"],
                    ["id", "source"],
                    ["integer", storeType],
                    new[,] { { 42, value } },
                    schema: null,
                    uniqueKeys: null),
                SafeMigrationPolicy.ThrowIfDifferent));
        }

        // Act
        var report = await Analyze(context, builder, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM temporal_expression WHERE id=42;",
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

    /// <summary>Builds a contract independently from the native fixture SQL.</summary>
    private static MigrationBuilder Build(
        DbContext context,
        TemporalScenario scenario,
        string consumer,
        bool typed,
        object value
    )
    {
        var literal = SafeMigrationSql.Literal(value,
            typed
                ? scenario.StoreType
                : null);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (consumer == "generated")
        {
            builder.EnsureColumn("temporal_expression",
                new ExpectedColumnDefinition("v",
                    scenario.ClrType,
                    true,
                    storeType: scenario.StoreType,
                    computedExpression: literal,
                    isStored: true),
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else if (consumer == "check")
        {
            builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_temporal",
                    "temporal_expression",
                    SafeMigrationSql.Binary(SafeMigrationSql.Identifier("source"),
                        SafeMigrationSqlBinaryOperator.Equal,
                        literal)),
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureIndex(new ExpectedIndexDefinition("ix_temporal",
                    "temporal_expression",
                    [new ExpectedIndexKeyDefinition(structuredExpression: literal)]),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        return builder;
    }

    /// <summary>Creates one independently authored native expression and its preserved row.</summary>
    private static string CreateNative(
        TemporalScenario scenario,
        string consumer
    ) => $"""
        CREATE TABLE temporal_expression(id integer, source {scenario.StoreType}{(consumer == "generated"
            ? $", v {scenario.StoreType} GENERATED ALWAYS AS ({scenario.Native}) STORED"
            : "")});
        INSERT INTO temporal_expression(id,source) VALUES(42,
        {scenario.Native}); {(consumer == "check"
            ? $"ALTER TABLE temporal_expression ADD CONSTRAINT ck_temporal CHECK(source={scenario.Native});"
            : consumer == "index"
                ? $"CREATE INDEX ix_temporal ON temporal_expression (({scenario.Native}));"
                : "")}
        """;

    /// <summary>Uses the public application preflight runner.</summary>
    private static Task<SafeMigrationRunReport> Analyze(
        DbContext context,
        MigrationBuilder builder,
        CancellationToken cancellationToken
    ) => context
        .GetService<ISafeMigrationRunner>()
        .AnalyzeAsync(context,
            builder.Operations,
            new SafeMigrationRunOptions("temporal-expression-identity"),
            cancellationToken);

    /// <summary>Retains CLR values, changed values and independently authored native literal input.</summary>
    private static TemporalScenario Scenario(
        string kind
    ) => kind switch
    {
        "interval" => new TemporalScenario(typeof(TimeSpan),
            "interval",
            new TimeSpan(2, 1, 2, 3).Add(TimeSpan.FromTicks(1_234_560)),
            new TimeSpan(2, 1, 2, 3),
            "INTERVAL '2 days 01:02:03.123456'"),
        "negative_interval" => new TemporalScenario(typeof(TimeSpan),
            "interval",
            new TimeSpan(-2, -1, -2, -3).Subtract(TimeSpan.FromTicks(1_234_560)),
            new TimeSpan(-2, -1, -2, -3),
            "INTERVAL '-2 days -01:02:03.123456'"),
        "interval_precision" => new TemporalScenario(typeof(TimeSpan),
            "interval(3)",
            new TimeSpan(2, 1, 2, 3).Add(TimeSpan.FromTicks(1_234_560)),
            new TimeSpan(2, 1, 2, 3).Add(TimeSpan.FromTicks(1_244_560)),
            "INTERVAL '2 days 01:02:03.123'"),
        "clock" => new TemporalScenario(typeof(TimeOnly),
            "time(6) without time zone",
            new TimeOnly(12, 0).Add(TimeSpan.FromTicks(1_234_560)),
            new TimeOnly(12, 0),
            "TIME '12:00:00.123456'"),
        "endpoint" => new TemporalScenario(typeof(TimeSpan),
            "time(6) without time zone",
            TimeSpan.FromDays(1),
            TimeSpan.Zero,
            "TIME '24:00:00'"),
        "clock_precision" => new TemporalScenario(typeof(TimeOnly),
            "time(3) without time zone",
            new TimeOnly(12, 0).Add(TimeSpan.FromTicks(1_234_560)),
            new TimeOnly(12, 0).Add(TimeSpan.FromTicks(1_244_560)),
            "TIME '12:00:00.123'"),
        "timestamp" => new TemporalScenario(typeof(DateTimeOffset),
            "timestamp(6) with time zone",
            new DateTimeOffset(2026,
                11,
                10,
                12,
                0,
                0,
                TimeSpan.Zero).AddTicks(1_234_560),
            new DateTimeOffset(2026,
                11,
                10,
                12,
                0,
                0,
                TimeSpan.Zero),
            "TIMESTAMPTZ '2026-11-10 12:00:00.123456+00'"),
        "timestamp_utc" => new TemporalScenario(typeof(DateTime),
            "timestamp(6) with time zone",
            new DateTime(2026,
                11,
                10,
                12,
                0,
                0,
                DateTimeKind.Utc).AddTicks(1_234_560),
            new DateTime(2026,
                11,
                10,
                12,
                0,
                0,
                DateTimeKind.Utc),
            "TIMESTAMPTZ '2026-11-10 12:00:00.123456+00'"),
        "timestamp_plain" => new TemporalScenario(typeof(DateTime),
            "timestamp(6) without time zone",
            new DateTime(2026, 11, 10, 12, 0, 0).AddTicks(1_234_560),
            new DateTime(2026, 11, 10, 12, 0, 0),
            "TIMESTAMP '2026-11-10 12:00:00.123456'"),
        "timetz" => new TemporalScenario(typeof(DateTimeOffset),
            "time(6) with time zone",
            new DateTimeOffset(2026,
                11,
                10,
                12,
                0,
                0,
                TimeSpan.FromHours(2)).AddTicks(1_234_560),
            new DateTimeOffset(2026,
                11,
                10,
                12,
                0,
                0,
                TimeSpan.FromHours(2)),
            "TIMETZ '12:00:00.123456+02'"),
        "date" => new TemporalScenario(typeof(DateOnly),
            "date",
            new DateOnly(2026, 11, 10),
            new DateOnly(2026, 11, 9),
            "DATE '2026-11-10'"),
        "infinity" => new TemporalScenario(typeof(DateOnly),
            "date",
            DateOnly.MaxValue,
            new DateOnly(9999, 12, 30),
            "DATE 'infinity'"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Owns one finite temporal value-domain test contract.</summary>
    private sealed record TemporalScenario(
        Type ClrType,
        string StoreType,
        object Value,
        object Other,
        string Native
    );
}
