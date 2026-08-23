namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static AddColumnOperation CreateOperation(
        EnsureColumnIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation
    ) => CreateColumn(intent.Table, intent.Schema, intent.Definition, renderExpression, renderCollation);

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
        AlterColumnIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation
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
            Collation = Render(target.Collation, renderCollation),
            Comment = target.Comment,
            ComputedColumnSql =
                target.ComputedColumnSql
                ?? (target.ComputedExpression is null ? null : Render(target.ComputedExpression, renderExpression)),
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
                : CreateColumn(intent.Table, intent.Schema, intent.OldDefinition, renderExpression, renderCollation),
        };

        ApplyDefault(operation, target.DefaultValue, renderExpression);
        return operation;
    }

    private static AddColumnOperation CreateColumn(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        Func<SafeMigrationSqlExpression, string>? renderExpression,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation
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
            Collation = Render(definition.Collation, renderCollation),
            Comment = definition.Comment,
            ComputedColumnSql = definition.ComputedColumnSql
                ?? (definition.ComputedExpression is null
                    ? null
                    : Render(definition.ComputedExpression, renderExpression)),
            IsStored = definition.IsStored,
        };

        ApplyDefault(operation, definition.DefaultValue, renderExpression);
        return operation;
    }

    private static void ApplyDefault(
        ColumnOperation operation,
        SafeMigrationDefaultValue defaultValue,
        Func<SafeMigrationSqlExpression, string>? renderExpression
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
                operation.DefaultValueSql = defaultValue.SqlExpression
                    ?? Render(defaultValue.StructuredExpression!, renderExpression);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(defaultValue));
        }
    }
}
