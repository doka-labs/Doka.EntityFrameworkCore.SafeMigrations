namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Identifies a database-object family in an unexpected-object report.</summary>
public enum SafeMigrationDatabaseObjectKind
{
    /// <summary>A base table.</summary>
    Table = 0,

    /// <summary>A table column.</summary>
    Column = 1,

    /// <summary>An index that is not owned by a constraint.</summary>
    Index = 2,

    /// <summary>A primary-key constraint.</summary>
    PrimaryKey = 3,

    /// <summary>A unique constraint.</summary>
    UniqueConstraint = 4,

    /// <summary>A check constraint.</summary>
    CheckConstraint = 5,

    /// <summary>A foreign-key constraint.</summary>
    ForeignKey = 6,
}

/// <summary>
/// Describes an additive live object outside the supplied canonical table
/// definitions. Unexpected objects are reported but are never deleted or
/// treated as semantically equivalent automatically.
/// </summary>
public sealed class SafeMigrationUnexpectedObject
{
    /// <summary>Initializes an immutable unexpected-object result.</summary>
    /// <param name="objectKind">The database-object family.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="table">The table name.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="code">The stable low-cardinality result code.</param>
    public SafeMigrationUnexpectedObject(
        SafeMigrationDatabaseObjectKind objectKind,
        string? schema,
        string? table,
        string name,
        string code
    )
    {
        if (!Enum.IsDefined(objectKind))
        {
            throw new ArgumentOutOfRangeException(nameof(objectKind));
        }

        ObjectKind = objectKind;
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        Table = SafeMigrationDefinitionValidator.Optional(table, nameof(table));
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Code = SafeMigrationDefinitionValidator.Required(code, nameof(code));
    }

    /// <summary>Gets the database-object family.</summary>
    public SafeMigrationDatabaseObjectKind ObjectKind { get; }

    /// <summary>Gets the relational schema when the provider supports schemas.</summary>
    public string? Schema { get; }

    /// <summary>Gets the owning table for table children.</summary>
    public string? Table { get; }

    /// <summary>Gets the live database-object name.</summary>
    public string Name { get; }

    /// <summary>Gets the stable low-cardinality finding code.</summary>
    public string Code { get; }
}
