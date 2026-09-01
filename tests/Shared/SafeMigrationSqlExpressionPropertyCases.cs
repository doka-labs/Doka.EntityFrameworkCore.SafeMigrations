namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>
/// Generates bounded provider-neutral expression cases shared by the MySQL,
/// MariaDB, and PostgreSQL renderer property suites.
/// </summary>
internal static class SafeMigrationSqlExpressionPropertyCases
{
    private const int MaximumGeneratedDepth = 4;

    private static readonly SafeMigrationSqlBinaryOperator[] s_binaryOperators =
        Enum.GetValues<SafeMigrationSqlBinaryOperator>();

    /// <summary>Creates a deterministic bounded expression from FsCheck primitive inputs.</summary>
    /// <param name="rawIdentifier">The generated identifier payload.</param>
    /// <param name="rawLiteral">The generated numeric literal.</param>
    /// <param name="shapeSelector">The seed used to select and combine expression nodes.</param>
    /// <param name="depthSelector">The seed used to select bounded tree depth.</param>
    /// <returns>A provider-neutral structurally comparable expression.</returns>
    public static SafeMigrationSqlExpression Create(
        string? rawIdentifier,
        int rawLiteral,
        int shapeSelector,
        byte depthSelector
    )
    {
        var identifier = Identifier(rawIdentifier);
        var literal = rawLiteral & int.MaxValue;
        var selector = unchecked((uint)shapeSelector);
        var depth = 1 + (depthSelector % MaximumGeneratedDepth);

        return CreateExpression(identifier, literal, selector, depth);
    }

    /// <summary>Verifies structural and textual stability across two render-parse cycles.</summary>
    /// <param name="expected">The generated expression to render.</param>
    /// <param name="render">The provider renderer under test.</param>
    /// <returns><see langword="true" /> when both cycles preserve the contract.</returns>
    public static bool PreservesStableRoundTrip(
        SafeMigrationSqlExpression expected,
        Func<SafeMigrationSqlExpression, string> render
    )
    {
        var firstSql = render(expected);

        if (!SafeMigrationSqlExpressionParser.TryParse(
                firstSql,
                out var firstParsed,
                out var firstFailureCode)
            || firstFailureCode.Length != 0
            || !SafeMigrationSqlExpressionContract.Equivalent(expected, firstParsed))
        {
            return false;
        }

        var secondSql = render(firstParsed);

        if (!SafeMigrationSqlExpressionParser.TryParse(
                secondSql,
                out var secondParsed,
                out var secondFailureCode))
        {
            return false;
        }

        return secondFailureCode.Length == 0
            && StringComparer.Ordinal.Equals(firstSql, secondSql)
            && SafeMigrationSqlExpressionContract.Equivalent(firstParsed, secondParsed);
    }

    /// <summary>Verifies that valid rendered SQL cannot absorb an appended statement.</summary>
    /// <param name="expression">The generated expression to render.</param>
    /// <param name="render">The provider renderer under test.</param>
    /// <returns><see langword="true" /> when the parser rejects the complete payload.</returns>
    public static bool RejectsAppendedStatement(
        SafeMigrationSqlExpression expression,
        Func<SafeMigrationSqlExpression, string> render
    )
    {
        var unsafeSql = render(expression) + "; DROP TABLE audit_log";
        var parsed = SafeMigrationSqlExpressionParser.TryParse(
            unsafeSql,
            out var result,
            out var failureCode);

        return !parsed
            && result is null
            && StringComparer.Ordinal.Equals("invalid_token", failureCode);
    }

    private static SafeMigrationSqlExpression CreateExpression(
        string identifier,
        int literal,
        uint selector,
        int depth
    )
    {
        if (depth == 0)
        {
            return CreateLeaf(identifier, literal, selector);
        }

        var first = Mix(selector, 0x9E3779B9u);
        var second = Mix(selector, 0x85EBCA6Bu);
        var third = Mix(selector, 0xC2B2AE35u);

        // Keep generated trees inside the lossless intersection of the parser
        // and both provider renderers. Opaque SQL, provider fragments, and
        // provider-typed literals require separate negative contracts.
        return (selector % 10) switch
        {
            0 => SafeMigrationSql.Binary(
                CreateExpression(identifier, literal, first, depth - 1),
                s_binaryOperators[second % (uint)s_binaryOperators.Length],
                CreateExpression(identifier, literal, third, depth - 1)),
            1 => SafeMigrationSql.Unary(
                (selector & 1) == 0
                    ? SafeMigrationSqlUnaryOperator.Not
                    : SafeMigrationSqlUnaryOperator.Negate,
                CreateExpression(identifier, literal, first, depth - 1)),
            2 => (selector & 1) == 0
                ? SafeMigrationSql.IsNull(CreateExpression(identifier, literal, first, depth - 1))
                : SafeMigrationSql.IsNotNull(CreateExpression(identifier, literal, first, depth - 1)),
            3 => SafeMigrationSql.Between(
                CreateExpression(identifier, literal, first, depth - 1),
                CreateExpression(identifier, literal, second, depth - 1),
                CreateExpression(identifier, literal, third, depth - 1),
                negated: (selector & 1) != 0),
            4 => SafeMigrationSql.In(
                CreateExpression(identifier, literal, first, depth - 1),
                [
                    CreateExpression(identifier, literal, second, depth - 1),
                    CreateExpression(identifier, literal, third, depth - 1),
                ],
                negated: (selector & 1) != 0),
            5 => SafeMigrationSql.Function(
                "coalesce",
                CreateExpression(identifier, literal, first, depth - 1),
                CreateExpression(identifier, literal, second, depth - 1)),
            6 => SafeMigrationSql.Cast(
                CreateExpression(identifier, literal, first, depth - 1),
                "DATE"),
            7 => SafeMigrationSql.Collate(
                CreateExpression(identifier, literal, first, depth - 1),
                "canonical"),
            8 => CreateCurrentValue(selector),
            _ => CreateLeaf(identifier, literal, selector),
        };
    }

    private static SafeMigrationSqlExpression CreateLeaf(
        string identifier,
        int literal,
        uint selector
    ) => (selector % 5) switch
    {
        0 => SafeMigrationSql.Identifier("app", identifier),
        1 => SafeMigrationSql.Literal(literal),
        2 => SafeMigrationSql.Literal(identifier),
        3 => SafeMigrationSql.Literal(null),
        _ => CreateCurrentValue(selector),
    };

    private static SafeMigrationSqlExpression CreateCurrentValue(
        uint selector
    )
    {
        var value = (SafeMigrationSqlCurrentValue)(selector % 3);
        var precision = value == SafeMigrationSqlCurrentValue.Date
            ? null
            : (int?)(selector % 7);

        return SafeMigrationSql.Current(value, precision);
    }

    private static string Identifier(
        string? value
    )
    {
        var source = string.IsNullOrEmpty(value) ? "value" : value;
        var length = Math.Min(source.Length, 24);
        var builder = new StringBuilder(length + 16);

        builder.Append("Value_");

        for (var index = 0; index < length; index++)
        {
            var character = source[index];
            builder.Append(character is '`' or '"' or '\'' || char.IsAsciiLetterOrDigit(character)
                ? character
                : '_');
        }

        // Always exercise both provider delimiter-escaping paths instead of
        // relying on a random string to happen to contain their quote tokens.
        builder.Append("_`\"");

        return builder.ToString();
    }

    private static uint Mix(
        uint value,
        uint salt
    )
    {
        value ^= salt;
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;

        return value ^ (value >> 16);
    }
}
