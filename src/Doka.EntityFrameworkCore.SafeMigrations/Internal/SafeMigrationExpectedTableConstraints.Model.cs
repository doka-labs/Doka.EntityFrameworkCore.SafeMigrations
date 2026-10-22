namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationExpectedTableConstraints
{
    /// <summary>
    /// Resolves the target constraint contract from EF's relational model.
    /// </summary>
    /// <param name="model">The migration target model.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The table schema, or null for the provider default.</param>
    /// <param name="runtimeCheckConstraints">
    /// The operation-owned check constraints used when EF supplies a
    /// read-optimized runtime model.
    /// </param>
    /// <returns>The target constraints, or null when the model does not own the table.</returns>
    public static SafeMigrationExpectedTableConstraints? FromModel(
        IModel? model,
        string table,
        string? schema,
        IReadOnlyList<ExpectedCheckConstraintDefinition>? runtimeCheckConstraints = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var relationalTable = model
            ?.GetRelationalModel()
            .FindTable(table, schema);

        if (relationalTable is null)
        {
            return null;
        }

        var primaryKey = relationalTable.PrimaryKey is not null
            ? new ExpectedPrimaryKeyDefinition(
                relationalTable.PrimaryKey.Name,
                table,
                relationalTable.PrimaryKey.Columns.Select(static column => column.Name),
                schema)
            : null;

        var uniqueConstraints = relationalTable
            .UniqueConstraints
            .Where(constraint => !ReferenceEquals(constraint, relationalTable.PrimaryKey))
            .OrderBy(static constraint => constraint.Name, StringComparer.Ordinal)
            .Select(constraint => new ExpectedUniqueConstraintDefinition(
                constraint.Name,
                table,
                constraint.Columns.Select(static column => column.Name),
                schema))
            .ToArray();

        var checkConstraints = ReadCheckConstraints(relationalTable, table, schema, runtimeCheckConstraints);

        var foreignKeys = relationalTable
            .ForeignKeyConstraints
            .OrderBy(static constraint => constraint.Name, StringComparer.Ordinal)
            .Select(constraint => new ExpectedForeignKeyDefinition(
                constraint.Name,
                table,
                constraint.Columns.Select(static column => column.Name),
                constraint.PrincipalTable.Name,
                constraint.PrincipalColumns.Select(static column => column.Name),
                schema,
                constraint.PrincipalTable.Schema,
                onDelete: constraint.OnDeleteAction))
            .ToArray();

        return new SafeMigrationExpectedTableConstraints(
            primaryKey,
            uniqueConstraints,
            checkConstraints,
            foreignKeys,
            primaryKey is null ? [] : [primaryKey],
            primaryKey is null,
            uniqueConstraints,
            uniqueConstraints,
            checkConstraints,
            checkConstraints,
            foreignKeys,
            foreignKeys);
    }

    private static IReadOnlyList<ExpectedCheckConstraintDefinition> ReadCheckConstraints(
        ITable table,
        string tableName,
        string? schema,
        IReadOnlyList<ExpectedCheckConstraintDefinition>? runtimeCheckConstraints
    )
    {
        IEnumerable<ICheckConstraint> checkConstraints;

        try
        {
            checkConstraints = table.CheckConstraints;
        }
        catch (InvalidOperationException) when (runtimeCheckConstraints is not null)
        {
            // WHY: EF deliberately omits check-constraint configuration from
            // runtime models. The exact EnsureTable definition remains the
            // authoritative baseline for those constraints.
            return runtimeCheckConstraints;
        }

        return checkConstraints
            .OrderBy(static constraint => constraint.Name, StringComparer.Ordinal)
            .Select(constraint => SafeMigrationExpectedDefinitionFactory.CheckConstraint(
                constraint.Name
                ?? throw new InvalidOperationException(
                    $"The relational check constraint on '{tableName}' has no database name."),
                tableName,
                constraint.Sql,
                schema))
            .ToArray();
    }
}
