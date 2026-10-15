namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies ordinary-expression stability and default-specific SQL-mode independence.</summary>
public sealed class MySqlSafeMigrationSqlExpressionRendererTests
{
    [Fact]
    public void Render_ProducesDeterministicSqlForEveryStructuredNode()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var value = SafeMigrationSql.Identifier("app", "Value");

        var expectations = new (SafeMigrationSqlExpression Expression, string Sql)[]
        {
            (value, "`app`.`Value`"), (SafeMigrationSql.Literal(null), "NULL"),
            (SafeMigrationSql.Literal(null, "int"), "CAST(NULL AS SIGNED)"),
            (SafeMigrationSql.Literal(42), "42"), (SafeMigrationSql.Literal(42, "int"), "CAST(42 AS SIGNED)"),
            (SafeMigrationSql.Literal(42, "signed"), "CAST(42 AS SIGNED)"),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value), "(NOT `app`.`Value`)"),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, value), "(-`app`.`Value`)"),
            (SafeMigrationSql.IsNull(value), "(`app`.`Value` IS NULL)"),
            (SafeMigrationSql.IsNotNull(value), "(`app`.`Value` IS NOT NULL)"),
            (SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10)),
                "(`app`.`Value` BETWEEN 1 AND 10)"),
            (SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10), negated: true),
                "(`app`.`Value` NOT BETWEEN 1 AND 10)"),
            (SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
                "(`app`.`Value` IN (1, 2))"),
            (SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1)], negated: true),
                "(`app`.`Value` NOT IN (1))"),
            (SafeMigrationSql.Function("LOWER", value), "lower(`app`.`Value`)"),
            (SafeMigrationSql.Cast(value, "char"), "CAST(`app`.`Value` AS CHAR)"),
            (SafeMigrationSql.Cast(value, "int"), "CAST(`app`.`Value` AS SIGNED)"),
            (SafeMigrationSql.Collate(value, "utf8mb4_bin"), "(`app`.`Value` COLLATE `utf8mb4_bin`)"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Date), "CURRENT_DATE"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 3), "CURRENT_TIME(3)"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp), "CURRENT_TIMESTAMP"),
            (SafeMigrationSql.ProviderFragment("doka_mysql", "CURRENT_USER()"), "CURRENT_USER()"),
            (SafeMigrationSql.Opaque("value + 1"), "value + 1"),
        };

        foreach (var expectation in expectations)
        {
            Assert.Equal(expectation.Sql, renderer.Render(expectation.Expression));
        }
    }

    [Theory]
    [InlineData("tinyint(1)", "SIGNED")]
    [InlineData("integer", "SIGNED")]
    [InlineData("bigint unsigned", "UNSIGNED")]
    [InlineData("int(11) zerofill", "UNSIGNED")]
    [InlineData("decimal(18, 4)", "DECIMAL(18,4)")]
    [InlineData("decimal(18)", "DECIMAL(18)")]
    [InlineData("numeric", "DECIMAL")]
    [InlineData("float", "FLOAT")]
    [InlineData("double precision", "DOUBLE")]
    [InlineData("char", "CHAR")]
    [InlineData("varchar(320)", "CHAR(320)")]
    [InlineData("longtext", "CHAR")]
    [InlineData("binary", "BINARY")]
    [InlineData("binary(16)", "BINARY(16)")]
    [InlineData("date", "DATE")]
    [InlineData("datetime", "DATETIME")]
    [InlineData("datetime(6)", "DATETIME(6)")]
    [InlineData("time", "TIME")]
    [InlineData("time(3)", "TIME(3)")]
    public void Render_NormalizesColumnStoreTypesToCommonCastTargets(
        string storeType,
        string expectedCastType
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        var sql = renderer.Render(expression);

        Assert.Equal($"CAST(`value` AS {expectedCastType})", sql);
        Assert.Null(renderer.GetUnsupportedFeature(expression));
    }

    [Theory]
    [InlineData("t\u00EBxt")]
    [InlineData("timestamp(6)")]
    [InlineData("json")]
    [InlineData("varchar(20) CHARACTER SET utf8mb4")]
    [InlineData("varchar(0)")]
    [InlineData("varbinary(16)")]
    [InlineData("binary(0)")]
    [InlineData("binary(999999999999999999999999999999)")]
    [InlineData("decimal(0)")]
    [InlineData("decimal(66, 0)")]
    [InlineData("decimal(10, 31)")]
    [InlineData("decimal(5, 6)")]
    [InlineData("datetime(7)")]
    [InlineData("time(7)")]
    [InlineData("int); DROP TABLE items; --")]
    public void Render_RejectsStoreTypesOutsideTheCommonCastGrammar(
        string storeType
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        var exception = Assert.Throws<NotSupportedException>(() => renderer.Render(expression));

        Assert.Contains("common MySQL and MariaDB CAST target", exception.Message, StringComparison.Ordinal);
        Assert.Equal("structured_cast_type", renderer.GetUnsupportedFeature(expression));
    }

    [Fact]
    public void Render_RejectsOversizedStoreTypeBeforePatternEvaluation()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var storeType = new string('a', 129);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        var exception = Assert.Throws<NotSupportedException>(() => renderer.Render(expression));

        Assert.Contains("common MySQL and MariaDB CAST target", exception.Message, StringComparison.Ordinal);
        Assert.Equal("structured_cast_type", renderer.GetUnsupportedFeature(expression));
    }

    [Fact]
    public void TryRender_RejectsInvalidDirectInput()
    {
        var whitespaceAccepted = MySqlSafeMigrationCastTypeRenderer.TryRender(" ", out var whitespaceResult);

        Assert.False(whitespaceAccepted);
        Assert.Empty(whitespaceResult);
        Assert.Throws<ArgumentNullException>(
            () => MySqlSafeMigrationCastTypeRenderer.TryRender(null!, out _));
    }

    [Fact]
    public void GetUnsupportedFeature_ClassifiesProviderSpecificExpressionFailures()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);

        var unknownLiteral = SafeMigrationSql.Literal(new object());
        var qualifiedCollation = SafeMigrationSql.Collate(
            SafeMigrationSql.Identifier("value"),
            "utf8mb4_bin",
            "schema");

        var matchingProviderFragment = SafeMigrationSql.ProviderFragment("doka_mysql", "CURRENT_USER()");
        var foreignProviderFragment = SafeMigrationSql.ProviderFragment("foreign_provider", "CURRENT_USER()");

        Assert.Equal("structured_literal_mapping", renderer.GetUnsupportedFeature(unknownLiteral));
        Assert.Equal(
            "schema_qualified_expression_collation",
            renderer.GetUnsupportedFeature(qualifiedCollation));
        Assert.Null(renderer.GetUnsupportedFeature(matchingProviderFragment));
        Assert.Equal("provider_fragment_mismatch", renderer.GetUnsupportedFeature(foreignProviderFragment));
    }

    [Fact]
    public void Render_ProducesEveryBinaryOperator()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var operators = new[]
        {
            SafeMigrationSqlBinaryOperator.And, SafeMigrationSqlBinaryOperator.Or,
            SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSqlBinaryOperator.NotEqual,
            SafeMigrationSqlBinaryOperator.LessThan, SafeMigrationSqlBinaryOperator.LessThanOrEqual,
            SafeMigrationSqlBinaryOperator.GreaterThan, SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
            SafeMigrationSqlBinaryOperator.Add, SafeMigrationSqlBinaryOperator.Subtract,
            SafeMigrationSqlBinaryOperator.Multiply, SafeMigrationSqlBinaryOperator.Divide,
            SafeMigrationSqlBinaryOperator.Modulo,
        };

        var rendered = operators
            .Select(value => renderer.Render(
                SafeMigrationSql.Binary(SafeMigrationSql.Literal(1), value, SafeMigrationSql.Literal(2))))
            .ToArray();

        Assert.Equal(
            [
                "(1 AND 2)",
                "(1 OR 2)",
                "(1 = 2)",
                "(1 <> 2)",
                "(1 < 2)",
                "(1 <= 2)",
                "(1 > 2)",
                "(1 >= 2)",
                "(1 + 2)",
                "(1 - 2)",
                "(1 * 2)",
                "(1 / 2)",
                "(1 % 2)"
            ],
            rendered);
    }

    [Fact]
    public void Render_RejectsForeignFragmentsSchemaQualifiedCollationsAndUnknownLiterals()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);

        Assert.Throws<ArgumentNullException>(() => renderer.Render(null!));
        Assert.Throws<NotSupportedException>(() => renderer.Render(SafeMigrationSql.Literal(new object())));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "int); DROP TABLE items; --")));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.ProviderFragment("other", "CURRENT_USER()")));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.Collate(SafeMigrationSql.Identifier("value"), "utf8mb4_bin", "schema")));
    }

    /// <summary>Default strings retain their exact bytes without session-dependent escape spelling.</summary>
    /// <param name="value">The authored text, including control or Unicode characters.</param>
    /// <param name="expected">The complete canonical SQL spelling.</param>
    [Theory]
    [InlineData("", "''")]
    [InlineData("plain", "convert(0x706c61696e using utf8mb4)")]
    [InlineData("O'Reilly", "convert(0x4f275265696c6c79 using utf8mb4)")]
    [InlineData("N\nnew", "convert(0x4e0a6e6577 using utf8mb4)")]
    [InlineData("\\n", "convert(0x5c6e using utf8mb4)")]
    [InlineData("\0\t\r\n\b\u001a", "convert(0x00090d0a081a using utf8mb4)")]
    [InlineData("\u00e4\u20ac\ud83d\ude42", "convert(0xc3a4e282acf09f9982 using utf8mb4)")]
    public void RenderStringDefaultPreservesExactUtf8Bytes(
        string value,
        string expected
    )
    {
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value);

        Assert.Equal(expected, sql);
    }

    /// <summary>Binary defaults retain all authored octets without UTF-8 decoding or escape interpretation.</summary>
    /// <param name="hex">The exact byte sequence in independently readable hexadecimal notation.</param>
    /// <param name="expected">The complete expected binary default expression.</param>
    [Theory]
    [InlineData("", "X''")]
    [InlineData("00", "convert(0x00 using binary)")]
    [InlineData("FF", "convert(0xff using binary)")]
    [InlineData("010A5C6EFF", "convert(0x010a5c6eff using binary)")]
    [InlineData("000A5C6EFF", "convert(0x000a5c6eff using binary)")]
    [InlineData("00090A0D1A275CFF", "convert(0x00090a0d1a275cff using binary)")]
    [InlineData("FFFE80C0", "convert(0xfffe80c0 using binary)")]
    public void RenderBinaryDefaultPreservesExactBytes(
        string hex,
        string expected
    )
    {
        var bytes = Convert.FromHexString(hex);

        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(bytes);

        Assert.Equal(expected, sql);
    }

    /// <summary>A default span is the valid empty byte sequence, not a missing string argument.</summary>
    [Fact]
    public void RenderBinaryDefaultAcceptsDefaultEmptySpan()
    {
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(default);

        Assert.Equal("X''", sql);
    }

    /// <summary>Binary conversion can consume stack-backed bytes without allocating a temporary array.</summary>
    [Fact]
    public void RenderBinaryDefaultAcceptsStackBackedBytes()
    {
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0, 255, 10, 92, };

        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(bytes);

        Assert.Equal("convert(0x00ff0a5c using binary)", sql);
    }

    /// <summary>Large binary defaults respect the visible span rather than its backing buffer.</summary>
    /// <param name="length">The exact visible byte count, including sizes beyond the text helper's stack limit.</param>
    [Theory]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(4097)]
    public void RenderBinaryDefaultPreservesLargeSpanBoundaries(
        int length
    )
    {
        const string prefix = "convert(0x";
        const string suffix = " using binary)";
        var backing = new byte[length + 4];
        Array.Fill(backing, (byte)0xee);
        for (var index = 0; index < length; index++)
        {
            backing[index + 2] = (byte)(index & byte.MaxValue);
        }

        var expected = backing.AsSpan(2, length).ToArray();

        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(backing.AsSpan(2, length));
        var decoded = Convert.FromHexString(sql.AsSpan(prefix.Length, sql.Length - prefix.Length - suffix.Length));

        Assert.StartsWith(prefix, sql, StringComparison.Ordinal);
        Assert.EndsWith(suffix, sql, StringComparison.Ordinal);
        Assert.Equal(prefix.Length + (length * 2) + suffix.Length, sql.Length);
        Assert.Equal(expected, decoded);
        Assert.Equal(sql.ToLowerInvariant(), sql);
        Assert.Equal((byte)0xee, backing[0]);
        Assert.Equal((byte)0xee, backing[^1]);
    }

    /// <summary>The completed SQL cannot retain or mutate the caller's byte storage.</summary>
    [Fact]
    public void RenderBinaryDefaultSnapshotsCallerOwnedBytes()
    {
        var bytes = new byte[] { 0, 10, 92, 110, 255, };

        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(bytes);
        Array.Fill(bytes, (byte)0x42);

        Assert.Equal("convert(0x000a5c6eff using binary)", sql);
    }

    /// <summary>The binary helper does not broaden recursive default rendering or rewrite binary AST leaves.</summary>
    [Fact]
    public void RenderDefaultDoesNotRecursivelyConvertBinaryLeaves()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var binary = SafeMigrationSql.Literal(new byte[] { 0, 10, 92, 255, });
        var expression = SafeMigrationSql.Function("COALESCE", binary, SafeMigrationSql.Literal(null));
        var expectedLiteral = renderer.Render(binary);

        var sql = renderer.RenderDefault(expression);

        Assert.Equal("coalesce(" + expectedLiteral + ",NULL)", sql);
        Assert.DoesNotContain(" using binary)", sql, StringComparison.Ordinal);
    }

    /// <summary>Character defaults use byte-stable text without changing ordinary rendering.</summary>
    /// <param name="value">The authored single character.</param>
    /// <param name="expected">The complete expected default expression.</param>
    [Theory]
    [InlineData('a', "convert(0x61 using utf8mb4)")]
    [InlineData('\n', "convert(0x0a using utf8mb4)")]
    [InlineData('\'', "convert(0x27 using utf8mb4)")]
    [InlineData('\\', "convert(0x5c using utf8mb4)")]
    [InlineData('\0', "convert(0x00 using utf8mb4)")]
    [InlineData('\u00e4', "convert(0xc3a4 using utf8mb4)")]
    public void RenderDefaultPreservesCharacterBytesAndOrdinaryMapping(
        char value,
        string expected
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal(value);
        var ordinaryExpected = context.GetService<IRelationalTypeMappingSource>()
            .FindMapping(typeof(char))!.GenerateSqlLiteral(value);

        var defaultSql = renderer.RenderDefault(expression);
        var ordinarySql = renderer.Render(expression);

        Assert.Equal(expected, defaultSql);
        Assert.Equal(ordinaryExpected, ordinarySql);
    }

    /// <summary>An isolated surrogate character cannot be silently replaced in a persisted default.</summary>
    /// <param name="codeUnit">The surrogate code unit constructed at runtime.</param>
    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdfff)]
    public void RenderDefaultRejectsUnpairedSurrogateCharacter(
        int codeUnit
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal((char)codeUnit);

        var exception = Record.Exception(() => renderer.RenderDefault(expression));

        Assert.IsType<EncoderFallbackException>(exception);
    }

    /// <summary>Default string encoding behaves identically on both sides of the bounded stack-buffer path.</summary>
    /// <param name="byteCount">The exact ASCII UTF-8 byte count to encode.</param>
    [Theory]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(4097)]
    public void RenderStringDefaultPreservesStackAndPoolBoundaries(
        int byteCount
    )
    {
        var value = new string('a', byteCount - 1) + "\n";
        var expected = "convert(0x" + string.Concat(Enumerable.Repeat("61", byteCount - 1)) + "0a using utf8mb4)";

        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value);

        Assert.Equal(expected, sql);
    }

    /// <summary>The stack threshold counts UTF-8 bytes rather than UTF-16 characters.</summary>
    /// <param name="euroCount">The number of three-byte Unicode scalars.</param>
    /// <param name="asciiCount">The number of one-byte suffix characters.</param>
    /// <param name="expectedBytes">The independently calculated byte count.</param>
    [Theory]
    [InlineData(170, 1, 511)]
    [InlineData(170, 2, 512)]
    [InlineData(171, 0, 513)]
    [InlineData(1024, 1, 3073)]
    public void RenderStringDefaultUsesUtf8ByteBoundary(
        int euroCount,
        int asciiCount,
        int expectedBytes
    )
    {
        const string prefix = "convert(0x";
        const string suffix = " using utf8mb4)";
        var value = new string('\u20ac', euroCount) + new string('x', asciiCount);

        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value);
        var decoded = Encoding.UTF8.GetString(Convert.FromHexString(
            sql.AsSpan(prefix.Length, sql.Length - prefix.Length - suffix.Length)));

        Assert.StartsWith(prefix, sql, StringComparison.Ordinal);
        Assert.EndsWith(suffix, sql, StringComparison.Ordinal);
        Assert.Equal(prefix.Length + (2 * expectedBytes) + suffix.Length, sql.Length);
        Assert.Equal(value, decoded);
        Assert.Equal(sql.ToLowerInvariant(), sql);
    }

    /// <summary>A later pooled encoding cannot mutate a previously returned default expression.</summary>
    [Fact]
    public void RenderStringDefaultSnapshotsPooledBytes()
    {
        var firstValue = new string('a', 4096);
        var secondValue = new string('b', 4096);
        var firstExpected = "convert(0x" + string.Concat(Enumerable.Repeat("61", 4096)) + " using utf8mb4)";
        var secondExpected = "convert(0x" + string.Concat(Enumerable.Repeat("62", 4096)) + " using utf8mb4)";

        var first = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(firstValue);
        var second = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(secondValue);

        Assert.Equal(firstExpected, first);
        Assert.Equal(secondExpected, second);
    }

    /// <summary>Malformed UTF-16 fails closed instead of silently replacing a default character.</summary>
    /// <param name="codeUnit">The surrogate code unit to include in the runtime string.</param>
    /// <param name="prependText">Whether ordinary text precedes the surrogate.</param>
    [Theory]
    [InlineData(0xd800, false)]
    [InlineData(0xdfff, false)]
    [InlineData(0xd800, true)]
    [InlineData(0xdfff, true)]
    public void RenderStringDefaultRejectsUnpairedSurrogates(
        int codeUnit,
        bool prependText
    )
    {
        var value = (prependText ? "a" : "") + (char)codeUnit;

        var exception = Record.Exception(() => MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value));

        Assert.IsType<EncoderFallbackException>(exception);
    }

    /// <summary>Every structured node carries the default context into its nested string leaves.</summary>
    /// <param name="expression">The structured tree, including nested operators and functions.</param>
    /// <param name="expected">The complete expected default SQL.</param>
    [Theory]
    [MemberData(nameof(DefaultStructuredExpressions))]
    public void RenderDefaultRecursesThroughStructuredNodes(
        SafeMigrationSqlExpression expression,
        string expected
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);

        var sql = renderer.RenderDefault(expression);

        Assert.Equal(expected, sql);
    }

    /// <summary>Supplies exact default spellings for each recursively rendered expression shape.</summary>
    /// <returns>One authored expression and its independently spelled expected SQL per case.</returns>
    public static IEnumerable<object[]> DefaultStructuredExpressions()
    {
        const string a = "convert(0x61 using utf8mb4)";
        const string b = "convert(0x62 using utf8mb4)";
        const string c = "convert(0x63 using utf8mb4)";

        yield return [SafeMigrationSql.Identifier("app", "Value"), "`app`.`Value`"];
        yield return [SafeMigrationSql.Literal(null), "NULL"];
        yield return [SafeMigrationSql.Literal(null, "int"), "CAST(NULL AS SIGNED)"];
        yield return [SafeMigrationSql.Literal(""), "''"];
        yield return [SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, SafeMigrationSql.Literal("a")),
            "(NOT " + a + ")"];
        yield return [SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, SafeMigrationSql.Literal("a")),
            "(-" + a + ")"];
        yield return [SafeMigrationSql.Binary(SafeMigrationSql.Literal("a"), SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal("b")),
            "(" + a + " = " + b + ")"];
        yield return [SafeMigrationSql.IsNull(SafeMigrationSql.Literal("a")), "(" + a + " IS NULL)"];
        yield return [SafeMigrationSql.IsNotNull(SafeMigrationSql.Literal("a")), "(" + a + " IS NOT NULL)"];
        yield return [SafeMigrationSql.Between(SafeMigrationSql.Literal("a"), SafeMigrationSql.Literal("b"),
                SafeMigrationSql.Literal("c")),
            "(" + a + " BETWEEN " + b + " AND " + c + ")"];
        yield return [SafeMigrationSql.Between(SafeMigrationSql.Literal("a"), SafeMigrationSql.Literal("b"),
                SafeMigrationSql.Literal("c"), negated: true),
            "(" + a + " NOT BETWEEN " + b + " AND " + c + ")"];
        yield return [SafeMigrationSql.In(SafeMigrationSql.Literal("a"),
                [SafeMigrationSql.Literal("b"), SafeMigrationSql.Literal("c")]),
            "(" + a + " IN (" + b + "," + c + "))"];
        yield return [SafeMigrationSql.In(SafeMigrationSql.Literal("a"),
                [SafeMigrationSql.Literal("b"), SafeMigrationSql.Literal("c")], negated: true),
            "(" + a + " NOT IN (" + b + "," + c + "))"];
        yield return [SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Literal("a"),
                SafeMigrationSql.Function("CONCAT", SafeMigrationSql.Literal("b"), SafeMigrationSql.Literal("c"))),
            "coalesce(" + a + ",concat(" + b + "," + c + "))"];
        yield return [SafeMigrationSql.Cast(SafeMigrationSql.Literal("a"), "varchar(20)"),
            "CAST(" + a + " AS CHAR(20))"];
        yield return [SafeMigrationSql.Collate(SafeMigrationSql.Literal("a"), "utf8mb4_bin"),
            "(" + a + " COLLATE `utf8mb4_bin`)"];
        yield return [SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Date), "CURRENT_DATE"];
        yield return [SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 3), "CURRENT_TIME(3)"];
        yield return [SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp), "CURRENT_TIMESTAMP"];
    }

    /// <summary>Typed string defaults retain the original outer CAST grammar.</summary>
    /// <param name="storeType">The declared source storage type.</param>
    /// <param name="castType">The existing normalized CAST target.</param>
    [Theory]
    [InlineData("varchar(20)", "CHAR(20)")]
    [InlineData("longtext", "CHAR")]
    [InlineData("char", "CHAR")]
    public void RenderDefaultRetainsTypedLiteralCast(
        string storeType,
        string castType
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal("a\n", storeType);

        var sql = renderer.RenderDefault(expression);

        Assert.Equal("CAST(convert(0x610a using utf8mb4) AS " + castType + ")", sql);
    }

    /// <summary>Non-string mappings remain byte-identical between ordinary and default rendering.</summary>
    /// <param name="value">The supported non-string literal.</param>
    [Theory]
    [MemberData(nameof(NonStringDefaultValues))]
    public void RenderDefaultPreservesNonStringLiteralMapping(
        object? value
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal(value);

        var ordinary = renderer.Render(expression);
        var defaultSql = renderer.RenderDefault(expression);

        Assert.Equal(ordinary, defaultSql);
    }

    /// <summary>Supplies independently mapped scalar families unaffected by default string encoding.</summary>
    /// <returns>One supported non-string literal per mapping family.</returns>
    public static IEnumerable<object?[]> NonStringDefaultValues()
    {
        yield return [null];
        yield return [false];
        yield return [42];
        yield return [42L];
        yield return [1.25m];
        yield return [1.25d];
        yield return [Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")];
        yield return [new byte[] { 0, 10, 39, 255, }];
        yield return [new DateTime(2026, 10, 15, 12, 34, 56, DateTimeKind.Utc)];
        yield return [new DateOnly(2026, 10, 15)];
        yield return [new TimeOnly(12, 34, 56)];
        yield return [new TimeSpan(1, 2, 3)];
    }

    /// <summary>The normal expression path continues to use the active provider's exact literal spelling.</summary>
    /// <param name="value">The text for a computed, check, or index expression.</param>
    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("O'Reilly")]
    [InlineData("N\nnew")]
    [InlineData("\\n")]
    [InlineData("\0\t\r\n\b\u001a")]
    [InlineData("\u00e4\u20ac\ud83d\ude42")]
    public void RenderRetainsOrdinaryNativeStringSpelling(
        string value
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal(value);
        var expected = context.GetService<IRelationalTypeMappingSource>()
            .FindMapping(typeof(string))!.GenerateSqlLiteral(value);

        var sql = renderer.Render(expression);

        Assert.Equal(expected, sql);
    }

    /// <summary>Opaque SQL and matching provider fragments are never parsed or rewritten as default literals.</summary>
    /// <param name="providerFragment">Whether the authored SQL is provider-owned rather than opaque.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderDefaultPreservesOpaqueAndProviderFragments(
        bool providerFragment
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        const string sql = "COALESCE('N\\n', _utf8mb4 X'0A')";
        var expression = providerFragment
            ? (SafeMigrationSqlExpression)SafeMigrationSql.ProviderFragment("doka_mysql", sql)
            : SafeMigrationSql.Opaque(sql);

        var result = renderer.RenderDefault(expression);

        Assert.Equal(sql, result);
    }

    /// <summary>Nested malformed text is rejected by the same strict UTF-8 contract as direct defaults.</summary>
    /// <param name="codeUnit">The surrogate code unit to include in the nested string.</param>
    /// <param name="prependText">Whether ordinary text precedes the surrogate.</param>
    [Theory]
    [InlineData(0xd800, false)]
    [InlineData(0xdfff, false)]
    [InlineData(0xd800, true)]
    [InlineData(0xdfff, true)]
    public void RenderDefaultRejectsNestedUnpairedSurrogates(
        int codeUnit,
        bool prependText
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var value = (prependText ? "a" : "") + (char)codeUnit;
        var expression = SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Literal(null),
            SafeMigrationSql.Literal(value));

        var exception = Record.Exception(() => renderer.RenderDefault(expression));

        Assert.IsType<EncoderFallbackException>(exception);
    }

    /// <summary>Default context does not expand the permitted typed-literal CAST grammar.</summary>
    /// <param name="storeType">An unsupported or injected CAST target.</param>
    [Theory]
    [InlineData("json")]
    [InlineData("timestamp(6)")]
    [InlineData("varchar(20) CHARACTER SET utf8mb4")]
    [InlineData("int); DROP TABLE items; --")]
    public void RenderDefaultRejectsInvalidTypedLiteralCast(
        string storeType
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal("valid", storeType);

        var exception = Record.Exception(() => renderer.RenderDefault(expression));

        Assert.IsType<NotSupportedException>(exception);
        Assert.Equal("structured_cast_type", renderer.GetUnsupportedFeature(expression));
    }

    /// <summary>Type validation precedes encoding, without hiding an invalid CAST behind a text failure.</summary>
    [Fact]
    public void RenderDefaultValidatesCastBeforeEncodingText()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal("\ud800", "json");

        var exception = Record.Exception(() => renderer.RenderDefault(expression));

        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>Default rendering retains all existing unsupported-node rejection boundaries.</summary>
    /// <param name="expression">The unsupported node or null expression.</param>
    /// <param name="exceptionType">The original public failure type.</param>
    [Theory]
    [MemberData(nameof(UnsupportedDefaultExpressions))]
    public void RenderDefaultRetainsUnsupportedBoundaries(
        SafeMigrationSqlExpression? expression,
        Type exceptionType
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);

        var exception = Record.Exception(() => renderer.RenderDefault(expression!));

        Assert.IsType(exceptionType, exception);
    }

    /// <summary>Supplies unsupported expression shapes without introducing additional SQL interpretation.</summary>
    /// <returns>One rejected input and its expected exception type per case.</returns>
    public static IEnumerable<object?[]> UnsupportedDefaultExpressions()
    {
        yield return [null, typeof(ArgumentNullException)];
        yield return [SafeMigrationSql.Literal(new object()), typeof(NotSupportedException)];
        yield return [SafeMigrationSql.ProviderFragment("other", "CURRENT_USER()"), typeof(NotSupportedException)];
        yield return [SafeMigrationSql.Collate(SafeMigrationSql.Literal("a"), "utf8mb4_bin", "schema"),
            typeof(NotSupportedException)];
    }

    /// <summary>A missing default string remains an argument error, not an encoded empty value.</summary>
    [Fact]
    public void RenderStringDefaultRejectsNull()
    {
        var exception = Record.Exception(() => MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(null!));

        Assert.IsType<ArgumentNullException>(exception);
    }

    private static SafeMigrationDbContext CreateContext() => new(
        "Server=localhost;Database=renderer;User ID=test;Password=test;AllowUserVariables=true",
        MySqlServerVersion.MySql(new Version(8, 4, 11)));

    private static MySqlSafeMigrationSqlExpressionRenderer CreateRenderer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
