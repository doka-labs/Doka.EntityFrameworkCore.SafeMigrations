namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Parses a bounded raw predicate without rewriting its authored SQL or contract fingerprint.</summary>
    /// <param name="definition">The immutable authored CHECK.</param>
    /// <returns>The existing or completely parsed AST, or null for opaque syntax.</returns>
    internal static SafeMigrationSqlExpression? GetStructuredCheckExpression(
        ExpectedCheckConstraintDefinition definition
    )
        => definition.Sql is { } authored && !HasSupportedRawCheckQuoting(authored) ? null
            : definition.Expression ?? (definition.Sql is { } sql
                && SafeMigrationSqlExpressionParser.TryParse(sql, out var parsed) ? parsed : null);

    private static bool HasSupportedRawCheckQuoting(
        string sql
    )
    {
        // WHY: The shared parser recognizes multiple identifier dialects.
        // Raw SQL is preserved, so backticks are invalid and double quotes
        // depend on unproved QUOTED_IDENTIFIER session state. Bracketed names
        // may contain either character and must retain their exact identity.
        var bracketed = false;

        for (var index = 0; index < sql.Length; index++)
        {
            if (bracketed)
            {
                if (sql[index] == ']')
                {
                    if (index + 1 < sql.Length
                        && sql[index + 1] == ']')
                    {
                        index++;
                    }
                    else
                    {
                        bracketed = false;
                    }
                }
            }
            else if (sql[index] == '[')
            {
                bracketed = true;
            }
            else if (sql[index] is '`' or '"')
            {
                return false;
            }
        }

        return !bracketed;
    }

    /// <summary>Recognizes nonthrowing integer comparisons, Boolean composition, and null tests only.</summary>
    /// <param name="expression">The bounded expression AST.</param>
    /// <returns>True only when every scalar operand is a column or an uncast signed-integral constant.</returns>
    internal static bool IsSafeIntegerCheckPredicate(
        SafeMigrationSqlExpression expression
    )
        => IsSafeIntegerCheckPredicate(expression, 0);

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
            SafeMigrationSqlBinaryExpression binary when binary.Operator is SafeMigrationSqlBinaryOperator.And
                or SafeMigrationSqlBinaryOperator.Or => IsSafeIntegerCheckPredicate(binary.Left, depth + 1)
                    && IsSafeIntegerCheckPredicate(binary.Right, depth + 1),
            SafeMigrationSqlBinaryExpression binary when binary.Operator is >= SafeMigrationSqlBinaryOperator.Equal
                and <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual
                => IsSafeIntegerCheckScalar(binary.Left) && IsSafeIntegerCheckScalar(binary.Right),
            SafeMigrationSqlNullTestExpression nullTest => IsSafeIntegerCheckScalar(nullTest.Operand),
            _ => false,
        };
    }

    private static bool IsSafeIntegerCheckScalar(
        SafeMigrationSqlExpression expression
    )
        => expression is SafeMigrationSqlIdentifierExpression { Parts.Count: 1 }
            || TryGetIntegerCheckLiteral(expression, out _);

    private static bool TryGetIntegerCheckLiteral(
        SafeMigrationSqlExpression expression,
        out decimal value
    )
    {
        if (expression is SafeMigrationSqlUnaryExpression
            {
                Operator: SafeMigrationSqlUnaryOperator.Negate,
                Operand: SafeMigrationSqlLiteralExpression { StoreType: null } operand,
            } && TryGetUnsignedCheckLiteral(operand, out var positive))
        {
            value = -positive;

            return value >= long.MinValue && value <= long.MaxValue;
        }

        if (expression is SafeMigrationSqlLiteralExpression { StoreType: null } literal
            && TryGetUnsignedCheckLiteral(literal, out value))
        {
            return value >= long.MinValue && value <= long.MaxValue;
        }

        value = 0;

        return false;
    }

    private static bool TryGetUnsignedCheckLiteral(
        SafeMigrationSqlLiteralExpression literal,
        out decimal value
    )
    {
        // WHY: SQL Server promotes large bare integer constants to decimal;
        // direct comparison remains exact because every built-in integer fits
        // the literal's decimal domain. No arithmetic or narrowing CAST is
        // admitted, including the signed-minimum unary literal boundary.
        switch (literal.Value)
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
        var proofs = columns.Select(column => "EXISTS(SELECT 1 FROM sys.columns c JOIN sys.types ty "
            + "ON ty.user_type_id=c.user_type_id "
            + $"WHERE c.object_id={TableId(definition.Table, definition.Schema)} AND c.name={Literal(column)} "
            + "AND ty.is_user_defined = 0 AND ty.is_assembly_type = 0 "
            + "AND ty.name IN (N'tinyint', N'smallint', N'int', N'bigint') "
            + "AND c.is_computed=0 AND c.is_sparse=0 AND c.is_column_set=0 AND c.is_hidden=0 "
            + "AND c.generated_always_type=0 AND c.encryption_type IS NULL)");

        var predicate = string.Join(" AND ", proofs);

        return predicate.Length == 0 ? "1 = 1" : predicate;
    }
}
