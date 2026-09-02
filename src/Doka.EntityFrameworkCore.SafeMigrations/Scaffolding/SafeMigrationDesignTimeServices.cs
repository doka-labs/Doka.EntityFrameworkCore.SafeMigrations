namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Registers the SafeMigrations C# scaffolder with EF Core design-time tools.
/// </summary>
/// <remarks>
/// Applications normally do not call this type. Provider package build assets
/// expose it to EF Core through <see cref="DesignTimeServicesReferenceAttribute"/>.
/// </remarks>
internal sealed class SafeMigrationDesignTimeServices : IDesignTimeServices
{
    /// <summary>
    /// Initializes the design-time service registrar. The public constructor is
    /// required so EF Core can activate this internal type through package metadata.
    /// </summary>
    public SafeMigrationDesignTimeServices() { }

    /// <inheritdoc />
    public void ConfigureDesignTimeServices(
        IServiceCollection serviceCollection
    )
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        serviceCollection.AddSingleton(static provider =>
            SafeMigrationScaffoldingConfiguration.From(provider.GetService<IDbContextOptions>()));
        serviceCollection.Replace(
            ServiceDescriptor.Singleton<ICSharpMigrationOperationGenerator,
                SafeMigrationCSharpMigrationOperationGenerator>());
        serviceCollection.Replace(
            ServiceDescriptor.Singleton<IMigrationsCodeGeneratorSelector,
                SafeMigrationMigrationsCodeGeneratorSelector>());
        SafeMigrationServiceCollectionDecorator.DecorateMigrationsModelDiffer(serviceCollection);
    }
}
