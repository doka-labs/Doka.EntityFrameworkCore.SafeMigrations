namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Prevents default capture from certifying a different native token role.</summary>
public sealed class SafeMigrationCapturedDefaultSourceTests
{
    /// <summary>Native roles without a portable representation must retain their opaque authored source.</summary>
    /// <param name="sql">The native default with an unrepresentable function, type or operator role.</param>
    [Theory]
    [InlineData("\"ABS\"(1)")]
    [InlineData("[ABS](1)")]
    [InlineData("`ABS`(1)")]
    [InlineData("CAST(1 AS \"integer\")")]
    [InlineData("CAST(1 AS [integer])")]
    [InlineData("CAST(1 AS `integer`)")]
    [InlineData("CONVERT([int], 1)")]
    [InlineData("+1")]
    [InlineData("ABS(+1)")]
    public void UnsupportedNativeDefaultRolesRemainOpaque(
        string sql
    )
    {
        // Arrange
        var source = Column(sql);

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(source);

        // Assert
        Assert.Equal(SafeMigrationDefaultValueKind.Sql, definition.DefaultValue.Kind);
        Assert.Null(definition.DefaultValue.StructuredExpression);
        Assert.Same(sql, definition.DefaultValue.SqlExpression);
    }

    /// <summary>Captured numeric defaults retain the native lexeme consumed by every provider renderer.</summary>
    /// <param name="sql">The native numeric token whose type or precision can depend on its spelling.</param>
    /// <param name="expectedType">The parsed CLR domain retained beside the native token.</param>
    [Theory]
    [InlineData("2147483648", typeof(long))]
    [InlineData("9223372036854775807", typeof(long))]
    [InlineData("1e0", typeof(double))]
    [InlineData("1.00", typeof(decimal))]
    public void NumericDefaultsRetainTheirSourceToken(
        string sql,
        Type expectedType
    )
    {
        // Arrange
        var source = Column(sql);

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(source);
        var operation = SafeMigrationStandardOperationFactory.Create(new EnsureColumnIntent("items", definition),
            static expression => ((SafeMigrationSqlLiteralExpression)expression).NumericSourceToken.ToString());

        // Assert
        var literal = Assert.IsType<SafeMigrationSqlLiteralExpression>(definition.DefaultValue.StructuredExpression);
        Assert.Equal(sql, literal.NumericSourceToken.ToString());
        Assert.Equal(expectedType, literal.Value?.GetType());
        Assert.Null(definition.DefaultValue.SqlExpression);
        Assert.Equal(sql,
            Assert.IsType<AddColumnOperation>(operation)
                .DefaultValueSql);
    }

    /// <summary>Ordinary supported defaults remain structurally comparable rather than being rejected.</summary>
    /// <param name="sql">The representable native default.</param>
    [Theory]
    [InlineData("ABS(1)")]
    [InlineData("CAST(1 AS integer)")]
    [InlineData("CURRENT_TIMESTAMP(6)")]
    [InlineData("NULL")]
    [InlineData("'A\"`[Z'")]
    public void SupportedDefaultRolesRemainStructured(
        string sql
    )
    {
        // Arrange
        var source = Column(sql);

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(source);

        // Assert
        Assert.NotNull(definition.DefaultValue.StructuredExpression);
        Assert.Null(definition.DefaultValue.SqlExpression);
    }

    /// <summary>Unknown native defaults are reconstructed unchanged, never through an unquoted AST.</summary>
    [Fact]
    public void QuotedFunctionDefaultReconstructionDoesNotInvokeThePortableRenderer()
    {
        // Arrange
        const string sql = "\"ABS\"(1)";
        var definition = SafeMigrationExpectedDefinitionFactory.From(Column(sql));

        // Act
        var operation = SafeMigrationStandardOperationFactory.Create(new EnsureColumnIntent("items", definition),
            static _ => throw new InvalidOperationException("An opaque default must not be re-rendered."));

        // Assert
        Assert.Same(sql,
            Assert.IsType<AddColumnOperation>(operation)
                .DefaultValueSql);
    }

    /// <summary>Builds ordinary-EF metadata rather than an already certified default.</summary>
    private static AddColumnOperation Column(
        string sql
    ) => new()
    {
        Name = "value",
        Table = "items",
        ClrType = typeof(int),
        IsNullable = false,
        DefaultValueSql = sql,
    };
}
