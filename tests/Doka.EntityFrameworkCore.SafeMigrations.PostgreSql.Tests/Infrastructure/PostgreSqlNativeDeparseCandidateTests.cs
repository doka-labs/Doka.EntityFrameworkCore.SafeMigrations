namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Constrains structured native deparse variants without rewriting executable SQL or opaque text.</summary>
public sealed class PostgreSqlNativeDeparseCandidateTests
{
    /// <summary>Only validated builtin integer constant inputs use the native folded representation.</summary>
    /// <param name="input">The explicit literal input text.</param>
    /// <param name="target">The approved native integer target.</param>
    /// <param name="expected">The exact catalog candidate SQL literal.</param>
    [Theory]
    [InlineData("1", "integer", "'1'")]
    [InlineData(" +001 ", "int4", "'1'")]
    [InlineData("-2147483648", "integer", "'-2147483648'")]
    [InlineData("32767", "smallint", "'''32767''::smallint'")]
    [InlineData("9223372036854775807", "bigint", "'''9223372036854775807''::bigint'")]
    public void ValidIntegerLiteralCastsRetainNativeValueAndType(string input, string target, string expected)
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Literal(input), target);

        // Act
        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal);
        var executable = renderer.Render(expression);

        // Assert
        Assert.Equal(expected, candidate);
        Assert.StartsWith("CAST(", executable, StringComparison.Ordinal);
    }

    /// <summary>Invalid, out-of-range or lossy literal conversions never receive a folded native certificate.</summary>
    /// <param name="input">The unsupported constant input text.</param>
    /// <param name="target">The explicitly authored CAST target.</param>
    [Theory]
    [InlineData("2147483648", "integer")]
    [InlineData("32768", "smallint")]
    [InlineData("9223372036854775808", "bigint")]
    [InlineData("1.5", "integer")]
    [InlineData("bad", "integer")]
    [InlineData("1\0", "integer")]
    public void InvalidIntegerConstantDoesNotAcquireFoldedCandidate(string input, string target)
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Literal(input), target);

        // Act
        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal);

        // Assert
        Assert.Contains("::" + target, candidate, StringComparison.Ordinal);
        Assert.Contains("::text", candidate, StringComparison.Ordinal);
    }

    /// <summary>Opaque SQL and unneeded integer expressions do not introduce another catalog rendering pass.</summary>
    [Fact]
    public void OrdinaryAndOpaqueExpressionsDoNotIntroduceNativeVariant()
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var ordinary = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("id"),
            SafeMigrationSqlBinaryOperator.GreaterThan, SafeMigrationSql.Literal(0));

        var opaque = SafeMigrationSql.Opaque("COALESCE(id, CAST('1' AS integer)) > 0");

        // Act
        var ordinaryCandidate = renderer.RenderCatalogNativeDeparsedCandidateSql(ordinary, Literal);
        var opaqueCandidate = renderer.RenderCatalogNativeDeparsedCandidateSql(opaque, Literal);

        // Assert
        Assert.Null(ordinaryCandidate);
        Assert.Null(opaqueCandidate);
    }

    /// <summary>Executable SQL retains authored literals and ordinary function spelling.</summary>
    [Fact]
    public void NativeSpecialFormVariantDoesNotChangeExecution()
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var expression = SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Identifier("id"),
            SafeMigrationSql.Literal("fallback"));

        // Act
        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal);
        var executable = renderer.Render(expression);

        // Assert
        Assert.Contains("COALESCE(", candidate, StringComparison.Ordinal);
        Assert.Contains("''fallback''::text", candidate, StringComparison.Ordinal);
        Assert.Equal("coalesce(id, 'fallback')", executable);
    }

    /// <summary>Unknown integer inputs use the actual native owning column rather than assumed CLR storage.</summary>
    [Fact]
    public void UnknownIntegerLiteralCandidateIsScopedToNativeRelationAndExactMember()
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("member'name"),
            SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal(" +001 "));

        // Act
        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal, "co.conrelid");
        var executable = renderer.Render(expression);

        // Assert
        Assert.Contains("a.attrelid OPERATOR(pg_catalog.=) co.conrelid", candidate, StringComparison.Ordinal);
        Assert.Contains("a.attname OPERATOR(pg_catalog.=) 'member''name'", candidate, StringComparison.Ordinal);
        Assert.Contains("WHEN 23 THEN '1'", candidate, StringComparison.Ordinal);
        Assert.Contains("WHEN 21 THEN '''1''::smallint'", candidate, StringComparison.Ordinal);
        Assert.Contains("WHEN 20 THEN '''1''::bigint'", candidate, StringComparison.Ordinal);
        Assert.Contains("WHEN 25 THEN ''' +001 ''::text'", candidate, StringComparison.Ordinal);
        Assert.Contains("ELSE NULL END", candidate, StringComparison.Ordinal);
        Assert.Contains("' +001 '", executable, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_attribute", executable, StringComparison.Ordinal);
    }

    /// <summary>A candidate without native column context cannot guess an unknown input's effective type.</summary>
    /// <param name="literal">The exact unknown string input, including unsupported conversion text.</param>
    [Theory]
    [InlineData("1")]
    [InlineData("not-an-int")]
    [InlineData("1\0")]
    public void UnknownLiteralWithoutNativeRelationDoesNotFoldIntegerInput(string literal)
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("id"),
            SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal(literal));

        // Act
        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal);

        // Assert
        Assert.DoesNotContain("pg_attribute", candidate, StringComparison.Ordinal);
        Assert.Contains("::text", candidate, StringComparison.Ordinal);
    }

    /// <summary>
    /// The native VARCHAR cast candidate requires an exact owner and excludes binding-changing overloads.
    /// </summary>
    [Fact]
    public void LowerVarcharCandidateRequiresVisibleBuiltinAndNoCompetingExactOverload()
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Function("LOWER",
            SafeMigrationSql.Identifier("id")), SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
            SafeMigrationSql.Literal(string.Empty));

        // Act
        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal, "co.conrelid");
        var executable = renderer.Render(expression);

        // Assert
        Assert.Contains("a.attrelid = co.conrelid", candidate, StringComparison.Ordinal);
        Assert.Contains("a.atttypid = 1043", candidate, StringComparison.Ordinal);
        Assert.Contains("n.nspname = 'pg_catalog'", candidate, StringComparison.Ordinal);
        Assert.Contains("p.proargtypes[0] = 25", candidate, StringComparison.Ordinal);
        Assert.Contains("p.proargtypes[0] IN (1043, 2283, 5077)", candidate, StringComparison.Ordinal);
        Assert.Contains("p.pronargs - p.pronargdefaults <= 1", candidate, StringComparison.Ordinal);
        Assert.Contains("pg_function_is_visible", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_proc", executable, StringComparison.Ordinal);
    }

    /// <summary>Native Boolean deparse flattens only identical operators without rewriting executable SQL.</summary>
    /// <param name="operation">The structured Boolean operator to flatten.</param>
    /// <param name="rightNested">Whether the authored tree's right operand shares the same operator.</param>
    [Theory]
    [InlineData(SafeMigrationSqlBinaryOperator.And, false)]
    [InlineData(SafeMigrationSqlBinaryOperator.And, true)]
    [InlineData(SafeMigrationSqlBinaryOperator.Or, false)]
    [InlineData(SafeMigrationSqlBinaryOperator.Or, true)]
    public void NativeBooleanVariantFlattensOnlyIdenticalStructuredOperators(
        SafeMigrationSqlBinaryOperator operation,
        bool rightNested)
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var first = SafeMigrationSql.Identifier("first");
        var second = SafeMigrationSql.Identifier("second");
        var third = SafeMigrationSql.Identifier("third");
        var expression = rightNested
            ? SafeMigrationSql.Binary(first, operation, SafeMigrationSql.Binary(second, operation, third))
            : SafeMigrationSql.Binary(SafeMigrationSql.Binary(first, operation, second), operation, third);

        var token = operation == SafeMigrationSqlBinaryOperator.And ? "AND" : "OR";

        // Act
        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal);
        var executable = renderer.Render(expression);

        // Assert
        Assert.Equal("('(' || pg_catalog.quote_ident('first') || ' " + token + " ' || "
            + "pg_catalog.quote_ident('second') || ' " + token + " ' || "
            + "pg_catalog.quote_ident('third') || ')')", candidate);
        Assert.Equal(rightNested ? $"(first {token} (second {token} third))"
            : $"((first {token} second) {token} third)", executable);
    }

    /// <summary>Different Boolean operators retain their nested precedence in the native catalog candidate.</summary>
    [Fact]
    public void NativeBooleanVariantRetainsUnlikeNestedOperators()
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var first = SafeMigrationSql.Identifier("first");
        var second = SafeMigrationSql.Identifier("second");
        var third = SafeMigrationSql.Identifier("third");
        var fourth = SafeMigrationSql.Identifier("fourth");
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Binary(first,
            SafeMigrationSqlBinaryOperator.And, second), SafeMigrationSqlBinaryOperator.And,
            SafeMigrationSql.Binary(third, SafeMigrationSqlBinaryOperator.Or, fourth));

        // Act
        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal);
        var executable = renderer.Render(expression);

        // Assert
        Assert.Equal("('(' || pg_catalog.quote_ident('first') || ' AND ' || "
            + "pg_catalog.quote_ident('second') || ' AND (' || pg_catalog.quote_ident('third') || ' OR ' || "
            + "pg_catalog.quote_ident('fourth') || '))')", candidate);
        Assert.Equal("((first AND second) AND (third OR fourth))", executable);
    }

    /// <summary>Literal folding proves both source and target ranges and cannot erase a truncating typmod.</summary>
    /// <param name="input">The exact source input text.</param>
    /// <param name="source">The explicitly typed input storage.</param>
    /// <param name="target">The explicitly requested native CAST target.</param>
    /// <param name="folded">Whether both input and target conversions preserve the same native value.</param>
    [Theory]
    [InlineData("1", "text", "integer", true)]
    [InlineData("1", "integer", "bigint", true)]
    [InlineData("2147483648", "integer", "bigint", false)]
    [InlineData("32768", "smallint", "bigint", false)]
    [InlineData("12", "varchar(1)", "integer", false)]
    public void TypedLiteralFoldingRetainsSourceConversionBoundary(
        string input,
        string source,
        string target,
        bool folded)
    {
        // Arrange
        using var context = Context();
        var renderer = Renderer(context);
        var expression = new SafeMigrationSqlCastExpression(SafeMigrationSql.Literal(input, source), target);

        // Act
        var proven = PostgreSqlSafeMigrationSqlExpressionRenderer.TryGetNativeIntegerLiteralValue(
            expression, target, out var integer);

        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal);
        var executable = renderer.Render(expression);

        // Assert
        Assert.Equal(folded, proven);
        if (folded)
        {
            Assert.Equal(1, integer);
        }
        else
        {
            Assert.Contains("::", candidate, StringComparison.Ordinal);
        }

        Assert.StartsWith("CAST(", executable, StringComparison.Ordinal);
    }

    /// <summary>Creates the same provider service graph without opening an application connection.</summary>
    private static SafeMigrationDbContext Context()
        => new("Host=localhost;Database=unused;Username=unused;Password=unused");

    /// <summary>Uses the provider's actual canonical mappings and identifier formatter.</summary>
    private static PostgreSqlSafeMigrationSqlExpressionRenderer Renderer(DbContext context)
        => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Encodes candidate text independently from the expression's typed literals.</summary>
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
