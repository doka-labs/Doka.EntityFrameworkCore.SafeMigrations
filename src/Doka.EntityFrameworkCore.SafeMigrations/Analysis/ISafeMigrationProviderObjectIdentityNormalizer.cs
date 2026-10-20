namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Normalizes provider identifiers and qualifiers only after the provider has
/// established their physical identity for the active connection.
/// </summary>
internal interface ISafeMigrationProviderObjectIdentityNormalizer
{
    /// <summary>Gets the provider's physical object identifier comparer.</summary>
    StringComparer IdentifierComparer { get; }

    /// <summary>Gets the provider's physical foreign-key member-name comparer.</summary>
    StringComparer ForeignKeyIdentifierComparer => IdentifierComparer;

    /// <summary>Gets the physical column-member comparer without changing table or schema identity.</summary>
    StringComparer ColumnIdentifierComparer => IdentifierComparer;

    /// <summary>Gets the native opening identifier quote for captured-source rewrites, or null if unverified.</summary>
    char? CapturedSqlIdentifierDelimiter => null;

    /// <summary>Gets whether captured unquoted identifiers use PostgreSQL's native lower-case binding.</summary>
    bool FoldUnquotedCapturedSqlIdentifiers => false;

    /// <summary>Gets the physical index-member comparer without changing table or schema identity.</summary>
    StringComparer IndexIdentifierComparer => IdentifierComparer;

    /// <summary>Gets whether index names share one relation namespace across verified schema owners.</summary>
    bool IndexUsesSchemaScopedIdentity => false;

    /// <summary>Gets whether PK/UNIQUE names identify equally named schema-scoped backing indexes.</summary>
    /// <remarks>SQLite constraint symbols do not identify its generated sqlite_autoindex names.</remarks>
    bool KeyConstraintsUseSchemaScopedBackingIndexNames => false;

    /// <summary>Creates an ordinal-scoped final-catalog normalizer for mutable provider identity.</summary>
    /// <param name="operations">The complete ordered migration stream.</param>
    /// <returns>The provider's verified postflight identity view.</returns>
    ISafeMigrationProviderObjectIdentityNormalizer GetPostflightIdentityNormalizer(
        IReadOnlyList<MigrationOperation> operations
    ) => this;

    /// <summary>Checks whether fresh final-catalog identity proves the historical operation's owner.</summary>
    /// <param name="intent">The operation's owner and reference contract.</param>
    /// <param name="ordinal">Its complete-stream position, including ordinary provider operations.</param>
    /// <returns>False when a later opaque or qualifier-changing boundary prevents identity proof.</returns>
    bool IsPostflightIdentityProven(SafeMigrationIntent intent, int ordinal) => true;

    /// <summary>Normalizes an object identifier for provider-specific identity comparison.</summary>
    /// <param name="identifier">The captured database identifier.</param>
    /// <returns>The provider-normalized identifier.</returns>
    string NormalizeIdentifier(
        string identifier
    );

    /// <summary>Canonicalizes typed column identity separately from physical owner identity.</summary>
    /// <param name="identifier">The typed column member, never SQL text or a key value.</param>
    /// <returns>Identical text exactly when the provider certifies identical column members.</returns>
    string NormalizeColumnIdentifier(string identifier) => NormalizeIdentifier(identifier);

    /// <summary>Normalizes an optional schema or database qualifier for ordered projection.</summary>
    /// <param name="schema">The captured provider qualifier.</param>
    /// <returns>The provider-normalized qualifier.</returns>
    string? NormalizeSchema(
        string? schema
    );

    /// <summary>Determines whether provider analysis rejected the requested physical identity.</summary>
    /// <param name="analysis">The immutable live provider analysis.</param>
    /// <returns>True when ordered projection must preserve the identity rejection.</returns>
    bool IsObjectIdentityMismatch(
        SafeMigrationProviderAnalysis analysis
    );
}
