namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    public static bool PrimaryKey(
        ExpectedPrimaryKeyDefinition left,
        ExpectedPrimaryKeyDefinition right
    ) => StringComparer.Ordinal.Equals(left.Name, right.Name)
        && PrimaryKeySemantics(left, right);

    public static bool PrimaryKeySemantics(
        ExpectedPrimaryKeyDefinition left,
        ExpectedPrimaryKeyDefinition right
    ) => Identity(left.Table, left.Schema, right.Table, right.Schema)
        && Strings(left.Columns, right.Columns);
}
