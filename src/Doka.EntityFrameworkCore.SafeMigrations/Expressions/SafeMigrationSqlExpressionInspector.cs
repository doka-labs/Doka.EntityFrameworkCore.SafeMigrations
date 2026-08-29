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
    ) => expression switch
    {
        SafeMigrationSqlIdentifierExpression value =>
            new SafeMigrationSqlIdentifierExpression(
                value.Parts.Select(part => StringComparer.Ordinal.Equals(part, source) ? target : part)),
        SafeMigrationSqlLiteralExpression => expression,
        SafeMigrationSqlUnaryExpression value =>
            new SafeMigrationSqlUnaryExpression(value.Operator, RenameIdentifier(value.Operand, source, target)),
        SafeMigrationSqlBinaryExpression value =>
            new SafeMigrationSqlBinaryExpression(
                RenameIdentifier(value.Left, source, target),
                value.Operator,
                RenameIdentifier(value.Right, source, target)),
        SafeMigrationSqlNullTestExpression value =>
            new SafeMigrationSqlNullTestExpression(RenameIdentifier(value.Operand, source, target), value.Negated),
        SafeMigrationSqlBetweenExpression value =>
            new SafeMigrationSqlBetweenExpression(
                RenameIdentifier(value.Operand, source, target),
                RenameIdentifier(value.Lower, source, target),
                RenameIdentifier(value.Upper, source, target),
                value.Negated),
        SafeMigrationSqlInExpression value =>
            new SafeMigrationSqlInExpression(
                RenameIdentifier(value.Operand, source, target),
                value.Values.Select(item => RenameIdentifier(item, source, target)),
                value.Negated),
        SafeMigrationSqlFunctionExpression value =>
            new SafeMigrationSqlFunctionExpression(
                value.Name,
                value.Arguments.Select(item => RenameIdentifier(item, source, target))),
        SafeMigrationSqlCastExpression value =>
            new SafeMigrationSqlCastExpression(RenameIdentifier(value.Operand, source, target), value.StoreType),
        SafeMigrationSqlCollateExpression value => new SafeMigrationSqlCollateExpression(
            RenameIdentifier(value.Operand, source, target),
            value.Name,
            value.Schema),
        SafeMigrationSqlCurrentValueExpression => expression,
        SafeMigrationSqlProviderFragmentExpression value => SafeMigrationSql.OpaqueAfterRename(value.Sql),
        SafeMigrationSqlOpaqueExpression value => SafeMigrationSql.OpaqueAfterRename(value.Sql),
        _ => throw new UnreachableException(),
    };
}
