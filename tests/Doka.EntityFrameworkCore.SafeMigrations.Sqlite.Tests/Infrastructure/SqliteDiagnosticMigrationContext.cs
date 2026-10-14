namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Provides an empty target model for isolated actual-migrator diagnostic regressions.</summary>
internal sealed class SqliteDiagnosticMigrationContext : DbContext
{
    private readonly DbConnection _connection;
    private readonly IServiceProvider _internalServices;

    /// <summary>Initializes the context with the fixture's already-open in-memory database.</summary>
    /// <param name="connection">The fixture's isolated open connection.</param>
    /// <param name="internalServices">The class-owned provider, never a process-wide cache entry.</param>
    internal SqliteDiagnosticMigrationContext(DbConnection connection, IServiceProvider internalServices)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(internalServices);

        _connection = connection;
        _internalServices = internalServices;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(_connection,
            provider => provider.MigrationsAssembly(typeof(SqliteDiagnosticMigrationContext).Assembly.FullName));

        // WHY: This additional fixed-family model must not grow EF's global
        // provider cache or mask ManyServiceProvidersCreatedWarning in other tests.
        optionsBuilder.UseSqliteSafeMigrations();
        optionsBuilder.UseInternalServiceProvider(_internalServices);
    }
}

/// <summary>Adds a required column without a replacement to exercise managed runtime rejection.</summary>
[DbContext(typeof(SqliteDiagnosticMigrationContext))]
[Migration(MigrationIdentifier)]
public sealed class SqliteDiagnosticGuardMigration : Migration
{
    /// <summary>Gets the EF-valid isolated fixture identity.</summary>
    public const string MigrationIdentifier = "20261014000300_SqliteDiagnosticGuard";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumnIfNotExists<string>("RequiredValue", "diagnostic_migration", type: "TEXT");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumnIfExists("RequiredValue", "diagnostic_migration");
    }
}
