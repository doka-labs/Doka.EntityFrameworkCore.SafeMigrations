namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Supplies the shared model shape for strict and legacy SQL Server EF tooling qualification.
/// </summary>
public abstract class SqlServerScaffoldingModelDbContext : DbContext
{
    /// <summary>
    /// Gets the model used to exercise tables, indexes, and model-managed data.
    /// </summary>
    public DbSet<SqlServerScaffoldingUser> Users => Set<SqlServerScaffoldingUser>();

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<SqlServerScaffoldingUser>(entity =>
        {
            entity.ToTable("scaffolding_users");
            entity.HasKey(user => user.Id);

            entity.Property(user => user.Email)
                .HasMaxLength(320)
                .IsRequired();

            entity.Property(user => user.DisplayName)
                .HasMaxLength(120)
                .IsRequired();

            entity.HasIndex(user => user.Email)
                .IsUnique()
                .IncludeProperties(user => user.DisplayName);

            entity.HasData(new SqlServerScaffoldingUser
            {
                Id = 1,
                Email = "administrator@example.test",
                DisplayName = "Administrator",
            });
        });
    }
}

/// <summary>
/// Supplies strict SQL Server migration scaffolding for EF CLI, scripts, and bundles.
/// </summary>
public sealed class SqlServerSafeMigrationScaffoldingDbContext : SqlServerScaffoldingModelDbContext
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates the strict design-time context with an explicit connection string.
    /// </summary>
    public SqlServerSafeMigrationScaffoldingDbContext(
        string connectionString
    )
    {
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlServer(
            _connectionString,
            provider => provider
                .MigrationsAssembly(typeof(SqlServerSafeMigrationScaffoldingDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__SqlServerSafeMigrationsHistory"));
        optionsBuilder.UseSqlServerSafeMigrations<SqlServerSafeMigrationScaffoldingDbContext>();
    }
}

/// <summary>
/// Supplies legacy-convergence SQL Server migration scaffolding over the same model.
/// </summary>
public sealed class SqlServerLegacySafeMigrationScaffoldingDbContext : SqlServerScaffoldingModelDbContext
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates the legacy-convergence design-time context with an explicit connection string.
    /// </summary>
    public SqlServerLegacySafeMigrationScaffoldingDbContext(
        string connectionString
    )
    {
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlServer(
            _connectionString,
            provider => provider
                .MigrationsAssembly(typeof(SqlServerLegacySafeMigrationScaffoldingDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__SqlServerLegacySafeMigrationsHistory"));
        optionsBuilder.UseSqlServerSafeMigrations<SqlServerLegacySafeMigrationScaffoldingDbContext>(options =>
        {
            options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence);
            options.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
        });
    }
}

/// <summary>
/// Creates the SQL Server design-time context for EF tooling.
/// </summary>
public sealed class SqlServerSafeMigrationScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<SqlServerSafeMigrationScaffoldingDbContext>
{
    /// <inheritdoc />
    public SqlServerSafeMigrationScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        var connectionString = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "SAFE_MIGRATIONS_CONNECTION_STRING is required for SQL Server EF tooling.");
        }

        return new SqlServerSafeMigrationScaffoldingDbContext(connectionString);
    }
}

/// <summary>
/// Creates the SQL Server legacy-convergence design-time context for EF tooling.
/// </summary>
public sealed class SqlServerLegacySafeMigrationScaffoldingDbContextFactory
    : IDesignTimeDbContextFactory<SqlServerLegacySafeMigrationScaffoldingDbContext>
{
    /// <inheritdoc />
    public SqlServerLegacySafeMigrationScaffoldingDbContext CreateDbContext(
        string[] args
    )
    {
        ArgumentNullException.ThrowIfNull(args);

        var connectionString = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "SAFE_MIGRATIONS_CONNECTION_STRING is required for SQL Server EF tooling.");
        }

        return new SqlServerLegacySafeMigrationScaffoldingDbContext(connectionString);
    }
}

/// <summary>
/// A model-managed row for SQL Server scaffolding qualification.
/// </summary>
public sealed class SqlServerScaffoldingUser
{
    /// <summary>Gets or sets the user key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the indexed email address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the non-key column included by the email index.</summary>
    public string DisplayName { get; set; } = string.Empty;
}
