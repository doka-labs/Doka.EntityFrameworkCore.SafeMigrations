namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Owns a complete rebuild model for direct column-repair value checks.</summary>
/// <typeparam name="TValue">The target CLR value domain.</typeparam>
internal sealed class SqliteLosslessAlterTestContext<TValue> : DbContext
{
    private readonly DbConnection _connection;
    private readonly IServiceProvider _internalServices;

    /// <summary>Uses an open isolated connection without taking ownership of it.</summary>
    /// <param name="connection">The isolated test database.</param>
    /// <param name="internalServices">The fixture-owned shared EF provider services.</param>
    public SqliteLosslessAlterTestContext(DbConnection connection, IServiceProvider internalServices)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(internalServices);

        _connection = connection;
        _internalServices = internalServices;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(_connection);
        // WHY: These tests execute against the exact runtime context and do
        // not redirect migration discovery. Sharing this service configuration
        // avoids a separate internal provider for every CLR boundary fixture.
        optionsBuilder.UseSqliteSafeMigrations();
        optionsBuilder.UseInternalServiceProvider(_internalServices);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LosslessAlterEntity>(entity =>
        {
            entity.ToTable("lossless_alter_records");
            entity.HasKey(value => value.Id).HasName("pk_lossless_alter_records");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Value)
                .IsRequired(Nullable.GetUnderlyingType(typeof(TValue)) is null);
        });
    }

    private sealed class LosslessAlterEntity
    {
        public int Id { get; set; }

        public TValue Value { get; set; } = default!;
    }
}

/// <summary>Owns a same-type default replacement without a parameter-dependent model cache.</summary>
internal sealed class SqliteDefaultAlterTestContext : DbContext
{
    private readonly DbConnection _connection;
    private readonly IServiceProvider _internalServices;

    /// <summary>Uses the isolated repair database.</summary>
    /// <param name="connection">The open test connection.</param>
    /// <param name="internalServices">The fixture-owned shared EF provider services.</param>
    public SqliteDefaultAlterTestContext(DbConnection connection, IServiceProvider internalServices)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(internalServices);

        _connection = connection;
        _internalServices = internalServices;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations();
        optionsBuilder.UseInternalServiceProvider(_internalServices);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DefaultAlterEntity>(entity =>
        {
            entity.ToTable("lossless_alter_records");
            entity.HasKey(value => value.Id).HasName("pk_lossless_alter_records");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Value).HasDefaultValue(7L);
        });
    }

    private sealed class DefaultAlterEntity
    {
        public int Id { get; set; }

        public long Value { get; set; }
    }
}

/// <summary>Shares provider services across value-domain models and disposes their cache after the class.</summary>
public sealed class SqliteLosslessAlterServiceFixture : IDisposable
{
    /// <summary>Builds one bounded service provider rather than adding models to EF's process-wide cache.</summary>
    public SqliteLosslessAlterServiceFixture()
    {
        // WHY: This matrix requires distinct CLR models, not distinct provider
        // registrations. Explicit fixture ownership keeps both model caches
        // and singleton services bounded without suppressing EF warnings.
        Provider = new ServiceCollection()
            .AddEntityFrameworkSqlite()
            .AddSqliteSafeMigrations()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Gets the provider shared by this class's isolated database contexts.</summary>
    public ServiceProvider Provider { get; }

    /// <inheritdoc />
    public void Dispose() => Provider.Dispose();
}

/// <summary>Proves that rejected repair plans never reach the EF baseline DDL execution path.</summary>
internal sealed class SqliteLosslessAlterDdlCounter : DbCommandInterceptor
{
    /// <summary>Gets the number of executed baseline DDL commands.</summary>
    public int Count { get; private set; }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result)
    {
        var sql = command.CommandText.AsSpan().TrimStart();
        if (sql.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase))
        {
            Count++;
        }

        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(NonQueryExecuting(command, eventData, result));
    }
}
