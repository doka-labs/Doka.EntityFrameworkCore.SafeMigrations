namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed class PostgreSqlSafeMigrationsOptionsExtension
    : IDbContextOptionsExtension, ISafeMigrationScaffoldingOptions
{
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public Type BaselineGeneratorType { get; private init; } = typeof(NpgsqlMigrationsSqlGenerator);

    public Type? CanonicalContextType { get; private init; }

    /// <summary>Gets the mode consumed by the SafeMigrations design-time scaffolder.</summary>
    public SafeMigrationScaffoldingMode ScaffoldingMode { get; private init; }

    /// <summary>Gets the policy consumed by legacy-convergence scaffolding.</summary>
    public SafeMigrationPolicy LegacyConvergencePolicy { get; private init; } = SafeMigrationPolicy.ThrowIfDifferent;

    /// <summary>Gets whether excluded tables also exclude model-managed-data differences.</summary>
    public bool ExcludeModelManagedDataForExcludedTablesEnabled { get; private init; }

    public void ApplyServices(
        IServiceCollection services
    ) => services.AddPostgreSqlSafeMigrations(BaselineGeneratorType, CanonicalContextType);

    public static PostgreSqlSafeMigrationsOptionsExtension WithConfiguration(
        Type baselineGeneratorType,
        Type? canonicalContextType,
        SafeMigrationScaffoldingMode scaffoldingMode,
        SafeMigrationPolicy legacyConvergencePolicy = SafeMigrationPolicy.ThrowIfDifferent,
        bool excludeModelManagedDataForExcludedTables = false
    ) => new()
    {
        BaselineGeneratorType = baselineGeneratorType,
        CanonicalContextType = canonicalContextType,
        ScaffoldingMode = scaffoldingMode,
        LegacyConvergencePolicy = legacyConvergencePolicy,
        ExcludeModelManagedDataForExcludedTablesEnabled = excludeModelManagedDataForExcludedTables,
    };

    public void Validate(
        IDbContextOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var npgsqlAssembly = typeof(NpgsqlDbContextOptionsBuilderExtensions).Assembly;
        if (!options.Extensions.Any(extension => extension.Info.IsDatabaseProvider
                && extension.GetType()
                    .Assembly
                == npgsqlAssembly))
        {
            throw new InvalidOperationException("PostgreSQL safe migrations require the Npgsql EF Core provider.");
        }
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(
            IDbContextOptionsExtension extension
        ) : base(extension) { }

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "doka-postgresql-safe-migrations ";

        private new PostgreSqlSafeMigrationsOptionsExtension Extension =>
            (PostgreSqlSafeMigrationsOptionsExtension)base.Extension;

        // WHY: The ownership option changes runtime pending-model detection.
        // It must participate in EF's service-provider identity so a context
        // never reuses a differ configured for the opposite ownership rule.
        public override int GetServiceProviderHashCode() => HashCode.Combine(
            Extension.BaselineGeneratorType,
            Extension.CanonicalContextType,
            Extension.ExcludeModelManagedDataForExcludedTablesEnabled);

        public override void PopulateDebugInfo(
            IDictionary<string, string> debugInfo
        ) => debugInfo["Doka:PostgreSqlSafeMigrations"] = string.Concat(
            Extension.BaselineGeneratorType.FullName,
            ":",
            Extension.CanonicalContextType?.FullName ?? "runtime",
            ":exclude-model-data=",
            Extension.ExcludeModelManagedDataForExcludedTablesEnabled);

        public override bool ShouldUseSameServiceProvider(
            DbContextOptionsExtensionInfo other
        ) => other is ExtensionInfo otherInfo
            && otherInfo.Extension.BaselineGeneratorType == Extension.BaselineGeneratorType
            && otherInfo.Extension.CanonicalContextType == Extension.CanonicalContextType
            && otherInfo.Extension.ExcludeModelManagedDataForExcludedTablesEnabled
            == Extension.ExcludeModelManagedDataForExcludedTablesEnabled;
    }
}
