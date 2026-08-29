namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Derives the database columns that must exist before a provider can safely
/// inspect or apply an operation.
/// </summary>
internal static class SafeMigrationPrerequisiteColumns
{
    /// <summary>
    /// Gets the local columns referenced by an operation's structural
    /// definition.
    /// </summary>
    /// <param name="intent">The operation whose local dependencies are inspected.</param>
    /// <returns>Distinct column names in ordinal order.</returns>
    public static IReadOnlyList<string> Local(
        SafeMigrationIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        var columns = new HashSet<string>(StringComparer.Ordinal);
        switch (intent)
        {
            case EnsureColumnIntent value:
                AddColumnExpressions(value.Definition, columns);
                break;
            case AlterColumnIntent value:
                AddColumnExpressions(value.Definition, columns);
                if (value.OldDefinition is not null)
                {
                    AddColumnExpressions(value.OldDefinition, columns);
                }

                break;
            case EnsureIndexIntent value:
                AddIndex(value.Definition, columns);
                break;
            case EnsurePrimaryKeyIntent value:
                Add(columns, value.Definition.Columns);
                break;
            case EnsureUniqueConstraintIntent value:
                Add(columns, value.Definition.Columns);
                break;
            case EnsureCheckConstraintIntent value when value.Definition.Expression is not null:
                SafeMigrationSqlExpressionInspector.CollectIdentifiers(value.Definition.Expression, columns);
                break;
            case EnsureForeignKeyIntent value:
                Add(columns, value.Definition.Columns);
                break;
        }

        return columns.Count == 0
            ? []
            : columns
                .Order(StringComparer.Ordinal)
                .ToArray();
    }

    /// <summary>Gets the referenced columns required on a foreign key's principal table.</summary>
    /// <param name="intent">The foreign-key operation to inspect.</param>
    /// <returns>Distinct principal-column names in ordinal order.</returns>
    public static IReadOnlyList<string> Principal(
        EnsureForeignKeyIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        return intent.Definition.PrincipalColumns.Count == 0
            ? []
            : intent
                .Definition
                .PrincipalColumns
                .Order(StringComparer.Ordinal)
                .ToArray();
    }

    private static void AddColumnExpressions(
        ExpectedColumnDefinition definition,
        HashSet<string> columns
    )
    {
        if (definition.ComputedExpression is not null)
        {
            SafeMigrationSqlExpressionInspector.CollectIdentifiers(definition.ComputedExpression, columns);
        }

        if (definition.DefaultValue.StructuredExpression is not null)
        {
            SafeMigrationSqlExpressionInspector.CollectIdentifiers(
                definition.DefaultValue.StructuredExpression,
                columns);
        }
    }

    private static void AddIndex(
        ExpectedIndexDefinition definition,
        HashSet<string> columns
    )
    {
        foreach (var key in definition.Keys)
        {
            if (key.Column is not null)
            {
                columns.Add(key.Column);
            }
            else if (key.StructuredExpression is not null)
            {
                SafeMigrationSqlExpressionInspector.CollectIdentifiers(key.StructuredExpression, columns);
            }
        }

        Add(columns, definition.IncludedColumns);
        if (definition.StructuredFilter is not null)
        {
            SafeMigrationSqlExpressionInspector.CollectIdentifiers(definition.StructuredFilter, columns);
        }
    }

    private static void Add(
        HashSet<string> target,
        IReadOnlyList<string> values
    )
    {
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}
