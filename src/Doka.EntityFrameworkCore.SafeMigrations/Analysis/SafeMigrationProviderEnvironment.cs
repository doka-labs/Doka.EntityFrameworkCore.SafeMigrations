namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Describes the live provider environment used by a report.</summary>
public sealed class SafeMigrationProviderEnvironment
{
    /// <summary>Initializes provider environment metadata.</summary>
    /// <param name="providerId">The stable SafeMigrations provider identifier.</param>
    /// <param name="engineFamily">The low-cardinality database engine family.</param>
    /// <param name="serverVersion">The exact server version reported by the connection.</param>
    public SafeMigrationProviderEnvironment(
        string providerId,
        string engineFamily,
        string serverVersion
    )
    {
        ProviderId = SafeMigrationDefinitionValidator.Required(providerId, nameof(providerId));
        EngineFamily = SafeMigrationDefinitionValidator.Required(engineFamily, nameof(engineFamily));
        ServerVersion = SafeMigrationDefinitionValidator.Required(serverVersion, nameof(serverVersion));

        if (EngineFamily is not ("mysql" or "mariadb" or "postgresql"))
        {
            throw new ArgumentException(
                "The engine family must be mysql, mariadb, or postgresql.",
                nameof(engineFamily));
        }
    }

    /// <summary>Gets the stable SafeMigrations provider identifier.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the low-cardinality database engine family.</summary>
    public string EngineFamily { get; }

    /// <summary>Gets the exact version reported by the live server connection.</summary>
    public string ServerVersion { get; }
}
