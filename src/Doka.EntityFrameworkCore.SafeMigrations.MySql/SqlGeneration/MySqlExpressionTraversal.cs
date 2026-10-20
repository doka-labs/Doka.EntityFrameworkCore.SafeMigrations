namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Visits ordered expression children without recursion or a work item for every list element.</summary>
/// <remarks>Keep one owning traversal scoped to a try/finally and do not copy its pooled-stack ownership.</remarks>
internal ref struct MySqlExpressionTraversal
{
    private SafeMigrationWorkStack<MySqlExpressionChildCursor> _pending;
    private SafeMigrationSqlExpression? _current;
    private bool _started;
    private readonly bool _caseElseFirst;

    /// <summary>Starts a preorder traversal without renting a buffer for a leaf or unary-only tree.</summary>
    /// <param name="expression">The first node, including a previously tolerated unknown node.</param>
    /// <param name="caseElseFirst">Whether CASE metadata reads retain their historical ELSE-first order.</param>
    internal MySqlExpressionTraversal(
        SafeMigrationSqlExpression expression,
        bool caseElseFirst = false
    )
    {
        _current = expression;
        _caseElseFirst = caseElseFirst;
    }

    /// <summary>Returns the next occurrence, preserving shared-node occurrences and short-circuiting callers.</summary>
    /// <param name="expression">The next node, or null after traversal is exhausted.</param>
    /// <returns>Whether another occurrence was available.</returns>
    internal bool TryMoveNext(
        [NotNullWhen(true)] out SafeMigrationSqlExpression? expression
    )
    {
        if (!_started)
        {
            _started = true;
            expression = _current;

            return expression is not null;
        }

        if (_current is not null)
        {
            var children = new MySqlExpressionChildCursor(_current, _caseElseFirst);
            if (children.TryMoveNext(out expression))
            {
                if (children.HasRemaining)
                {
                    _pending.Push(children);
                }

                _current = expression;

                return true;
            }
        }

        if (_pending.TryPop(out var remaining)
            && remaining.TryMoveNext(out expression))
        {
            if (remaining.HasRemaining)
            {
                _pending.Push(remaining);
            }

            _current = expression;

            return true;
        }

        _current = null;
        expression = null;

        return false;
    }

    /// <summary>Clears pending references on completion, early return and exceptional exits.</summary>
    internal void Dispose()
    {
        _current = null;
        _pending.Dispose();
    }
}

/// <summary>Retains one parent and its next child index, regardless of argument or CASE-list width.</summary>
internal struct MySqlExpressionChildCursor
{
    private readonly bool _caseElseFirst;
    private readonly int _count;

    /// <summary>Captures child roles without changing each consumer's unknown-node policy.</summary>
    /// <param name="expression">The parent whose known child roles are enumerated.</param>
    /// <param name="caseElseFirst">Whether CASE metadata visits ELSE before the ordered WHEN pairs.</param>
    internal MySqlExpressionChildCursor(
        SafeMigrationSqlExpression expression,
        bool caseElseFirst = false
    )
    {
        Expression = expression;
        _caseElseFirst = caseElseFirst;
        _count = expression switch
        {
            SafeMigrationSqlUnaryExpression
                or SafeMigrationSqlNullTestExpression
                or SafeMigrationSqlCastExpression
                or SafeMigrationSqlCollateExpression => 1,
            SafeMigrationSqlBinaryExpression => 2,
            SafeMigrationSqlBetweenExpression => 3,
            SafeMigrationSqlInExpression value => checked(1 + value.Values.Count),
            SafeMigrationSqlFunctionExpression value => value.Arguments.Count,
            SafeMigrationSqlCaseExpression value => checked((2 * value.WhenClauses.Count)
                + (value.ElseResult is null ? 0 : 1)),
            _ => 0,
        };
    }

    /// <summary>Gets the original parent for renderer continuations.</summary>
    internal SafeMigrationSqlExpression Expression { get; }

    /// <summary>Gets the index of the next child, before advancing the cursor.</summary>
    internal int NextIndex { get; private set; }

    /// <summary>Gets whether this cursor must be retained while the current child is visited.</summary>
    internal readonly bool HasRemaining => NextIndex < _count;

    /// <summary>Advances one known child in authored order without creating an enumerator.</summary>
    /// <param name="expression">The next child, or null when all children have been visited.</param>
    /// <returns>Whether another child was available.</returns>
    internal bool TryMoveNext(
        [NotNullWhen(true)] out SafeMigrationSqlExpression? expression
    )
    {
        if (NextIndex == _count)
        {
            expression = null;

            return false;
        }

        var index = NextIndex++;
        expression = Expression switch
        {
            SafeMigrationSqlUnaryExpression value => value.Operand,
            SafeMigrationSqlBinaryExpression value => index == 0 ? value.Left : value.Right,
            SafeMigrationSqlNullTestExpression value => value.Operand,
            SafeMigrationSqlBetweenExpression value => index switch
            {
                0 => value.Operand, 1 => value.Lower, _ => value.Upper,
            },
            SafeMigrationSqlInExpression value => index == 0 ? value.Operand : value.Values[index - 1],
            SafeMigrationSqlFunctionExpression value => value.Arguments[index],
            SafeMigrationSqlCastExpression value => value.Operand,
            SafeMigrationSqlCollateExpression value => value.Operand,
            SafeMigrationSqlCaseExpression value => CaseChild(value, index),
            _ => throw new UnreachableException(),
        };

        return true;
    }

    /// <summary>Keeps the aggregate text scan's ELSE-first order separate from executable CASE order.</summary>
    private readonly SafeMigrationSqlExpression CaseChild(
        SafeMigrationSqlCaseExpression expression,
        int index
    )
    {
        if (_caseElseFirst && expression.ElseResult is not null)
        {
            if (index == 0)
            {
                return expression.ElseResult;
            }

            index--;
        }
        else if (index == expression.WhenClauses.Count * 2)
        {
            return expression.ElseResult!;
        }

        var clause = expression.WhenClauses[index / 2];

        return index % 2 == 0 ? clause.Condition : clause.Result;
    }
}
