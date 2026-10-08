namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Owns the valid terminal AUTOINCREMENT model for generation-transition rejection tests.</summary>
internal sealed class SqliteAutoincrementAlterTestContext : DbContext
{
    private readonly DbConnection _connection;
    private readonly IServiceProvider _internalServices;
    private readonly DbCommandInterceptor _commandInterceptor;

    /// <summary>Configures an isolated database with the shared provider and a baseline DDL observer.</summary>
    /// <param name="connection">The isolated open connection.</param>
    /// <param name="internalServices">The class-fixture provider services.</param>
    /// <param name="commandInterceptor">The command observer that proves pre-DDL rejection.</param>
    public SqliteAutoincrementAlterTestContext(DbConnection connection, IServiceProvider internalServices,
        DbCommandInterceptor commandInterceptor)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(internalServices);
        ArgumentNullException.ThrowIfNull(commandInterceptor);

        _connection = connection;
        _internalServices = internalServices;
        _commandInterceptor = commandInterceptor;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations();
        optionsBuilder.UseInternalServiceProvider(_internalServices);
        optionsBuilder.AddInterceptors(_commandInterceptor);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AutoincrementAlterEntity>(entity =>
        {
            entity.ToTable("lossless_alter_records");
            entity.HasKey(value => value.Id).HasName("pk_lossless_alter_records");
            entity.Property(value => value.Id).ValueGeneratedOnAdd();
        });
    }

    private sealed class AutoincrementAlterEntity
    {
        public int Id { get; set; }

        public long Value { get; set; }
    }
}
