namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Gives CREATE and RENAME distinct cached native target-model snapshots without mutable model keys.</summary>
/// <typeparam name="TShape">The immutable marker identifying the before or after rename model.</typeparam>
internal sealed class SqliteGeneratedRenameTargetContext<TShape> : DbContext
{
    private readonly DbConnection _connection;
    private readonly IServiceProvider _internalServices;

    /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
    /// <param name="connection">The supplied connection for this fixture.</param>
    /// <param name="internalServices">The supplied internal services for this fixture.</param>
    public SqliteGeneratedRenameTargetContext(
        DbConnection connection,
        IServiceProvider internalServices
    )
    {
        _connection = connection;
        _internalServices = internalServices;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder
            .UseSqlite(_connection)
            .UseSqliteSafeMigrations();

        optionsBuilder.UseInternalServiceProvider(_internalServices);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        var source = typeof(TShape) == typeof(SqliteGeneratedRenamedShape)
            ? "renamed"
            : "source";

        modelBuilder.Entity(
            "GeneratedTargetNode",
            entity =>
            {
                entity.ToTable(
                    "source_nodes",
                    table =>
                        table.HasCheckConstraint("ck_source_nodes", "\"" + source + "\" >= 0"));

                entity
                    .Property<int>("Id")
                    .HasColumnName("id")
                    .ValueGeneratedNever();

                entity
                    .HasKey("Id")
                    .HasName("pk_source_nodes");

                entity
                    .Property<int>("Source")
                    .HasColumnName(source);

                entity
                    .Property<int>("Derived")
                    .HasColumnName("derived")
                    .HasComputedColumnSql("\"" + source + "\" + 1", stored: false);
            });
    }
}

/// <summary>Identifies the native target model before the accepted column rename.</summary>
// WHY: The generic EF context uses this type as an independent model-cache identity.
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class SqliteGeneratedOriginalShape;

/// <summary>Identifies the native target model after the accepted column rename.</summary>
internal sealed class SqliteGeneratedRenamedShape;
