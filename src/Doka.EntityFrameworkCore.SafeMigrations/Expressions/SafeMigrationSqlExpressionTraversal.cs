namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Emits the existing preorder expression and collection-count contract without recursive calls.</summary>
internal ref struct SafeMigrationSqlExpressionTraversal
{
    private SafeMigrationWorkStack<TraversalFrame> _pending;
    private TraversalFrame _next;
    private bool _hasNext;

    /// <summary>Starts a traversal without materializing or copying the expression graph.</summary>
    /// <param name="expression">The root expression, including a null fallback.</param>
    public SafeMigrationSqlExpressionTraversal(
        SafeMigrationSqlExpression? expression
    )
    {
        _next = new TraversalFrame(expression);
        _hasNext = true;
    }

    /// <summary>Gets one node or a nonnegative collection count in the original serialization order.</summary>
    /// <param name="expression">The node, including a null CASE fallback, when the count is negative.</param>
    /// <param name="collectionCount">A collection count, or minus one for an expression node.</param>
    /// <returns>Whether another contract item remains.</returns>
    public bool TryTake(
        out SafeMigrationSqlExpression? expression,
        out int collectionCount
    )
    {
        if (!_hasNext
            && !_pending.TryPop(out _next))
        {
            expression = null;
            collectionCount = -1;

            return false;
        }

        var frame = _next;

        _hasNext = false;
        if (frame.Index == -2)
        {
            collectionCount = frame.Expression switch
            {
                SafeMigrationSqlInExpression value => value.Values.Count,
                SafeMigrationSqlFunctionExpression value => value.Arguments.Count,
                SafeMigrationSqlCaseExpression value => value.WhenClauses.Count,
                _ => throw new UnreachableException(),
            };

            expression = null;
            if (collectionCount > 0
                || frame.Expression is SafeMigrationSqlCaseExpression)
            {
                SetNext(frame with { Index = 0 });
            }

            return true;
        }

        collectionCount = -1;
        expression = frame.Index < 0
            ? frame.Expression
            : ResumeCollection(frame);

        switch (expression)
        {
            case SafeMigrationSqlUnaryExpression value:
                SetNext(new TraversalFrame(value.Operand));
                break;
            case SafeMigrationSqlBinaryExpression value:
                _pending.Push(new TraversalFrame(value.Right));
                SetNext(new TraversalFrame(value.Left));
                break;
            case SafeMigrationSqlNullTestExpression value:
                SetNext(new TraversalFrame(value.Operand));
                break;
            case SafeMigrationSqlBetweenExpression value:
                _pending.Push(new TraversalFrame(value.Upper));
                _pending.Push(new TraversalFrame(value.Lower));
                SetNext(new TraversalFrame(value.Operand));
                break;
            case SafeMigrationSqlInExpression value:
                // WHY: IN serializes its operand before the collection count; moving this count changes fingerprints.
                _pending.Push(new TraversalFrame(value, -2));
                SetNext(new TraversalFrame(value.Operand));
                break;
            case SafeMigrationSqlFunctionExpression or SafeMigrationSqlCaseExpression:
                SetNext(new TraversalFrame(expression, -2));
                break;
            case SafeMigrationSqlCastExpression value:
                SetNext(new TraversalFrame(value.Operand));
                break;
            case SafeMigrationSqlCollateExpression value:
                SetNext(new TraversalFrame(value.Operand));
                break;
        }

        return true;
    }

    /// <summary>Releases any pending frames after completion, early return, or an exception.</summary>
    public void Dispose()
    {
        _pending.Dispose();
        _next = default;
        _hasNext = false;
    }

    /// <summary>Schedules one tail child without allocating or retaining a recursive frame.</summary>
    private void SetNext(
        TraversalFrame frame
    )
    {
        _next = frame;
        _hasNext = true;
    }

    /// <summary>Queues one collection continuation so wide lists need only constant additional storage.</summary>
    private SafeMigrationSqlExpression? ResumeCollection(
        TraversalFrame frame
    )
    {
        switch (frame.Expression)
        {
            case SafeMigrationSqlInExpression value:
                if (frame.Index + 1 < value.Values.Count)
                {
                    _pending.Push(frame with { Index = frame.Index + 1 });
                }

                return value.Values[frame.Index];
            case SafeMigrationSqlFunctionExpression value:
                if (frame.Index + 1 < value.Arguments.Count)
                {
                    _pending.Push(frame with { Index = frame.Index + 1 });
                }

                return value.Arguments[frame.Index];
            case SafeMigrationSqlCaseExpression value:
                if (frame.Index == value.WhenClauses.Count)
                {
                    return value.ElseResult;
                }

                _pending.Push(
                    frame.Result
                        ? frame with
                        {
                            Index = frame.Index + 1,
                            Result = false,
                        }
                        : frame with { Result = true });

                return frame.Result
                    ? value.WhenClauses[frame.Index].Result
                    : value.WhenClauses[frame.Index].Condition;
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Distinguishes a node, a deferred count, and one collection member without per-node metadata.</summary>
    private readonly record struct TraversalFrame(
        SafeMigrationSqlExpression? Expression,
        int Index = -1,
        bool Result = false
    );
}
