namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Hosts an isolated SQL Server model for provider integration tests.
/// </summary>
public sealed class SafeMigrationDbContext : DbContext
{
    /// <summary>The command timeout applied to every live contract test.</summary>
    public const int CommandTimeoutSeconds = 120;

    private readonly string? _connectionString;
    private readonly bool _registerSafeMigrations;

    /// <summary>
    /// Creates a test context with an optional SafeMigrations registration.
    /// </summary>
    public SafeMigrationDbContext(
        string connectionString,
        bool registerSafeMigrations = true
    )
    {
        _connectionString = connectionString;
        _registerSafeMigrations = registerSafeMigrations;
    }

    /// <summary>
    /// Creates a context whose SQL Server and SafeMigrations services were configured externally.
    /// </summary>
    public SafeMigrationDbContext(
        DbContextOptions<SafeMigrationDbContext> options
    ) : base(options)
    {
        _registerSafeMigrations = false;
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

        optionsBuilder.UseSqlServer(
            _connectionString ?? throw new InvalidOperationException("A SQL Server connection string is required."),
            provider => provider
                .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)

                // WHY: Live contracts use an explicit finite wait budget rather than a driver default.
                // This timeout is neither performance evidence nor a diagnosis of an exceeded budget.
                .CommandTimeout(CommandTimeoutSeconds));

        if (_registerSafeMigrations)
        {
            optionsBuilder.UseSqlServerSafeMigrations<SafeMigrationDbContext>();
        }
    }
}
