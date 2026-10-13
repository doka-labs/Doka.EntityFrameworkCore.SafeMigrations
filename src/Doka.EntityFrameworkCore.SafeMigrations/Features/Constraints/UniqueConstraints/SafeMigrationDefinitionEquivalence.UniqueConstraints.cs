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
    ) => UniqueConstraintSemantics(left, right, StringComparer.Ordinal);

    /// <summary>Compares candidate-key members with certified column identity and exact owner/schema/order.</summary>
    /// <param name="left">The accepted physical constraint shape.</param>
    /// <param name="right">The requested semantic constraint shape.</param>
    /// <param name="columnComparer">The provider's typed column comparer, not its owner comparer.</param>
    public static bool UniqueConstraintSemantics(
        ExpectedUniqueConstraintDefinition left,
        ExpectedUniqueConstraintDefinition right,
        StringComparer columnComparer
    ) => Identity(left.Table, left.Schema, right.Table, right.Schema)
        && Strings(left.Columns, right.Columns, columnComparer);
}
