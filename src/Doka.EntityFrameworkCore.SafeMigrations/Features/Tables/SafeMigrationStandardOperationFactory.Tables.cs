namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static CreateTableOperation CreateOperation(
        EnsureTableIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation
    )
    {
        var definition = intent.Definition;
        var operation = new CreateTableOperation
        {
            Name = definition.Table,
            Schema = definition.Schema,
            Comment = definition.Comment,
        };

        foreach (var column in definition.Columns)
        {
            operation.Columns.Add(
                CreateColumn(definition.Table, definition.Schema, column, renderExpression, renderCollation));
        }

        if (definition.PrimaryKey is not null)
        {
            operation.PrimaryKey = CreatePrimaryKey(definition.PrimaryKey);
        }

        foreach (var uniqueConstraint in definition.UniqueConstraints)
        {
            operation.UniqueConstraints.Add(CreateUniqueConstraint(uniqueConstraint));
        }

        foreach (var checkConstraint in definition.CheckConstraints)
        {
            operation.CheckConstraints.Add(CreateCheckConstraint(checkConstraint, renderExpression));
        }

        foreach (var foreignKey in definition.ForeignKeys)
        {
            operation.ForeignKeys.Add(CreateForeignKey(foreignKey));
        }

        return operation;
    }

    private static DropTableOperation CreateOperation(
        DropTableIntent intent
    ) => new()
    {
        Name = intent.Table,
        Schema = intent.Schema,
    };

    private static RenameTableOperation CreateOperation(
        RenameTableIntent intent
    ) => new()
    {
        Name = intent.Name,
        Schema = intent.Schema,
        NewName = intent.NewName,
        NewSchema = intent.NewSchema,
    };
}
