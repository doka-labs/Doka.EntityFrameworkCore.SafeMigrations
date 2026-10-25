namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Compares and serializes expression contracts without changing their token or child order.</summary>
internal static class SafeMigrationSqlExpressionContract
{
    /// <summary>Compares expression structure with reference fast paths and left-to-right short-circuiting.</summary>
    /// <param name="left">The first expression, including null.</param>
    /// <param name="right">The second expression, including null.</param>
    /// <returns>Whether every expression facet and ordered child is equivalent.</returns>
    public static bool Equivalent(
        SafeMigrationSqlExpression? left,
        SafeMigrationSqlExpression? right
    )
    {
        var pending = default(SafeMigrationWorkStack<ComparisonFrame>);

        try
        {
            while (true)
            {
                if (!ReferenceEquals(left, right))
                {
                    if (left is null
                        || right is null
                        || left.GetType() != right.GetType())
                    {
                        return false;
                    }

                    switch (left, right)
                    {
                        case (SafeMigrationSqlIdentifierExpression first, SafeMigrationSqlIdentifierExpression second):
                            if (!first.Parts.SequenceEqual(second.Parts, StringComparer.Ordinal))
                            {
                                return false;
                            }

                            break;
                        case (SafeMigrationSqlLiteralExpression first, SafeMigrationSqlLiteralExpression second):
                            if (!StringComparer.Ordinal.Equals(first.StoreType, second.StoreType)
                                || !first.NumericSourceToken.Span.SequenceEqual(second.NumericSourceToken.Span)
                                || !LiteralEquivalent(first.Value, second.Value))
                            {
                                return false;
                            }

                            break;
                        case (SafeMigrationSqlUnaryExpression first, SafeMigrationSqlUnaryExpression second):
                            if (first.Operator != second.Operator)
                            {
                                return false;
                            }

                            left = first.Operand;
                            right = second.Operand;
                            continue;
                        case (SafeMigrationSqlBinaryExpression first, SafeMigrationSqlBinaryExpression second):
                            if (first.Operator != second.Operator)
                            {
                                return false;
                            }

                            pending.Push(new ComparisonFrame(first.Right, second.Right));
                            left = first.Left;
                            right = second.Left;
                            continue;
                        case (SafeMigrationSqlNullTestExpression first, SafeMigrationSqlNullTestExpression second):
                            if (first.Negated != second.Negated)
                            {
                                return false;
                            }

                            left = first.Operand;
                            right = second.Operand;
                            continue;
                        case (SafeMigrationSqlBetweenExpression first, SafeMigrationSqlBetweenExpression second):
                            if (first.Negated != second.Negated)
                            {
                                return false;
                            }

                            pending.Push(new ComparisonFrame(first.Upper, second.Upper));
                            pending.Push(new ComparisonFrame(first.Lower, second.Lower));
                            left = first.Operand;
                            right = second.Operand;
                            continue;
                        case (SafeMigrationSqlInExpression first, SafeMigrationSqlInExpression second):
                            if (first.Negated != second.Negated)
                            {
                                return false;
                            }

                            // WHY: The existing short-circuit contract checks the operand before even the list count.
                            pending.Push(new ComparisonFrame(first, second, 0));
                            left = first.Operand;
                            right = second.Operand;
                            continue;
                        case (SafeMigrationSqlFunctionExpression first, SafeMigrationSqlFunctionExpression second):
                            if (!StringComparer.Ordinal.Equals(first.Name, second.Name)
                                || first.Arguments.Count != second.Arguments.Count)
                            {
                                return false;
                            }

                            if (first.Arguments.Count > 0)
                            {
                                pending.Push(new ComparisonFrame(first, second, 0));
                            }

                            break;
                        case (SafeMigrationSqlCaseExpression first, SafeMigrationSqlCaseExpression second):
                            if (first.WhenClauses.Count != second.WhenClauses.Count)
                            {
                                return false;
                            }

                            pending.Push(new ComparisonFrame(first, second, 0));
                            break;
                        case (SafeMigrationSqlCastExpression first, SafeMigrationSqlCastExpression second):
                            if (first.UsesSqlServerConvert != second.UsesSqlServerConvert
                                || !StringComparer.Ordinal.Equals(first.StoreType, second.StoreType))
                            {
                                return false;
                            }

                            left = first.Operand;
                            right = second.Operand;
                            continue;
                        case (SafeMigrationSqlCollateExpression first, SafeMigrationSqlCollateExpression second):
                            if (!StringComparer.Ordinal.Equals(first.Name, second.Name)
                                || !StringComparer.Ordinal.Equals(first.Schema, second.Schema))
                            {
                                return false;
                            }

                            left = first.Operand;
                            right = second.Operand;
                            continue;
                        case (SafeMigrationSqlCurrentValueExpression first, SafeMigrationSqlCurrentValueExpression
                            second):
                            if (first.Value != second.Value
                                || first.Precision != second.Precision)
                            {
                                return false;
                            }

                            break;
                        case (SafeMigrationSqlProviderFragmentExpression first,
                            SafeMigrationSqlProviderFragmentExpression second):
                            if (!StringComparer.Ordinal.Equals(first.ProviderId, second.ProviderId)
                                || !StringComparer.Ordinal.Equals(first.Sql, second.Sql))
                            {
                                return false;
                            }

                            break;
                        case (SafeMigrationSqlOpaqueExpression first, SafeMigrationSqlOpaqueExpression second):
                            if (first.FollowsIdentifierRename != second.FollowsIdentifierRename
                                || !StringComparer.Ordinal.Equals(first.Sql, second.Sql))
                            {
                                return false;
                            }

                            break;
                        default:
                            throw new UnreachableException();
                    }
                }

                if (!pending.TryPop(out var next))
                {
                    return true;
                }

                left = next.Left;
                right = next.Right;
                if (next.Index >= 0
                    && !ResumeComparison(ref pending, next, out left, out right))
                {
                    return false;
                }
            }
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Writes the established canonical bytes without imposing a public-tree depth limit.</summary>
    /// <param name="writer">The canonical contract writer.</param>
    /// <param name="expression">The expression, including null.</param>
    public static void Write(
        CanonicalHashWriter writer,
        SafeMigrationSqlExpression? expression
    )
    {
        var traversal = new SafeMigrationSqlExpressionTraversal(expression);

        try
        {
            while (traversal.TryTake(out expression, out var collectionCount))
            {
                if (collectionCount >= 0)
                {
                    writer.Add(collectionCount);
                    continue;
                }

                writer.Add(expression is not null);
                if (expression is null)
                {
                    continue;
                }

                switch (expression)
                {
                    case SafeMigrationSqlIdentifierExpression value:
                        writer.Add("identifier");
                        WriteStrings(writer, value.Parts);
                        break;
                    case SafeMigrationSqlLiteralExpression value:
                        writer.Add(value.NumericSourceToken.IsEmpty ? "literal" : "sql_numeric_literal");
                        if (!value.NumericSourceToken.IsEmpty)
                        {
                            writer.Add(value.NumericSourceToken.Span);
                        }

                        writer.Add(value.StoreType);
                        WriteLiteral(writer, value.Value);
                        break;
                    case SafeMigrationSqlUnaryExpression value:
                        writer.Add("unary");
                        writer.Add((int)value.Operator);
                        break;
                    case SafeMigrationSqlBinaryExpression value:
                        writer.Add("binary");
                        writer.Add((int)value.Operator);
                        break;
                    case SafeMigrationSqlNullTestExpression value:
                        writer.Add("null_test");
                        writer.Add(value.Negated);
                        break;
                    case SafeMigrationSqlBetweenExpression value:
                        writer.Add("between");
                        writer.Add(value.Negated);
                        break;
                    case SafeMigrationSqlInExpression value:
                        writer.Add("in");
                        writer.Add(value.Negated);
                        break;
                    case SafeMigrationSqlFunctionExpression value:
                        writer.Add("function");
                        writer.Add(value.Name);
                        break;
                    case SafeMigrationSqlCastExpression value:
                        writer.Add(value.UsesSqlServerConvert ? "sqlserver_convert" : "cast");
                        writer.Add(value.StoreType);
                        break;
                    case SafeMigrationSqlCaseExpression:
                        writer.Add("case");
                        break;
                    case SafeMigrationSqlCollateExpression value:
                        writer.Add("collate");
                        writer.Add(value.Schema);
                        writer.Add(value.Name);
                        break;
                    case SafeMigrationSqlCurrentValueExpression value:
                        writer.Add("current");
                        writer.Add((int)value.Value);
                        writer.Add(value.Precision);
                        break;
                    case SafeMigrationSqlProviderFragmentExpression value:
                        writer.Add("provider_fragment");
                        writer.Add(value.ProviderId);
                        writer.Add(value.Sql);
                        break;
                    case SafeMigrationSqlOpaqueExpression value:
                        writer.Add("opaque");
                        writer.Add(value.FollowsIdentifierRename);
                        writer.Add(value.Sql);
                        break;
                    default:
                        throw new UnreachableException();
                }
            }
        }
        finally
        {
            traversal.Dispose();
        }
    }

    /// <summary>Resumes one ordered collection child without queuing its remaining width.</summary>
    private static bool ResumeComparison(
        ref SafeMigrationWorkStack<ComparisonFrame> pending,
        ComparisonFrame frame,
        out SafeMigrationSqlExpression? left,
        out SafeMigrationSqlExpression? right
    )
    {
        switch (frame.Left, frame.Right)
        {
            case (SafeMigrationSqlInExpression first, SafeMigrationSqlInExpression second):
                return ResumeSequence(ref pending, frame, first.Values, second.Values, out left, out right);
            case (SafeMigrationSqlFunctionExpression first, SafeMigrationSqlFunctionExpression second):
                return ResumeSequence(ref pending, frame, first.Arguments, second.Arguments, out left, out right);
            case (SafeMigrationSqlCaseExpression first, SafeMigrationSqlCaseExpression second):
                if (frame.Index == first.WhenClauses.Count)
                {
                    left = first.ElseResult;
                    right = second.ElseResult;

                    return true;
                }

                var firstClause = first.WhenClauses[frame.Index];
                var secondClause = second.WhenClauses[frame.Index];

                pending.Push(
                    frame.Result
                        ? frame with { Index = frame.Index + 1, Result = false }
                        : frame with { Result = true });

                left = frame.Result ? firstClause.Result : firstClause.Condition;
                right = frame.Result ? secondClause.Result : secondClause.Condition;

                return true;
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Preserves sequence count checks and the original element comparison order.</summary>
    private static bool ResumeSequence(
        ref SafeMigrationWorkStack<ComparisonFrame> pending,
        ComparisonFrame frame,
        IReadOnlyList<SafeMigrationSqlExpression> first,
        IReadOnlyList<SafeMigrationSqlExpression> second,
        out SafeMigrationSqlExpression? left,
        out SafeMigrationSqlExpression? right
    )
    {
        left = null;
        right = null;
        if (first.Count != second.Count)
        {
            return false;
        }

        if (frame.Index < first.Count)
        {
            if (frame.Index + 1 < first.Count)
            {
                pending.Push(frame with { Index = frame.Index + 1 });
            }

            left = first[frame.Index];
            right = second[frame.Index];
        }

        return true;
    }

    /// <summary>Retains a branch or collection continuation without copying or mutating expression nodes.</summary>
    private readonly record struct ComparisonFrame(
        SafeMigrationSqlExpression? Left,
        SafeMigrationSqlExpression? Right,
        int Index = -1,
        bool Result = false
    );

    private static bool LiteralEquivalent(
        object? left,
        object? right
    ) => left is byte[] leftBytes && right is byte[] rightBytes
        ? leftBytes
            .AsSpan()
            .SequenceEqual(rightBytes)
        : Equals(left, right);

    private static void WriteStrings(
        CanonicalHashWriter writer,
        IReadOnlyList<string> values
    )
    {
        writer.Add(values.Count);
        foreach (var value in values)
        {
            writer.Add(value);
        }
    }

    private static void WriteLiteral(
        CanonicalHashWriter writer,
        object? value
    )
    {
        if (value is null)
        {
            writer.Add("null");
            return;
        }

        writer.Add(value.GetType().FullName ?? value.GetType().Name);
        if (value is byte[] bytes)
        {
            writer.Add(bytes);
            return;
        }

        writer.Add(
            value switch
            {
                float number => number.ToString("R", CultureInfo.InvariantCulture),
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
                DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
                TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
                DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
                TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
                Guid guid => guid.ToString("D"),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            });
    }
}

/// <summary>Emits the existing preorder expression and collection-count contract without recursive calls.</summary>
internal ref struct SafeMigrationSqlExpressionTraversal(SafeMigrationSqlExpression? expression)
{
    private SafeMigrationWorkStack<TraversalFrame> _pending;
    private TraversalFrame _next = new(expression);
    private bool _hasNext = true;

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
        expression = frame.Index < 0 ? frame.Expression : ResumeCollection(frame);
        switch (expression)
        {
            case SafeMigrationSqlUnaryExpression value:
                SetNext(new(value.Operand));
                break;
            case SafeMigrationSqlBinaryExpression value:
                _pending.Push(new(value.Right));
                SetNext(new(value.Left));
                break;
            case SafeMigrationSqlNullTestExpression value:
                SetNext(new(value.Operand));
                break;
            case SafeMigrationSqlBetweenExpression value:
                _pending.Push(new(value.Upper));
                _pending.Push(new(value.Lower));
                SetNext(new(value.Operand));
                break;
            case SafeMigrationSqlInExpression value:
                // WHY: IN serializes its operand before the collection count; moving this count changes fingerprints.
                _pending.Push(new(value, -2));
                SetNext(new(value.Operand));
                break;
            case SafeMigrationSqlFunctionExpression or SafeMigrationSqlCaseExpression:
                SetNext(new(expression, -2));
                break;
            case SafeMigrationSqlCastExpression value:
                SetNext(new(value.Operand));
                break;
            case SafeMigrationSqlCollateExpression value:
                SetNext(new(value.Operand));
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
                        ? frame with { Index = frame.Index + 1, Result = false }
                        : frame with { Result = true });

                return frame.Result ? value.WhenClauses[frame.Index].Result : value.WhenClauses[frame.Index].Condition;
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
