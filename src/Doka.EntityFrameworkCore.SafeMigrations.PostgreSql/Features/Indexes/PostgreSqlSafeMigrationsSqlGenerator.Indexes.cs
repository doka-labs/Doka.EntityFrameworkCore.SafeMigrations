namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

public sealed partial class PostgreSqlSafeMigrationsSqlGenerator
{
    private string BuildCustomCreateIndexSql(
        ExpectedIndexDefinition definition
    )
    {
        var builder = new StringBuilder("CREATE ");
        if (definition.Unique)
        {
            builder.Append("UNIQUE ");
        }

        builder
            .Append("INDEX ")
            .Append(_sqlGenerationHelper.DelimitIdentifier(definition.Name))
            .Append(" ON ")
            .Append(Qualified(definition.Table, definition.Schema));

        if (definition.Method is not null)
        {
            builder
                .Append(" USING ")
                .Append(_sqlGenerationHelper.DelimitIdentifier(definition.Method));
        }

        builder.Append(" (");
        for (var index = 0; index < definition.Keys.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            var key = definition.Keys[index];
            builder.Append(
                key.Column is not null
                    ? _sqlGenerationHelper.DelimitIdentifier(key.Column)
                    : $"({_expressionRenderer.Render(key.StructuredExpression ?? SafeMigrationSql.Opaque(key.Expression!))})");

            if (key.Collation is not null)
            {
                builder
                    .Append(" COLLATE ")
                    .Append(Delimited(key.Collation));
            }

            if (key.OperatorClass is not null)
            {
                builder
                    .Append(' ')
                    .Append(DelimitedPath(key.OperatorClass));
            }

            builder.Append(
                key.SortOrder switch
                {
                    SafeMigrationIndexSortOrder.ProviderDefault => string.Empty,
                    SafeMigrationIndexSortOrder.Ascending => " ASC",
                    SafeMigrationIndexSortOrder.Descending => " DESC",
                    _ => throw new ArgumentOutOfRangeException(nameof(definition)),
                });

            builder.Append(
                key.NullOrder switch
                {
                    SafeMigrationIndexNullOrder.ProviderDefault => string.Empty,
                    SafeMigrationIndexNullOrder.First => " NULLS FIRST",
                    SafeMigrationIndexNullOrder.Last => " NULLS LAST",
                    _ => throw new ArgumentOutOfRangeException(nameof(definition)),
                });
        }

        builder.Append(')');
        if (definition.IncludedColumns.Count > 0)
        {
            builder
                .Append(" INCLUDE (")
                .Append(string.Join(", ", definition.IncludedColumns.Select(_sqlGenerationHelper.DelimitIdentifier)))
                .Append(')');
        }

        if (definition is { Unique: true, NullsDistinct: false })
        {
            builder.Append(" NULLS NOT DISTINCT");
        }

        if (definition.Filter is not null
            || definition.StructuredFilter is not null)
        {
            builder
                .Append(" WHERE ")
                .Append(definition.Filter ?? _expressionRenderer.Render(definition.StructuredFilter!));
        }

        var sql = builder
            .Append(';')
            .ToString();

        if (definition.NullsDistinct != false)
        {
            return sql;
        }

        var tag = SelectDollarTag(sql);

        return $"EXECUTE {tag}{sql}{tag};";
    }

    private string Qualified(
        string name,
        string? schema
    ) => schema is null
        ? _sqlGenerationHelper.DelimitIdentifier(name)
        : _sqlGenerationHelper.DelimitIdentifier(name, schema);

    private string DelimitedPath(
        string value
    ) => string.Join(
        ".",
        value
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(_sqlGenerationHelper.DelimitIdentifier));

    private string Delimited(
        SafeMigrationCollationIdentifier value
    ) => value.Schema is null
        ? _sqlGenerationHelper.DelimitIdentifier(value.Name)
        : _sqlGenerationHelper.DelimitIdentifier(value.Name, value.Schema);

    private static bool RequiresCustomIndexSql(
        ExpectedIndexDefinition definition
    ) => definition.Keys.Any(static key => key.Expression is not null
            || key.StructuredExpression is not null
            || key.Collation is not null
            || key.OperatorClass is not null)
        || definition.IncludedColumns.Count > 0
        || definition.Method is not null
        || definition.StructuredFilter is not null
        || definition.Keys.Any(static key => key.NullOrder != SafeMigrationIndexNullOrder.ProviderDefault)
        || definition.NullsDistinct is not null;
}
