namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    public static bool Index(
        ExpectedIndexDefinition left,
        ExpectedIndexDefinition right
    ) => StringComparer.Ordinal.Equals(left.Name, right.Name)
        && IndexSemantics(left, right);

    public static bool IndexSemantics(
        ExpectedIndexDefinition left,
        ExpectedIndexDefinition right
    ) => IndexSemantics(left, right, StringComparer.Ordinal);

    /// <summary>Compares index semantics with separately certified typed column-member identity.</summary>
    /// <param name="left">The accepted physical index contract.</param>
    /// <param name="right">The requested semantic index contract.</param>
    /// <param name="columnComparer">The provider's typed column-member comparer only.</param>
    public static bool IndexSemantics(
        ExpectedIndexDefinition left,
        ExpectedIndexDefinition right,
        StringComparer columnComparer
    )
    {
        if (!Identity(left.Table, left.Schema, right.Table, right.Schema)
            || left.Unique != right.Unique
            || !StringComparer.Ordinal.Equals(left.Filter, right.Filter)
            || !SafeMigrationSqlExpressionContract.Equivalent(left.StructuredFilter, right.StructuredFilter)
            || !StringComparer.Ordinal.Equals(left.Method, right.Method)
            || left.NullsDistinct != right.NullsDistinct
            || left.Keys.Count != right.Keys.Count
            || !Strings(left.IncludedColumns, right.IncludedColumns, columnComparer))
        {
            return false;
        }

        for (var index = 0; index < left.Keys.Count; index++)
        {
            if (!IndexKey(left.Keys[index], right.Keys[index], columnComparer))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IndexKey(
        ExpectedIndexKeyDefinition left,
        ExpectedIndexKeyDefinition right,
        StringComparer columnComparer
    ) => columnComparer.Equals(left.Column, right.Column)
        && StringComparer.Ordinal.Equals(left.Expression, right.Expression)
        && SafeMigrationSqlExpressionContract.Equivalent(left.StructuredExpression, right.StructuredExpression)
        && left.SortOrder == right.SortOrder
        && left.NullOrder == right.NullOrder
        && left.PrefixLength == right.PrefixLength
        && Equals(left.Collation, right.Collation)
        && StringComparer.Ordinal.Equals(left.OperatorClass, right.OperatorClass);
}
