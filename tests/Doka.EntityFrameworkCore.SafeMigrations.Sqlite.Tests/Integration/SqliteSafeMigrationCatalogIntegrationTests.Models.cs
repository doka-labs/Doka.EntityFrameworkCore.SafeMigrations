namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Preserves original EF model identities without inheritable test methods.</summary>
public abstract partial class SqliteSafeMigrationCatalogIntegrationTests : SqliteIntegrationTestBase
{
    /// <summary>Shares only fixture model and registration mechanics, not a mutable model-cache key.</summary>
    protected abstract class SqliteConstraintShapeContext : DbContext
    {
        private readonly DbConnection _connection;
        private readonly IServiceProvider _internalServices;

        /// <summary>Initializes the fixture with its independent open connection.</summary>
        /// <param name="connection">The test-owned native connection.</param>
        /// <param name="internalServices">The class-owned shared provider services.</param>
        protected SqliteConstraintShapeContext(DbConnection connection, IServiceProvider internalServices)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _internalServices = internalServices ?? throw new ArgumentNullException(nameof(internalServices));
        }

        /// <summary>Registers each concrete fixture context without additional services or dependencies.</summary>
        /// <param name="optionsBuilder">The concrete fixture's context options.</param>
        protected void ConfigureConstraintShapeOptions(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(_connection);
            optionsBuilder.UseSqliteSafeMigrations();

            // WHY: Different CLR model types need distinct model cache entries,
            // not distinct singleton providers. Fixture ownership bounds both
            // caches without suppressing ManyServiceProvidersCreatedWarning.
            optionsBuilder.UseInternalServiceProvider(_internalServices);
        }

        /// <summary>
        /// Maps the final logical symbol required by the rebuild target, with unchanged column and row contracts.
        /// </summary>
        /// <param name="modelBuilder">The fixed concrete type's model.</param>
        /// <param name="kind">The fixed native constraint family.</param>
        protected static void ConfigureConstraintShapeModel(ModelBuilder modelBuilder, SafeMigrationOperationKind kind)
        {
            modelBuilder.Entity<ConstraintShapeParent>(entity =>
            {
                entity.ToTable("shape_parents");
                entity.HasKey(value => value.Id).HasName("pk_shape_parents");
                entity.Property(value => value.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<ConstraintShapeItem>(entity =>
            {
                entity.ToTable("shape_items", table =>
                {
                    if (kind == SafeMigrationOperationKind.EnsureCheckConstraint)
                    {
                        table.HasCheckConstraint("logical_constraint", "Code > 0");
                    }

                });

                entity.HasKey(value => value.Id).HasName("pk_shape_items");
                entity.Property(value => value.Id).ValueGeneratedNever();
                if (kind == SafeMigrationOperationKind.EnsureUniqueConstraint)
                {
                    entity.HasAlternateKey(value => value.Code).HasName("logical_constraint");
                }

                if (kind == SafeMigrationOperationKind.EnsureForeignKey)
                {
                    entity.HasIndex(value => value.ParentId).HasDatabaseName("ix_shape_items_parent");
                    entity.HasOne<ConstraintShapeParent>().WithMany().HasForeignKey(value => value.ParentId)
                        .HasConstraintName("logical_constraint").OnDelete(DeleteBehavior.NoAction);
                }

            });
        }

        /// <summary>Represents the fixture principal without generated key values.</summary>
        private sealed class ConstraintShapeParent
        {
            /// <summary>Gets or sets the fixture's principal key.</summary>
            public int Id { get; set; }
        }

        /// <summary>Represents the stable row copied by each constraint rebuild.</summary>
        private sealed class ConstraintShapeItem
        {
            /// <summary>Gets or sets the stable row key.</summary>
            public int Id { get; set; }

            /// <summary>Gets or sets the unique/check member.</summary>
            public int Code { get; set; }

            /// <summary>Gets or sets the existing valid principal reference.</summary>
            public int ParentId { get; set; }
        }
    }

    /// <summary>Provides the fixed UNIQUE rebuild contract.</summary>
    protected sealed class SqliteUniqueConstraintShapeContext : SqliteConstraintShapeContext
    {
        /// <summary>Initializes the fixed UNIQUE fixture.</summary>
        /// <param name="connection">The test-owned native connection.</param>
        /// <param name="internalServices">The class-owned shared provider services.</param>
        public SqliteUniqueConstraintShapeContext(DbConnection connection, IServiceProvider internalServices)
            : base(connection, internalServices)
        {
        }

        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            ConfigureConstraintShapeOptions(optionsBuilder);

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureConstraintShapeModel(modelBuilder, SafeMigrationOperationKind.EnsureUniqueConstraint);
    }

    /// <summary>Provides the fixed CHECK rebuild contract.</summary>
    protected sealed class SqliteCheckConstraintShapeContext : SqliteConstraintShapeContext
    {
        /// <summary>Initializes the fixed CHECK fixture.</summary>
        /// <param name="connection">The test-owned native connection.</param>
        /// <param name="internalServices">The class-owned shared provider services.</param>
        public SqliteCheckConstraintShapeContext(DbConnection connection, IServiceProvider internalServices)
            : base(connection, internalServices)
        {
        }

        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            ConfigureConstraintShapeOptions(optionsBuilder);

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureConstraintShapeModel(modelBuilder, SafeMigrationOperationKind.EnsureCheckConstraint);
    }

    /// <summary>Provides the fixed FK rebuild contract.</summary>
    protected sealed class SqliteForeignKeyConstraintShapeContext : SqliteConstraintShapeContext
    {
        /// <summary>Initializes the fixed FK fixture.</summary>
        /// <param name="connection">The test-owned native connection.</param>
        /// <param name="internalServices">The class-owned shared provider services.</param>
        public SqliteForeignKeyConstraintShapeContext(DbConnection connection, IServiceProvider internalServices)
            : base(connection, internalServices)
        {
        }

        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            ConfigureConstraintShapeOptions(optionsBuilder);

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureConstraintShapeModel(modelBuilder, SafeMigrationOperationKind.EnsureForeignKey);
    }

    /// <summary>Keeps the target rebuild model aligned with an explicit NULL-default ADD.</summary>
    protected sealed class SqliteProjectedForeignKeyNullDefaultContext(
        DbConnection connection,
        IServiceProvider internalServices) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(connection);
            optionsBuilder.UseSqliteSafeMigrations();

            // WHY: Distinct target models need model-cache entries, not extra
            // singleton provider caches. The existing class fixture owns both.
            optionsBuilder.UseInternalServiceProvider(internalServices);
        }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NullDefaultParent>(entity =>
            {
                entity.ToTable("constraint_parents");
                entity.HasKey(value => value.Id).HasName("pk_constraint_parents");
                entity.Property(value => value.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<NullDefaultChild>(entity =>
            {
                entity.ToTable("constraint_children");
                entity.HasKey(value => value.Id).HasName("pk_constraint_children");
                entity.Property(value => value.Id).ValueGeneratedNever();
                entity.Property(value => value.ParentId).HasDefaultValueSql("NULL");
                entity.HasIndex(value => value.ParentId).HasDatabaseName("ix_constraint_children_parent_id");
                entity.HasOne<NullDefaultParent>().WithMany().HasForeignKey(value => value.ParentId)
                    .HasConstraintName("fk_constraint_children_parent").OnDelete(DeleteBehavior.Cascade);
            });
        }

        /// <summary>Models the untouched native FK principal.</summary>
        private sealed class NullDefaultParent
        {
            /// <summary>Gets or sets the principal key.</summary>
            public int Id { get; set; }
        }

        /// <summary>Models the FK dependent after adding the NULL-preserving column.</summary>
        private sealed class NullDefaultChild
        {
            /// <summary>Gets or sets the dependent key.</summary>
            public int Id { get; set; }

            /// <summary>Gets or sets the nullable FK reference.</summary>
            public int? ParentId { get; set; }
        }
    }

    /// <summary>Models one FK containing both an unchanged live column and a newly added nullable column.</summary>
    protected sealed class SqliteProjectedCompositeForeignKeyContext(
        DbConnection connection,
        IServiceProvider internalServices) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(connection);
            optionsBuilder.UseSqliteSafeMigrations();

            // WHY: Keep independent CLR model identities in the fixture-owned
            // shared provider instead of growing EF's process-wide cache.
            optionsBuilder.UseInternalServiceProvider(internalServices);
        }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompositeParent>(entity =>
            {
                entity.ToTable("composite_parents");
                entity.HasKey(value => new { value.TenantId, value.Id }).HasName("pk_composite_parents");
                entity.Property(value => value.TenantId).ValueGeneratedNever();
                entity.Property(value => value.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<CompositeChild>(entity =>
            {
                entity.ToTable("composite_children");
                entity.HasKey(value => value.Id).HasName("pk_composite_children");
                entity.Property(value => value.Id).ValueGeneratedNever();
                entity.HasIndex(value => new { value.Id, value.ParentId })
                    .HasDatabaseName("ix_composite_children_parent");
                entity.HasOne<CompositeParent>().WithMany().HasForeignKey(value => new { value.Id, value.ParentId })
                    .HasConstraintName("fk_composite_children_parent").OnDelete(DeleteBehavior.NoAction);
            });
        }

        /// <summary>Models the native composite principal key.</summary>
        private sealed class CompositeParent
        {
            /// <summary>Gets or sets the first principal key component.</summary>
            public int TenantId { get; set; }

            /// <summary>Gets or sets the second principal key component.</summary>
            public int Id { get; set; }
        }

        /// <summary>Models the accepted composite FK endpoints.</summary>
        private sealed class CompositeChild
        {
            /// <summary>Gets or sets the unchanged live dependent component.</summary>
            public int Id { get; set; }

            /// <summary>Gets or sets the newly materialized NULL dependent component.</summary>
            public int? ParentId { get; set; }
        }
    }

    /// <summary>Supplies only the final rebuild model; preflight consumes actual operation definitions.</summary>
    protected sealed class SqliteProjectedSeparateAddContext(
        DbConnection connection,
        IServiceProvider internalServices) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite(connection);
            optionsBuilder.UseSqliteSafeMigrations();
            optionsBuilder.UseInternalServiceProvider(internalServices);
        }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SeparateAddParent>(entity =>
            {
                entity.ToTable("constraint_parents");
                entity.HasKey(value => value.Id).HasName("pk_constraint_parents");
                entity.Property(value => value.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<SeparateAddChild>(entity =>
            {
                entity.ToTable("constraint_children");
                entity.HasKey(value => value.Id).HasName("pk_constraint_children");
                entity.Property(value => value.Id).ValueGeneratedNever();
                entity.HasIndex(value => value.ParentId).HasDatabaseName("ix_constraint_children_parent_id");
                entity.HasOne<SeparateAddParent>().WithMany().HasForeignKey(value => value.ParentId)
                    .HasConstraintName("fk_constraint_children_parent").OnDelete(DeleteBehavior.Cascade);
            });
        }

        /// <summary>Models the unchanged native principal used by the final rebuild contract.</summary>
        private sealed class SeparateAddParent
        {
            /// <summary>Gets or sets the native principal key.</summary>
            public int Id { get; set; }
        }

        /// <summary>Models the final child after its separately authored column ADD.</summary>
        private sealed class SeparateAddChild
        {
            /// <summary>Gets or sets the native child key.</summary>
            public int Id { get; set; }

            /// <summary>Gets or sets the nullable native parent reference.</summary>
            public int? ParentId { get; set; }
        }
    }
}
