namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies ordinary-expression stability and default-specific SQL-mode independence.</summary>
public sealed partial class MySqlSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Requires exact deterministic SQL for every supported structured expression node.</summary>
    /// <param name="kind">The independently discoverable structured node shape.</param>
    /// <param name="expected">The exact SQL value, including grouping and token quoting.</param>
    [Theory]
    [InlineData("identifier", "`app`.`Value`")]
    [InlineData("null", "NULL")]
    [InlineData("typed-null", "CAST(NULL AS SIGNED)")]
    [InlineData("integer", "42")]
    [InlineData("typed-integer", "CAST(42 AS SIGNED)")]
    [InlineData("signed-integer", "CAST(42 AS SIGNED)")]
    [InlineData("not", "(NOT `app`.`Value`)")]
    [InlineData("negate", "(-`app`.`Value`)")]
    [InlineData("is-null", "(`app`.`Value` IS NULL)")]
    [InlineData("is-not-null", "(`app`.`Value` IS NOT NULL)")]
    [InlineData("between", "(`app`.`Value` BETWEEN 1 AND 10)")]
    [InlineData("not-between", "(`app`.`Value` NOT BETWEEN 1 AND 10)")]
    [InlineData("in", "(`app`.`Value` IN (1, 2))")]
    [InlineData("not-in", "(`app`.`Value` NOT IN (1))")]
    [InlineData("function", "lower(`app`.`Value`)")]
    [InlineData("char-cast", "CAST(`app`.`Value` AS CHAR)")]
    [InlineData("integer-cast", "CAST(`app`.`Value` AS SIGNED)")]
    [InlineData("collate", "(`app`.`Value` COLLATE `utf8mb4_bin`)")]
    [InlineData("date", "CURRENT_DATE")]
    [InlineData("time", "CURRENT_TIME(3)")]
    [InlineData("timestamp", "CURRENT_TIMESTAMP")]
    [InlineData("provider", "CURRENT_USER()")]
    [InlineData("opaque", "value + 1")]
    public void Render_ProducesDeterministicSqlForEveryStructuredNode(
        string kind,
        string expected
    )
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var value = SafeMigrationSql.Identifier("app", "Value");
        var expression = kind switch
        {
            "identifier" => value,
            "null" => SafeMigrationSql.Literal(null),
            "typed-null" => SafeMigrationSql.Literal(null, "int"),
            "integer" => SafeMigrationSql.Literal(42),
            "typed-integer" => SafeMigrationSql.Literal(42, "int"),
            "signed-integer" => SafeMigrationSql.Literal(42, "signed"),
            "not" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value),
            "negate" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, value),
            "is-null" => SafeMigrationSql.IsNull(value),
            "is-not-null" => SafeMigrationSql.IsNotNull(value),
            "between" => SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10)),
            "not-between" => SafeMigrationSql.Between(
                value,
                SafeMigrationSql.Literal(1),
                SafeMigrationSql.Literal(10),
                negated: true),
            "in" => SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
            "not-in" => SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1)], negated: true),
            "function" => SafeMigrationSql.Function("LOWER", value),
            "char-cast" => SafeMigrationSql.Cast(value, "char"),
            "integer-cast" => SafeMigrationSql.Cast(value, "int"),
            "collate" => SafeMigrationSql.Collate(value, "utf8mb4_bin"),
            "date" => SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Date),
            "time" => SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 3),
            "timestamp" => SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp),
            "provider" => SafeMigrationSql.ProviderFragment("doka_mysql", "CURRENT_USER()"),
            "opaque" => SafeMigrationSql.Opaque("value + 1"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // Act
        var rendered = renderer.Render(expression);

        // Assert
        Assert.Equal(expected, rendered);
    }

    /// <summary>Maps supported column store types to the common native CAST grammar.</summary>
    /// <param name="storeType">The exact authored native physical store type.</param>
    /// <param name="expectedCastType">The canonical shared MySQL and MariaDB CAST target.</param>
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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        // Act
        var sql = renderer.Render(expression);

        var unsupportedFeature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal($"CAST(`value` AS {expectedCastType})", sql);
        Assert.Null(unsupportedFeature);
    }

    /// <summary>
    /// Rejects cast targets outside the shared MySQL and MariaDB grammar with the precise unsupported reason.
    /// </summary>
    /// <param name="storeType">The exact authored native physical store type.</param>
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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        // Act
        var capturedException = Record.Exception(() => renderer.Render(expression));

        var unsupportedFeature = renderer.GetUnsupportedFeature(expression);

        // Assert
        var exception = Assert.IsType<NotSupportedException>(capturedException);
        Assert.Contains("common MySQL and MariaDB CAST target", exception.Message, StringComparison.Ordinal);
        Assert.Equal("structured_cast_type", unsupportedFeature);
    }

    /// <summary>Rejects an oversized cast target before applying its parsing pattern.</summary>
    [Fact]
    public void Render_RejectsOversizedStoreTypeBeforePatternEvaluation()
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var storeType = new string('a', 129);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        // Act
        var capturedException = Record.Exception(() => renderer.Render(expression));

        var unsupportedFeature = renderer.GetUnsupportedFeature(expression);

        // Assert
        var exception = Assert.IsType<NotSupportedException>(capturedException);
        Assert.Contains("common MySQL and MariaDB CAST target", exception.Message, StringComparison.Ordinal);
        Assert.Equal("structured_cast_type", unsupportedFeature);
    }

    /// <summary>Rejects invalid direct cast input without returning a partial rendered target.</summary>
    [Fact]
    public void TryRender_RejectsWhitespaceWithoutPartialOutput()
    {
        // Arrange
        const string input = " ";

        // Act
        var whitespaceAccepted = MySqlSafeMigrationCastTypeRenderer.TryRender(input, out var whitespaceResult);

        // Assert
        Assert.False(whitespaceAccepted);
        Assert.Empty(whitespaceResult);
    }

    /// <summary>Preserves the null argument boundary independently of unsupported non-null cast input.</summary>
    [Fact]
    public void TryRender_RejectsNullDirectInput()
    {
        // Arrange
        string? input = null;

        // Act
        var failure = Record.Exception(() => MySqlSafeMigrationCastTypeRenderer.TryRender(input!, out _));

        // Assert
        Assert.IsType<ArgumentNullException>(failure);
    }

    /// <summary>Distinguishes unknown literal mappings from foreign fragments and qualified collations.</summary>
    /// <param name="kind">The independent supported or rejected expression family.</param>
    /// <param name="expected">The precise unsupported feature, or null for a matching provider fragment.</param>
    [Theory]
    [InlineData("unknown-literal", "structured_literal_mapping")]
    [InlineData("qualified-collation", "schema_qualified_expression_collation")]
    [InlineData("matching-provider", null)]
    [InlineData("foreign-provider", "provider_fragment_mismatch")]
    public void GetUnsupportedFeature_ClassifiesProviderSpecificExpressionFailures(
        string kind,
        string? expected
    )
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = kind switch
        {
            "unknown-literal" => SafeMigrationSql.Literal(new object()),
            "qualified-collation" => SafeMigrationSql.Collate(
                SafeMigrationSql.Identifier("value"),
                "utf8mb4_bin",
                "schema"),
            "matching-provider" => SafeMigrationSql.ProviderFragment("doka_mysql", "CURRENT_USER()"),
            "foreign-provider" => SafeMigrationSql.ProviderFragment("foreign_provider", "CURRENT_USER()"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // Act
        var unsupported = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal(expected, unsupported);
    }

    /// <summary>Preserves each binary operator's exact SQL token and grouping.</summary>
    /// <param name="operator">The independently rendered binary operator.</param>
    /// <param name="expected">The exact grouped SQL spelling.</param>
    [Theory]
    [InlineData(SafeMigrationSqlBinaryOperator.And, "(1 AND 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.Or, "(1 OR 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.Equal, "(1 = 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.NotEqual, "(1 <> 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.LessThan, "(1 < 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.LessThanOrEqual, "(1 <= 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.GreaterThan, "(1 > 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, "(1 >= 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.Add, "(1 + 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.Subtract, "(1 - 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.Multiply, "(1 * 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.Divide, "(1 / 2)")]
    [InlineData(SafeMigrationSqlBinaryOperator.Modulo, "(1 % 2)")]
    public void Render_ProducesEveryBinaryOperator(
        SafeMigrationSqlBinaryOperator @operator,
        string expected
    )
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Literal(1), @operator, SafeMigrationSql.Literal(2));

        // Act
        var rendered = renderer.Render(expression);

        // Assert
        Assert.Equal(expected, rendered);
    }

    /// <summary>Preserves the null argument contract independently of unsupported-expression classification.</summary>
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

    /// <summary>Rejects each unsupported expression family without allowing one failure to mask the others.</summary>
    /// <param name="expressionKind">The unsupported leaf, cast, foreign fragment, or qualified collation.</param>
    [Theory]
    [InlineData("unknown-literal")]
    [InlineData("injected-cast")]
    [InlineData("foreign-provider")]
    [InlineData("qualified-collation")]
    public void Render_RejectsUnsupportedExpression(
        string expressionKind
    )
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = expressionKind switch
        {
            "unknown-literal" => SafeMigrationSql.Literal(new object()),
            "injected-cast" => SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "int); DROP TABLE items; --"),
            "foreign-provider" => SafeMigrationSql.ProviderFragment("other", "CURRENT_USER()"),
            "qualified-collation" => SafeMigrationSql.Collate(
                SafeMigrationSql.Identifier("value"),
                "utf8mb4_bin",
                "schema"),
            _ => throw new ArgumentOutOfRangeException(nameof(expressionKind)),
        };

        // Act
        var exception = Record.Exception(() => renderer.Render(expression));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
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
        // Arrange: The theory supplies the text and its exact UTF-8 SQL representation.

        // Act
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value);

        // Assert
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
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(bytes);

        // Assert
        Assert.Equal(expected, sql);
    }

    /// <summary>A default span is the valid empty byte sequence, not a missing string argument.</summary>
    [Fact]
    public void RenderBinaryDefaultAcceptsDefaultEmptySpan()
    {
        // Arrange
        ReadOnlySpan<byte> bytes = default;

        // Act
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(bytes);

        // Assert
        Assert.Equal("X''", sql);
    }

    /// <summary>Binary conversion can consume stack-backed bytes without allocating a temporary array.</summary>
    [Fact]
    public void RenderBinaryDefaultAcceptsStackBackedBytes()
    {
        // Arrange
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0, 255, 10, 92 };

        // Act
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(bytes);

        // Assert
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
        // Arrange
        const string prefix = "convert(0x";
        const string suffix = " using binary)";
        var backing = new byte[length + 4];
        Array.Fill(backing, (byte)0xee);
        for (var index = 0; index < length; index++)
        {
            backing[index + 2] = (byte)(index & byte.MaxValue);
        }

        var expected = backing
            .AsSpan(2, length)

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        // Act
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(backing.AsSpan(2, length));
        var decoded = Convert.FromHexString(sql.AsSpan(prefix.Length, sql.Length - prefix.Length - suffix.Length));

        // Assert
        Assert.StartsWith(prefix, sql, StringComparison.Ordinal);
        Assert.EndsWith(suffix, sql, StringComparison.Ordinal);
        Assert.Equal(prefix.Length + length * 2 + suffix.Length, sql.Length);
        Assert.Equal(expected, decoded);
        Assert.Equal(sql.ToLowerInvariant(), sql);
        Assert.Equal((byte)0xee, backing[0]);
        Assert.Equal((byte)0xee, backing[^1]);
    }

    /// <summary>The completed SQL cannot retain or mutate the caller's byte storage.</summary>
    [Fact]
    public void RenderBinaryDefaultSnapshotsCallerOwnedBytes()
    {
        // Arrange
        var bytes = new byte[]
        {
            0,
            10,
            92,
            110,
            255,
        };

        // Act
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(bytes);
        Array.Fill(bytes, (byte)0x42);

        // Assert
        Assert.Equal("convert(0x000a5c6eff using binary)", sql);
    }

    /// <summary>The binary helper does not broaden recursive default rendering or rewrite binary AST leaves.</summary>
    [Fact]
    public void RenderDefaultDoesNotRecursivelyConvertBinaryLeaves()
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var binary = SafeMigrationSql.Literal(new byte[] { 0, 10, 92, 255 });
        var expression = SafeMigrationSql.Function("COALESCE", binary, SafeMigrationSql.Literal(null));

        // Act
        var expectedLiteral = renderer.Render(binary);

        var sql = renderer.RenderDefault(expression);

        // Assert
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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal(value);
        var ordinaryExpected = context
            .GetService<IRelationalTypeMappingSource>()
            .FindMapping(typeof(char))!.GenerateSqlLiteral(value);

        // Act
        var defaultSql = renderer.RenderDefault(expression);
        var ordinarySql = renderer.Render(expression);

        // Assert
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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal((char)codeUnit);

        // Act
        var exception = Record.Exception(() => renderer.RenderDefault(expression));

        // Assert
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
        // Arrange
        var value = new string('a', byteCount - 1) + "\n";
        var expected = "convert(0x" + string.Concat(Enumerable.Repeat("61", byteCount - 1)) + "0a using utf8mb4)";

        // Act
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value);

        // Assert
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
        // Arrange
        const string prefix = "convert(0x";
        const string suffix = " using utf8mb4)";
        var value = new string('\u20ac', euroCount) + new string('x', asciiCount);

        // Act
        var sql = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value);
        var decoded = Encoding.UTF8.GetString(
            Convert.FromHexString(
                sql.AsSpan(prefix.Length, sql.Length - prefix.Length - suffix.Length)));

        // Assert
        Assert.StartsWith(prefix, sql, StringComparison.Ordinal);
        Assert.EndsWith(suffix, sql, StringComparison.Ordinal);
        Assert.Equal(prefix.Length + 2 * expectedBytes + suffix.Length, sql.Length);
        Assert.Equal(value, decoded);
        Assert.Equal(sql.ToLowerInvariant(), sql);
    }

    /// <summary>A later pooled encoding cannot mutate a previously returned default expression.</summary>
    [Fact]
    public void RenderStringDefaultSnapshotsPooledBytes()
    {
        // Arrange
        var firstValue = new string('a', 4096);
        var secondValue = new string('b', 4096);
        var firstExpected = "convert(0x" + string.Concat(Enumerable.Repeat("61", 4096)) + " using utf8mb4)";
        var secondExpected = "convert(0x" + string.Concat(Enumerable.Repeat("62", 4096)) + " using utf8mb4)";

        // Act
        var first = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(firstValue);
        var second = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(secondValue);

        // Assert
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
        // Arrange
        var value = (prependText
                ? "a"
                : "")
            + (char)codeUnit;

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value));

        // Assert
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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);

        // Act
        var sql = renderer.RenderDefault(expression);

        // Assert
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
        yield return
        [
            SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, SafeMigrationSql.Literal("a")),
            "(NOT " + a + ")",
        ];

        yield return
        [
            SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, SafeMigrationSql.Literal("a")),
            "(-" + a + ")",
        ];

        yield return
        [
            SafeMigrationSql.Binary(
                SafeMigrationSql.Literal("a"),
                SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal("b")),
            "(" + a + " = " + b + ")",
        ];

        yield return [SafeMigrationSql.IsNull(SafeMigrationSql.Literal("a")), "(" + a + " IS NULL)"];
        yield return [SafeMigrationSql.IsNotNull(SafeMigrationSql.Literal("a")), "(" + a + " IS NOT NULL)"];
        yield return
        [
            SafeMigrationSql.Between(
                SafeMigrationSql.Literal("a"),
                SafeMigrationSql.Literal("b"),
                SafeMigrationSql.Literal("c")),
            "(" + a + " BETWEEN " + b + " AND " + c + ")",
        ];

        yield return
        [
            SafeMigrationSql.Between(
                SafeMigrationSql.Literal("a"),
                SafeMigrationSql.Literal("b"),
                SafeMigrationSql.Literal("c"),
                negated: true),
            "(" + a + " NOT BETWEEN " + b + " AND " + c + ")",
        ];

        yield return
        [
            SafeMigrationSql.In(
                SafeMigrationSql.Literal("a"),
                [SafeMigrationSql.Literal("b"), SafeMigrationSql.Literal("c")]),
            "(" + a + " IN (" + b + "," + c + "))",
        ];

        yield return
        [
            SafeMigrationSql.In(
                SafeMigrationSql.Literal("a"),
                [SafeMigrationSql.Literal("b"), SafeMigrationSql.Literal("c")],
                negated: true),
            "(" + a + " NOT IN (" + b + "," + c + "))",
        ];

        yield return
        [
            SafeMigrationSql.Function(
                "COALESCE",
                SafeMigrationSql.Literal("a"),
                SafeMigrationSql.Function("CONCAT", SafeMigrationSql.Literal("b"), SafeMigrationSql.Literal("c"))),
            "coalesce(" + a + ",concat(" + b + "," + c + "))",
        ];

        yield return
        [
            SafeMigrationSql.Cast(SafeMigrationSql.Literal("a"), "varchar(20)"),
            "CAST(" + a + " AS CHAR(20))",
        ];

        yield return
        [
            SafeMigrationSql.Collate(SafeMigrationSql.Literal("a"), "utf8mb4_bin"),
            "(" + a + " COLLATE `utf8mb4_bin`)",
        ];

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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal("a\n", storeType);

        // Act
        var sql = renderer.RenderDefault(expression);

        // Assert
        Assert.Equal("CAST(convert(0x610a using utf8mb4) AS " + castType + ")", sql);
    }

    /// <summary>Non-string mappings remain byte-identical between ordinary and default rendering.</summary>
    /// <param name="kind">The serializable identity of the supported non-string literal.</param>
    /// <param name="expectedType">The exact runtime type required by the original native mapping probe.</param>
    [Theory]
    [InlineData("null", null)]
    [InlineData("boolean", typeof(bool))]
    [InlineData("int32", typeof(int))]
    [InlineData("int64", typeof(long))]
    [InlineData("decimal", typeof(decimal))]
    [InlineData("double", typeof(double))]
    [InlineData("guid", typeof(Guid))]
    [InlineData("binary", typeof(byte[]))]
    [InlineData("datetime", typeof(DateTime))]
    [InlineData("date", typeof(DateOnly))]
    [InlineData("time", typeof(TimeOnly))]
    [InlineData("duration", typeof(TimeSpan))]
    public void RenderDefaultPreservesNonStringLiteralMapping(
        string kind,
        Type? expectedType
    )
    {
        // Arrange
        object? value = kind switch
        {
            "null" => null,
            "boolean" => false,
            "int32" => 42,
            "int64" => 42L,
            "decimal" => 1.25m,
            "double" => 1.25d,
            "guid" => Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            "binary" => new byte[] { 0, 10, 39, 255 },
            "datetime" => new DateTime(2026, 10, 15, 12, 34, 56, DateTimeKind.Utc),
            "date" => new DateOnly(2026, 10, 15),
            "time" => new TimeOnly(12, 34, 56),
            "duration" => new TimeSpan(1, 2, 3),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal(value);

        // Act
        var ordinary = renderer.Render(expression);
        var defaultSql = renderer.RenderDefault(expression);

        // Assert
        Assert.Equal(expectedType, value?.GetType());
        Assert.Equal(ordinary, defaultSql);
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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal(value);
        var expected = context
            .GetService<IRelationalTypeMappingSource>()
            .FindMapping(typeof(string))!.GenerateSqlLiteral(value);

        // Act
        var sql = renderer.Render(expression);

        // Assert
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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        const string sql = "COALESCE('N\\n', _utf8mb4 X'0A')";
        var expression = providerFragment
            ? SafeMigrationSql.ProviderFragment("doka_mysql", sql)
            : SafeMigrationSql.Opaque(sql);

        // Act
        var result = renderer.RenderDefault(expression);

        // Assert
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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var value = (prependText
                ? "a"
                : "")
            + (char)codeUnit;

        var expression = SafeMigrationSql.Function(
            "COALESCE",
            SafeMigrationSql.Literal(null),
            SafeMigrationSql.Literal(value));

        // Act
        var exception = Record.Exception(() => renderer.RenderDefault(expression));

        // Assert
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
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal("valid", storeType);

        // Act
        var exception = Record.Exception(() => renderer.RenderDefault(expression));

        var unsupportedFeature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Equal("structured_cast_type", unsupportedFeature);
    }

    /// <summary>Type validation precedes encoding, without hiding an invalid CAST behind a text failure.</summary>
    [Fact]
    public void RenderDefaultValidatesCastBeforeEncodingText()
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal("\ud800", "json");

        // Act
        var exception = Record.Exception(() => renderer.RenderDefault(expression));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>Default rendering retains all existing unsupported-node rejection boundaries.</summary>
    /// <param name="shape">The serializable identity of the unsupported node or null expression.</param>
    /// <param name="exceptionType">The original public failure type.</param>
    [Theory]
    [InlineData("null", typeof(ArgumentNullException))]
    [InlineData("opaque-literal", typeof(NotSupportedException))]
    [InlineData("foreign-provider", typeof(NotSupportedException))]
    [InlineData("qualified-collation", typeof(NotSupportedException))]
    public void RenderDefaultRetainsUnsupportedBoundaries(
        string shape,
        Type exceptionType
    )
    {
        // Arrange
        var expression = shape switch
        {
            "null" => null,
            "opaque-literal" => SafeMigrationSql.Literal(new object()),
            "foreign-provider" => SafeMigrationSql.ProviderFragment("other", "CURRENT_USER()"),
            "qualified-collation" => SafeMigrationSql.Collate(SafeMigrationSql.Literal("a"), "utf8mb4_bin", "schema"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        using var context = CreateContext();
        var renderer = CreateRenderer(context);

        // Act
        var exception = Record.Exception(() => renderer.RenderDefault(expression!));

        // Assert
        Assert.IsType(exceptionType, exception);
    }

    /// <summary>A missing default string remains an argument error, not an encoded empty value.</summary>
    [Fact]
    public void RenderStringDefaultRejectsNull()
    {
        // Arrange
        Action render = () => MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(null!);

        // Act
        var exception = Record.Exception(render);

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    private static SafeMigrationDbContext CreateContext() => new(
        "Server=localhost;Database=renderer;User ID=test;Password=test;AllowUserVariables=true",
        MySqlServerVersion.MySql(new Version(8, 4, 11)));

    private static MySqlSafeMigrationSqlExpressionRenderer CreateRenderer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
