namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>
/// Registers the SafeMigrations scaffolder and SQL Server's supported index
/// metadata projection for EF Core design-time operations.
/// </summary>
internal sealed class SqlServerSafeMigrationDesignTimeServices : IDesignTimeServices
{
    /// <summary>
    /// Initializes the registrar. EF Core activates this type from assembly
    /// metadata and requires a public constructor even though the type is internal.
    /// </summary>
    public SqlServerSafeMigrationDesignTimeServices() { }

    /// <inheritdoc />
    public void ConfigureDesignTimeServices(
        IServiceCollection serviceCollection
    )
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        new SafeMigrationDesignTimeServices().ConfigureDesignTimeServices(serviceCollection);
        serviceCollection.TryAddEnumerable(
            ServiceDescriptor.Singleton<ISafeMigrationCreateIndexScaffoldingProjector,
                SqlServerSafeMigrationCreateIndexScaffoldingProjector>());
    }
}
