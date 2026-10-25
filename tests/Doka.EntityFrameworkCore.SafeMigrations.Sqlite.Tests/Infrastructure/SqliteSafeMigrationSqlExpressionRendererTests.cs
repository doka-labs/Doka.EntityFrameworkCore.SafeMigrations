namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises independently observable SQLite expression rendering and rejection contracts.</summary>
public sealed partial class SqliteSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Each structured node has an independently selectable exact SQLite rendering contract.</summary>
    /// <param name="caseId">The stable expression-factory case.</param>
    /// <param name="expectedSql">The exact SQL emitted by the original renderer contract.</param>
    [Theory]
    [InlineData("identifier", "\"app\".\"Value\"")]
    [InlineData("null", "NULL")]
    [InlineData("typed-null", "CAST(NULL AS INTEGER)")]
    [InlineData("literal", "42")]
    [InlineData("typed-literal", "CAST(42 AS INTEGER)")]
    [InlineData("not", "(NOT \"app\".\"Value\")")]
    [InlineData("negate", "(-\"app\".\"Value\")")]
    [InlineData("is-null", "(\"app\".\"Value\" IS NULL)")]
    [InlineData("is-not-null", "(\"app\".\"Value\" IS NOT NULL)")]
    [InlineData("between", "(\"app\".\"Value\" BETWEEN 1 AND 10)")]
    [InlineData("not-between", "(\"app\".\"Value\" NOT BETWEEN 1 AND 10)")]
    [InlineData("in", "(\"app\".\"Value\" IN (1, 2))")]
    [InlineData("not-in", "(\"app\".\"Value\" NOT IN (1))")]
    [InlineData("function", "LOWER(\"app\".\"Value\")")]
    [InlineData("cast", "CAST(\"app\".\"Value\" AS TEXT)")]
    [InlineData("collate", "(\"app\".\"Value\" COLLATE \"NOCASE\")")]
    [InlineData("current-date", "CURRENT_DATE")]
    [InlineData("current-time", "CURRENT_TIME")]
    [InlineData("current-timestamp", "CURRENT_TIMESTAMP")]
    [InlineData("provider-fragment", "CURRENT_USER")]
    [InlineData("opaque", "value + 1")]
    public void Render_ProducesDeterministicSqlForStructuredNode(string caseId, string expectedSql)
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = CreateStructuredExpression(caseId);

        // Act
        var sql = renderer.Render(expression);
        var unsupportedFeature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal(expectedSql, sql);
        Assert.Null(unsupportedFeature);
    }

    /// <summary>Each binary operator preserves its independently selectable exact SQL.</summary>
    /// <param name="operation">The declared binary operator.</param>
    /// <param name="expectedSql">The exact SQL for the two integer operands.</param>
    [Theory]
    [MemberData(nameof(BinaryOperatorCases))]
    public void Render_ProducesBinaryOperator(SafeMigrationSqlBinaryOperator operation, string expectedSql)
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Binary(
            SafeMigrationSql.Literal(1), operation, SafeMigrationSql.Literal(2));

        // Act
        var sql = renderer.Render(expression);

        // Assert
        Assert.Equal(expectedSql, sql);
    }

    /// <summary>New enum members cannot silently escape the independently selectable operator cases.</summary>
    [Fact]
    public void BinaryOperatorCases_CoverEveryDeclaredOperator()
    {
        // Arrange
        var declared = Enum.GetValues<SafeMigrationSqlBinaryOperator>();

        // Act
        var covered = BinaryOperatorCases().Select(row => (SafeMigrationSqlBinaryOperator)row[0]).ToArray();

        // Assert
        Assert.Equal(declared, covered);
    }

    /// <summary>Every unsupported boundary retains its exact provider classification.</summary>
    /// <param name="caseId">The stable unsupported-expression factory case.</param>
    /// <param name="expectedFeature">The original unsupported feature code.</param>
    [Theory]
    [InlineData("unknown-literal", "structured_literal_mapping")]
    [InlineData("nested-unknown-literal", "structured_literal_mapping")]
    [InlineData("unsafe-cast", "structured_cast_type")]
    [InlineData("qualified-collation", "schema_qualified_expression_collation")]
    [InlineData("precise-current-value", "current_value_precision")]
    [InlineData("foreign-fragment", "provider_fragment_mismatch")]
    public void GetUnsupportedFeature_ClassifiesProviderBoundary(string caseId, string expectedFeature)
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = CreateUnsupportedExpression(caseId);

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal(expectedFeature, feature);
    }

    /// <summary>Classification rejects a missing expression before accessing its nodes.</summary>
    [Fact]
    public void GetUnsupportedFeature_RejectsNullExpression()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);

        // Act
        var exception = Record.Exception(() => renderer.GetUnsupportedFeature(null!));

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>Unrenderable inputs fail independently without publishing SQL.</summary>
    /// <param name="caseId">The stable unsupported-expression factory case.</param>
    [Theory]
    [InlineData("unknown-literal")]
    [InlineData("unsafe-literal-type")]
    [InlineData("unsafe-cast-literal")]
    [InlineData("qualified-collation")]
    [InlineData("precise-current-value")]
    [InlineData("foreign-fragment")]
    public void Render_RejectsUnrenderableProviderBoundary(string caseId)
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = CreateUnsupportedExpression(caseId);

        // Act
        var exception = Record.Exception(() => renderer.Render(expression));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>Rendering rejects a missing expression independently of provider feature validation.</summary>
    [Fact]
    public void Render_RejectsNullExpression()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);

        // Act
        var exception = Record.Exception(() => renderer.Render(null!));

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>Bounded SQLite type names remain accepted without normalization.</summary>
    /// <param name="storeType">The supported provider store-type spelling.</param>
    [Theory]
    [InlineData("TEXT")]
    [InlineData("VARCHAR(32)")]
    [InlineData("DECIMAL(18, 4)")]
    [InlineData("DOUBLE PRECISION")]
    public void Render_AcceptsBoundedSQLiteStoreTypes(string storeType)
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        // Act
        var sql = renderer.Render(expression);
        var feature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal($"CAST(\"value\" AS {storeType})", sql);
        Assert.Null(feature);
    }

    /// <summary>Unsupported type syntax retains both its rejection and classification.</summary>
    /// <param name="storeType">The store-type spelling outside the supported grammar.</param>
    [Theory]
    [InlineData("t\u00EBxt")]
    [InlineData("INTEGER; DROP TABLE items")]
    [InlineData("VARCHAR(-1)")]
    [InlineData("VARCHAR(1, 2, 3)")]
    [InlineData("\"TEXT\"")]
    public void Render_RejectsStoreTypesOutsideTheBoundedGrammar(string storeType)
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        // Act
        var exception = Record.Exception(() => renderer.Render(expression));
        var feature = renderer.GetUnsupportedFeature(expression);

        // Assert
        var unsupported = Assert.IsType<NotSupportedException>(exception);
        Assert.Contains("not safe to render", unsupported.Message, StringComparison.Ordinal);
        Assert.Equal("structured_cast_type", feature);
    }

    /// <summary>Creates the renderer from the same EF services used by the provider.</summary>
    private static SqliteSafeMigrationSqlExpressionRenderer CreateRenderer(
        DbContext context
    ) => new(
        context.GetService<IRelationalTypeMappingSource>(),
        context.GetService<ISqlGenerationHelper>());
}
