namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationSqlExpressionInspector
{
    /// <summary>Reconstructs renamed expressions without using the managed call stack.</summary>
    private static SafeMigrationSqlExpression RenameIdentifiers(
        SafeMigrationSqlExpression expression,
        string source,
        string target,
        IEqualityComparer<string> comparer,
        bool columnOnly
    )
    {
        var parents = default(SafeMigrationWorkStack<RenameFrame>);
        var current = expression;

        try
        {
            while (true)
            {
                // WHY: The old CASE constructor evaluated ELSE before enumerating
                // its lazy WHEN clauses. Comparers can observe that order.
                var child = ChildAt(current, 0, caseElseFirst: true);
                if (child is not null)
                {
                    parents.Push(new RenameFrame(current));
                    current = child;
                    continue;
                }

                var result = RenameLeaf(current, source, target, comparer, columnOnly);
                while (true)
                {
                    if (!parents.TryPop(out var parent))
                    {
                        return result;
                    }

                    parent.Accept(result);
                    child = ChildAt(parent.Expression, ++parent.Ordinal, caseElseFirst: true);
                    if (child is not null)
                    {
                        parents.Push(parent);
                        current = child;
                        break;
                    }

                    result = parent.Complete();
                }
            }
        }
        finally
        {
            parents.Dispose();
        }
    }

    /// <summary>Retains leaf reuse and invalidates opaque SQL rather than rewriting untyped tokens.</summary>
    private static SafeMigrationSqlExpression RenameLeaf(
        SafeMigrationSqlExpression expression,
        string source,
        string target,
        IEqualityComparer<string> comparer,
        bool columnOnly
    ) => expression switch
    {
        SafeMigrationSqlIdentifierExpression value => RenameParts(value, source, target, comparer, columnOnly),
        SafeMigrationSqlLiteralExpression or SafeMigrationSqlCurrentValueExpression => expression,
        SafeMigrationSqlProviderFragmentExpression value => SafeMigrationSql.OpaqueAfterRename(value.Sql),
        SafeMigrationSqlOpaqueExpression value => SafeMigrationSql.OpaqueAfterRename(value.Sql),
        SafeMigrationSqlFunctionExpression value => new SafeMigrationSqlFunctionExpression(value.Name),
        _ => throw new UnreachableException(),
    };

    /// <summary>Applies column comparison only to terminal members, never to owner qualifiers.</summary>
    private static SafeMigrationSqlIdentifierExpression RenameParts(
        SafeMigrationSqlIdentifierExpression expression,
        string source,
        string target,
        IEqualityComparer<string> comparer,
        bool columnOnly
    )
    {
        var parts = new string[expression.Parts.Count];
        for (var ordinal = 0; ordinal < parts.Length; ordinal++)
        {
            var part = expression.Parts[ordinal];
            parts[ordinal] = (!columnOnly || ordinal == parts.Length - 1) && comparer.Equals(part, source)
                ? target
                : part;
        }

        return new SafeMigrationSqlIdentifierExpression(parts);
    }

    /// <summary>Retains completed children only for one active reconstruction branch.</summary>
    private struct RenameFrame
    {
        private SafeMigrationSqlExpression? _first;
        private SafeMigrationSqlExpression? _second;
        private SafeMigrationSqlExpression? _third;
        private readonly SafeMigrationSqlExpression[]? _values;
        private readonly SafeMigrationSqlCaseWhenClause[]? _clauses;

        /// <summary>Allocates child-result arrays only for variable-width immutable constructors.</summary>
        public RenameFrame(
            SafeMigrationSqlExpression expression
        )
        {
            Expression = expression;
            _values = expression switch
            {
                SafeMigrationSqlInExpression value => new SafeMigrationSqlExpression[value.Values.Count],
                SafeMigrationSqlFunctionExpression value => new SafeMigrationSqlExpression[value.Arguments.Count],
                _ => null,
            };
            _clauses = expression is SafeMigrationSqlCaseExpression @case
                ? new SafeMigrationSqlCaseWhenClause[@case.WhenClauses.Count]
                : null;
        }

        /// <summary>Gets the original node and its unchanged operator, token and qualification facets.</summary>
        public SafeMigrationSqlExpression Expression { get; }

        /// <summary>Gets or sets the child whose result is being completed.</summary>
        public long Ordinal { get; set; }

        /// <summary>Stores one completed child without an intermediate result stack for all siblings.</summary>
        public void Accept(
            SafeMigrationSqlExpression result
        )
        {
            switch (Expression)
            {
                case SafeMigrationSqlFunctionExpression:
                    _values![(int)Ordinal] = result;
                    break;
                case SafeMigrationSqlInExpression when Ordinal > 0:
                    _values![(int)(Ordinal - 1)] = result;
                    break;
                case SafeMigrationSqlCaseExpression value:
                    AcceptCase(value, result);
                    break;
                default:
                    if (Ordinal == 0)
                    {
                        _first = result;
                    }
                    else if (Ordinal == 1)
                    {
                        _second = result;
                    }
                    else
                    {
                        _third = result;
                    }

                    break;
            }
        }

        /// <summary>Pairs WHEN results after retaining the separately evaluated ELSE result.</summary>
        private void AcceptCase(
            SafeMigrationSqlCaseExpression expression,
            SafeMigrationSqlExpression result
        )
        {
            var clauseOrdinal = Ordinal;
            if (expression.ElseResult is not null)
            {
                if (clauseOrdinal == 0)
                {
                    _first = result;

                    return;
                }

                clauseOrdinal--;
            }

            if (clauseOrdinal % 2 == 0)
            {
                _second = result;
            }
            else
            {
                _clauses![(int)(clauseOrdinal / 2)] = new SafeMigrationSqlCaseWhenClause(_second!, result);
            }
        }

        /// <summary>Rebuilds exactly the previously constructed node form after its children complete.</summary>
        public readonly SafeMigrationSqlExpression Complete() => Expression switch
        {
            SafeMigrationSqlUnaryExpression value => new SafeMigrationSqlUnaryExpression(value.Operator, _first!),
            SafeMigrationSqlBinaryExpression value =>
                new SafeMigrationSqlBinaryExpression(_first!, value.Operator, _second!),
            SafeMigrationSqlNullTestExpression value => new SafeMigrationSqlNullTestExpression(_first!, value.Negated),
            SafeMigrationSqlBetweenExpression value =>
                new SafeMigrationSqlBetweenExpression(_first!, _second!, _third!, value.Negated),
            SafeMigrationSqlInExpression value => new SafeMigrationSqlInExpression(_first!, _values!, value.Negated),
            SafeMigrationSqlFunctionExpression value => new SafeMigrationSqlFunctionExpression(value.Name, _values!),
            SafeMigrationSqlCaseExpression => new SafeMigrationSqlCaseExpression(_clauses!, _first),
            SafeMigrationSqlCastExpression value => new SafeMigrationSqlCastExpression(
                _first!,
                value.StoreType,
                value.UsesSqlServerConvert),
            SafeMigrationSqlCollateExpression value => new SafeMigrationSqlCollateExpression(
                _first!,
                value.Name,
                value.Schema),
            _ => throw new UnreachableException(),
        };
    }
}
