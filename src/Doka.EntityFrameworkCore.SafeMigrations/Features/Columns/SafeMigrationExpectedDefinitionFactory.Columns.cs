namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedColumnDefinition From(
        ColumnOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        return new ExpectedColumnDefinition(
            operation.Name,
            operation.ClrType,
            operation.IsNullable,
            operation.ColumnType,
            operation.IsUnicode,
            operation.MaxLength,
            operation.IsFixedLength,
            operation.IsRowVersion,
            operation.Precision,
            operation.Scale,
            operation.Collation,
            operation.Comment,
            CaptureDefault(operation),
            operation.ComputedColumnSql,
            operation.IsStored);
    }

    private static SafeMigrationDefaultValue CaptureDefault(
        ColumnOperation operation
    )
    {
        if (operation.DefaultValueSql is not null)
        {
            return SafeMigrationDefaultValue.Sql(operation.DefaultValueSql);
        }

        return operation.DefaultValue is null
            ? SafeMigrationDefaultValue.None
            : SafeMigrationDefaultValue.Literal(operation.DefaultValue);
    }
}
