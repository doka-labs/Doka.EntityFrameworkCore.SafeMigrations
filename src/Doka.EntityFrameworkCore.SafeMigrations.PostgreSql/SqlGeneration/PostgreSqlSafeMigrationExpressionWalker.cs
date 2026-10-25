namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Visits structured operands in authored order without recursive calls or wide-list copies.</summary>
internal ref struct PostgreSqlSafeMigrationExpressionWalker
{
    private SafeMigrationWorkStack<(SafeMigrationSqlExpression Expression, int Index)> _pending;
    private SafeMigrationSqlExpression? _next;
    private readonly bool _inValuesFirst;

    /// <summary>Starts one independently owned preorder traversal.</summary>
    /// <param name="expression">The expression whose descendants are visited.</param>
    /// <param name="inValuesFirst">Preserves the captured CAST collector's historical IN registration order.</param>
    public PostgreSqlSafeMigrationExpressionWalker(SafeMigrationSqlExpression expression, bool inValuesFirst = false)
    {
        _next = expression;
        _inValuesFirst = inValuesFirst;
    }

    /// <summary>Takes the next operand, retaining only continuations for actual branches.</summary>
    /// <param name="expression">The next node, or null after the traversal has completed.</param>
    /// <returns>Whether another node was available.</returns>
    public bool TryTake([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SafeMigrationSqlExpression? expression)
    {
        if (_next is null && _pending.TryPop(out var continuation))
        {
            TryGetTraversalChild(continuation.Expression, continuation.Index, out _next);
            if (TryGetTraversalChild(continuation.Expression, continuation.Index + 1, out _))
            {
                _pending.Push((continuation.Expression, continuation.Index + 1));
            }
        }

        expression = _next;
        if (expression is null)
        {
            return false;
        }

        TryGetTraversalChild(expression, 0, out _next);
        if (TryGetTraversalChild(expression, 1, out _))
        {
            _pending.Push((expression, 1));
        }

        return true;
    }

    /// <summary>Releases pending operands even after a caller short-circuits or throws.</summary>
    public void Dispose() => _pending.Dispose();

    /// <summary>Keeps the one historical collector ordering separate from ordinary authored traversal.</summary>
    private readonly bool TryGetTraversalChild(
        SafeMigrationSqlExpression expression, int index,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SafeMigrationSqlExpression? child)
    {
        if (_inValuesFirst && expression is SafeMigrationSqlInExpression list)
        {
            child = index < list.Values.Count ? list.Values[index] : index == list.Values.Count ? list.Operand : null;

            return child is not null;
        }

        return TryGetChild(expression, index, out child);
    }

    /// <summary>Reads one direct operand without allocating an enumerable or inspecting later siblings.</summary>
    internal static bool TryGetChild(
        SafeMigrationSqlExpression expression, int index,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SafeMigrationSqlExpression? child)
    {
        child = expression switch
        {
            SafeMigrationSqlUnaryExpression value when index == 0 => value.Operand,
            SafeMigrationSqlBinaryExpression value => index switch { 0 => value.Left, 1 => value.Right, _ => null },
            SafeMigrationSqlNullTestExpression value when index == 0 => value.Operand,
            SafeMigrationSqlBetweenExpression value => index switch
            {
                0 => value.Operand, 1 => value.Lower, 2 => value.Upper, _ => null,
            },
            SafeMigrationSqlInExpression value when index == 0 => value.Operand,
            SafeMigrationSqlInExpression value when index <= value.Values.Count => value.Values[index - 1],
            SafeMigrationSqlFunctionExpression value when index < value.Arguments.Count => value.Arguments[index],
            SafeMigrationSqlCastExpression value when index == 0 => value.Operand,
            SafeMigrationSqlCollateExpression value when index == 0 => value.Operand,
            SafeMigrationSqlCaseExpression value when index / 2 < value.WhenClauses.Count => index % 2 == 0
                ? value.WhenClauses[index / 2].Condition : value.WhenClauses[index / 2].Result,
            SafeMigrationSqlCaseExpression value when index / 2 == value.WhenClauses.Count && index % 2 == 0
                => value.ElseResult,
            _ => null,
        };

        return child is not null;
    }
}
