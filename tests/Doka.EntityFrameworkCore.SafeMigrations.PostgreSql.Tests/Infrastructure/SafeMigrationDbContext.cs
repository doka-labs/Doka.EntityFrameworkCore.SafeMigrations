namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies SafeMigrations Db Context contracts in independently owned test state.</summary>
public class SafeMigrationDbContext : DbContext
{
    private readonly string? _connectionString;
    private readonly bool _registerSafeMigrations;

    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    public SafeMigrationDbContext(
        DbContextOptions<SafeMigrationDbContext> options
    ) : base(options)
    {
        _registerSafeMigrations = false;
    }

    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    public SafeMigrationDbContext(
        string connectionString,
        bool registerSafeMigrations = true
    )
    {
        _connectionString = connectionString;
        _registerSafeMigrations = registerSafeMigrations;
    }

    /// <inheritdoc />
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
                .MigrationsHistoryTable("__ApplicationDbContextMigrationsHistory"));

        if (_registerSafeMigrations)
        {
            optionsBuilder.UsePostgreSqlSafeMigrations<SafeMigrationDbContext>();
        }
    }
}
