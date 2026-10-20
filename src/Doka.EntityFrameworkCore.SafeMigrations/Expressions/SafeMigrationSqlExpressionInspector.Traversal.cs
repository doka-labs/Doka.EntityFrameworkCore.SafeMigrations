namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationSqlExpressionInspector
{
    /// <summary>Reads one child without allocating a child list or imposing a tree-depth limit.</summary>
    private static SafeMigrationSqlExpression? ChildAt(
        SafeMigrationSqlExpression expression,
        long ordinal,
        bool inValuesFirst = false,
        bool caseElseFirst = false
    ) => expression switch
    {
        SafeMigrationSqlUnaryExpression value when ordinal == 0 => value.Operand,
        SafeMigrationSqlNullTestExpression value when ordinal == 0 => value.Operand,
        SafeMigrationSqlCastExpression value when ordinal == 0 => value.Operand,
        SafeMigrationSqlCollateExpression value when ordinal == 0 => value.Operand,
        SafeMigrationSqlBinaryExpression value when ordinal == 0 => value.Left,
        SafeMigrationSqlBinaryExpression value when ordinal == 1 => value.Right,
        SafeMigrationSqlBetweenExpression value when ordinal == 0 => value.Operand,
        SafeMigrationSqlBetweenExpression value when ordinal == 1 => value.Lower,
        SafeMigrationSqlBetweenExpression value when ordinal == 2 => value.Upper,
        SafeMigrationSqlInExpression value when inValuesFirst => ordinal < value.Values.Count
            ? value.Values[(int)ordinal]
            : ordinal == value.Values.Count
                ? value.Operand
                : null,
        SafeMigrationSqlInExpression value => ordinal == 0
            ? value.Operand
            : ordinal <= value.Values.Count
                ? value.Values[(int)(ordinal - 1)]
                : null,
        SafeMigrationSqlFunctionExpression value when ordinal < value.Arguments.Count => value.Arguments[(int)ordinal],
        SafeMigrationSqlCaseExpression value => CaseChildAt(value, ordinal, caseElseFirst),
        _ => null,
    };

    /// <summary>Keeps CASE rewrite argument evaluation distinct from ordinary SQL-order inspection.</summary>
    private static SafeMigrationSqlExpression? CaseChildAt(
        SafeMigrationSqlCaseExpression expression,
        long ordinal,
        bool elseFirst
    )
    {
        if (elseFirst && expression.ElseResult is not null)
        {
            if (ordinal == 0)
            {
                return expression.ElseResult;
            }

            ordinal--;
        }

        var clauseIndex = ordinal / 2;
        if (clauseIndex < expression.WhenClauses.Count)
        {
            var clause = expression.WhenClauses[(int)clauseIndex];

            return ordinal % 2 == 0 ? clause.Condition : clause.Result;
        }

        return !elseFirst && ordinal == 2L * expression.WhenClauses.Count ? expression.ElseResult : null;
    }

    /// <summary>Resumes one sibling at a time, so wide lists do not consume a work frame per element.</summary>
    private readonly record struct InspectionFrame(
        SafeMigrationSqlExpression Expression,
        long Ordinal
    );

    /// <summary>Traverses preorder with no managed recursion or rental for single-child paths.</summary>
    private ref struct InspectionCursor
    {
        private readonly bool _inValuesFirst;
        private SafeMigrationSqlExpression? _next;
        private SafeMigrationWorkStack<InspectionFrame> _parents;

        /// <summary>Retains only the root and the caller's existing IN visitation order.</summary>
        public InspectionCursor(
            SafeMigrationSqlExpression expression,
            bool inValuesFirst = false
        )
        {
            _next = expression;
            _inValuesFirst = inValuesFirst;
        }

        /// <summary>Returns the next occurrence, without coalescing repeated references to a shared node.</summary>
        public bool TryMoveNext(
            [NotNullWhen(true)] out SafeMigrationSqlExpression? expression
        )
        {
            expression = _next;
            if (expression is null)
            {
                if (!_parents.TryPop(out var parent))
                {
                    return false;
                }

                expression = ChildAt(parent.Expression, parent.Ordinal, _inValuesFirst)!;
                if (ChildAt(parent.Expression, parent.Ordinal + 1, _inValuesFirst) is not null)
                {
                    _parents.Push(parent with { Ordinal = parent.Ordinal + 1 });
                }
            }

            _next = ChildAt(expression, 0, _inValuesFirst);
            if (_next is not null
                && ChildAt(expression, 1, _inValuesFirst) is not null)
            {
                _parents.Push(new InspectionFrame(expression, 1));
            }

            return true;
        }

        /// <summary>Releases pending references even when a consumer short-circuits or throws.</summary>
        public void Dispose() => _parents.Dispose();
    }
}
