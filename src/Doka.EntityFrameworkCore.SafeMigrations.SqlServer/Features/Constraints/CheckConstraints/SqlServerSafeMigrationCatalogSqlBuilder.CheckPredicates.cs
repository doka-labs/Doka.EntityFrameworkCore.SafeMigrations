namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Collects only proven CHECK column roles for native rename-dependency comparison.</summary>
    /// <param name="definition">The exact authored CHECK contract.</param>
    /// <returns>Known local members, or null when native argument roles remain unproven.</returns>
    internal static string[]? GetCheckRenamePrerequisiteColumns(
        ExpectedCheckConstraintDefinition definition
    )
    {
        var expression = GetStructuredCheckExpression(definition);
        if (expression is null
            || !SqlServerComputedColumnContract.IsDeterministic(expression)
            || !SqlServerSourceExpressionRoles.IsNativePredicate(expression))
        {
            // WHY: Portable function arguments do not identify every native
            // type role. An unknown TRY_CONVERT type token, literal contents,
            // or function name must never become a column-name comparison.
            return null;
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression, referenced);

        return referenced.ToArray();
    }

    /// <summary>Parses a bounded raw predicate without rewriting its authored SQL or contract fingerprint.</summary>
    /// <param name="definition">The immutable authored CHECK.</param>
    /// <returns>The existing or completely parsed AST, or null for opaque syntax.</returns>
    internal static SafeMigrationSqlExpression? GetStructuredCheckExpression(
        ExpectedCheckConstraintDefinition definition
    ) => (definition.CapturedSql ?? definition.Sql) is { } authored && !HasSupportedRawCheckQuoting(authored)
        ? null
        : definition.Expression
        ?? (definition.Sql is { } sql
            && SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var parsed)
                ? parsed
                : null);

    // WHY: The shared parser recognizes multiple identifier dialects.
    // Keep native bracket roles but ignore delimiter characters inside
    // literal values; the shared quote-aware scan owns those boundaries.
    /// <summary>Certifies native identifier delimiters without reading string contents as syntax.</summary>
    private static bool HasSupportedRawCheckQuoting(
        string sql
    ) => SafeMigrationSqlExpressionParser.IsCapturedSourceDialectCompatible(sql, '[');

    /// <summary>Recognizes nonthrowing integer comparisons, Boolean composition, and null tests only.</summary>
    /// <param name="expression">The bounded expression AST.</param>
    /// <returns>True only when every scalar operand is a column or an uncast signed-integral constant.</returns>
    internal static bool IsSafeIntegerCheckPredicate(
        SafeMigrationSqlExpression expression
    ) => IsSafeIntegerCheckPredicate(expression, 0);

    /// <summary>Qualifies an accepted authored operand against the native integer CHECK contract.</summary>
    /// <param name="definition">The accepted projected physical column definition.</param>
    /// <returns>Whether comparison is nonthrowing under the same scalar family as live catalog validation.</returns>
    internal bool IsSupportedIntegerCheckColumn(
        ExpectedColumnDefinition definition
    ) => definition.ComputedColumnSql is null
        && definition.ComputedExpression is null
        && TryGetColumnType(definition, out var type)
        && type.Name is "tinyint" or "smallint" or "int" or "bigint";

    private static bool IsSafeIntegerCheckPredicate(
        SafeMigrationSqlExpression expression,
        int depth
    )
    {
        if (depth > SafeMigrationSqlExpressionParser.MaximumDepth)
        {
            return false;
        }

        return expression switch
        {
            SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not } unary
                => IsSafeIntegerCheckPredicate(unary.Operand, depth + 1),
            SafeMigrationSqlBinaryExpression
                {
                    Operator: SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or,
                } binary
                => IsSafeIntegerCheckPredicate(binary.Left, depth + 1)
                && IsSafeIntegerCheckPredicate(binary.Right, depth + 1),
            SafeMigrationSqlBinaryExpression
                {
                    Operator: >= SafeMigrationSqlBinaryOperator.Equal
                    and <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                } binary
                => IsSafeIntegerCheckScalar(binary.Left) && IsSafeIntegerCheckScalar(binary.Right),
            SafeMigrationSqlNullTestExpression nullTest => IsSafeIntegerCheckScalar(nullTest.Operand),
            _ => false,
        };
    }

    private static bool IsSafeIntegerCheckScalar(
        SafeMigrationSqlExpression expression
    ) => expression is SafeMigrationSqlIdentifierExpression { Parts.Count: 1 }
        || IsIntegerCheckLiteral(expression);

    /// <summary>Accepts only exact signed-integer literals without exposing an unused parsed value.</summary>
    private static bool IsIntegerCheckLiteral(
        SafeMigrationSqlExpression expression
    )
    {
        if (expression is SafeMigrationSqlUnaryExpression
            {
                Operator: SafeMigrationSqlUnaryOperator.Negate,
                Operand: SafeMigrationSqlLiteralExpression { StoreType: null } operand,
            }
            && TryGetUnsignedCheckLiteral(operand, out var positive))
        {
            var negativeValue = -positive;

            return negativeValue is >= long.MinValue and <= long.MaxValue;
        }

        if (expression is SafeMigrationSqlLiteralExpression { StoreType: null } literal
            && TryGetUnsignedCheckLiteral(literal, out var value))
        {
            return value is >= long.MinValue and <= long.MaxValue;
        }

        return false;
    }

    private static bool TryGetUnsignedCheckLiteral(
        SafeMigrationSqlLiteralExpression literal,
        out decimal value
    )
    {
        if (literal.NumericSourceToken.Span.IndexOfAny('e', 'E') >= 0)
        {
            // WHY: T-SQL exponent tokens are floating-point values, even when
            // the shared parser's CLR numeric value happens to be integral.
            // Preserve that distinction before granting an exact integer proof.
            value = 0;

            return false;
        }

        // WHY: SQL Server promotes large bare integer constants to decimal;
        // direct comparison remains exact because every built-in integer fits
        // the literal's decimal domain. No arithmetic or narrowing CAST is
        // admitted, including the signed-minimum unary literal boundary.
        switch (literal.BorrowedValue)
        {
            case byte number:
                value = number;
                break;
            case sbyte number:
                value = number;
                break;
            case short number:
                value = number;
                break;
            case ushort number:
                value = number;
                break;
            case int number:
                value = number;
                break;
            case uint number:
                value = number;
                break;
            case long number:
                value = number;
                break;
            case ulong number:
                value = number;
                break;
            case decimal number when decimal.Truncate(number) == number:
                value = number;
                break;
            default:
                value = 0;

                return false;
        }

        return true;
    }

    private string BuildIntegerCheckColumnProof(
        ExpectedCheckConstraintDefinition definition,
        IEnumerable<string> columns
    )
    {
        var proofs = columns.Select(column => $"""
            EXISTS(SELECT 1 FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id=c.user_type_id
            WHERE c.object_id={TableId(definition.Table, definition.Schema)}
            AND c.name={Literal(column)}
            AND ty.is_user_defined = 0 AND ty.is_assembly_type = 0
            AND ty.name IN (N'tinyint', N'smallint', N'int', N'bigint')
            AND c.is_computed=0 AND c.is_sparse=0
            AND c.is_column_set=0 AND c.is_hidden=0
            AND c.generated_always_type=0
            AND c.encryption_type IS NULL)
            """);

        var predicate = string.Join(" AND ", proofs);

        return predicate.Length == 0
            ? "1 = 1"
            : predicate;
    }
}
