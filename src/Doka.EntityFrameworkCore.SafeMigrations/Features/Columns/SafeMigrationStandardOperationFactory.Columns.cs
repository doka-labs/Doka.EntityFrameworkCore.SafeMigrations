namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static AddColumnOperation CreateOperation(
        EnsureColumnIntent intent
    ) => CreateColumn(intent.Table, intent.Schema, intent.Definition);

    private static DropColumnOperation CreateOperation(
        DropColumnIntent intent
    ) => new()
    {
        Name = intent.Name,
        Table = intent.Table,
        Schema = intent.Schema,
    };

    private static RenameColumnOperation CreateOperation(
        RenameColumnIntent intent
    ) => new()
    {
        Name = intent.Name,
        Table = intent.Table,
        Schema = intent.Schema,
        NewName = intent.NewName,
    };

    private static AlterColumnOperation CreateOperation(
        AlterColumnIntent intent
    )
    {
        var target = intent.Definition;
        var operation = new AlterColumnOperation
        {
            Name = target.Name,
            Table = intent.Table,
            Schema = intent.Schema,
            ClrType = target.ClrType,
            ColumnType = target.StoreType,
            IsUnicode = target.IsUnicode,
            MaxLength = target.MaxLength,
            IsFixedLength = target.IsFixedLength,
            IsRowVersion = target.IsRowVersion,
            IsNullable = target.IsNullable,
            Precision = target.Precision,
            Scale = target.Scale,
            Collation = target.Collation,
            Comment = target.Comment,
            ComputedColumnSql = target.ComputedColumnSql,
            IsStored = target.IsStored,
            OldColumn = intent.OldDefinition is null
                ? new AddColumnOperation
                {
                    Name = target.Name,
                    Table = intent.Table,
                    Schema = intent.Schema,
                    ClrType = target.ClrType,
                    ColumnType = target.StoreType,
                    IsNullable = target.IsNullable,
                }
                : CreateColumn(intent.Table, intent.Schema, intent.OldDefinition),
        };

        ApplyDefault(operation, target.DefaultValue);
        return operation;
    }

    private static AddColumnOperation CreateColumn(
        string table,
        string? schema,
        ExpectedColumnDefinition definition
    )
    {
        var operation = new AddColumnOperation
        {
            Name = definition.Name,
            Table = table,
            Schema = schema,
            ClrType = definition.ClrType,
            ColumnType = definition.StoreType,
            IsUnicode = definition.IsUnicode,
            MaxLength = definition.MaxLength,
            IsFixedLength = definition.IsFixedLength,
            IsRowVersion = definition.IsRowVersion,
            IsNullable = definition.IsNullable,
            Precision = definition.Precision,
            Scale = definition.Scale,
            Collation = definition.Collation,
            Comment = definition.Comment,
            ComputedColumnSql = definition.ComputedColumnSql,
            IsStored = definition.IsStored,
        };

        ApplyDefault(operation, definition.DefaultValue);
        return operation;
    }

    private static void ApplyDefault(
        ColumnOperation operation,
        SafeMigrationDefaultValue defaultValue
    )
    {
        switch (defaultValue.Kind)
        {
            case SafeMigrationDefaultValueKind.None:
                return;
            case SafeMigrationDefaultValueKind.Literal:
                var literal = defaultValue.GetLiteralValue();
                if (literal is null)
                {
                    operation.DefaultValueSql = "NULL";
                }
                else
                {
                    operation.DefaultValue = literal;
                }

                return;
            case SafeMigrationDefaultValueKind.Sql:
                operation.DefaultValueSql = defaultValue.SqlExpression;
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(defaultValue));
        }
    }
}
