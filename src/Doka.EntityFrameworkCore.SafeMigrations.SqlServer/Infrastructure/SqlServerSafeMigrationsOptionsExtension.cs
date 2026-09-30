namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed class SqlServerSafeMigrationsOptionsExtension
    : IDbContextOptionsExtension, ISafeMigrationScaffoldingOptions
{
    private DbContextOptionsExtensionInfo? _info;

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <summary>Gets the ordinary EF generator preserved beneath guarded SQL generation.</summary>
    public Type BaselineGeneratorType { get; private init; } = typeof(SqlServerMigrationsSqlGenerator);

    /// <summary>Gets the canonical context used to validate the migration model, when explicitly configured.</summary>
    public Type? CanonicalContextType { get; private init; }

    /// <summary>Gets the mode consumed by the SafeMigrations design-time scaffolder.</summary>
    public SafeMigrationScaffoldingMode ScaffoldingMode { get; private init; }

    /// <summary>Gets the policy consumed by legacy-convergence scaffolding.</summary>
    public SafeMigrationPolicy LegacyConvergencePolicy { get; private init; } = SafeMigrationPolicy.ThrowIfDifferent;

    /// <summary>Gets whether excluded tables also exclude model-managed-data differences.</summary>
    public bool ExcludeModelManagedDataForExcludedTablesEnabled { get; private init; }

    /// <inheritdoc />
    public void ApplyServices(
        IServiceCollection services
    ) => services.AddSqlServerSafeMigrations(BaselineGeneratorType, CanonicalContextType);

    /// <summary>
    /// Creates the immutable provider configuration consumed by runtime and design-time registration.
    /// </summary>
    /// <param name="baselineGeneratorType">The underlying EF SQL generator.</param>
    /// <param name="canonicalContextType">The optional canonical migration context.</param>
    /// <param name="scaffoldingMode">The migration-authoring mode.</param>
    /// <param name="legacyConvergencePolicy">The explicit policy for legacy-convergence operations.</param>
    /// <param name="excludeModelManagedDataForExcludedTables">
    /// Whether excluded tables also exclude seed differences.
    /// </param>
    /// <returns>The new provider configuration.</returns>
    public static SqlServerSafeMigrationsOptionsExtension WithConfiguration(
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

    /// <inheritdoc />
    public void Validate(
        IDbContextOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var sqlServerAssembly = typeof(SqlServerDbContextOptionsExtensions).Assembly;
        if (!options.Extensions.Any(extension => extension.Info.IsDatabaseProvider
                && extension.GetType()
                    .Assembly
                == sqlServerAssembly))
        {
            throw new InvalidOperationException("SQL Server safe migrations require the EF Core SQL Server provider.");
        }
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        /// <summary>Creates the EF service-provider identity for this options extension.</summary>
        /// <param name="extension">The immutable provider configuration.</param>
        public ExtensionInfo(
            IDbContextOptionsExtension extension
        ) : base(extension) { }

        /// <inheritdoc />
        public override bool IsDatabaseProvider => false;

        /// <inheritdoc />
        public override string LogFragment => "doka-sqlserver-safe-migrations ";

        private new SqlServerSafeMigrationsOptionsExtension Extension =>
            (SqlServerSafeMigrationsOptionsExtension)base.Extension;

        // WHY: The ownership option changes runtime pending-model detection.
        // It must participate in EF's service-provider identity so a context
        // never reuses a differ configured for the opposite ownership rule.
        /// <inheritdoc />
        public override int GetServiceProviderHashCode() => HashCode.Combine(
            Extension.BaselineGeneratorType,
            Extension.CanonicalContextType,
            Extension.ExcludeModelManagedDataForExcludedTablesEnabled);

        /// <inheritdoc />
        public override void PopulateDebugInfo(
            IDictionary<string, string> debugInfo
        ) => debugInfo["Doka:SqlServerSafeMigrations"] = string.Concat(
            Extension.BaselineGeneratorType.FullName,
            ":",
            Extension.CanonicalContextType?.FullName ?? "runtime",
            ":exclude-model-data=",
            Extension.ExcludeModelManagedDataForExcludedTablesEnabled);

        /// <inheritdoc />
        public override bool ShouldUseSameServiceProvider(
            DbContextOptionsExtensionInfo other
        ) => other is ExtensionInfo otherInfo
            && otherInfo.Extension.BaselineGeneratorType == Extension.BaselineGeneratorType
            && otherInfo.Extension.CanonicalContextType == Extension.CanonicalContextType
            && otherInfo.Extension.ExcludeModelManagedDataForExcludedTablesEnabled
            == Extension.ExcludeModelManagedDataForExcludedTablesEnabled;
    }
}
