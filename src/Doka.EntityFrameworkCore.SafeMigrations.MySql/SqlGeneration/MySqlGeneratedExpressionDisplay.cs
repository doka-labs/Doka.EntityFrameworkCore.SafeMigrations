namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Builds native catalog spellings from token roles without normalizing arbitrary catalog SQL.</summary>
internal static class MySqlGeneratedExpressionDisplay
{
    /// <summary>Leaves unchanged native spellings on their allocation-preserving fast path.</summary>
    internal static IReadOnlyList<MySqlExpressionCatalogCandidate> AdditionalNativeCandidates(
        SafeMigrationSqlExpression? expression,
        MySqlSafeMigrationSqlExpressionRenderer renderer,
        bool isMariaDb,
        bool capturedSource = false
    )
    {
        if (expression is null
            || (!capturedSource && !NeedsNativeDisplay(expression, isMariaDb)))
        {
            return Array.Empty<MySqlExpressionCatalogCandidate>();
        }

        var display = Render(expression, renderer, isMariaDb);

        var candidates = MySqlExpressionCanonicalizer.BuildComparisonCandidates(
            display,
            includeMySqlEncodedDisplay: !isMariaDb);

        if (capturedSource && MySqlCapturedExpressionContract.RequiresStableStrings(expression))
        {
            // WHY: Captured SQL is deliberately retained verbatim. Its parsed
            // delimiter-doubled string values are unambiguous, so retain their
            // ordinary native display alongside the stable structured-API DDL
            // display instead of requiring authors to rewrite historical SQL.
            return candidates
                .Concat(
                    MySqlExpressionCanonicalizer.BuildComparisonCandidates(
                        Render(expression, renderer, isMariaDb, preserveCapturedStringLiterals: true),
                        includeMySqlEncodedDisplay: !isMariaDb))
                .ToArray();
        }

        return candidates;
    }

    /// <summary>Renders one engine-specific display candidate while retaining the authored expression tree.</summary>
    /// <param name="expression">The validated structured generated or CHECK expression.</param>
    /// <param name="renderer">The native provider's identifier and literal renderer.</param>
    /// <param name="isMariaDb">Whether the catalog emits minimally parenthesized MariaDB expressions.</param>
    /// <param name="preserveCapturedStringLiterals">Whether author SQL retains ordinary value tokens.</param>
    /// <returns>A structurally equivalent candidate, never a rewritten catalog value.</returns>
    internal static string Render(
        SafeMigrationSqlExpression expression,
        MySqlSafeMigrationSqlExpressionRenderer renderer,
        bool isMariaDb,
        bool preserveCapturedStringLiterals = false
    )
    {
        var builder = new StringBuilder();
        Append(
            builder,
            expression,
            renderer,
            isMariaDb,
            stableStrings: !preserveCapturedStringLiterals
            && MySqlCapturedExpressionContract.RequiresStableStrings(expression));

        return builder.ToString();
    }

    /// <summary>Uses one output buffer and one continuation per active parent, never the process call stack.</summary>
    private static void Append(
        StringBuilder builder,
        SafeMigrationSqlExpression expression,
        MySqlSafeMigrationSqlExpressionRenderer renderer,
        bool isMariaDb,
        int parentPrecedence = 0,
        bool rightOperand = false,
        bool stableStrings = false
    )
    {
        var pending = new SafeMigrationWorkStack<DisplayFrame>();
        try
        {
            while (true)
            {
                while (expression is SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not } negation)
                {
                    expression = NormalizeNegation(negation.Operand, isMariaDb);
                }

                var precedence = Precedence(expression);

                // WHY: Grouping follows the same parent/child binding as native
                // deparsers; a flat output buffer must not flatten the AST.
                var parentheses = isMariaDb
                    ? precedence < parentPrecedence
                    || (rightOperand && precedence == parentPrecedence)
                    || (precedence == 40 && parentPrecedence == 40)
                    : expression is SafeMigrationSqlBinaryExpression
                        or SafeMigrationSqlUnaryExpression
                        or SafeMigrationSqlNullTestExpression
                        or SafeMigrationSqlBetweenExpression
                        or SafeMigrationSqlInExpression
                        or SafeMigrationSqlCaseExpression
                        or SafeMigrationSqlCollateExpression;

                if (parentheses)
                {
                    builder.Append('(');
                }

                AppendStart(builder, expression, renderer, isMariaDb, stableStrings);
                var frame = new DisplayFrame(expression, precedence, parentheses);
                if (frame.Children.TryMoveNext(out var child))
                {
                    BeforeChild(builder, expression, 0);
                    ChildContext(expression, 0, precedence, out parentPrecedence, out rightOperand);
                    pending.Push(frame);
                    expression = child;

                    continue;
                }

                AppendEnd(builder, expression, parentheses);
                while (true)
                {
                    if (!pending.TryPop(out frame))
                    {
                        return;
                    }

                    var index = frame.Children.NextIndex;
                    if (frame.Children.TryMoveNext(out child))
                    {
                        BeforeChild(builder, frame.Children.Expression, index);
                        ChildContext(
                            frame.Children.Expression,
                            index,
                            frame.Precedence,
                            out parentPrecedence,
                            out rightOperand);
                        pending.Push(frame);
                        expression = child;

                        break;
                    }

                    AppendEnd(builder, frame.Children.Expression, frame.Parentheses);
                }
            }
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Emits prefixes and complete leaves before any child traversal begins.</summary>
    private static void AppendStart(
        StringBuilder builder,
        SafeMigrationSqlExpression expression,
        MySqlSafeMigrationSqlExpressionRenderer renderer,
        bool isMariaDb,
        bool stableStrings
    )
    {
        switch (expression)
        {
            case SafeMigrationSqlLiteralExpression { Value: string or char } when stableStrings:
                builder.Append(renderer.RenderDefault(expression));
                break;
            case SafeMigrationSqlLiteralExpression { Value: string text, StoreType: null }:
                // WHY: Delimiter doubling has identical meaning in either
                // native SQL mode and let's catalog encoders retain the actual
                // value role without depending on a mapping's escape style.
                builder
                    .Append('\'')
                    .Append(text.Replace("'", "''", StringComparison.Ordinal))
                    .Append('\'');
                break;
            case SafeMigrationSqlLiteralExpression { StoreType: null, NumericSourceToken.IsEmpty: false } value:
                // WHY: A captured exponent or large integer token keeps its
                // native numeric domain. Reformatting its CLR value can alter
                // expression arithmetic even when the displayed value agrees.
                builder.Append(value.NumericSourceToken.Span);
                break;
            case SafeMigrationSqlLiteralExpression { Value: bool boolean, StoreType: null } when isMariaDb:
                // WHY: MariaDB deparses Boolean literal leaves as integer 0/1;
                // MySQL retains true/false. This changes a typed literal only,
                // never a quoted string or a caller-authored identifier.
                builder.Append(boolean ? '1' : '0');
                break;
            case SafeMigrationSqlBinaryExpression
                or SafeMigrationSqlNullTestExpression
                or SafeMigrationSqlBetweenExpression
                or SafeMigrationSqlInExpression
                or SafeMigrationSqlCollateExpression:
                break;
            case SafeMigrationSqlUnaryExpression value:
                builder.Append(value.Operator == SafeMigrationSqlUnaryOperator.Not ? "not " : "-");
                break;
            case SafeMigrationSqlFunctionExpression value:
                // WHY: MariaDB persists LOWER's built-in synonym LCASE. This
                // certified function role is distinct from rewriting source
                // text or accepting arbitrary user-defined function aliases.
                builder
                    .Append(
                        isMariaDb && value.Name.Equals("LOWER", StringComparison.OrdinalIgnoreCase)
                            ? "lcase"
                            : value.Name.ToLowerInvariant())
                    .Append('(');
                break;
            case SafeMigrationSqlCaseExpression:
                builder.Append("case");
                break;
            case SafeMigrationSqlCastExpression:
                builder.Append("cast(");
                break;
            default:
                builder.Append(renderer.Render(expression));
                break;
        }
    }

    /// <summary>Emits native separators immediately before the corresponding child.</summary>
    private static void BeforeChild(
        StringBuilder builder,
        SafeMigrationSqlExpression expression,
        int index
    )
    {
        switch (expression)
        {
            case SafeMigrationSqlBinaryExpression value when index == 1:
                builder
                    .Append(' ')
                    .Append(Operator(value.Operator))
                    .Append(' ');
                break;
            case SafeMigrationSqlBetweenExpression value when index == 1:
                builder.Append(value.Negated ? " not between " : " between ");
                break;
            case SafeMigrationSqlBetweenExpression when index == 2:
                builder.Append(" and ");
                break;
            case SafeMigrationSqlInExpression value when index == 1:
                builder.Append(value.Negated ? " not in (" : " in (");
                break;
            case SafeMigrationSqlInExpression when index > 1:
            case SafeMigrationSqlFunctionExpression when index > 0:
                builder.Append(',');
                break;
            case SafeMigrationSqlCaseExpression value:
                builder.Append(
                    index == value.WhenClauses.Count * 2
                        ? " else "
                        : index % 2 == 0
                            ? " when "
                            : " then ");
                break;
        }
    }

    /// <summary>Restores each native deparser's original child-precedence context.</summary>
    private static void ChildContext(
        SafeMigrationSqlExpression expression,
        int index,
        int precedence,
        out int parentPrecedence,
        out bool rightOperand
    )
    {
        parentPrecedence = expression switch
        {
            SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not } => 90,
            SafeMigrationSqlBinaryExpression
                or SafeMigrationSqlUnaryExpression
                or SafeMigrationSqlNullTestExpression
                or SafeMigrationSqlBetweenExpression
                or SafeMigrationSqlCollateExpression => precedence,
            SafeMigrationSqlInExpression when index == 0 => precedence,
            _ => 0,
        };
        rightOperand = index > 0 && expression is SafeMigrationSqlBinaryExpression or SafeMigrationSqlBetweenExpression;
    }

    /// <summary>Completes suffixes after their children, preserving native CAST error priority.</summary>
    private static void AppendEnd(
        StringBuilder builder,
        SafeMigrationSqlExpression expression,
        bool parentheses
    )
    {
        switch (expression)
        {
            case SafeMigrationSqlNullTestExpression value:
                builder.Append(value.Negated ? " is not null" : " is null");
                break;
            case SafeMigrationSqlInExpression or SafeMigrationSqlFunctionExpression:
                builder.Append(')');
                break;
            case SafeMigrationSqlCaseExpression:
                builder.Append(" end");
                break;
            case SafeMigrationSqlCastExpression value:
                builder
                    .Append(" as ")
                    .Append(
                        MySqlSafeMigrationCastTypeRenderer
                            .Render(value.StoreType)
                            .ToLowerInvariant())
                    .Append(')');
                break;
            case SafeMigrationSqlCollateExpression value:
                // WHY: Native metadata exposes the resolved symbol without
                // executable DDL's identifier quoting.
                builder
                    .Append(" collate ")
                    .Append(value.Name.ToLowerInvariant());
                break;
        }

        if (parentheses)
        {
            builder.Append(')');
        }
    }

    /// <summary>Finds native display differences without allocating a visitor or scanning quoted source text.</summary>
    private static bool NeedsNativeDisplay(
        SafeMigrationSqlExpression expression,
        bool isMariaDb
    )
    {
        var traversal = new MySqlExpressionTraversal(expression);
        try
        {
            while (traversal.TryMoveNext(out var current))
            {
                var required = current switch
                {
                    SafeMigrationSqlCaseExpression or SafeMigrationSqlInExpression => true,
                    SafeMigrationSqlLiteralExpression { Value: bool } => isMariaDb,
                    SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not } => true,
                    SafeMigrationSqlBinaryExpression value =>
                        isMariaDb && (!IsAtom(value.Left) || !IsAtom(value.Right)),
                    SafeMigrationSqlNullTestExpression value => isMariaDb && !IsAtom(value.Operand),
                    SafeMigrationSqlBetweenExpression value => isMariaDb
                        && (!IsAtom(value.Operand) || !IsAtom(value.Lower) || !IsAtom(value.Upper)),
                    _ => false,
                };

                if (required)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            traversal.Dispose();
        }
    }

    /// <summary>Detects an operand whose native spelling needs no internal precedence normalization.</summary>
    private static bool IsAtom(
        SafeMigrationSqlExpression expression
    ) => expression is SafeMigrationSqlIdentifierExpression
        or SafeMigrationSqlLiteralExpression
        or SafeMigrationSqlCurrentValueExpression;

    /// <summary>Uses native predicate negation identities without interpreting or replacing raw SQL text.</summary>
    private static SafeMigrationSqlExpression NormalizeNegation(
        SafeMigrationSqlExpression expression,
        bool isMariaDb
    )
    {
        var applications = 1;
        while (expression is SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not } value)
        {
            applications++;
            expression = value.Operand;
        }

        // WHY: N(NOT x) historically means N(N(x)), not simply x. Counting
        // applications preserves SQL's three-valued leaf coercion without a
        // recursive double call for every leading NOT node.
        while (applications-- > 0)
        {
            expression = NegateOnce(expression, isMariaDb);
        }

        return expression;
    }

    /// <summary>Applies one native negation identity without visiting child expressions.</summary>
    private static SafeMigrationSqlExpression NegateOnce(
        SafeMigrationSqlExpression expression,
        bool isMariaDb
    ) => expression switch
    {
        SafeMigrationSqlBinaryExpression { Operator: SafeMigrationSqlBinaryOperator.And } value =>
            SafeMigrationSql.Binary(
                SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value.Left),
                SafeMigrationSqlBinaryOperator.Or,
                SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value.Right)),
        SafeMigrationSqlBinaryExpression { Operator: SafeMigrationSqlBinaryOperator.Or } value =>
            SafeMigrationSql.Binary(
                SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value.Left),
                SafeMigrationSqlBinaryOperator.And,
                SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value.Right)),
        SafeMigrationSqlBinaryExpression
        {
            Operator: >= SafeMigrationSqlBinaryOperator.Equal
            and <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual
        } value => SafeMigrationSql.Binary(value.Left, NegatedComparison(value.Operator), value.Right),
        SafeMigrationSqlNullTestExpression value =>
            new SafeMigrationSqlNullTestExpression(value.Operand, !value.Negated),
        SafeMigrationSqlBetweenExpression value =>
            SafeMigrationSql.Between(value.Operand, value.Lower, value.Upper, !value.Negated),
        SafeMigrationSqlInExpression value => SafeMigrationSql.In(value.Operand, value.Values, !value.Negated),
        _ => isMariaDb
            ? SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal(0))
            : SafeMigrationSql.Binary(SafeMigrationSql.Literal(0), SafeMigrationSqlBinaryOperator.Equal, expression),
    };

    /// <summary>Keeps one active output continuation independently of CASE or function argument width.</summary>
    private struct DisplayFrame(
        SafeMigrationSqlExpression expression,
        int precedence,
        bool parentheses
    )
    {
        /// <summary>The parent and next child whose output resumes after the active operand.</summary>
        internal MySqlExpressionChildCursor Children = new(expression);

        /// <summary>The parent's binding strength for restoring each child context.</summary>
        internal readonly int Precedence = precedence;

        /// <summary>Whether this continuation must close a native grouping delimiter.</summary>
        internal readonly bool Parentheses = parentheses;
    }

    /// <summary>Preserves three-valued comparison semantics when native parsing removes a NOT node.</summary>
    private static SafeMigrationSqlBinaryOperator NegatedComparison(
        SafeMigrationSqlBinaryOperator value
    ) => value switch
    {
        SafeMigrationSqlBinaryOperator.Equal => SafeMigrationSqlBinaryOperator.NotEqual,
        SafeMigrationSqlBinaryOperator.NotEqual => SafeMigrationSqlBinaryOperator.Equal,
        SafeMigrationSqlBinaryOperator.LessThan => SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
        SafeMigrationSqlBinaryOperator.LessThanOrEqual => SafeMigrationSqlBinaryOperator.GreaterThan,
        SafeMigrationSqlBinaryOperator.GreaterThan => SafeMigrationSqlBinaryOperator.LessThanOrEqual,
        SafeMigrationSqlBinaryOperator.GreaterThanOrEqual => SafeMigrationSqlBinaryOperator.LessThan,
        _ => throw new UnreachableException(),
    };

    /// <summary>Retains grouping whenever a child would otherwise bind outside its authored parent.</summary>
    private static int Precedence(
        SafeMigrationSqlExpression expression
    ) => expression switch
    {
        SafeMigrationSqlBinaryExpression { Operator: SafeMigrationSqlBinaryOperator.Or } => 10,
        SafeMigrationSqlBinaryExpression { Operator: SafeMigrationSqlBinaryOperator.And } => 20,
        SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not } => 30,
        SafeMigrationSqlBinaryExpression { Operator: <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual }
            or SafeMigrationSqlNullTestExpression
            or SafeMigrationSqlBetweenExpression
            or SafeMigrationSqlInExpression => 40,
        SafeMigrationSqlBinaryExpression
        {
            Operator: SafeMigrationSqlBinaryOperator.Add or SafeMigrationSqlBinaryOperator.Subtract
        } => 50,
        SafeMigrationSqlBinaryExpression => 60,
        SafeMigrationSqlUnaryExpression => 70,
        SafeMigrationSqlCollateExpression => 80,
        _ => 90,
    };

    /// <summary>Uses exactly the supported operator tokens, never SQL supplied by a caller.</summary>
    private static string Operator(
        SafeMigrationSqlBinaryOperator value
    ) => value switch
    {
        SafeMigrationSqlBinaryOperator.And => "and",
        SafeMigrationSqlBinaryOperator.Or => "or",
        SafeMigrationSqlBinaryOperator.Equal => "=",
        SafeMigrationSqlBinaryOperator.NotEqual => "<>",
        SafeMigrationSqlBinaryOperator.LessThan => "<",
        SafeMigrationSqlBinaryOperator.LessThanOrEqual => "<=",
        SafeMigrationSqlBinaryOperator.GreaterThan => ">",
        SafeMigrationSqlBinaryOperator.GreaterThanOrEqual => ">=",
        SafeMigrationSqlBinaryOperator.Add => "+",
        SafeMigrationSqlBinaryOperator.Subtract => "-",
        SafeMigrationSqlBinaryOperator.Multiply => "*",
        SafeMigrationSqlBinaryOperator.Divide => "/",
        SafeMigrationSqlBinaryOperator.Modulo => "%",
        _ => throw new UnreachableException(),
    };
}
