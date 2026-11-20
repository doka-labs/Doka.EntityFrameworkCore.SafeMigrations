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
                                || !LiteralEquivalent(first.BorrowedValue, second.BorrowedValue))
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
                        writer.Add(
                            value.NumericSourceToken.IsEmpty
                                ? "literal"
                                : "sql_numeric_literal");

                        if (!value.NumericSourceToken.IsEmpty)
                        {
                            writer.Add(value.NumericSourceToken.Span);
                        }

                        writer.Add(value.StoreType);
                        WriteLiteral(writer, value.BorrowedValue);
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
                        writer.Add(
                            value.UsesSqlServerConvert
                                ? "sqlserver_convert"
                                : "cast");

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
                        ? frame with
                        {
                            Index = frame.Index + 1,
                            Result = false,
                        }
                        : frame with { Result = true });

                left = frame.Result
                    ? firstClause.Result
                    : firstClause.Condition;

                right = frame.Result
                    ? secondClause.Result
                    : secondClause.Condition;

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

    private static bool LiteralEquivalent(
        object? left,
        object? right
    ) => (left, right) switch
    {
        (byte[] leftBytes, byte[] rightBytes) => leftBytes
            .AsSpan()
            .SequenceEqual(rightBytes),
        _ => Equals(left, right),
    };

    private static void WriteStrings(
        CanonicalHashWriter writer,
        IReadOnlyList<string> values
    )
    {
        writer.Add(values.Count);

        // WHY: The immutable ordered snapshot exposes indexing without an interface enumerator.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < values.Count; index++)
        {
            writer.Add(values[index]);
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

        writer.Add(value.GetType()
                .FullName
            ?? value.GetType()
                .Name);

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

    /// <summary>Retains a branch or collection continuation without copying or mutating expression nodes.</summary>
    private readonly record struct ComparisonFrame(
        SafeMigrationSqlExpression? Left,
        SafeMigrationSqlExpression? Right,
        int Index = -1,
        bool Result = false
    );
}
