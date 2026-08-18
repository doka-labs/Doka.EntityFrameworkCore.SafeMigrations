namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    public static bool UniqueConstraint(
        ExpectedUniqueConstraintDefinition left,
        ExpectedUniqueConstraintDefinition right
    ) => Identity(left.Table, left.Schema, right.Table, right.Schema)
        && StringComparer.Ordinal.Equals(left.Name, right.Name)
        && Strings(left.Columns, right.Columns);
}
