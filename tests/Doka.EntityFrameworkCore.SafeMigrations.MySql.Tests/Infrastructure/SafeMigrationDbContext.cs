namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public class SafeMigrationDbContext : DbContext
{
    private readonly string? _connectionString;
    private readonly bool _excludeModelManagedDataForExcludedTables;
    private readonly bool _registerSafeMigrations;
    private readonly MySqlServerVersion? _serverVersion;

    public SafeMigrationDbContext(
        DbContextOptions<SafeMigrationDbContext> options
    ) : base(options)
    {
        _registerSafeMigrations = false;
    }

    public SafeMigrationDbContext(
        string connectionString,
        MySqlServerVersion serverVersion,
        bool registerSafeMigrations = true,
        bool excludeModelManagedDataForExcludedTables = false
    )
    {
        _connectionString = connectionString;
        _serverVersion = serverVersion;
        _registerSafeMigrations = registerSafeMigrations;
        _excludeModelManagedDataForExcludedTables = excludeModelManagedDataForExcludedTables;
    }

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        if (optionsBuilder.IsConfigured)
        {
            return;
        }

        optionsBuilder.UseMySql(
            _connectionString ?? throw new InvalidOperationException("A MySQL connection string is required."),
            _serverVersion ?? throw new InvalidOperationException("A MySQL server version is required."),
            provider => provider
                .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__ApplicationDbContextMigrationsHistory"));

        if (_registerSafeMigrations)
        {
            optionsBuilder.UseMySqlSafeMigrations<SafeMigrationDbContext>(safeMigrations =>
            {
                if (_excludeModelManagedDataForExcludedTables)
                {
                    safeMigrations.ExcludeModelManagedDataForExcludedTables();
                }
            });
        }
    }
}
