namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the complete expected table definition used by strict table
/// comparison and baseline DDL generation.
/// </summary>
public sealed class ExpectedTableDefinition
{
    /// <summary>Initializes an expected table definition.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="columns">The ordered expected column definitions.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="comment">The expected database comment, or null when unspecified.</param>
    /// <param name="primaryKey">The expected primary key, or null when absent.</param>
    /// <param name="uniqueConstraints">The expected unique constraints.</param>
    /// <param name="checkConstraints">The expected check constraints.</param>
    /// <param name="foreignKeys">The expected foreign keys.</param>
    public ExpectedTableDefinition(
        string table,
        IEnumerable<ExpectedColumnDefinition> columns,
        string? schema = null,
        string? comment = null,
        ExpectedPrimaryKeyDefinition? primaryKey = null,
        IEnumerable<ExpectedUniqueConstraintDefinition>? uniqueConstraints = null,
        IEnumerable<ExpectedCheckConstraintDefinition>? checkConstraints = null,
        IEnumerable<ExpectedForeignKeyDefinition>? foreignKeys = null
    )
    {
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        Comment = comment;
        Columns = SafeMigrationDefinitionValidator.Definitions(columns, nameof(columns), allowEmpty: false);
        PrimaryKey = primaryKey;

        UniqueConstraints = SafeMigrationDefinitionValidator.Definitions(
            uniqueConstraints ?? [],
            nameof(uniqueConstraints));

        CheckConstraints = SafeMigrationDefinitionValidator.Definitions(
            checkConstraints ?? [],
            nameof(checkConstraints));

        ForeignKeys = SafeMigrationDefinitionValidator.Definitions(foreignKeys ?? [], nameof(foreignKeys));

        EnsureUniqueNames(Columns.Select(static definition => definition.Name), nameof(columns));
        EnsureUniqueNames(UniqueConstraints.Select(static definition => definition.Name), nameof(uniqueConstraints));
        EnsureUniqueNames(CheckConstraints.Select(static definition => definition.Name), nameof(checkConstraints));
        EnsureUniqueNames(ForeignKeys.Select(static definition => definition.Name), nameof(foreignKeys));

        if (PrimaryKey is not null)
        {
            ValidateOwner(PrimaryKey.Table, PrimaryKey.Schema, nameof(primaryKey));
        }

        foreach (var definition in UniqueConstraints)
        {
            ValidateOwner(definition.Table, definition.Schema, nameof(uniqueConstraints));
        }

        foreach (var definition in CheckConstraints)
        {
            ValidateOwner(definition.Table, definition.Schema, nameof(checkConstraints));
        }

        foreach (var definition in ForeignKeys)
        {
            ValidateOwner(definition.Table, definition.Schema, nameof(foreignKeys));
        }

        ValidateColumnReferences();
    }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the expected table comment when specified.</summary>
    public string? Comment { get; }

    /// <summary>Gets the ordered expected columns.</summary>
    public IReadOnlyList<ExpectedColumnDefinition> Columns { get; }

    /// <summary>Gets the expected primary key when specified.</summary>
    public ExpectedPrimaryKeyDefinition? PrimaryKey { get; }

    /// <summary>Gets the expected unique constraints.</summary>
    public IReadOnlyList<ExpectedUniqueConstraintDefinition> UniqueConstraints { get; }

    /// <summary>Gets the expected check constraints.</summary>
    public IReadOnlyList<ExpectedCheckConstraintDefinition> CheckConstraints { get; }

    /// <summary>Gets the expected foreign keys.</summary>
    public IReadOnlyList<ExpectedForeignKeyDefinition> ForeignKeys { get; }

    private static void EnsureUniqueNames(
        IEnumerable<string> names,
        string parameterName
    )
    {
        var snapshot = names.ToArray();
        if (snapshot
                .Distinct(StringComparer.Ordinal)
                .Count()
            != snapshot.Length)
        {
            throw new ArgumentException("Duplicate definition names are not allowed.", parameterName);
        }
    }

    private void ValidateOwner(
        string table,
        string? schema,
        string parameterName
    )
    {
        if (!StringComparer.Ordinal.Equals(Table, table)
            || !StringComparer.Ordinal.Equals(Schema, schema))
        {
            throw new ArgumentException(
                "Every table constraint must target the containing table and schema.",
                parameterName);
        }
    }

    private void ValidateColumnReferences()
    {
        var columns = Columns
            .Select(static value => value.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (PrimaryKey is not null)
        {
            ValidateColumns(columns, PrimaryKey.Columns, nameof(PrimaryKey));
        }

        foreach (var definition in UniqueConstraints)
        {
            ValidateColumns(columns, definition.Columns, nameof(UniqueConstraints));
        }

        foreach (var definition in ForeignKeys)
        {
            ValidateColumns(columns, definition.Columns, nameof(ForeignKeys));
        }
    }

    private static void ValidateColumns(
        HashSet<string> available,
        IReadOnlyList<string> referenced,
        string parameterName
    )
    {
        if (referenced.Any(column => !available.Contains(column)))
        {
            throw new ArgumentException(
                "Every local constraint column must exist in the containing table definition.",
                parameterName);
        }
    }
}
