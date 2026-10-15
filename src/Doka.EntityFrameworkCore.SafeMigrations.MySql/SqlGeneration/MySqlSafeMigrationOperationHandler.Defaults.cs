namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    /// <summary>
    /// Materializes only owned defaults before delegating complete operations to the native provider.
    /// </summary>
    /// <param name="context">The active native rendering lease.</param>
    /// <param name="operation">The freshly reconstructed standard operation, never the caller's operation.</param>
    /// <param name="intent">The immutable definitions that distinguish structured defaults from opaque SQL.</param>
    /// <returns>The native command boundaries with all annotations and resource scopes preserved.</returns>
    private IReadOnlyList<MySqlMigrationCommandSpec> RenderNativeOperation(
        MySqlMigrationOperationContext context,
        MigrationOperation operation,
        SafeMigrationIntent intent
    )
    {
        switch (operation, intent)
        {
            case (CreateTableOperation table, EnsureTableIntent ensure):
                for (var index = 0; index < table.Columns.Count; index++)
                {
                    ApplyPersistedDefault(table.Columns[index], ensure.Definition.Columns[index],
                        context.ServerVersion.IsMariaDb);
                }

                break;
            case (ColumnOperation column, EnsureColumnIntent ensure):
                ApplyPersistedDefault(column, ensure.Definition, context.ServerVersion.IsMariaDb);
                break;
            case (AlterColumnOperation column, AlterColumnIntent alter):
                ApplyPersistedDefault(column, alter.Definition, context.ServerVersion.IsMariaDb);
                if (alter.OldDefinition is { } previous)
                {
                    ApplyPersistedDefault(column.OldColumn, previous, context.ServerVersion.IsMariaDb);
                }

                break;
        }

        return context.RenderStandardOperation(operation);
    }

    /// <summary>Prevents persisted expression literals from being reinterpreted under a later SQL mode.</summary>
    /// <param name="operation">The private native column operation whose default is being materialized.</param>
    /// <param name="definition">The authored definition supplying the exact value or structured expression.</param>
    /// <param name="isMariaDb">Whether binary defaults need MariaDB's lossless expression catalog form.</param>
    private void ApplyPersistedDefault(
        ColumnOperation operation,
        ExpectedColumnDefinition definition,
        bool isMariaDb)
    {
        if (definition.DefaultValue.StructuredExpression is { } expression)
        {
            // WHY: Native string literals are safe for one statement, but the
            // engine can serialize them with backslash escapes and reparse them
            // in another mode. Only owned AST leaves can be encoded without
            // guessing at opaque SQL or changing computed/check expressions.
            operation.DefaultValueSql = isMariaDb && definition.ClrType == typeof(byte[])
                && expression is SafeMigrationSqlLiteralExpression { Value: byte[] bytes, StoreType: null }
                    ? MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(bytes)
                    : _expressionRenderer.RenderDefault(expression);

            return;
        }

        if (definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.Literal)
        {
            return;
        }

        // WHY: The factory already owns a defensive literal snapshot. Reuse
        // it instead of allocating another copy for unrelated binary defaults.
        var literal = operation.DefaultValue;
        if (isMariaDb && definition.ClrType == typeof(byte[]) && literal is byte[] binary)
        {
            // WHY: Older MariaDB catalogs expose binary literals as escaped
            // text and can replace non-UTF-8 bytes with '?'. The binary
            // conversion retains exact hex identity and DEFAULT(column).
            operation.DefaultValue = null;
            operation.DefaultValueSql = MySqlSafeMigrationSqlExpressionRenderer.RenderBinaryDefault(binary);

            return;
        }

        if (literal is not (string or char))
        {
            return;
        }

        var storeType = definition.StoreType
            ?? _typeMappingSource.FindMapping(definition.ClrType, storeTypeName: null, keyOrIndex: false,
                definition.IsUnicode, definition.MaxLength, definition.IsRowVersion, definition.IsFixedLength,
                definition.Precision, definition.Scale)?.StoreType;

        if (storeType is null || !MySqlSafeMigrationCatalogSqlBuilder.TryGetTextCapacity(storeType, out _))
        {
            // WHY: Ordinary character literal defaults do not require a
            // persisted expression. Keep their native literal semantics,
            // including DEFAULT(column), instead of changing every default.
            return;
        }

        var value = literal is string text
            ? text
            : ((char)literal).ToString();

        operation.DefaultValue = null;
        operation.DefaultValueSql = MySqlSafeMigrationSqlExpressionRenderer.RenderStringDefault(value);
    }
}
