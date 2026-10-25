namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Reports independent PostgreSQL expression-rendering contracts.</summary>
public sealed partial class PostgreSqlSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Preserves the exact authored SQL for one independently selectable node shape.</summary>
    /// <param name="caseId">The stable, serializable expression case identifier.</param>
    [Theory]
    [InlineData("identifier")]
    [InlineData("null")]
    [InlineData("typed-null")]
    [InlineData("alias-null")]
    [InlineData("array-null")]
    [InlineData("integer")]
    [InlineData("typed-integer")]
    [InlineData("not")]
    [InlineData("negate")]
    [InlineData("is-null")]
    [InlineData("is-not-null")]
    [InlineData("between")]
    [InlineData("in")]
    [InlineData("function")]
    [InlineData("cast")]
    [InlineData("alias-cast")]
    [InlineData("collate")]
    [InlineData("current-date")]
    [InlineData("current-time")]
    [InlineData("current-timestamp")]
    [InlineData("provider-fragment")]
    [InlineData("opaque")]
    public void Render_ProducesDeterministicSqlForStructuredNode(string caseId)
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var (expression, expectedSql) = StructuredNodeCase(caseId);

        // Act
        var actual = renderer.Render(expression);

        // Assert
        Assert.Equal(expectedSql, actual);
    }

    /// <summary>Uses PostgreSQL's canonical spelling for one supported built-in alias.</summary>
    /// <param name="storeType">The authored alias with its precision and array suffixes.</param>
    /// <param name="expectedStoreType">The corresponding native catalog spelling.</param>
    [Theory]
    [InlineData("int", "integer")]
    [InlineData("int2", "smallint")]
    [InlineData("int4", "integer")]
    [InlineData("int8", "bigint")]
    [InlineData("float4", "real")]
    [InlineData("float8", "double precision")]
    [InlineData("float", "double precision")]
    [InlineData("float(1)", "real")]
    [InlineData("float ( 24 )", "real")]
    [InlineData("float(25)", "double precision")]
    [InlineData("float(53)", "double precision")]
    [InlineData("float(24)[]", "real[]")]
    [InlineData("bool", "boolean")]
    [InlineData("decimal(18,4)", "numeric(18,4)")]
    [InlineData("varchar(32)", "character varying(32)")]
    [InlineData("char(8)", "character(8)")]
    [InlineData("varbit(16)", "bit varying(16)")]
    [InlineData("timestamp(6)", "timestamp(6) without time zone")]
    [InlineData("timestamptz(6)", "timestamp(6) with time zone")]
    [InlineData("time(6)", "time(6) without time zone")]
    [InlineData("timetz(6)", "time(6) with time zone")]
    public void Render_CanonicalizesPostgreSqlBuiltInAliases(
        string storeType,
        string expectedStoreType
    )
    {
        // Arrange
        using var context = CreateContext();
        var renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var result = renderer.Render(
            SafeMigrationSql.Cast(
                SafeMigrationSql.Identifier("value"),
                storeType));

        // Assert
        Assert.Equal($"CAST(value AS {expectedStoreType})", result);
    }

    /// <summary>Rejects a malformed or out-of-range FLOAT alias before rendering.</summary>
    /// <param name="storeType">The unsupported authored alias.</param>
    [Theory]
    [InlineData("float(0)")]
    [InlineData("float(54)")]
    [InlineData("float(-1)")]
    [InlineData("float(24); DROP TABLE items; --")]
    public void Render_RejectsInvalidPostgreSqlFloatAliases(
        string storeType
    )
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var exception = Record.Exception(() => renderer.Render(expression));

        // Assert
        Assert.Equal("structured_cast_type", feature);
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>Catalog collation names resolve exact namespaces before using PostgreSQL's deparsed spelling.</summary>
    /// <param name="schema">The authored namespace, or null for the search path.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("pg_catalog")]
    [InlineData("custom's schema")]
    public void RenderCatalogCandidateSql_ResolvesPhysicalCollationSpelling(
        string? schema
    )
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Collate(SafeMigrationSql.Identifier("value"), "C", schema);

        // Act
        var candidate = renderer.RenderCatalogCandidateSql(expression, Literal);

        // Assert
        var identifier = schema is null
            ? "pg_catalog.quote_ident('C')"
            : $"pg_catalog.quote_ident({Literal(schema)}) || '.' || pg_catalog.quote_ident('C')";

        Assert.Contains($"pg_catalog.to_regcollation({identifier})::text", candidate, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.quote_ident('value')", candidate, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pretty catalog candidate keeps nested collation without extra function-argument parentheses.
    /// </summary>
    [Fact]
    public void RenderCatalogDeparsedCandidateSql_PreservesNestedCollationWithoutExtraParentheses()
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Function(
            "lower",
            SafeMigrationSql.Collate(SafeMigrationSql.Identifier("value"), "C", "pg_catalog"));

        // Act
        var candidate = renderer.RenderCatalogDeparsedCandidateSql(expression, Literal);

        // Assert
        Assert.Contains("'lower(' || pg_catalog.quote_ident('value') || ' COLLATE '", candidate,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'lower(('", candidate, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.to_regcollation(", candidate, StringComparison.Ordinal);
    }

    /// <summary>Preserves every binary token as an independently selectable rendering contract.</summary>
    /// <param name="value">The structured binary operator.</param>
    /// <param name="token">The expected PostgreSQL SQL token.</param>
    [Theory]
    [InlineData(SafeMigrationSqlBinaryOperator.And, "AND")]
    [InlineData(SafeMigrationSqlBinaryOperator.Or, "OR")]
    [InlineData(SafeMigrationSqlBinaryOperator.Equal, "=")]
    [InlineData(SafeMigrationSqlBinaryOperator.NotEqual, "<>")]
    [InlineData(SafeMigrationSqlBinaryOperator.LessThan, "<")]
    [InlineData(SafeMigrationSqlBinaryOperator.LessThanOrEqual, "<=")]
    [InlineData(SafeMigrationSqlBinaryOperator.GreaterThan, ">")]
    [InlineData(SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, ">=")]
    [InlineData(SafeMigrationSqlBinaryOperator.Add, "+")]
    [InlineData(SafeMigrationSqlBinaryOperator.Subtract, "-")]
    [InlineData(SafeMigrationSqlBinaryOperator.Multiply, "*")]
    [InlineData(SafeMigrationSqlBinaryOperator.Divide, "/")]
    [InlineData(SafeMigrationSqlBinaryOperator.Modulo, "%")]
    public void Render_ProducesBinaryOperator(SafeMigrationSqlBinaryOperator value, string token)
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Literal(1), value, SafeMigrationSql.Literal(2));

        // Act
        var actual = renderer.Render(expression);

        // Assert
        Assert.Equal($"(1 {token} 2)", actual);
    }

    /// <summary>Fails when a new binary operator has no independently reported rendering case.</summary>
    [Fact]
    public void Render_BinaryOperatorCasesCoverEveryDefinedOperator()
    {
        // Arrange
        var method = typeof(PostgreSqlSafeMigrationSqlExpressionRendererTests)
            .GetMethod(nameof(Render_ProducesBinaryOperator))!;

        // Act
        var covered = method.GetCustomAttributes<InlineDataAttribute>()
            .SelectMany(attribute => attribute.GetData(method))
            .Select(static data => Assert.IsType<SafeMigrationSqlBinaryOperator>(data[0]))
            .OrderBy(static value => value)
            .ToArray();

        // Assert
        Assert.Equal(Enum.GetValues<SafeMigrationSqlBinaryOperator>().OrderBy(static value => value), covered);
        Assert.Equal(13, covered.Length);
    }

    /// <summary>Rejects a missing expression before attempting SQL generation.</summary>
    [Fact]
    public void Render_RejectsNullExpression()
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);

        // Act
        var exception = Record.Exception(() => renderer.Render(null!));

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>Reports and rejects one independently selectable unsupported input.</summary>
    /// <param name="caseId">The stable unsupported-input case identifier.</param>
    /// <param name="expectedFeature">The exact provider compatibility code.</param>
    [Theory]
    [InlineData("unmapped-literal", "structured_literal_mapping")]
    [InlineData("unsafe-cast-literal", "structured_cast_type")]
    [InlineData("unsafe-cast-identifier", "structured_cast_type")]
    [InlineData("foreign-fragment", "provider_fragment_mismatch")]
    public void Render_RejectsUnsupportedValue(string caseId, string expectedFeature)
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = UnsupportedExpressionCase(caseId);

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var exception = Record.Exception(() => renderer.Render(expression));

        // Assert
        Assert.Equal(expectedFeature, feature);
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>Accepts an explicitly PostgreSQL-owned provider fragment.</summary>
    [Fact]
    public void GetUnsupportedFeature_AcceptsPostgreSqlProviderFragment()
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.ProviderFragment("npgsql_postgresql", "CURRENT_USER");

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Null(feature);
    }

    /// <summary>Creates only the services needed for resource-free rendering contracts.</summary>
    private static SafeMigrationDbContext CreateContext() =>
        new("Host=localhost;Database=renderer;Username=test;Password=test");

    /// <summary>Uses the active provider's real mapping and delimiting services.</summary>
    private static PostgreSqlSafeMigrationSqlExpressionRenderer CreateRenderer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Encodes catalog text without opening a database connection.</summary>
    private static string Literal(
        string value
    ) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
