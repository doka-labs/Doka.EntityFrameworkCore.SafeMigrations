namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static AddPrimaryKeyOperation CreateOperation(
        EnsurePrimaryKeyIntent intent
    ) => CreatePrimaryKey(intent.Definition);

    private static DropPrimaryKeyOperation CreateOperation(
        DropPrimaryKeyIntent intent
    ) => new()
    {
        Name = intent.Name,
        Table = intent.Table,
        Schema = intent.Schema,
    };

    private static AddPrimaryKeyOperation CreatePrimaryKey(
        ExpectedPrimaryKeyDefinition definition
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
