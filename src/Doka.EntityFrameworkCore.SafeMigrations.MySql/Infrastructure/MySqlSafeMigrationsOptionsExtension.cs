namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlSafeMigrationsOptionsExtension : IDbContextOptionsExtension, ISafeMigrationScaffoldingOptions
{
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public Type? CanonicalContextType { get; private init; }

    /// <summary>Gets the mode consumed by the SafeMigrations design-time scaffolder.</summary>
    public SafeMigrationScaffoldingMode ScaffoldingMode { get; private init; }

    /// <summary>Gets the policy consumed by legacy-convergence scaffolding.</summary>
    public SafeMigrationPolicy LegacyConvergencePolicy { get; private init; } = SafeMigrationPolicy.ThrowIfDifferent;

    public void ApplyServices(
        IServiceCollection services
    ) => services.AddEntityFrameworkDokaMySqlSafeMigrations(CanonicalContextType);

    public static MySqlSafeMigrationsOptionsExtension WithCanonicalContext(
        Type? canonicalContextType,
        SafeMigrationScaffoldingMode scaffoldingMode,
        SafeMigrationPolicy legacyConvergencePolicy = SafeMigrationPolicy.ThrowIfDifferent
    ) => new()
    {
        CanonicalContextType = canonicalContextType,
        ScaffoldingMode = scaffoldingMode,
        LegacyConvergencePolicy = legacyConvergencePolicy,
    };

    public void Validate(
        IDbContextOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var providerOptions = options.FindExtension<MySqlOptionsExtension>();
        if (providerOptions is null)
        {
            throw new InvalidOperationException("MySQL safe migrations require Doka.EntityFrameworkCore.MySql.");
        }

        if (providerOptions.ConnectionString is not null)
        {
            MySqlSafeMigrationConnectionValidator.Validate(providerOptions.ConnectionString);
        }

        if (providerOptions.Connection is not null)
        {
            MySqlSafeMigrationConnectionValidator.Validate(providerOptions.Connection);
        }
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(
            IDbContextOptionsExtension extension
        ) : base(extension) { }

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "doka-mysql-safe-migrations ";

        private new MySqlSafeMigrationsOptionsExtension Extension =>
            (MySqlSafeMigrationsOptionsExtension)base.Extension;

        // Scaffolding settings change generated source only. Excluding them
        // avoids fragmenting EF's runtime service-provider cache without
        // changing a runtime service registration.
        public override int GetServiceProviderHashCode() => Extension.CanonicalContextType?.GetHashCode() ?? 0;

        public override void PopulateDebugInfo(
            IDictionary<string, string> debugInfo
        ) => debugInfo["Doka:MySqlSafeMigrations"] = Extension.CanonicalContextType?.FullName ?? "runtime";

        public override bool ShouldUseSameServiceProvider(
            DbContextOptionsExtensionInfo other
        ) => other is ExtensionInfo otherInfo
            && otherInfo.Extension.CanonicalContextType == Extension.CanonicalContextType;
    }
}
