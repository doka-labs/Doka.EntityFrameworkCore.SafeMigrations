namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Registers SafeMigrations design-time services and the Doka metadata
/// projection for MySQL and MariaDB consumers.
/// </summary>
internal sealed class MySqlSafeMigrationDesignTimeServices : IDesignTimeServices
{
    /// <summary>
    /// Initializes the registrar. EF Core requires a public constructor when it
    /// activates an internal design-time service through assembly metadata.
    /// </summary>
    public MySqlSafeMigrationDesignTimeServices() { }

    /// <inheritdoc />
    public void ConfigureDesignTimeServices(
        IServiceCollection serviceCollection
    )
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(serviceCollection);
        serviceCollection.TryAddEnumerable(
            ServiceDescriptor.Singleton<ISafeMigrationCreateIndexScaffoldingProjector,
                MySqlSafeMigrationCreateIndexScaffoldingProjector>());
    }
}
