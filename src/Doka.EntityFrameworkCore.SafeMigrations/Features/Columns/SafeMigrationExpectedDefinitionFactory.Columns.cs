namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    /// <summary>Captures an immutable expected column definition from an EF operation.</summary>
    /// <param name="operation">The EF Core column operation to snapshot.</param>
    /// <returns>The complete SafeMigrations column definition.</returns>
    public static ExpectedColumnDefinition From(
        ColumnOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        return From(operation, operation.Name);
    }

    /// <summary>
    /// Captures a column definition with the owning operation's identity.
    /// EF Core leaves the nested old-column identity empty in generated alter operations.
    /// </summary>
    /// <param name="operation">The EF Core column operation to snapshot.</param>
    /// <param name="name">The owning column name.</param>
    /// <returns>The complete SafeMigrations column definition.</returns>
    public static ExpectedColumnDefinition From(
        ColumnOperation operation,
        string name
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        return new ExpectedColumnDefinition(
            name,
            operation.ClrType,
            operation.IsNullable,
            operation.ColumnType,
            operation.IsUnicode,
            operation.MaxLength,
            operation.IsFixedLength,
            operation.IsRowVersion,
            operation.Precision,
            operation.Scale,
            operation.Collation is null ? null : new SafeMigrationCollationIdentifier(operation.Collation),
            operation.Comment,
            CaptureDefault(operation),
            operation.ComputedColumnSql,
            operation.IsStored)
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(operation),
        };
    }

    private static SafeMigrationDefaultValue CaptureDefault(
        ColumnOperation operation
    )
    {
        if (operation.DefaultValueSql is not null)
        {
            return SafeMigrationSqlExpressionParser.TryParse(operation.DefaultValueSql, out var expression)
                ? SafeMigrationDefaultValue.Sql(expression)
                : SafeMigrationDefaultValue.Sql(operation.DefaultValueSql);
        }

        return operation.DefaultValue is null
            ? SafeMigrationDefaultValue.None
            : SafeMigrationDefaultValue.Literal(operation.DefaultValue);
    }
}
