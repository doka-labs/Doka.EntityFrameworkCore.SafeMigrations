namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Protects catalog identity rules without depending on native connection state.</summary>
public sealed class PostgreSqlCatalogExpressionIdentityTests
{
    private const string OfflineConnection = "Host=localhost;Database=offline;Username=test;Password=test";

    /// <summary>An unqualified explicit collation must be visible under the active native search path.</summary>
    [Fact]
    public void UnqualifiedIndexCollationRequiresVisibleNativeIdentity()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var operation = new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
                "ix_identity",
                "identity_rows",
                [new ExpectedIndexKeyDefinition("value", collation: new SafeMigrationCollationIdentifier("C"))])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var state = BuildState(context, operation);

        // Assert
        Assert.Contains("pg_catalog.pg_collation_is_visible(coll.oid)", state, StringComparison.Ordinal);
    }

    /// <summary>Index-only matching must include PostgreSQL's uppercase special-form deparser spelling.</summary>
    [Fact]
    public void FunctionalIndexIncludesNativeSpecialFormCandidate()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var expression = SafeMigrationSql.Function("COALESCE",
            SafeMigrationSql.Identifier("value"),
            SafeMigrationSql.Literal(0));

        var operation = new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
                "ix_identity",
                "identity_rows",
                [new ExpectedIndexKeyDefinition(structuredExpression: expression)])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var state = BuildState(context, operation);

        // Assert
        Assert.Contains("COALESCE(", state, StringComparison.Ordinal);
    }

    /// <summary>Partial-index predicates retain the owning relation needed for native source coercions.</summary>
    [Fact]
    public void PartialIndexIncludesRelationAwareNativeTextCandidate()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var predicate = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("value"),
            SafeMigrationSqlBinaryOperator.Equal,
            SafeMigrationSql.Literal("x"));

        var operation = new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
                "ix_identity",
                "identity_rows",
                [new ExpectedIndexKeyDefinition("id")],
                structuredFilter: predicate)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var state = BuildState(context, operation);

        // Assert
        Assert.Contains("attrelid OPERATOR(pg_catalog.=) i.indrelid", state, StringComparison.Ordinal);
    }

    /// <summary>Temporal candidates derive the complete typed value instead of accepting shortened strings.</summary>
    [Fact]
    public void TemporalDefaultUsesNativeFullValueDisplay()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var column = new ExpectedColumnDefinition("value",
            typeof(TimeSpan),
            false,
            storeType: "interval",
            defaultValue: SafeMigrationDefaultValue.Literal(new TimeSpan(2, 1, 2, 3)));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("identity_rows", column),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var state = BuildState(context, operation);

        // Assert
        Assert.Contains("pg_catalog.quote_literal", state, StringComparison.Ordinal);
        Assert.DoesNotContain("'''01:02:03'''", state, StringComparison.Ordinal);
    }

    /// <summary>Explicit field units preserve negative days, microseconds and CLR range endpoints.</summary>
    /// <param name="ticks">The complete signed CLR duration.</param>
    /// <param name="expected">The independently specified native interval literal.</param>
    [Theory]
    [InlineData(-1_765_231_234_560L, "INTERVAL '-2 days -3723.123456 seconds'")]
    [InlineData(-10L, "INTERVAL '0 days -0.000001 seconds'")]
    [InlineData(-9L, "INTERVAL '0 days 0 seconds'")]
    [InlineData(long.MinValue, "INTERVAL '-10675199 days -10085.47758 seconds'")]
    [InlineData(long.MaxValue, "INTERVAL '10675199 days 10085.47758 seconds'")]
    public void DurationLiteralPreservesWholeSignWithoutFloatingPoint(
        long ticks,
        string expected
    )
    {
        // Arrange
        var duration = TimeSpan.FromTicks(ticks);

        // Act
        var actual = PostgreSqlTemporalLiteral.Render(duration);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>The pretty index candidate removes only proven null-test operand parentheses.</summary>
    [Fact]
    public void NativePrettyBooleanCandidateDoesNotChangeOrdinaryCatalogShape()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var left = SafeMigrationSql.Binary(SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("a")),
            SafeMigrationSqlBinaryOperator.And,
            SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("b")));

        var expression = SafeMigrationSql.Binary(left,
            SafeMigrationSqlBinaryOperator.And,
            SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("c")));

        // Act
        var ordinary = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, static text => "'" + text + "'");
        var pretty = renderer.RenderCatalogNativeDeparsedCandidateSql(expression,
            static text => "'" + text + "'",
            prettyIndexBooleanOperands: true);

        // Assert
        Assert.Contains("' IS NULL) AND ('", ordinary!, StringComparison.Ordinal);
        Assert.Contains("' IS NULL AND '", pretty!, StringComparison.Ordinal);
        Assert.DoesNotContain("' IS NULL) AND ('", pretty!, StringComparison.Ordinal);
    }

    /// <summary>TimeSpan's explicit clock-time mapping is not rewritten into an INTERVAL source node.</summary>
    [Fact]
    public void MappedClockTimeDurationRetainsNativeLiteralSourceType()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var expression = SafeMigrationSql.Literal(TimeSpan.FromHours(12), "time(6) without time zone");

        // Act
        var sql = renderer.Render(expression);

        // Assert
        Assert.Equal("TIME '12:00:00'::time(6) without time zone", sql);
    }

    /// <summary>An unmapped old literal definition remains Unsupported rather than escaping validation.</summary>
    [Fact]
    public void UnmappedOldDurationDefaultDoesNotEscapeAsMappingException()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var operation = new SafeMigrationOperation(new AlterColumnIntent("identity_rows",
                new ExpectedColumnDefinition("v", typeof(TimeSpan), false, storeType: "interval"),
                new ExpectedColumnDefinition("v",
                    typeof(object),
                    false,
                    storeType: "unmapped_temporal_type",
                    defaultValue: SafeMigrationDefaultValue.Literal(TimeSpan.FromHours(1)))),
            SafeMigrationPolicy.RepairIfSafe);

        PostgreSqlSafeMigrationRuntimePlan? plan = null;

        // Act
        var failure = Record.Exception(() => plan = catalog.Build(operation));

        // Assert
        Assert.Null(failure);
        Assert.NotNull(plan);
        Assert.True(plan.IsStaticallyUnsupported);
    }

    /// <summary>The shared seed literal path reports invalid clock values as Unsupported before rendering.</summary>
    [Fact]
    public void ManagedClockTimeDurationDoesNotEscapeAsLiteralException()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var operation = new SafeMigrationOperation(new EnsureModelManagedDataIntent("identity_rows",
                ["id"],
                ["integer"],
                ["id", "v"],
                ["integer", "time(6) without time zone"],
                new object?[,] { { 42, TimeSpan.FromHours(-1) } },
                schema: null,
                uniqueKeys: null),
            SafeMigrationPolicy.ThrowIfDifferent);

        PostgreSqlSafeMigrationRuntimePlan? plan = null;

        // Act
        var failure = Record.Exception(() => plan = catalog.Build(operation));

        // Assert
        Assert.Null(failure);
        Assert.NotNull(plan);
        Assert.True(plan.IsStaticallyUnsupported);
    }

    /// <summary>Certifies temporal literal mappings before native evaluation.</summary>
    /// <param name="kind">The independently unsupported scalar/store mapping.</param>
    [Theory]
    [InlineData("span_date")]
    [InlineData("date_interval")]
    [InlineData("scalar_array")]
    public void UnsupportedTemporalLiteralMappingIsClassifiedBeforeRendering(
        string kind
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var value = kind == "span_date"
            ? (object)TimeSpan.FromHours(2)
            : new DateTime(2026,
                11,
                10,
                12,
                0,
                0,
                DateTimeKind.Utc);

        var storeType = kind switch
        {
            "span_date" => "date", "date_interval" => "interval", _ => "timestamp[]",
        };

        var expression = SafeMigrationSql.Literal(value, storeType);

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var failure = Record.Exception(() => renderer.Render(expression));

        // Assert
        Assert.Equal("temporal_literal_mapping", feature);
        Assert.IsType<NotSupportedException>(failure);
    }

    /// <summary>Preserves composed value converters without a CLR-type denylist.</summary>
    /// <param name="storeType">The store type reached through the provider's converter selection.</param>
    [Theory]
    [InlineData("bigint")]
    [InlineData("bytea")]
    public void ComposedTemporalLiteralMappingPreservesProviderConverter(
        string storeType
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var source = context.GetService<IRelationalTypeMappingSource>();
        var renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(source,
            context.GetService<ISqlGenerationHelper>());

        var value = new DateTime(2026,
            11,
            10,
            12,
            0,
            0,
            DateTimeKind.Utc);

        var mapping = source.FindMapping(value.GetType(), storeType)!;
        var expression = SafeMigrationSql.Literal(value, storeType);

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var actual = renderer.Render(expression);

        // Assert
        Assert.NotNull(mapping.Converter);
        Assert.Null(feature);
        Assert.Equal(mapping.GenerateSqlLiteral(value) + "::" + storeType, actual);
    }

    /// <summary>Certifying Literal store mappings does not narrow the separately declared SQL CAST contract.</summary>
    [Fact]
    public void ExplicitSqlCastRemainsDistinctFromLiteralStoreMapping()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(OfflineConnection, registerSafeMigrations: false);
        var renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Literal(
                new DateTime(2026,
                    11,
                    10,
                    12,
                    0,
                    0,
                    DateTimeKind.Utc)),
            "date");

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var sql = renderer.Render(expression);

        // Assert
        Assert.Null(feature);
        Assert.StartsWith("CAST(TIMESTAMPTZ '", sql, StringComparison.Ordinal);
        Assert.EndsWith(" AS date)", sql, StringComparison.Ordinal);
    }

    /// <summary>Uses the same catalog builder consumed by preflight, runtime guards and postconditions.</summary>
    private static string BuildState(
        SafeMigrationDbContext context,
        SafeMigrationOperation operation
    )
    {
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        return builder.Build(operation)
            .StateExpression;
    }
}
