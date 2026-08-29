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

    public void ApplyServices(
        IServiceCollection services
    ) => services.AddPostgreSqlSafeMigrations(BaselineGeneratorType, CanonicalContextType);

    public static PostgreSqlSafeMigrationsOptionsExtension WithConfiguration(
        Type baselineGeneratorType,
        Type? canonicalContextType,
        SafeMigrationScaffoldingMode scaffoldingMode,
        SafeMigrationPolicy legacyConvergencePolicy = SafeMigrationPolicy.ThrowIfDifferent
    ) => new()
    {
        BaselineGeneratorType = baselineGeneratorType,
        CanonicalContextType = canonicalContextType,
        ScaffoldingMode = scaffoldingMode,
        LegacyConvergencePolicy = legacyConvergencePolicy,
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

        // Scaffolding settings change generated source only. Excluding them
        // avoids fragmenting EF's runtime service-provider cache without
        // changing a runtime service registration.
        public override int GetServiceProviderHashCode() => HashCode.Combine(
            Extension.BaselineGeneratorType,
            Extension.CanonicalContextType);

        public override void PopulateDebugInfo(
            IDictionary<string, string> debugInfo
        ) => debugInfo["Doka:PostgreSqlSafeMigrations"] = string.Concat(
            Extension.BaselineGeneratorType.FullName,
            ":",
            Extension.CanonicalContextType?.FullName ?? "runtime");

        public override bool ShouldUseSameServiceProvider(
            DbContextOptionsExtensionInfo other
        ) => other is ExtensionInfo otherInfo
            && otherInfo.Extension.BaselineGeneratorType == Extension.BaselineGeneratorType
            && otherInfo.Extension.CanonicalContextType == Extension.CanonicalContextType;
    }
}
