namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Identifies a database collation without encoding qualification into a
/// delimiter-sensitive string.
/// </summary>
public sealed class SafeMigrationCollationIdentifier : IEquatable<SafeMigrationCollationIdentifier>
{
    /// <summary>Initializes a collation identity.</summary>
    /// <param name="name">The collation name.</param>
    /// <param name="schema">The optional schema containing the collation.</param>
    public SafeMigrationCollationIdentifier(
        string name,
        string? schema = null
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
    }

    /// <summary>Gets the collation name.</summary>
    public string Name { get; }

    /// <summary>Gets the containing schema when the provider supports qualified collations.</summary>
    public string? Schema { get; }

    /// <inheritdoc />
    public bool Equals(
        SafeMigrationCollationIdentifier? other
    ) => other is not null
        && StringComparer.Ordinal.Equals(Name, other.Name)
        && StringComparer.Ordinal.Equals(Schema, other.Schema);

    /// <inheritdoc />
    public override bool Equals(
        object? obj
    ) => obj is SafeMigrationCollationIdentifier other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Name),
        Schema is null ? 0 : StringComparer.Ordinal.GetHashCode(Schema));
}
