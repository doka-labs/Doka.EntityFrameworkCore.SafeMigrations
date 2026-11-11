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

        var computed = operation.ComputedColumnSql;
        var expression = computed is not null
            && SafeMigrationSqlExpressionParser.TryParseCapturedSource(computed, out var parsed)
                ? parsed
                : null;

        // WHY: EF persists generated expressions as SQL text. Capture the
        // bounded grammar once for dependency roles, but retain the original
        // string reference: rendering a parsed integer as a typed CLR literal
        // can change native arithmetic. Unknown syntax remains opaque.
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
            operation.Collation is null
                ? null
                : new SafeMigrationCollationIdentifier(operation.Collation),
            operation.Comment,
            CaptureDefault(operation),
            expression is null
                ? computed
                : null,
            operation.IsStored,
            expression)
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(operation),
            CapturedComputedSql = expression is null
                ? null
                : computed,
        };
    }

    /// <summary>Preserves native default token roles before granting structural comparison.</summary>
    private static SafeMigrationDefaultValue CaptureDefault(
        ColumnOperation operation
    )
    {
        if (operation.DefaultValueSql is not null)
        {
            // WHY: Default SQL has the same native function/type/operator roles
            // as computed and CHECK source. Ordinary parsing would unquote a
            // different function or erase an overloadable unary plus. Preserve
            // numeric lexemes and leave unrepresentable native syntax opaque.
            return SafeMigrationSqlExpressionParser.TryParseCapturedSource(
                operation.DefaultValueSql,
                out var expression)
                ? SafeMigrationDefaultValue.Sql(expression)
                : SafeMigrationDefaultValue.Sql(operation.DefaultValueSql);
        }

        return operation.DefaultValue is null
            ? SafeMigrationDefaultValue.None
            : SafeMigrationDefaultValue.Literal(operation.DefaultValue);
    }
}
