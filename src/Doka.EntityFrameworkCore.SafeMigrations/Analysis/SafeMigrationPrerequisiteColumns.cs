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

        // WHY: Ordinary column definitions commonly have no structured source references. Prove
        // absence on both sides of an ALTER before allocating the deduplication set; opaque SQL
        // remains the provider's capability concern, not an identifier-collection permission.
        if (intent switch
            {
                EnsureColumnIntent value => !HasColumnExpressions(value.Definition),
                AlterColumnIntent value => !HasColumnExpressions(value.Definition)
                    && (value.OldDefinition is null || !HasColumnExpressions(value.OldDefinition)),
                _ => false,
            })
        {
            return [];
        }

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
            case EnsureCheckConstraintIntent { Definition.Expression: not null } value:
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

    /// <summary>Identifies every expression role consumed by local column-prerequisite collection.</summary>
    /// <param name="definition">The exact old or target column definition.</param>
    /// <returns>Whether collecting structured references may be necessary.</returns>
    private static bool HasColumnExpressions(
        ExpectedColumnDefinition definition
    ) => definition.ComputedExpression is not null || definition.DefaultValue.StructuredExpression is not null;

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
