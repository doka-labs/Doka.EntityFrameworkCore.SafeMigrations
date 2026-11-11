namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public class SafeMigrationDbContext : DbContext
{
    private readonly string? _connectionString;
    private readonly bool _excludeModelManagedDataForExcludedTables;
    private readonly bool _registerSafeMigrations;
    private readonly MySqlServerVersion? _serverVersion;
    private ServiceProvider? _ownedServices;

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

        if (!registerSafeMigrations)
        {
            // WHY: Missing-handler probes require an intentionally different native-only profile.
            // Own that rare root rather than adding it to EF's process-wide provider cache.
            _ownedServices = new ServiceCollection()
                .AddEntityFrameworkDokaMySql()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
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

        if (_ownedServices is not null)
        {
            optionsBuilder.UseInternalServiceProvider(_ownedServices);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        try
        {
            base.Dispose();
        }
        finally
        {
            // WHY: EF's scoped services retain their root until context cleanup finishes.
            Interlocked
                .Exchange(ref _ownedServices, null)
                ?.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (Interlocked.Exchange(ref _ownedServices, null) is { } services)
            {
                await services.DisposeAsync();
            }
        }

        GC.SuppressFinalize(this);
    }
}
