namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteSafeMigrationTestContext : DbContext
{
    private readonly DbConnection? _connection;
    private readonly bool _registerSafeMigrations;

    public SqliteSafeMigrationTestContext(
        DbConnection connection,
        bool registerSafeMigrations = true
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _registerSafeMigrations = registerSafeMigrations;
    }

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        if (optionsBuilder.IsConfigured)
        {
            return;
        }

        optionsBuilder.UseSqlite(
            _connection ?? throw new InvalidOperationException("A SQLite connection is required."),
            provider => provider.MigrationsAssembly(typeof(SqliteSafeMigrationTestContext).Assembly.FullName));
        if (_registerSafeMigrations)
        {
            optionsBuilder.UseSqliteSafeMigrations<SqliteSafeMigrationTestContext>();
        }
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<ModelEntity>(entity =>
        {
            entity.ToTable("model_entities");
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.Code).IsUnique();
            entity.Property(value => value.Code).HasMaxLength(80);
        });

        modelBuilder.Entity<RebuildEntity>(entity =>
        {
            entity.ToTable(
                "rebuild_entities",
                table => table.HasCheckConstraint("ck_rebuild_entities_code", "length(\"Code\") > 0"));
            entity.HasKey(value => value.Id).HasName("pk_rebuild_entities");
            entity.HasAlternateKey(value => value.Code).HasName("uq_rebuild_entities_code");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Code).IsRequired();
        });

        modelBuilder.Entity<ConstraintParent>(entity =>
        {
            entity.ToTable(
                "constraint_parents",
                table => table.HasCheckConstraint("ck_constraint_parents_positive", "Id > 0"));
            entity.HasKey(value => value.Id).HasName("pk_constraint_parents");
            entity.Property(value => value.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<ConstraintChild>(entity =>
        {
            entity.ToTable("constraint_children");
            entity.HasKey(value => value.Id).HasName("pk_constraint_children");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.HasIndex(value => value.ParentId).HasDatabaseName("ix_constraint_children_parent_id");
            entity.HasOne<ConstraintParent>()
                .WithMany()
                .HasForeignKey(value => value.ParentId)
                .HasConstraintName("fk_constraint_children_parent")
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private sealed class ModelEntity
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;
    }

    private sealed class RebuildEntity
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;
    }

    private sealed class ConstraintParent
    {
        public int Id { get; set; }
    }

    private sealed class ConstraintChild
    {
        public int Id { get; set; }

        public int? ParentId { get; set; }
    }
}
