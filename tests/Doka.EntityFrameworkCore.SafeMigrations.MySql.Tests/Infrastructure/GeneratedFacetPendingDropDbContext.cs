namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Hosts one real pending DROP with explicitly configured generated-column facets.</summary>
public sealed class GeneratedFacetPendingDropDbContext : DbContext
{
    internal const string TableName = "generated_facet_pending_drop";
    internal const string HistoryTable = "__GeneratedFacetPendingDropHistory";
    internal const string MigrationId = "20261020000101_GeneratedFacetPendingDrop";

    private readonly string _connectionString;
    private readonly MySqlServerVersion _serverVersion;
    private ServiceProvider? _ownedServices;

    /// <summary>Initializes the isolated native database and its provider version.</summary>
    /// <param name="connectionString">The database lease owned by the calling test.</param>
    /// <param name="serverVersion">The active MySQL or MariaDB server version.</param>
    public GeneratedFacetPendingDropDbContext(string connectionString, MySqlServerVersion serverVersion)
    {
        _connectionString = connectionString;
        _serverVersion = serverVersion;
        // WHY: Historical-target tests legitimately vary the canonical context and server
        // version. Their roots belong to this context, not EF's process-wide provider cache.
        _ownedServices = new ServiceCollection()
            .AddEntityFrameworkDokaMySql()
            .AddEntityFrameworkDokaMySqlSafeMigrations<GeneratedFacetPendingDropDbContext>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseMySql(_connectionString, _serverVersion,
            provider => provider.MigrationsAssembly(typeof(GeneratedFacetPendingDropDbContext).Assembly.FullName)
                .MigrationsHistoryTable(HistoryTable));

        optionsBuilder.UseMySqlSafeMigrations<GeneratedFacetPendingDropDbContext>();
        optionsBuilder.UseInternalServiceProvider(_ownedServices
            ?? throw new ObjectDisposedException(nameof(GeneratedFacetPendingDropDbContext)));
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
            // WHY: EF's scoped services may still need their root while the context is disposed.
            Interlocked.Exchange(ref _ownedServices, null)?.Dispose();
        }
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
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

    /// <summary>Defines the same complete historical target for the context, migration and snapshot.</summary>
    /// <param name="modelBuilder">The model receiving the explicit physical facets.</param>
    internal static void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // WHY: Sharing explicit configuration avoids a synthetic pending-model
        // mismatch hiding the actual historical-target versus runtime-model seam.
        modelBuilder.Entity<GeneratedFacetPendingDropRow>(builder =>
        {
            builder.ToTable(TableName);
            builder.HasKey(row => row.Id).HasName("PRIMARY");
            // WHY: Snapshot and historical-target builders have no provider
            // convention set. Persist the actual non-identity strategy instead
            // of making the model gate fail before dependency qualification.
            builder.Property(row => row.Id).HasColumnName("id").HasColumnType("int").ValueGeneratedNever()
                .HasAnnotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None);
            builder.Property(row => row.Result).HasColumnName("result").HasColumnType("varchar(20)")
                .HasMaxLength(20).IsRequired(false).HasComputedColumnSql("'fixed'", stored: false)
                .ValueGeneratedOnAddOrUpdate().HasComment("candidate-marker").UseCollation("utf8mb4_bin");
        });
    }
}

/// <summary>Exposes only the retained target members, not the physically existing unrelated column.</summary>
public sealed class GeneratedFacetPendingDropRow
{
    /// <summary>Gets or sets the stable native row identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the nullable physical contract of the generated string result.</summary>
    public string? Result { get; set; }
}
