namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    public static bool Index(
        ExpectedIndexDefinition left,
        ExpectedIndexDefinition right
    ) => Identity(left.Table, left.Schema, right.Table, right.Schema)
        && StringComparer.Ordinal.Equals(left.Name, right.Name)
        && left.Unique == right.Unique
        && StringComparer.Ordinal.Equals(left.Filter, right.Filter)
        && SafeMigrationSqlExpressionContract.Equivalent(left.StructuredFilter, right.StructuredFilter)
        && StringComparer.Ordinal.Equals(left.Method, right.Method)
        && left.NullsDistinct == right.NullsDistinct
        && Sequence(left.Keys, right.Keys, IndexKey)
        && Strings(left.IncludedColumns, right.IncludedColumns);

    private static bool IndexKey(
        ExpectedIndexKeyDefinition left,
        ExpectedIndexKeyDefinition right
    ) => StringComparer.Ordinal.Equals(left.Column, right.Column)
        && StringComparer.Ordinal.Equals(left.Expression, right.Expression)
        && SafeMigrationSqlExpressionContract.Equivalent(left.StructuredExpression, right.StructuredExpression)
        && left.SortOrder == right.SortOrder
        && left.NullOrder == right.NullOrder
        && left.PrefixLength == right.PrefixLength
        && Equals(left.Collation, right.Collation)
        && StringComparer.Ordinal.Equals(left.OperatorClass, right.OperatorClass);
}
