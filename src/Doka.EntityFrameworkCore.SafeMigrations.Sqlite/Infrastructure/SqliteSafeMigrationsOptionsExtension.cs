namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Stores SQLite SafeMigrations options in the EF Core options graph.</summary>
internal sealed class SqliteSafeMigrationsOptionsExtension : IDbContextOptionsExtension,
    ISafeMigrationScaffoldingOptions
{
    private DbContextOptionsExtensionInfo? _info;

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public Type BaselineGeneratorType { get; private init; } = typeof(SqliteMigrationsSqlGenerator);

    /// <inheritdoc />
    public Type? CanonicalContextType { get; private init; }

    /// <inheritdoc />
    public SafeMigrationScaffoldingMode ScaffoldingMode { get; private init; }

    /// <inheritdoc />
    public SafeMigrationPolicy LegacyConvergencePolicy { get; private init; } = SafeMigrationPolicy.ThrowIfDifferent;

    /// <inheritdoc />
    public bool ExcludeModelManagedDataForExcludedTablesEnabled { get; private init; }

    /// <inheritdoc />
    public void ApplyServices(
        IServiceCollection services
    ) => services.AddSqliteSafeMigrations(BaselineGeneratorType, CanonicalContextType);

    /// <summary>Creates an immutable configured options extension.</summary>
    public static SqliteSafeMigrationsOptionsExtension WithConfiguration(
        Type baselineGeneratorType,
        Type? canonicalContextType,
        SafeMigrationScaffoldingMode scaffoldingMode,
        SafeMigrationPolicy legacyConvergencePolicy,
        bool excludeModelManagedDataForExcludedTables
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

        var sqliteAssembly = typeof(SqliteDbContextOptionsBuilderExtensions).Assembly;
        if (!options.Extensions.Any(extension => extension.Info.IsDatabaseProvider
                && extension.GetType()
                    .Assembly
                == sqliteAssembly))
        {
            throw new InvalidOperationException(
                "SQLite safe migrations require the Microsoft EF Core SQLite provider.");
        }
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(
            IDbContextOptionsExtension extension
        ) : base(extension) { }

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "doka-sqlite-safe-migrations ";

        private new SqliteSafeMigrationsOptionsExtension Extension =>
            (SqliteSafeMigrationsOptionsExtension)base.Extension;

        public override int GetServiceProviderHashCode() => HashCode.Combine(
            Extension.BaselineGeneratorType,
            Extension.CanonicalContextType,
            Extension.ExcludeModelManagedDataForExcludedTablesEnabled);

        public override void PopulateDebugInfo(
            IDictionary<string, string> debugInfo
        ) => debugInfo["Doka:SqliteSafeMigrations"] = string.Concat(
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
