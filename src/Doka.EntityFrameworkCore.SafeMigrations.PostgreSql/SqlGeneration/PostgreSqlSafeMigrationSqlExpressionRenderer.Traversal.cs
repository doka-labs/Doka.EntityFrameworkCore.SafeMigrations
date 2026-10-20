namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationSqlExpressionRenderer
{
    /// <summary>Retains SQL and callback order with owned continuations instead of recursive rendering.</summary>
    private void AppendIterative(
        IExpressionTextWriter writer, SafeMigrationSqlExpression expression, bool catalogShape,
        bool functionArgument = false, bool deparsed = false)
    {
        var pending = new SafeMigrationWorkStack<RenderStep>();
        var step = new RenderStep(deparsed ? RenderStepKind.Deparsed : RenderStepKind.Expression,
            writer, expression, Flag: functionArgument);

        try
        {
            while (true)
            {
                ExecuteRenderStep(step, catalogShape, ref pending);
                if (!pending.TryPop(out step))
                {
                    return;
                }
            }
        }
        finally
        {
            // WHY: Recursive CASE rendering restored depth in finally. Pending
            // end frames retain that guarantee if mapping or a callback throws.
            while (pending.TryPop(out var abandoned))
            {
                if (abandoned.Kind == RenderStepKind.EndCase)
                {
                    abandoned.Writer.CaseDepth--;
                }
            }

            pending.Dispose();
        }
    }

    /// <summary>Executes one continuation without recursively rendering another node.</summary>
    private void ExecuteRenderStep(RenderStep step, bool catalogShape, ref SafeMigrationWorkStack<RenderStep> pending)
    {
        var writer = step.Writer;
        switch (step.Kind)
        {
            case RenderStepKind.Text:
                writer.Append(step.Text!);
                return;
            case RenderStepKind.CloseParentheses:
                for (var index = 0; index < step.Index; index++)
                {
                    writer.Append(')');
                }

                return;
            case RenderStepKind.NumericComplete:
                ((CatalogExpressionTextWriter)step.Parent!).CompleteNativeNumericOperand(step);
                return;
            case RenderStepKind.CaseResultComplete:
                ((CatalogExpressionTextWriter)step.Parent!).CompleteCaseResult(step);
                return;
            case RenderStepKind.Deparsed:
                if (step.Expression is not SafeMigrationSqlBinaryExpression deparsed)
                {
                    pending.Push(step with { Kind = RenderStepKind.Expression, Flag = false });
                    return;
                }

                if (step.Index == 0)
                {
                    writer.Append('(');
                }

                pending.Push(new RenderStep(RenderStepKind.DeparsedRight, writer, deparsed,
                    Flag: step.Index == 0));
                ScheduleDeparsedOperand(ref pending, writer, deparsed.Left, deparsed.Operator, false);
                return;
            case RenderStepKind.DeparsedRight:
                var deparsedBinary = (SafeMigrationSqlBinaryExpression)step.Expression;
                AppendOperator(writer, deparsedBinary.Operator);
                if (step.Flag)
                {
                    pending.Push(TextStep(writer, ")"));
                }

                ScheduleDeparsedOperand(ref pending, writer, deparsedBinary.Right, deparsedBinary.Operator, true);
                return;
            case RenderStepKind.BinaryRight:
                var binary = (SafeMigrationSqlBinaryExpression)step.Expression;
                AppendOperator(writer, binary.Operator);
                pending.Push(TextStep(writer, ")"));
                ScheduleBinaryOperand(ref pending, writer, binary.Right, binary.Left, binary.Operator, catalogShape);
                return;
            case RenderStepKind.NativeBoolean:
                if (step.Expression is SafeMigrationSqlBinaryExpression flattened
                    && flattened.Operator == (SafeMigrationSqlBinaryOperator)step.Index)
                {
                    // WHY: Only identical Boolean operators form one native
                    // n-ary node. Unlike operators keep their own parentheses.
                    // Interned separators avoid one new string per flattened node.
                    pending.Push(step with { Expression = flattened.Right });
                    pending.Push(TextStep(writer,
                        flattened.Operator == SafeMigrationSqlBinaryOperator.And ? " AND " : " OR "));
                    pending.Push(step with { Expression = flattened.Left });
                }
                else
                {
                    pending.Push(step with { Kind = RenderStepKind.Expression, Flag = false });
                }

                return;
            case RenderStepKind.Between:
                ScheduleBetweenStep(step, catalogShape, ref pending);
                return;
            case RenderStepKind.InValues:
                ScheduleInValues(step, catalogShape, ref pending);
                return;
            case RenderStepKind.FunctionValues:
                var function = (SafeMigrationSqlFunctionExpression)step.Expression;
                if (step.Index == function.Arguments.Count)
                {
                    writer.Append(')');
                    return;
                }

                if (step.Index > 0)
                {
                    writer.Append(", ");
                }

                pending.Push(step with { Index = step.Index + 1 });
                pending.Push(new RenderStep(RenderStepKind.Expression, writer, function.Arguments[step.Index],
                    Flag: true));
                return;
            case RenderStepKind.CaseClauses:
                ScheduleCaseClause(step, catalogShape, ref pending);
                return;
            case RenderStepKind.CaseResult:
                var conditional = (SafeMigrationSqlCaseExpression)step.Expression;
                var result = step.Index < conditional.WhenClauses.Count
                    ? conditional.WhenClauses[step.Index].Result : conditional.ElseResult!;
                if (writer is not CatalogExpressionTextWriter { NativeLiteralForms: true } catalog
                    || !catalog.TryScheduleCaseResult(result, conditional, ref pending))
                {
                    pending.Push(new RenderStep(RenderStepKind.Expression, writer, result));
                }

                return;
            case RenderStepKind.EndCase:
                if (writer.NativeLiteralForms)
                {
                    AppendCaseLine(writer, step.Index);
                }
                else
                {
                    writer.Append(' ');
                }

                writer.Append("END");
                writer.CaseDepth--;
                return;
            case RenderStepKind.Collation:
                var collation = (SafeMigrationSqlCollateExpression)step.Expression;
                writer.Append(" COLLATE ");
                writer.AppendCollation(collation.Name, collation.Schema);
                if (step.Flag)
                {
                    writer.Append(')');
                }

                return;
            case RenderStepKind.Expression:
                ScheduleExpression(step, catalogShape, ref pending);
                return;
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Schedules children while evaluating native special forms in their original order.</summary>
    private void ScheduleExpression(RenderStep step, bool catalogShape, ref SafeMigrationWorkStack<RenderStep> pending)
    {
        var writer = step.Writer;
        switch (step.Expression)
        {
            case SafeMigrationSqlUnaryExpression unary:
                if (writer.NativeLiteralForms && TryAppendCapturedNumericUnary(writer, unary))
                {
                    return;
                }

                if (writer.NativeLiteralForms && unary.Operator == SafeMigrationSqlUnaryOperator.Negate)
                {
                    // WHY: A failed signed-token proof applies to this entire
                    // consecutive run. Do not rescan its suffix at each node.
                    var operand = unary.Operand;
                    var count = 1;
                    writer.Append("(- ");
                    while (operand is SafeMigrationSqlUnaryExpression
                        { Operator: SafeMigrationSqlUnaryOperator.Negate } nested)
                    {
                        writer.Append("(- ");
                        count++;
                        operand = nested.Operand;
                    }

                    pending.Push(new RenderStep(RenderStepKind.CloseParentheses, writer, operand, Index: count));
                    pending.Push(new RenderStep(RenderStepKind.Expression, writer, operand));
                    return;
                }

                writer.Append(unary.Operator == SafeMigrationSqlUnaryOperator.Not ? "(NOT "
                    : writer.NativeLiteralForms ? "(- " : "(-");
                pending.Push(TextStep(writer, ")"));
                pending.Push(new RenderStep(RenderStepKind.Expression, writer, unary.Operand));
                return;
            case SafeMigrationSqlBinaryExpression binary:
                writer.Append('(');
                if (writer.NativeLiteralForms
                    && binary.Operator is SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or)
                {
                    pending.Push(TextStep(writer, ")"));
                    pending.Push(new RenderStep(RenderStepKind.NativeBoolean, writer, binary,
                        Index: (int)binary.Operator));
                }
                else
                {
                    pending.Push(new RenderStep(RenderStepKind.BinaryRight, writer, binary));
                    ScheduleBinaryOperand(ref pending, writer, binary.Left, binary.Right,
                        binary.Operator, catalogShape);
                }

                return;
            case SafeMigrationSqlNullTestExpression test:
                writer.Append('(');
                pending.Push(TextStep(writer, test.Negated ? " IS NOT NULL)" : " IS NULL)"));
                pending.Push(new RenderStep(RenderStepKind.Expression, writer, test.Operand));
                return;
            case SafeMigrationSqlBetweenExpression between:
                writer.Append(catalogShape ? "((" : "(");
                pending.Push(new RenderStep(RenderStepKind.Between, writer, between, Index: 1));
                pending.Push(new RenderStep(RenderStepKind.Expression, writer, between.Operand));
                return;
            case SafeMigrationSqlInExpression list:
                if (catalogShape && writer.NativeLiteralForms && list.Values.Count == 1)
                {
                    // WHY: Native singleton IN is scalar equality, not ANY.
                    pending.Push(new RenderStep(RenderStepKind.Expression, writer,
                        new SafeMigrationSqlBinaryExpression(list.Operand,
                            list.Negated ? SafeMigrationSqlBinaryOperator.NotEqual
                                : SafeMigrationSqlBinaryOperator.Equal,
                            list.Values[0])));
                    return;
                }

                writer.Append('(');
                pending.Push(new RenderStep(RenderStepKind.InValues, writer, list, Index: -1));
                pending.Push(new RenderStep(RenderStepKind.Expression, writer, list.Operand));
                return;
            case SafeMigrationSqlFunctionExpression function:
                writer.Append(writer.NativeLiteralForms && IsNativeSpecialForm(function.Name)
                    ? function.Name.ToUpperInvariant() : function.Name.ToLowerInvariant());
                writer.Append('(');
                if (writer.TryAppendNativeLowerOperand(function))
                {
                    writer.Append(')');
                }
                else
                {
                    pending.Push(new RenderStep(RenderStepKind.FunctionValues, writer, function));
                }

                return;
            case SafeMigrationSqlCaseExpression conditional:
                var indentation = writer.CaseDepth * 4;
                writer.CaseDepth++;
                pending.Push(new RenderStep(RenderStepKind.EndCase, writer, conditional, Index: indentation));
                if (writer.NativeLiteralForms)
                {
                    writer.TrimTrailingSpace();
                    AppendCaseLine(writer, indentation);
                }

                writer.Append("CASE");
                pending.Push(new RenderStep(RenderStepKind.CaseClauses, writer, conditional, Index: 0));
                return;
            case SafeMigrationSqlCastExpression cast:
                if (cast.UsesSqlServerConvert)
                {
                    throw new NotSupportedException("Type-first CONVERT syntax requires SQL Server.");
                }

                var castType = FindCanonicalStoreType(cast.StoreType);
                if (writer.NativeLiteralForms && TryAppendNativeIntegerConstant(writer, cast, castType))
                {
                    return;
                }

                writer.Append(catalogShape ? "(" : "CAST(");
                pending.Push(TextStep(writer, catalogShape ? ")::" + castType : " AS " + castType + ")"));
                pending.Push(new RenderStep(RenderStepKind.Expression, writer, cast.Operand));
                return;
            case SafeMigrationSqlCollateExpression collation:
                var parentheses = writer.ParenthesizeCollations || writer.NativeLiteralForms && !step.Flag;
                if (parentheses)
                {
                    writer.Append('(');
                }

                pending.Push(new RenderStep(RenderStepKind.Collation, writer, collation, Flag: parentheses));
                pending.Push(new RenderStep(RenderStepKind.Expression, writer, collation.Operand));
                return;
            default:
                AppendLeaf(writer, step.Expression, catalogShape);
                return;
        }
    }

    /// <summary>Defers native child writers instead of recursively rendering from coercion helpers.</summary>
    private static void ScheduleBinaryOperand(
        ref SafeMigrationWorkStack<RenderStep> pending, IExpressionTextWriter writer,
        SafeMigrationSqlExpression operand, SafeMigrationSqlExpression other,
        SafeMigrationSqlBinaryOperator operation, bool catalogShape)
    {
        if (operation is not (SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or)
            && writer is CatalogExpressionTextWriter { NativeLiteralForms: true } catalog
            && catalog.TryScheduleNativeNumericOperand(operand, other, ref pending))
        {
            return;
        }

        if (TryAppendNativeTextComparisonMember(writer, operand, other, operation)
            || operand is SafeMigrationSqlLiteralExpression literal
            && other is SafeMigrationSqlIdentifierExpression column
            && writer.TryAppendNativeUnknownLiteral(literal, column))
        {
            return;
        }

        pending.Push(new RenderStep(RenderStepKind.Expression, writer, operand));
    }

    /// <summary>Preserves deparser precedence without recursively descending binary spines.</summary>
    private static void ScheduleDeparsedOperand(
        ref SafeMigrationWorkStack<RenderStep> pending, IExpressionTextWriter writer,
        SafeMigrationSqlExpression operand, SafeMigrationSqlBinaryOperator parentOperator, bool right)
    {
        if (operand is not SafeMigrationSqlBinaryExpression binary)
        {
            pending.Push(new RenderStep(RenderStepKind.Expression, writer, operand));
            return;
        }

        var childPrecedence = BinaryPrecedence(binary.Operator);
        var parentPrecedence = BinaryPrecedence(parentOperator);
        var parentheses = childPrecedence < parentPrecedence || right && childPrecedence <= parentPrecedence;

        pending.Push(new RenderStep(RenderStepKind.Deparsed, writer, binary, Index: parentheses ? 0 : 1));
    }

    /// <summary>Visits each BETWEEN occurrence separately, preserving native parameter callback order.</summary>
    private static void ScheduleBetweenStep(
        RenderStep step, bool catalogShape, ref SafeMigrationWorkStack<RenderStep> pending)
    {
        var writer = step.Writer;
        var between = (SafeMigrationSqlBetweenExpression)step.Expression;
        SafeMigrationSqlExpression operand;
        if (step.Index == 1)
        {
            writer.Append(catalogShape ? between.Negated ? " < " : " >= "
                : between.Negated ? " NOT BETWEEN " : " BETWEEN ");
            operand = between.Lower;
        }
        else if (step.Index == 2 && catalogShape)
        {
            writer.Append(between.Negated ? ") OR (" : ") AND (");
            operand = between.Operand;
        }
        else if (step.Index == 2 || step.Index == 3 && catalogShape)
        {
            writer.Append(catalogShape ? between.Negated ? " > " : " <= " : " AND ");
            operand = between.Upper;
        }
        else
        {
            writer.Append(catalogShape ? "))" : ")");
            return;
        }

        pending.Push(step with { Index = step.Index + 1 });
        pending.Push(new RenderStep(RenderStepKind.Expression, writer, operand));
    }

    /// <summary>Streams IN arguments rather than pushing the entire authored list onto the work stack.</summary>
    private static void ScheduleInValues(
        RenderStep step, bool catalogShape, ref SafeMigrationWorkStack<RenderStep> pending)
    {
        var writer = step.Writer;
        var list = (SafeMigrationSqlInExpression)step.Expression;
        var index = step.Index;
        if (index < 0)
        {
            writer.Append(catalogShape ? list.Negated ? " <> ALL (ARRAY[" : " = ANY (ARRAY["
                : list.Negated ? " NOT IN (" : " IN (");
            index = 0;
        }

        if (index == list.Values.Count)
        {
            writer.Append(catalogShape ? "]))" : "))");
            return;
        }

        if (index > 0)
        {
            writer.Append(", ");
        }

        pending.Push(step with { Index = index + 1 });
        if (!catalogShape || list.Values[index] is not SafeMigrationSqlLiteralExpression literal
            || list.Operand is not SafeMigrationSqlIdentifierExpression column
            || !writer.TryAppendNativeUnknownLiteral(literal, column))
        {
            pending.Push(new RenderStep(RenderStepKind.Expression, writer, list.Values[index]));
        }
    }

    /// <summary>Retains WHEN/THEN/ELSE rendering order and one owned depth-restoration frame per CASE.</summary>
    private static void ScheduleCaseClause(
        RenderStep step, bool catalogShape, ref SafeMigrationWorkStack<RenderStep> pending)
    {
        var writer = step.Writer;
        var conditional = (SafeMigrationSqlCaseExpression)step.Expression;
        if (step.Index > conditional.WhenClauses.Count
            || step.Index == conditional.WhenClauses.Count && conditional.ElseResult is null
            && !writer.NativeLiteralForms)
        {
            return;
        }

        if (writer.NativeLiteralForms)
        {
            AppendCaseLine(writer, writer.CaseDepth * 4);
        }
        else
        {
            writer.Append(' ');
        }

        if (step.Index == conditional.WhenClauses.Count)
        {
            writer.Append("ELSE ");
            if (conditional.ElseResult is null)
            {
                writer.AppendImplicitCaseNull(conditional);
            }
            else
            {
                pending.Push(new RenderStep(RenderStepKind.CaseResult, writer, conditional, Index: step.Index));
            }

            return;
        }

        writer.Append("WHEN ");
        pending.Push(step with { Index = step.Index + 1 });
        pending.Push(new RenderStep(RenderStepKind.CaseResult, writer, conditional, Index: step.Index));
        pending.Push(TextStep(writer, " THEN "));
        pending.Push(new RenderStep(RenderStepKind.Expression, writer,
            conditional.WhenClauses[step.Index].Condition));
    }

    /// <summary>Appends one operator exactly where the recursive implementation visited it.</summary>
    private static void AppendOperator(IExpressionTextWriter writer, SafeMigrationSqlBinaryOperator operation)
    {
        writer.Append(' ');
        writer.Append(BinaryOperator(operation));
        writer.Append(' ');
    }

    private static RenderStep TextStep(IExpressionTextWriter writer, string text)
        => new(RenderStepKind.Text, writer, null!, Text: text);

    private enum RenderStepKind
    {
        Expression, Text, CloseParentheses, Deparsed, DeparsedRight, BinaryRight, NativeBoolean, Between, InValues,
        FunctionValues, CaseClauses, CaseResult, EndCase, Collation, NumericComplete, CaseResultComplete,
    }

    /// <summary>Stores value-type continuations without closures or per-operand task allocations.</summary>
    private readonly record struct RenderStep(
        RenderStepKind Kind, IExpressionTextWriter Writer, SafeMigrationSqlExpression Expression,
        int Index = 0, bool Flag = false, string? Text = null, IExpressionTextWriter? Parent = null,
        string? Input = null, string? Common = null);
}
