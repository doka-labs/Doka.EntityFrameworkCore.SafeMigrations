namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public class SafeMigrationDbContext : DbContext
{
    private readonly string? _connectionString;
    private readonly bool _registerSafeMigrations;

    public SafeMigrationDbContext(
        DbContextOptions<SafeMigrationDbContext> options
    ) : base(options)
    {
        _registerSafeMigrations = false;
    }

    public SafeMigrationDbContext(
        string connectionString,
        bool registerSafeMigrations = true
    )
    {
        _connectionString = connectionString;
        _registerSafeMigrations = registerSafeMigrations;
    }

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        if (optionsBuilder.IsConfigured)
        {
            return;
        }

        optionsBuilder.UseNpgsql(
            _connectionString ?? throw new InvalidOperationException("A PostgreSQL connection string is required."),
            provider => provider
                .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__CoreDbContextMigrationsHistory"));
        if (_registerSafeMigrations)
        {
            optionsBuilder.UsePostgreSqlSafeMigrations();
        }
    }
}
