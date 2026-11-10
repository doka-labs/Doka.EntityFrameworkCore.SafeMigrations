namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    public static bool Table(
        ExpectedTableDefinition left,
        ExpectedTableDefinition right
    ) => TableIntrinsic(left, right)
        && ProviderAnnotations(left.ProviderAnnotations, right.ProviderAnnotations);

    /// <summary>Compares neutral table facets after independent provider-metadata qualification.</summary>
    internal static bool TableIntrinsic(
        ExpectedTableDefinition left,
        ExpectedTableDefinition right
    ) => Identity(left.Table, left.Schema, right.Table, right.Schema)
        && StringComparer.Ordinal.Equals(left.Comment, right.Comment)
        && Sequence(left.Columns, right.Columns, Column)
        && Optional(left.PrimaryKey, right.PrimaryKey, PrimaryKey)
        && Sequence(left.UniqueConstraints, right.UniqueConstraints, UniqueConstraint)
        && Sequence(left.CheckConstraints, right.CheckConstraints, CheckConstraint)
        && Sequence(left.ForeignKeys, right.ForeignKeys, ForeignKey);
}
