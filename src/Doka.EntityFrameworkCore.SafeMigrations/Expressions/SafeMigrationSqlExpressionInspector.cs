namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Inspects and transforms provider-neutral SQL expression trees without
/// rendering provider SQL.
/// </summary>
internal static class SafeMigrationSqlExpressionInspector
{
    /// <summary>Collects the terminal identifier parts referenced by an expression.</summary>
    /// <param name="expression">The expression tree to inspect.</param>
    /// <param name="identifiers">The destination set for distinct identifier names.</param>
    public static void CollectIdentifiers(
        SafeMigrationSqlExpression expression,
        ISet<string> identifiers
    )
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(identifiers);

        // Advance through single-child and final branches iteratively. The
        // bounded parser still limits authored depth, while this shape avoids
        // adding one recursive frame for every right-associated binary node.
        while (true)
        {
            switch (expression)
            {
                case SafeMigrationSqlIdentifierExpression value:
                    identifiers.Add(value.Parts[^1]);
                    break;
                case SafeMigrationSqlLiteralExpression:
                case SafeMigrationSqlCurrentValueExpression:
                case SafeMigrationSqlProviderFragmentExpression:
                case SafeMigrationSqlOpaqueExpression:
                    break;
                case SafeMigrationSqlUnaryExpression value:
                    expression = value.Operand;
                    continue;
                case SafeMigrationSqlBinaryExpression value:
                    CollectIdentifiers(value.Left, identifiers);
                    expression = value.Right;
                    continue;
                case SafeMigrationSqlNullTestExpression value:
                    expression = value.Operand;
                    continue;
                case SafeMigrationSqlBetweenExpression value:
                    CollectIdentifiers(value.Operand, identifiers);
                    CollectIdentifiers(value.Lower, identifiers);
                    expression = value.Upper;
                    continue;
                case SafeMigrationSqlInExpression value:
                    CollectIdentifiers(value.Operand, identifiers);
                    foreach (var candidate in value.Values)
                    {
                        CollectIdentifiers(candidate, identifiers);
                    }

                    break;
                case SafeMigrationSqlFunctionExpression value:
                    foreach (var argument in value.Arguments)
                    {
                        CollectIdentifiers(argument, identifiers);
                    }

                    break;
                case SafeMigrationSqlCastExpression value:
                    expression = value.Operand;
                    continue;
                case SafeMigrationSqlCollateExpression value:
                    expression = value.Operand;
                    continue;
                default:
                    throw new UnreachableException();
            }

            break;
        }
    }

    /// <summary>Determines whether an expression references one exact identifier part.</summary>
    /// <param name="expression">The expression tree to inspect.</param>
    /// <param name="identifier">The exact terminal identifier part.</param>
    /// <returns><see langword="true" /> when the identifier is referenced.</returns>
    public static bool ReferencesIdentifier(
        SafeMigrationSqlExpression expression,
        string identifier
    )
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        CollectIdentifiers(expression, identifiers);

        return identifiers.Contains(identifier);
    }

    /// <summary>Determines whether every node has provider-neutral structural semantics.</summary>
    /// <param name="expression">The expression tree to inspect.</param>
    /// <returns><see langword="true" /> when structural comparison is safe.</returns>
    public static bool IsStructurallyComparable(
        SafeMigrationSqlExpression expression
    ) => expression switch
    {
        SafeMigrationSqlIdentifierExpression => true,
        SafeMigrationSqlLiteralExpression => true,
        SafeMigrationSqlUnaryExpression value => IsStructurallyComparable(value.Operand),
        SafeMigrationSqlBinaryExpression value =>
            IsStructurallyComparable(value.Left) && IsStructurallyComparable(value.Right),
        SafeMigrationSqlNullTestExpression value => IsStructurallyComparable(value.Operand),
        SafeMigrationSqlBetweenExpression value =>
            IsStructurallyComparable(value.Operand)
            && IsStructurallyComparable(value.Lower)
            && IsStructurallyComparable(value.Upper),
        SafeMigrationSqlInExpression value =>
            IsStructurallyComparable(value.Operand) && value.Values.All(IsStructurallyComparable),
        SafeMigrationSqlFunctionExpression value => value.Arguments.All(IsStructurallyComparable),
        SafeMigrationSqlCastExpression value => IsStructurallyComparable(value.Operand),
        SafeMigrationSqlCollateExpression value => IsStructurallyComparable(value.Operand),
        SafeMigrationSqlCurrentValueExpression => true,
        SafeMigrationSqlProviderFragmentExpression => false,
        SafeMigrationSqlOpaqueExpression => false,
        _ => throw new UnreachableException(),
    };

    /// <summary>Renames matching identifier parts throughout a structural expression.</summary>
    /// <param name="expression">The expression tree to transform.</param>
    /// <param name="source">The exact identifier part to replace.</param>
    /// <param name="target">The replacement identifier part.</param>
    /// <returns>The transformed expression tree.</returns>
    public static SafeMigrationSqlExpression RenameIdentifier(
        SafeMigrationSqlExpression expression,
        string source,
        string target
    ) => RenameIdentifiers(expression, source, target, StringComparer.Ordinal, columnOnly: false);

    /// <summary>Renames terminal column identifiers while preserving all owner qualifiers.</summary>
    /// <param name="expression">A column-dependent expression tree to transform.</param>
    /// <param name="source">The physical column member to replace.</param>
    /// <param name="target">The replacement column member.</param>
    /// <param name="columnComparer">The provider-qualified column-member identity comparer.</param>
    /// <returns>The transformed tree, with opaque fragments left unproven after the rename.</returns>
    /// <remarks>Use only where terminal identifier parts represent column references.</remarks>
    internal static SafeMigrationSqlExpression RenameColumnIdentifier(
        SafeMigrationSqlExpression expression,
        string source,
        string target,
        IEqualityComparer<string> columnComparer
    ) => RenameIdentifiers(expression, source, target, columnComparer, columnOnly: true);

    /// <summary>Shares expression traversal while retaining the caller's explicit identifier roles.</summary>
    private static SafeMigrationSqlExpression RenameIdentifiers(
        SafeMigrationSqlExpression expression,
        string source,
        string target,
        IEqualityComparer<string> comparer,
        bool columnOnly
    )
    {
        SafeMigrationSqlExpression RenameChild(SafeMigrationSqlExpression child) =>
            RenameIdentifiers(child, source, target, comparer, columnOnly);

        // WHY: Column-dependent identifiers end with the column member. Earlier
        // parts qualify its owner and must not inherit column case semantics.
        return expression switch
        {
            SafeMigrationSqlIdentifierExpression value =>
                new SafeMigrationSqlIdentifierExpression(value.Parts.Select((part, ordinal) =>
                    (!columnOnly || ordinal == value.Parts.Count - 1) && comparer.Equals(part, source)
                        ? target : part)),
            SafeMigrationSqlLiteralExpression => expression,
            SafeMigrationSqlUnaryExpression value =>
                new SafeMigrationSqlUnaryExpression(value.Operator, RenameChild(value.Operand)),
            SafeMigrationSqlBinaryExpression value =>
                new SafeMigrationSqlBinaryExpression(RenameChild(value.Left), value.Operator, RenameChild(value.Right)),
            SafeMigrationSqlNullTestExpression value =>
                new SafeMigrationSqlNullTestExpression(RenameChild(value.Operand), value.Negated),
            SafeMigrationSqlBetweenExpression value =>
                new SafeMigrationSqlBetweenExpression(
                    RenameChild(value.Operand),
                    RenameChild(value.Lower),
                    RenameChild(value.Upper),
                    value.Negated),
            SafeMigrationSqlInExpression value =>
                new SafeMigrationSqlInExpression(RenameChild(value.Operand), value.Values.Select(RenameChild),
                    value.Negated),
            SafeMigrationSqlFunctionExpression value =>
                new SafeMigrationSqlFunctionExpression(value.Name, value.Arguments.Select(RenameChild)),
            SafeMigrationSqlCastExpression value =>
                new SafeMigrationSqlCastExpression(RenameChild(value.Operand), value.StoreType),
            SafeMigrationSqlCollateExpression value =>
                new SafeMigrationSqlCollateExpression(RenameChild(value.Operand), value.Name, value.Schema),
            SafeMigrationSqlCurrentValueExpression => expression,
            SafeMigrationSqlProviderFragmentExpression value => SafeMigrationSql.OpaqueAfterRename(value.Sql),
            SafeMigrationSqlOpaqueExpression value => SafeMigrationSql.OpaqueAfterRename(value.Sql),
            _ => throw new UnreachableException(),
        };
    }
}
