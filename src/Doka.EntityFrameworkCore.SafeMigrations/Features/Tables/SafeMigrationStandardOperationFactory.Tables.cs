namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static CreateTableOperation CreateOperation(
        EnsureTableIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation,
        Func<SafeMigrationSqlExpression, string>? renderScalarExpression
    )
    {
        var definition = intent.Definition;
        var operation = new CreateTableOperation
        {
            Name = definition.Table,
            Schema = definition.Schema,
            Comment = definition.Comment,
        };

        ApplyProviderAnnotations(operation, definition.ProviderAnnotations);

        // WHY: This immutable definition snapshot is indexed; retain its authored order without interface enumeration.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var columnIndex = 0; columnIndex < definition.Columns.Count; columnIndex++)
        {
            var column = definition.Columns[columnIndex];

            operation.Columns.Add(
                CreateColumn(definition.Table, definition.Schema, column, renderScalarExpression, renderCollation));
        }

        if (definition.PrimaryKey is not null)
        {
            operation.PrimaryKey = CreatePrimaryKey(definition.PrimaryKey);
        }

        // WHY: This immutable definition snapshot is indexed; retain its authored order without interface enumeration.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var uniqueConstraintIndex = 0;
            uniqueConstraintIndex < definition.UniqueConstraints.Count;
            uniqueConstraintIndex++)
        {
            var uniqueConstraint = definition.UniqueConstraints[uniqueConstraintIndex];

            operation.UniqueConstraints.Add(CreateUniqueConstraint(uniqueConstraint));
        }

        // WHY: This immutable definition snapshot is indexed; retain its authored order without interface enumeration.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var checkConstraintIndex = 0;
            checkConstraintIndex < definition.CheckConstraints.Count;
            checkConstraintIndex++)
        {
            var checkConstraint = definition.CheckConstraints[checkConstraintIndex];

            operation.CheckConstraints.Add(CreateCheckConstraint(checkConstraint, renderExpression));
        }

        // WHY: This immutable definition snapshot is indexed; retain its authored order without interface enumeration.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var foreignKeyIndex = 0; foreignKeyIndex < definition.ForeignKeys.Count; foreignKeyIndex++)
        {
            var foreignKey = definition.ForeignKeys[foreignKeyIndex];

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
