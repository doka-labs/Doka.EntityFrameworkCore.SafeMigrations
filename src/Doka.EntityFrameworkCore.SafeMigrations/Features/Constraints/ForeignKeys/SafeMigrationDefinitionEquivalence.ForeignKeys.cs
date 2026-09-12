namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    public static bool ForeignKey(
        ExpectedForeignKeyDefinition left,
        ExpectedForeignKeyDefinition right
    ) => StringComparer.Ordinal.Equals(left.Name, right.Name)
        && ForeignKeySemantics(left, right);

    public static bool ForeignKeySemantics(
        ExpectedForeignKeyDefinition left,
        ExpectedForeignKeyDefinition right
    ) => Identity(left.Table, left.Schema, right.Table, right.Schema)
        && Strings(left.Columns, right.Columns)
        && Identity(left.PrincipalTable, left.PrincipalSchema, right.PrincipalTable, right.PrincipalSchema)
        && Strings(left.PrincipalColumns, right.PrincipalColumns)
        && left.OnUpdate == right.OnUpdate
        && left.OnDelete == right.OnDelete;
}
