namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Describes a complete expected foreign-key definition.</summary>
public sealed class ExpectedForeignKeyDefinition
{
    /// <summary>Initializes an expected foreign key.</summary>
    public ExpectedForeignKeyDefinition(
        string name,
        string table,
        IEnumerable<string> columns,
        string principalTable,
        IEnumerable<string> principalColumns,
        string? schema = null,
        string? principalSchema = null,
        ReferentialAction onUpdate = ReferentialAction.NoAction,
        ReferentialAction onDelete = ReferentialAction.NoAction
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        Columns = SafeMigrationDefinitionValidator.Identifiers(columns, nameof(columns));
        PrincipalTable = SafeMigrationDefinitionValidator.Required(principalTable, nameof(principalTable));
        PrincipalSchema = SafeMigrationDefinitionValidator.Optional(principalSchema, nameof(principalSchema));
        PrincipalColumns = SafeMigrationDefinitionValidator.Identifiers(principalColumns, nameof(principalColumns));

        if (Columns.Count != PrincipalColumns.Count)
        {
            throw new ArgumentException("Dependent and principal column counts must match.", nameof(principalColumns));
        }

        if (!Enum.IsDefined(onUpdate))
        {
            throw new ArgumentOutOfRangeException(nameof(onUpdate));
        }

        if (!Enum.IsDefined(onDelete))
        {
            throw new ArgumentOutOfRangeException(nameof(onDelete));
        }

        OnUpdate = onUpdate;
        OnDelete = onDelete;
    }

    /// <summary>Gets the constraint name.</summary>
    public string Name { get; }

    /// <summary>Gets the dependent table name.</summary>
    public string Table { get; }

    /// <summary>Gets the dependent schema name when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the ordered dependent columns.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the principal table name.</summary>
    public string PrincipalTable { get; }

    /// <summary>Gets the principal schema name when specified.</summary>
    public string? PrincipalSchema { get; }

    /// <summary>Gets the ordered principal columns.</summary>
    public IReadOnlyList<string> PrincipalColumns { get; }

    /// <summary>Gets the update referential action.</summary>
    public ReferentialAction OnUpdate { get; }

    /// <summary>Gets the delete referential action.</summary>
    public ReferentialAction OnDelete { get; }
}
