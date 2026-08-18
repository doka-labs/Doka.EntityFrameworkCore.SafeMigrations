namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed class PostgreSqlSafeMigrationsOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(
        IServiceCollection services
    ) => services.AddPostgreSqlSafeMigrations();

    public void Validate(
        IDbContextOptions options
    ) => ArgumentNullException.ThrowIfNull(options);

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(
            IDbContextOptionsExtension extension
        ) : base(extension) { }

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "doka-postgresql-safe-migrations ";

        public override int GetServiceProviderHashCode() => 0;

        public override void PopulateDebugInfo(
            IDictionary<string, string> debugInfo
        ) => debugInfo["Doka:PostgreSqlSafeMigrations"] = "1";

        public override bool ShouldUseSameServiceProvider(
            DbContextOptionsExtensionInfo other
        ) => other is ExtensionInfo;
    }
}
