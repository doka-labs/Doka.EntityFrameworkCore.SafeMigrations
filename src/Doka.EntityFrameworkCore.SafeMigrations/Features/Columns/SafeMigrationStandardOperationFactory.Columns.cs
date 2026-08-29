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
        var oldColumn = intent.OldDefinition is null
            ? new AddColumnOperation
            {
                Name = target.Name,
                Table = intent.Table,
                Schema = intent.Schema,
                ClrType = target.ClrType,
                ColumnType = target.StoreType,
                IsNullable = target.IsNullable,
            }
            : CreateColumn(intent.Table, intent.Schema, intent.OldDefinition, renderExpression, renderCollation);

        var operation = CreateAlterColumn(
            intent.Table,
            intent.Schema,
            target,
            oldColumn,
            renderExpression,
            renderCollation);

        ApplyProviderAnnotations(operation.OldColumn, intent.OldDefinition?.ProviderAnnotations ?? []);

        return operation;
    }

    private static AlterColumnOperation CreateRepairOperation(
        EnsureColumnIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation,
        bool declareNullabilityDifference
    )
    {
        var target = intent.Definition;
        var oldColumn = CreateColumn(intent.Table, intent.Schema, target, renderExpression, renderCollation);

        // Provider SQL generators compare target and old metadata to decide
        // which ALTER clauses to emit. Make every permitted mutable facet
        // observably different while preserving all invariant facets.
        oldColumn.IsNullable = declareNullabilityDifference ? !target.IsNullable : target.IsNullable;
        oldColumn.Comment = target.Comment is null ? "doka_sm_previous_comment" : null;
        oldColumn.DefaultValue = null;
        oldColumn.DefaultValueSql = target.DefaultValue.Kind == SafeMigrationDefaultValueKind.None ? "NULL" : null;

        return CreateAlterColumn(
            intent.Table,
            intent.Schema,
            target,
            oldColumn,
            renderExpression,
            renderCollation);
    }

    private static AlterColumnOperation CreateAlterColumn(
        string table,
        string? schema,
        ExpectedColumnDefinition target,
        AddColumnOperation oldColumn,
        Func<SafeMigrationSqlExpression, string>? renderExpression,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation
    )
    {
        var operation = new AlterColumnOperation
        {
            Name = target.Name,
            Table = table,
            Schema = schema,
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
            OldColumn = oldColumn,
        };

        ApplyDefault(operation, target.DefaultValue, renderExpression);
        ApplyProviderAnnotations(operation, target.ProviderAnnotations);

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
        ApplyProviderAnnotations(operation, definition.ProviderAnnotations);
        return operation;
    }

    private static void ApplyProviderAnnotations(
        MigrationOperation operation,
        IReadOnlyList<SafeMigrationProviderAnnotation> annotations
    )
    {
        foreach (var annotation in annotations)
        {
            operation[annotation.Name] = annotation.CreateValueSnapshot();
        }
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
