namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    public static bool UniqueConstraint(
        ExpectedUniqueConstraintDefinition left,
        ExpectedUniqueConstraintDefinition right
    ) => StringComparer.Ordinal.Equals(left.Name, right.Name)
        && UniqueConstraintSemantics(left, right);

    public static bool UniqueConstraintSemantics(
        ExpectedUniqueConstraintDefinition left,
        ExpectedUniqueConstraintDefinition right
    ) => Identity(left.Table, left.Schema, right.Table, right.Schema)
        && Strings(left.Columns, right.Columns);
}
