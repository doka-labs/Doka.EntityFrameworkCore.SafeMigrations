namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static AddUniqueConstraintOperation CreateOperation(
        EnsureUniqueConstraintIntent intent
    ) => CreateUniqueConstraint(intent.Definition);

    private static DropUniqueConstraintOperation CreateOperation(
        DropUniqueConstraintIntent intent
    ) => new()
    {
        Name = intent.Name,
        Table = intent.Table,
        Schema = intent.Schema,
    };

    private static AddUniqueConstraintOperation CreateUniqueConstraint(
        ExpectedUniqueConstraintDefinition definition
    ) => new()
    {
        Name = definition.Name,
        Table = definition.Table,
        Schema = definition.Schema,

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        Columns = definition.Columns.ToArray(),
    };
}
