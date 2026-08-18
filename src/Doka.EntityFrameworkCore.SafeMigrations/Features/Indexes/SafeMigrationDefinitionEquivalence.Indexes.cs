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
        && StringComparer.Ordinal.Equals(left.Method, right.Method)
        && left.NullsDistinct == right.NullsDistinct
        && Sequence(left.Keys, right.Keys, IndexKey)
        && Strings(left.IncludedColumns, right.IncludedColumns);

    private static bool IndexKey(
        ExpectedIndexKeyDefinition left,
        ExpectedIndexKeyDefinition right
    ) => StringComparer.Ordinal.Equals(left.Column, right.Column)
        && StringComparer.Ordinal.Equals(left.Expression, right.Expression)
        && left.Descending == right.Descending
        && left.PrefixLength == right.PrefixLength
        && StringComparer.Ordinal.Equals(left.Collation, right.Collation)
        && StringComparer.Ordinal.Equals(left.OperatorClass, right.OperatorClass);
}
