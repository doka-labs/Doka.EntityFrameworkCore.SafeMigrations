namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Gives CREATE and RENAME distinct cached native target-model snapshots without mutable model keys.</summary>
/// <typeparam name="TShape">The immutable marker identifying the before or after rename model.</typeparam>
internal sealed class SqliteGeneratedRenameTargetContext<TShape>(
    DbConnection connection,
    IServiceProvider internalServices
) : DbContext
{
    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(connection).UseSqliteSafeMigrations();
        optionsBuilder.UseInternalServiceProvider(internalServices);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var source = typeof(TShape) == typeof(SqliteGeneratedRenamedShape) ? "renamed" : "source";
        modelBuilder.Entity("GeneratedTargetNode", entity =>
        {
            entity.ToTable("source_nodes", table =>
                table.HasCheckConstraint("ck_source_nodes", "\"" + source + "\" >= 0"));
            entity.Property<int>("Id").HasColumnName("id").ValueGeneratedNever();
            entity.HasKey("Id").HasName("pk_source_nodes");
            entity.Property<int>("Source").HasColumnName(source);
            entity.Property<int>("Derived").HasColumnName("derived")
                .HasComputedColumnSql("\"" + source + "\" + 1", stored: false);
        });
    }
}

/// <summary>Identifies the native target model before the accepted column rename.</summary>
internal sealed class SqliteGeneratedOriginalShape;

/// <summary>Identifies the native target model after the accepted column rename.</summary>
internal sealed class SqliteGeneratedRenamedShape;
