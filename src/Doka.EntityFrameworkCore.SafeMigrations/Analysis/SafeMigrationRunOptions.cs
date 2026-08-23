namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Defines explicit, immutable metadata for one analysis or verification run.</summary>
public sealed class SafeMigrationRunOptions
{
    /// <summary>Initializes run metadata.</summary>
    /// <param name="instanceId">
    /// A caller-generated pseudonymous database-instance identifier. It must not
    /// contain a connection string, host name, database name or credential.
    /// </param>
    /// <param name="targetMigrationId">The intended target migration when known.</param>
    /// <param name="expectedModelFingerprint">The required canonical model fingerprint.</param>
    public SafeMigrationRunOptions(
        string instanceId,
        string? targetMigrationId = null,
        string? expectedModelFingerprint = null
    )
    {
        InstanceId = SafeMigrationDefinitionValidator.Required(instanceId, nameof(instanceId));
        TargetMigrationId = SafeMigrationDefinitionValidator.Optional(targetMigrationId, nameof(targetMigrationId));
        ExpectedModelFingerprint = SafeMigrationDefinitionValidator.Optional(
            expectedModelFingerprint,
            nameof(expectedModelFingerprint));

        if (ExpectedModelFingerprint is not null)
        {
            SafeMigrationModelFingerprint.ValidateFingerprint(
                ExpectedModelFingerprint,
                nameof(expectedModelFingerprint));
        }
    }

    /// <summary>Gets the caller-generated pseudonymous instance identifier.</summary>
    public string InstanceId { get; }

    /// <summary>Gets the intended target migration when specified.</summary>
    public string? TargetMigrationId { get; }

    /// <summary>Gets the expected canonical model fingerprint when specified.</summary>
    public string? ExpectedModelFingerprint { get; }
}
