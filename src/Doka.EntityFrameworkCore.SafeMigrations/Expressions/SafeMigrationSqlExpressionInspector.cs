namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Inspects and transforms provider-neutral SQL expression trees without
/// rendering provider SQL.
/// </summary>
internal static partial class SafeMigrationSqlExpressionInspector
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

        var cursor = new InspectionCursor(expression);

        try
        {
            while (cursor.TryMoveNext(out var current))
            {
                switch (current)
                {
                    case SafeMigrationSqlIdentifierExpression value:
                        identifiers.Add(value.Parts[^1]);
                        break;
                    case SafeMigrationSqlLiteralExpression
                        or SafeMigrationSqlCurrentValueExpression
                        or SafeMigrationSqlProviderFragmentExpression
                        or SafeMigrationSqlOpaqueExpression
                        or SafeMigrationSqlUnaryExpression
                        or SafeMigrationSqlBinaryExpression
                        or SafeMigrationSqlNullTestExpression
                        or SafeMigrationSqlBetweenExpression
                        or SafeMigrationSqlInExpression
                        or SafeMigrationSqlFunctionExpression
                        or SafeMigrationSqlCaseExpression
                        or SafeMigrationSqlCastExpression
                        or SafeMigrationSqlCollateExpression:
                        break;
                    default:
                        throw new UnreachableException();
                }
            }
        }
        finally
        {
            cursor.Dispose();
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

        return ReferencesColumnIdentifier(expression, identifier, StringComparer.Ordinal);
    }

    /// <summary>
    /// Finds one terminal member without allocating a collection for the common unrelated-rename path.
    /// </summary>
    internal static bool ReferencesColumnIdentifier(
        SafeMigrationSqlExpression expression,
        string identifier,
        IEqualityComparer<string> comparer
    )
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(comparer);

        // WHY: Reference search historically visits IN values before its operand.
        // Preserve that short-circuit order independently of collection/rendering order.
        var cursor = new InspectionCursor(expression, inValuesFirst: true);

        try
        {
            while (cursor.TryMoveNext(out var current))
            {
                switch (current)
                {
                    case SafeMigrationSqlIdentifierExpression value:
                        if (comparer.Equals(value.Parts[^1], identifier))
                        {
                            return true;
                        }

                        break;
                    case SafeMigrationSqlLiteralExpression
                        or SafeMigrationSqlCurrentValueExpression
                        or SafeMigrationSqlProviderFragmentExpression
                        or SafeMigrationSqlOpaqueExpression
                        or SafeMigrationSqlUnaryExpression
                        or SafeMigrationSqlBinaryExpression
                        or SafeMigrationSqlNullTestExpression
                        or SafeMigrationSqlBetweenExpression
                        or SafeMigrationSqlInExpression
                        or SafeMigrationSqlFunctionExpression
                        or SafeMigrationSqlCaseExpression
                        or SafeMigrationSqlCastExpression
                        or SafeMigrationSqlCollateExpression:
                        break;
                    default:
                        throw new UnreachableException();
                }
            }
        }
        finally
        {
            cursor.Dispose();
        }

        return false;
    }

    /// <summary>Determines whether every node has provider-neutral structural semantics.</summary>
    /// <param name="expression">The expression tree to inspect.</param>
    /// <returns><see langword="true" /> when structural comparison is safe.</returns>
    public static bool IsStructurallyComparable(
        SafeMigrationSqlExpression expression
    )
    {
        if (expression is null)
        {
            throw new UnreachableException();
        }

        var cursor = new InspectionCursor(expression);

        try
        {
            while (cursor.TryMoveNext(out var current))
            {
                switch (current)
                {
                    case SafeMigrationSqlProviderFragmentExpression or SafeMigrationSqlOpaqueExpression:
                        return false;
                    case SafeMigrationSqlIdentifierExpression
                        or SafeMigrationSqlLiteralExpression
                        or SafeMigrationSqlCurrentValueExpression
                        or SafeMigrationSqlUnaryExpression
                        or SafeMigrationSqlBinaryExpression
                        or SafeMigrationSqlNullTestExpression
                        or SafeMigrationSqlBetweenExpression
                        or SafeMigrationSqlInExpression
                        or SafeMigrationSqlFunctionExpression
                        or SafeMigrationSqlCaseExpression
                        or SafeMigrationSqlCastExpression
                        or SafeMigrationSqlCollateExpression:
                        break;
                    default:
                        throw new UnreachableException();
                }
            }
        }
        finally
        {
            cursor.Dispose();
        }

        return true;
    }

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
}
