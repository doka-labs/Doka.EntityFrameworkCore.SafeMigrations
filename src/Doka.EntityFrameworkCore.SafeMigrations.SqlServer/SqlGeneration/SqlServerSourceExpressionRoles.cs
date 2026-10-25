namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Validates native T-SQL source roles without applying the portable renderer's Boolean conversions.</summary>
internal static class SqlServerSourceExpressionRoles
{
    /// <summary>Whether an original parsed source expression occupies a native scalar position.</summary>
    internal static bool IsNativeScalar(SafeMigrationSqlExpression expression) => IsScalar(expression, depth: 0);

    /// <summary>Whether an original parsed source expression occupies a native search-condition position.</summary>
    internal static bool IsNativePredicate(SafeMigrationSqlExpression expression) => IsPredicate(expression, depth: 0);

    /// <summary>Retains source roles through every scalar child, rather than trusting the outer node alone.</summary>
    private static bool IsScalar(SafeMigrationSqlExpression expression, int depth)
    {
        if (depth > SafeMigrationSqlExpressionParser.MaximumDepth)
        {
            return false;
        }

        return expression switch
        {
            // WHY: Bare TRUE/FALSE are identifiers in native T-SQL, not the
            // portable Boolean literal represented by these parsed nodes.
            SafeMigrationSqlLiteralExpression literal => literal.Value is not bool,
            SafeMigrationSqlIdentifierExpression or SafeMigrationSqlCurrentValueExpression => true,
            SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Negate } unary =>
                IsScalar(unary.Operand, depth + 1),
            SafeMigrationSqlBinaryExpression
            {
                Operator: >= SafeMigrationSqlBinaryOperator.Add and <= SafeMigrationSqlBinaryOperator.Modulo,
            } binary => IsScalar(binary.Left, depth + 1) && IsScalar(binary.Right, depth + 1),
            SafeMigrationSqlCastExpression cast => IsScalar(cast.Operand, depth + 1),
            SafeMigrationSqlCollateExpression collate => IsScalar(collate.Operand, depth + 1),
            SafeMigrationSqlFunctionExpression function => AreScalars(function.Arguments, depth + 1),
            SafeMigrationSqlCaseExpression conditional => IsCase(conditional, depth + 1),
            _ => false,
        };
    }

    /// <summary>Requires a search condition wherever native SQL does not permit a scalar BIT or integer.</summary>
    private static bool IsPredicate(SafeMigrationSqlExpression expression, int depth)
    {
        if (depth > SafeMigrationSqlExpressionParser.MaximumDepth)
        {
            return false;
        }

        return expression switch
        {
            SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not } unary =>
                IsPredicate(unary.Operand, depth + 1),
            SafeMigrationSqlBinaryExpression
            {
                Operator: SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or,
            } binary => IsPredicate(binary.Left, depth + 1) && IsPredicate(binary.Right, depth + 1),
            SafeMigrationSqlBinaryExpression
            {
                Operator: >= SafeMigrationSqlBinaryOperator.Equal
                    and <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
            } binary => IsScalar(binary.Left, depth + 1) && IsScalar(binary.Right, depth + 1),
            SafeMigrationSqlNullTestExpression test => IsScalar(test.Operand, depth + 1),
            SafeMigrationSqlBetweenExpression between => IsScalar(between.Operand, depth + 1)
                && IsScalar(between.Lower, depth + 1) && IsScalar(between.Upper, depth + 1),
            SafeMigrationSqlInExpression list => IsScalar(list.Operand, depth + 1)
                && AreScalars(list.Values, depth + 1),
            _ => false,
        };
    }

    /// <summary>Every WHEN is native predicate SQL; every selected result is native scalar SQL.</summary>
    private static bool IsCase(SafeMigrationSqlCaseExpression expression, int depth)
    {
        // WHY: Whole-method role checks measured faster with snapshot enumeration for larger CASE branches.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var clause in expression.WhenClauses)
        {
            if (!IsPredicate(clause.Condition, depth) || !IsScalar(clause.Result, depth))
            {
                return false;
            }
        }

        return expression.ElseResult is null || IsScalar(expression.ElseResult, depth);
    }

    /// <summary>
    /// Checks argument and IN-list roles without allocating an enumerator or intermediate collection.
    /// </summary>
    private static bool AreScalars(IReadOnlyList<SafeMigrationSqlExpression> expressions, int depth)
    {
        // WHY: Indexing avoids an interface enumerator at each recursive argument-list check.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < expressions.Count; index++)
        {
            if (!IsScalar(expressions[index], depth))
            {
                return false;
            }
        }

        return true;
    }
}
