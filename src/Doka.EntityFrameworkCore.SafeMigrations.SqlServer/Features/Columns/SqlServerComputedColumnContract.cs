namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Qualifies immutable computed expressions without trusting opaque SQL or nullable metadata.</summary>
internal static class SqlServerComputedColumnContract
{
    /// <summary>Gets the bounded structured form of an authored computed column.</summary>
    internal static SafeMigrationSqlExpression? Expression(ExpectedColumnDefinition definition)
        => definition.ComputedExpression ?? (definition.ComputedColumnSql is { } sql
            && SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression) ? expression : null);

    /// <summary>Identifies authored generated storage independently of expression support.</summary>
    internal static bool IsComputed(ExpectedColumnDefinition definition)
        => definition.ComputedExpression is not null || definition.ComputedColumnSql is not null;

    /// <summary>Requires deterministic scalar syntax before native type and index metadata can be consulted.</summary>
    internal static bool IsDeterministic(SafeMigrationSqlExpression expression)
        => IsDeterministic(expression, 0);

    private static bool IsDeterministic(SafeMigrationSqlExpression expression, int depth)
    {
        if (depth > SafeMigrationSqlExpressionParser.MaximumDepth)
        {
            return false;
        }

        return expression switch
        {
            SafeMigrationSqlLiteralExpression => true,
            SafeMigrationSqlIdentifierExpression value => value.Parts.Count == 1,
            SafeMigrationSqlUnaryExpression value => IsDeterministic(value.Operand, depth + 1),
            SafeMigrationSqlBinaryExpression value => IsDeterministic(value.Left, depth + 1)
                && IsDeterministic(value.Right, depth + 1),
            SafeMigrationSqlNullTestExpression value => IsDeterministic(value.Operand, depth + 1),
            SafeMigrationSqlCastExpression value => IsDeterministic(value.Operand, depth + 1),
            SafeMigrationSqlCollateExpression value => IsDeterministic(value.Operand, depth + 1),
            SafeMigrationSqlBetweenExpression value => IsDeterministic(value.Operand, depth + 1)
                && IsDeterministic(value.Lower, depth + 1) && IsDeterministic(value.Upper, depth + 1),
            SafeMigrationSqlInExpression value => IsDeterministic(value.Operand, depth + 1)
                && AreDeterministic(value.Values, depth + 1),
            SafeMigrationSqlCaseExpression value => IsDeterministicCase(value, depth + 1),
            SafeMigrationSqlFunctionExpression value when value.Name.Equals("COALESCE",
                StringComparison.OrdinalIgnoreCase) || value.Name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase)
                || value.Name.Equals("ABS", StringComparison.OrdinalIgnoreCase)
                || value.Name.Equals("LEN", StringComparison.OrdinalIgnoreCase)
                || value.Name.Equals("LOWER", StringComparison.OrdinalIgnoreCase)
                || value.Name.Equals("UPPER", StringComparison.OrdinalIgnoreCase)
                || value.Name.Equals("CONCAT", StringComparison.OrdinalIgnoreCase)
                => AreDeterministic(value.Arguments, depth + 1),
            _ => false,
        };
    }

    private static bool AreDeterministic(IReadOnlyList<SafeMigrationSqlExpression> expressions, int depth)
    {
        // WHY: Whole-method validation measured faster with snapshot enumeration for larger argument lists.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var expression in expressions)
        {
            if (!IsDeterministic(expression, depth))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDeterministicCase(SafeMigrationSqlCaseExpression expression, int depth)
    {
        // WHY: Whole-method CASE validation measured faster with snapshot enumeration for larger branches.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var clause in expression.WhenClauses)
        {
            if (!IsDeterministic(clause.Condition, depth) || !IsDeterministic(clause.Result, depth))
            {
                return false;
            }
        }

        return expression.ElseResult is null || IsDeterministic(expression.ElseResult, depth);
    }

    /// <summary>Proves required literals and finite Boolean branches without optimistic casts.</summary>
    internal static bool IsNonNull(SafeMigrationSqlExpression expression)
    {
        while (expression is SafeMigrationSqlCollateExpression collated)
        {
            expression = collated.Operand;
        }

        return expression switch
        {
            SafeMigrationSqlLiteralExpression { Value: not null, StoreType: null } => true,
            SafeMigrationSqlLiteralExpression => IsFiniteBoolean(expression),
            SafeMigrationSqlCastExpression value => IsIntegralBooleanTarget(value.StoreType)
                && IsFiniteBoolean(value.Operand),
            SafeMigrationSqlNullTestExpression => true,
            SafeMigrationSqlCaseExpression => IsFiniteBoolean(expression),
            SafeMigrationSqlFunctionExpression value when value.Name.Equals("COALESCE",
                StringComparison.OrdinalIgnoreCase) => IsFiniteBoolean(expression),
            // WHY: Non-null inputs do not prove arbitrary SQL casts or mixed
            // CASE coercions safe under relaxed arithmetic settings.
            _ => false,
        };
    }

    /// <summary>Proves the same finite domain using bounded continuation storage rather than CLR recursion.</summary>
    private static bool IsFiniteBoolean(SafeMigrationSqlExpression expression)
    {
        var pending = default(SafeMigrationWorkStack<FiniteFrame>);
        var current = new FiniteFrame(expression);
        try
        {
            do
            {
                switch (current.Expression)
                {
                    case SafeMigrationSqlLiteralExpression { Value: null, StoreType: null }
                        when current.AllowNull:
                    case SafeMigrationSqlNullTestExpression:
                        break;

                    case SafeMigrationSqlLiteralExpression literal:
                        if (literal.StoreType is not null && !IsIntegralBooleanTarget(literal.StoreType)
                            || literal.Value is not (false or true or (byte)0 or (byte)1 or (sbyte)0 or (sbyte)1
                                or (short)0 or (short)1 or (ushort)0 or (ushort)1
                                or 0 or 1 or 0U or 1U or 0L or 1L or 0UL or 1UL))
                        {
                            return false;
                        }

                        break;

                    case SafeMigrationSqlCastExpression cast:
                        if (!IsIntegralBooleanTarget(cast.StoreType))
                        {
                            return false;
                        }

                        current = new FiniteFrame(cast.Operand);
                        continue;

                    case SafeMigrationSqlCaseExpression conditional:
                        if (conditional.ElseResult is null)
                        {
                            return false;
                        }

                        if (current.Index == 0)
                        {
                            pending.Push(current with { Index = 1 });
                            current = new FiniteFrame(conditional.ElseResult);
                            continue;
                        }

                        if (current.Index <= conditional.WhenClauses.Count)
                        {
                            pending.Push(current with { Index = current.Index + 1 });
                            current = new FiniteFrame(conditional.WhenClauses[current.Index - 1].Result);
                            continue;
                        }

                        break;

                    case SafeMigrationSqlFunctionExpression function
                        when function.Name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase):
                        if (current.Index == 0 && !function.Arguments.Any(static argument
                            => argument is not SafeMigrationSqlLiteralExpression { Value: null, StoreType: null }))
                        {
                            return false;
                        }

                        if (current.Index < function.Arguments.Count)
                        {
                            pending.Push(current with { Index = current.Index + 1 });
                            current = new FiniteFrame(function.Arguments[current.Index], AllowNull: true);
                            continue;
                        }

                        break;

                    default:
                        return false;
                }

                if (!pending.TryPop(out current))
                {
                    return true;
                }
            }
            while (true);
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Retains one branch position and the special untyped-NULL allowance of COALESCE.</summary>
    private readonly record struct FiniteFrame(
        SafeMigrationSqlExpression Expression,
        int Index = 0,
        bool AllowNull = false
    );

    private static bool IsIntegralBooleanTarget(string storeType)
        => storeType.Equals("bit", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("tinyint", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("smallint", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("int", StringComparison.OrdinalIgnoreCase)
            || storeType.Equals("bigint", StringComparison.OrdinalIgnoreCase);
}
