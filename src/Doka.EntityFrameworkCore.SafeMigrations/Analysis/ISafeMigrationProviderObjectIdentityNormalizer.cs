namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Normalizes provider identifiers and qualifiers only after the provider has
/// established their physical identity for the active connection.
/// </summary>
internal interface ISafeMigrationProviderObjectIdentityNormalizer
{
    /// <summary>Gets the provider's physical object identifier comparer.</summary>
    StringComparer IdentifierComparer { get; }

    /// <summary>Normalizes an object identifier for provider-specific identity comparison.</summary>
    /// <param name="identifier">The captured database identifier.</param>
    /// <returns>The provider-normalized identifier.</returns>
    string NormalizeIdentifier(
        string identifier
    );

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
