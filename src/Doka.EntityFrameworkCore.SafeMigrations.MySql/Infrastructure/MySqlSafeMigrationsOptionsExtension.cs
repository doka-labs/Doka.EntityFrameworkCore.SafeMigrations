namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlSafeMigrationsOptionsExtension : IDbContextOptionsExtension, ISafeMigrationScaffoldingOptions
{
    private DbContextOptionsExtensionInfo? _info;

    public Type? CanonicalContextType { get; private init; }

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(
        IServiceCollection services
    ) => services.AddEntityFrameworkDokaMySqlSafeMigrations(CanonicalContextType);

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
    }

    /// <summary>Gets the mode consumed by the SafeMigrations design-time scaffolder.</summary>
    public SafeMigrationScaffoldingMode ScaffoldingMode { get; private init; }

    /// <summary>Gets the policy consumed by legacy-convergence scaffolding.</summary>
    public SafeMigrationPolicy LegacyConvergencePolicy { get; private init; } = SafeMigrationPolicy.ThrowIfDifferent;

    /// <summary>Gets whether excluded tables also exclude model-managed-data differences.</summary>
    public bool ExcludeModelManagedDataForExcludedTablesEnabled { get; private init; }

    public static MySqlSafeMigrationsOptionsExtension WithCanonicalContext(
        Type? canonicalContextType,
        SafeMigrationScaffoldingMode scaffoldingMode,
        SafeMigrationPolicy legacyConvergencePolicy = SafeMigrationPolicy.ThrowIfDifferent,
        bool excludeModelManagedDataForExcludedTables = false
    ) => new()
    {
        CanonicalContextType = canonicalContextType,
        ScaffoldingMode = scaffoldingMode,
        LegacyConvergencePolicy = legacyConvergencePolicy,
        ExcludeModelManagedDataForExcludedTablesEnabled = excludeModelManagedDataForExcludedTables,
    };

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(
            IDbContextOptionsExtension extension
        ) : base(extension) { }

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "doka-mysql-safe-migrations ";

        private new MySqlSafeMigrationsOptionsExtension Extension
            => (MySqlSafeMigrationsOptionsExtension)base.Extension;

        // WHY: The ownership option changes runtime pending-model detection.
        // It must participate in EF's service-provider identity so a context
        // never reuses a differ configured for the opposite ownership rule.
        public override int GetServiceProviderHashCode() => HashCode.Combine(
            Extension.CanonicalContextType,
            Extension.ExcludeModelManagedDataForExcludedTablesEnabled);

        public override void PopulateDebugInfo(
            IDictionary<string, string> debugInfo
        ) => debugInfo["Doka:MySqlSafeMigrations"] = string.Concat(
            Extension.CanonicalContextType?.FullName ?? "runtime",
            ":exclude-model-data=",
            Extension.ExcludeModelManagedDataForExcludedTablesEnabled);

        public override bool ShouldUseSameServiceProvider(
            DbContextOptionsExtensionInfo other
        ) => other is ExtensionInfo otherInfo
            && otherInfo.Extension.CanonicalContextType == Extension.CanonicalContextType
            && otherInfo.Extension.ExcludeModelManagedDataForExcludedTablesEnabled
            == Extension.ExcludeModelManagedDataForExcludedTablesEnabled;
    }
}
