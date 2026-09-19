namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

internal sealed class SqliteConstraintDropTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteConstraintDropTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteConstraintDropTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<ConstraintDropParent>(entity =>
        {
            entity.ToTable("drop_parents");
            entity.HasKey(value => value.Id).HasName("pk_drop_parents");
            entity.Property(value => value.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<ConstraintDropEntity>(entity =>
        {
            entity.ToTable("drop_entities");
            entity.HasKey(value => value.Id).HasName("pk_drop_entities");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Code).IsRequired();
        });
    }

    private sealed class ConstraintDropParent
    {
        public int Id { get; set; }
    }

    private sealed class ConstraintDropEntity
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;

        public int? ParentId { get; set; }
    }
}

internal sealed class SqlitePrimaryKeyDropTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqlitePrimaryKeyDropTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqlitePrimaryKeyDropTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<PrimaryKeyDropEntity>(entity =>
        {
            entity.ToTable("primary_key_drop_entities");
            entity.HasNoKey();
        });
    }

    private sealed class PrimaryKeyDropEntity
    {
        public int Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }
}

internal sealed class SqliteSelfReferenceRebuildTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteSelfReferenceRebuildTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteSelfReferenceRebuildTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<SelfReferenceEntity>(entity =>
        {
            entity.ToTable(
                "self_reference_entities",
                table => table.HasCheckConstraint("ck_self_reference_entities_positive", "Id > 0"));
            entity.HasKey(value => value.Id).HasName("pk_self_reference_entities");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.HasOne<SelfReferenceEntity>()
                .WithMany()
                .HasForeignKey(value => value.ParentId)
                .HasConstraintName("fk_self_reference_entities_parent")
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(value => value.ParentId)
                .HasDatabaseName("ix_self_reference_entities_parent_id");
        });
    }

    private sealed class SelfReferenceEntity
    {
        public int Id { get; set; }

        public int? ParentId { get; set; }
    }
}

internal sealed class SqliteProviderRewriteTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteProviderRewriteTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteProviderRewriteTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<ProviderRewriteEntity>(entity =>
        {
            entity.ToTable("provider_rewrite_entities");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Value).HasColumnName("Legacy");
        });
    }

    private sealed class ProviderRewriteEntity
    {
        public int Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }
}

internal sealed class SqliteStructuralSequenceTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteStructuralSequenceTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteStructuralSequenceTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<StructuralPost>(entity =>
        {
            entity.ToTable("structural_posts");
            entity.HasKey(value => value.Id).HasName("pk_structural_posts");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Title).IsRequired();
        });
    }

    private sealed class StructuralPost
    {
        public int Id { get; set; }

        public int? AuthorId { get; set; }

        public string Title { get; set; } = string.Empty;
    }
}

internal sealed class SqliteRebuildShapeTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteRebuildShapeTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteRebuildShapeTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<RebuildShapeEntity>(entity =>
        {
            entity.ToTable(
                "rebuild_shape_entities",
                table => table.HasCheckConstraint("ck_rebuild_shape_entities_id", "Id > 0"));
            entity.HasKey(value => value.Id).HasName("pk_rebuild_shape_entities");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Code).IsRequired();
            entity.HasIndex(value => value.Code)
                .HasDatabaseName("ix_rebuild_shape_entities_code");
        });
    }

    private sealed class RebuildShapeEntity
    {
        public string Code { get; set; } = string.Empty;

        public int Id { get; set; }
    }
}

internal sealed class SqliteEscapedIdentifierRebuildTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteEscapedIdentifierRebuildTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteEscapedIdentifierRebuildTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<EscapedIdentifierEntity>(entity =>
        {
            entity.ToTable(
                "order\"items",
                table => table.HasCheckConstraint("ck_order_items_id", "Id > 0"));
            entity.HasKey(value => value.Id).HasName("pk_order_items");
            entity.Property(value => value.Id).ValueGeneratedNever();
        });
    }

    private sealed class EscapedIdentifierEntity
    {
        public int Id { get; set; }
    }
}

internal sealed class SqliteAddColumnCheckTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteAddColumnCheckTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteAddColumnCheckTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<AddColumnCheckEntity>(entity =>
        {
            entity.ToTable(
                "add_column_check_entities",
                table => table.HasCheckConstraint("ck_add_column_check_entities_id", "Id > 0"));
            entity.HasKey(value => value.Id).HasName("pk_add_column_check_entities");
            entity.Property(value => value.Id).ValueGeneratedNever();
        });
    }

    private sealed class AddColumnCheckEntity
    {
        public string? Description { get; set; }

        public int Id { get; set; }
    }
}

internal sealed class SqliteAddColumnReferencedCheckTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteAddColumnReferencedCheckTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteAddColumnReferencedCheckTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<AddColumnReferencedCheckEntity>(entity =>
        {
            entity.ToTable(
                "add_column_referenced_check_entities",
                table => table.HasCheckConstraint(
                    "ck_add_column_referenced_check_entities_description",
                    "length(\"Description\") > 0"));
            entity.HasKey(value => value.Id).HasName("pk_add_column_referenced_check_entities");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Description).IsRequired();
        });
    }

    private sealed class AddColumnReferencedCheckEntity
    {
        public string Description { get; set; } = string.Empty;

        public int Id { get; set; }
    }
}

internal sealed class SqliteBackfillDefaultRebuildTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteBackfillDefaultRebuildTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteBackfillDefaultRebuildTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<BackfillDefaultEntity>(entity =>
        {
            entity.ToTable(
                "backfill_default_entities",
                table => table.HasCheckConstraint("ck_backfill_default_entities_priority", "Priority >= 0"));
            entity.HasKey(value => value.Id).HasName("pk_backfill_default_entities");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Priority).IsRequired();
        });
    }

    private sealed class BackfillDefaultEntity
    {
        public int Id { get; set; }

        public int Priority { get; set; }
    }
}

internal sealed class SqliteForeignKeyCollationTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteForeignKeyCollationTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteForeignKeyCollationTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<CollationParent>(entity =>
        {
            entity.ToTable(
                "collation_parents",
                table => table.HasCheckConstraint("ck_collation_parents_id", "Id > 0"));
            entity.HasKey(value => value.Id).HasName("pk_collation_parents");
            entity.HasAlternateKey(value => value.Code).HasName("uq_collation_parents_code");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Code).UseCollation("NOCASE").IsRequired();
        });

        modelBuilder.Entity<CollationChild>(entity =>
        {
            entity.ToTable("collation_children");
            entity.HasKey(value => value.Id).HasName("pk_collation_children");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.ParentCode).IsRequired();
            entity.HasOne<CollationParent>()
                .WithMany()
                .HasForeignKey(value => value.ParentCode)
                .HasPrincipalKey(value => value.Code)
                .HasConstraintName("fk_collation_children_parent")
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(value => value.ParentCode)
                .HasDatabaseName("ix_collation_children_parent_code");
        });
    }

    private sealed class CollationParent
    {
        public string Code { get; set; } = string.Empty;

        public int Id { get; set; }
    }

    private sealed class CollationChild
    {
        public int Id { get; set; }

        public string ParentCode { get; set; } = string.Empty;
    }
}

internal sealed class SqliteConvertedBackfillDefaultRebuildTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteConvertedBackfillDefaultRebuildTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteConvertedBackfillDefaultRebuildTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<ConvertedBackfillEntity>(entity =>
        {
            entity.ToTable(
                "converted_backfill_entities",
                table => table.HasCheckConstraint(
                    "ck_converted_backfill_entities_state",
                    "length(\"State\") >= 0"));
            entity.HasKey(value => value.Id).HasName("pk_converted_backfill_entities");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.State).HasConversion<string>().IsRequired();
        });
    }

    private sealed class ConvertedBackfillEntity
    {
        public int Id { get; set; }

        public ConvertedBackfillState State { get; set; }
    }

    private enum ConvertedBackfillState
    {
        Unknown = 0,
        Active = 1,
    }
}

internal sealed class SqliteHighByteIdentifierRebuildTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteHighByteIdentifierRebuildTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteHighByteIdentifierRebuildTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<HighByteIdentifierEntity>(entity =>
        {
            entity.ToTable(
                "\u20AC",
                table => table.HasCheckConstraint("ck_high_byte_identifier_id", "Id > 0"));
            entity.HasKey(value => value.Id).HasName("pk_high_byte_identifier");
            entity.Property(value => value.Id).ValueGeneratedNever();
        });
    }

    private sealed class HighByteIdentifierEntity
    {
        public int Id { get; set; }
    }
}

internal sealed class SqliteMissingPrincipalForeignKeyTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteMissingPrincipalForeignKeyTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteMissingPrincipalForeignKeyTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<MissingPrincipal>(entity =>
        {
            entity.ToTable("missing_principals");
            entity.HasKey(value => value.Id).HasName("pk_missing_principals");
            entity.Property(value => value.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<MissingPrincipalChild>(entity =>
        {
            entity.ToTable(
                "missing_principal_children",
                table => table.HasCheckConstraint("ck_missing_principal_children_id", "Id > 0"));
            entity.HasKey(value => value.Id).HasName("pk_missing_principal_children");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.HasOne<MissingPrincipal>()
                .WithMany()
                .HasForeignKey(value => value.ParentId)
                .HasConstraintName("fk_missing_principal_children_parent")
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(value => value.ParentId)
                .HasDatabaseName("ix_missing_principal_children_parent_id");
        });
    }

    private sealed class MissingPrincipal
    {
        public int Id { get; set; }
    }

    private sealed class MissingPrincipalChild
    {
        public int Id { get; set; }

        public int? ParentId { get; set; }
    }
}

internal sealed class SqliteCheckLiteralRebuildTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteCheckLiteralRebuildTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteCheckLiteralRebuildTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<CheckLiteralEntity>(entity =>
        {
            entity.ToTable(
                "check_literal_entities",
                table => table.HasCheckConstraint("ck_check_literal_entities_id", "Id > 0"));
            entity.HasKey(value => value.Id).HasName("pk_check_literal_entities");
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.Note).HasDefaultValue("mycheck(");
        });
    }

    private sealed class CheckLiteralEntity
    {
        public int Id { get; set; }

        public string? Note { get; set; }
    }
}
