namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Supplies bounded provider-certified live facts for otherwise sparse ordered projection.</summary>
/// <remarks>
/// Facts describe the initial live catalog, not target-model wishes. Core must
/// reject changed, dropped or unknown physical lifetimes before consulting them.
/// </remarks>
internal interface ISafeMigrationLivePrerequisiteSource
{
    /// <summary>Checks independently observed presence of one requested live column.</summary>
    /// <param name="table">The physical table owner.</param>
    /// <param name="schema">The provider-qualified schema, or null for its active default.</param>
    /// <param name="column">The requested physical column.</param>
    /// <returns>Whether the captured catalog independently proves owner and column presence.</returns>
    bool HasLiveColumn(string table, string? schema, string column);

    /// <summary>Requires native metadata qualification even when accepted CLR definitions look equal.</summary>
    /// <param name="intent">The exact affected FK contract.</param>
    /// <returns>Whether this invocation captured native owner and endpoint evidence for this FK.</returns>
    /// <remarks>
    /// Raw null collation facets are not a certificate that two existing tables
    /// inherit the same effective collation. Providers opt in only for captured
    /// physical facts, including defaults of an actually accepted new lifetime.
    /// Other projected owners retain the default-false separate contract.
    /// </remarks>
    bool RequiresLiveForeignKeyQualification(EnsureForeignKeyIntent intent) => false;

    /// <summary>
    /// Requalifies a dynamic native FK blocker after certified accepted prerequisite mutations.
    /// </summary>
    /// <param name="intent">The exact FK contract whose native prerequisite changed.</param>
    /// <param name="liveAnalysis">The dynamic unsupported observation, not a static capability rejection.</param>
    /// <param name="columns">The currently accepted ordered physical definitions.</param>
    /// <returns>Whether the original blocker is independently discharged in the current physical lifetime.</returns>
    /// <remarks>
    /// This permits only prerequisite re-evaluation, never Matching or Ready.
    /// Core separately requires compatible current structure and fresh row proof.
    /// Identity, static unsupported, opaque SQL and deferred boundaries retain precedence.
    /// </remarks>
    bool CanRequalifyLiveForeignKeyPrerequisites(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns) => false;

    /// <summary>
    /// Certifies safe removal of a captured index after exact accepted dependency removals.
    /// </summary>
    /// <param name="intent">The exact physical index to remove.</param>
    /// <param name="liveAnalysis">The original dynamic unsupported observation.</param>
    /// <param name="columns">The currently accepted ordered physical definitions and lifetimes.</param>
    /// <returns>Whether native index presence and removal of every affected dependency are proven.</returns>
    /// <remarks>
    /// This certifies metadata-only removal, not constraint validity or application rows.
    /// An equivalent no-op alias is not proof that a physical dependency was removed.
    /// Identity, static unsupported, opaque SQL and deferred boundaries retain precedence.
    /// </remarks>
    bool CanRequalifyLiveDropIndexPrerequisites(
        DropIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns) => false;

    /// <summary>Qualifies mixed live/projected FK endpoints and returns actual live candidate-key identities.</summary>
    /// <param name="intent">The exact authored foreign-key contract.</param>
    /// <param name="columns">Accepted ordered column definitions, never future or rejected operations.</param>
    /// <returns>
    /// Null when endpoint or owner compatibility is unproven; otherwise verified
    /// native candidate keys, possibly empty if only an accepted projected key can satisfy the contract.
    /// </returns>
    /// <remarks>
    /// This proves physical metadata only. Core separately owns surviving key
    /// identities and current empty-table or newly materialized NULL row proofs.
    /// Runtime must still revalidate rows and apply native constraint validation.
    /// </remarks>
    IReadOnlyList<SafeMigrationLiveCandidateKey>? GetLiveForeignKeyCandidateKeys(
        EnsureForeignKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns);
}

/// <summary>Identifies a real native candidate key without conflating its constraint and backing index.</summary>
/// <param name="Kind">Whether the candidate is a primary key, unique constraint or independent unique index.</param>
/// <param name="PhysicalName">The independently observed native key name.</param>
/// <param name="BackingIndexName">Its independently observed backing index, when separate from the constraint.</param>
internal readonly record struct SafeMigrationLiveCandidateKey(
    SafeMigrationDatabaseObjectKind Kind,
    string PhysicalName,
    string? BackingIndexName = null);
