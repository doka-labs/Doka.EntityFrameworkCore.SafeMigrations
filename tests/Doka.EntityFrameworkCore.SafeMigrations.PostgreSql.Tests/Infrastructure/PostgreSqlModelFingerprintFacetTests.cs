namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlModelFingerprintFacetTests
{
    private const string ProviderContract = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static readonly (Type Marker, string Facet)[] s_variants =
    [
        (typeof(TableName), "table name"),
        (typeof(TableSchema), "table schema"),
        (typeof(TableComment), "table comment"),
        (typeof(TableExcluded), "table migration exclusion"),
        (typeof(ColumnName), "column name"),
        (typeof(ColumnType), "column store type"),
        (typeof(ColumnNullability), "column nullability"),
        (typeof(ColumnLength), "column maximum length"),
        (typeof(ColumnPrecision), "column precision"),
        (typeof(ColumnScale), "column scale"),
        (typeof(ColumnUnicode), "column unicode"),
        (typeof(ColumnFixedLength), "column fixed length"),
        (typeof(ColumnCollation), "column collation"),
        (typeof(ColumnComment), "column comment"),
        (typeof(ColumnOrder), "column order"),
        (typeof(ColumnComputed), "computed column"),
        (typeof(ColumnDefaultSql), "SQL default"),
        (typeof(ColumnDefaultLiteral), "literal default"),
        (typeof(PrimaryKeyName), "primary key"),
        (typeof(UniqueConstraintName), "unique constraint"),
        (typeof(ForeignKeyName), "foreign key name"),
        (typeof(ForeignKeyDelete), "foreign key delete action"),
        (typeof(IndexName), "index name"),
        (typeof(IndexUnique), "index uniqueness"),
        (typeof(IndexFilter), "index filter"),
        (typeof(IndexDescending), "index direction"),
        (typeof(CheckSql), "check SQL"),
        (typeof(TriggerName), "trigger"),
        (typeof(ViewName), "view"),
        (typeof(QuerySql), "SQL query"),
        (typeof(SequenceIncrement), "sequence"),
        (typeof(FunctionName), "function"),
        (typeof(StoredProcedureName), "stored procedure"),
    ];

    [Fact]
    public void Create_IsSensitiveToEverySerializedRelationalFacetFamily()
    {
        using var baselineContext = new FacetContext<Baseline>();
        var baseline = Fingerprint(baselineContext);

        foreach (var (marker, facet) in s_variants)
        {
            using var variant = (DbContext)Activator.CreateInstance(typeof(FacetContext<>).MakeGenericType(marker))!;
            var actual = Fingerprint(variant);

            Assert.True(
                !StringComparer.Ordinal.Equals(baseline, actual),
                $"The '{facet}' mutation did not change the relational model fingerprint.");
        }
    }

    [Fact]
    public void Create_ComplexRelationalModelHasPatchInvariantGoldenFingerprint()
    {
        using var context = new FacetContext<Baseline>();

        var actual = Fingerprint(context);

        Assert.Equal(
            "safe-relational-model:v1:Npgsql.EntityFrameworkCore.PostgreSQL:sha256:"
            + "d3f10dbf2826f558cd812ad3a46544b157e5aef3758a222e16a87eb862e4c191",
            actual);
    }

    private static string Fingerprint(
        DbContext context
    ) => SafeMigrationModelFingerprint.Create(
        context.GetService<IDesignTimeModel>()
            .Model,
        ProviderContract);

    private sealed class FacetContext<TMarker> : DbContext
    {
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        ) => optionsBuilder.UseNpgsql("Host=localhost;Database=fingerprint;Username=test;Password=test");

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.HasDefaultSchema("review");
            modelBuilder
                .HasSequence<long>("document_sequence", "review")
                .StartsAt(10)
                .IncrementsBy(typeof(TMarker) == typeof(SequenceIncrement) ? 7 : 5)
                .HasMin(10)
                .HasMax(10_000)
                .IsCyclic(false);

            var parent = modelBuilder.Entity<Parent>();
            parent.ToTable(
                typeof(TMarker) == typeof(TableName) ? "parents_changed" : "parents",
                typeof(TMarker) == typeof(TableSchema) ? "other" : "review",
                table =>
                {
                    table.HasComment(typeof(TMarker) == typeof(TableComment) ? "changed" : "parent table");
                    table.HasTrigger(typeof(TMarker) == typeof(TriggerName) ? "trg_changed" : "trg_parent");
                    if (typeof(TMarker) == typeof(TableExcluded))
                    {
                        table.ExcludeFromMigrations();
                    }
                });
            parent
                .HasKey(static value => value.Id)
                .HasName(typeof(TMarker) == typeof(PrimaryKeyName) ? "pk_changed" : "pk_parents");
            parent
                .HasAlternateKey(static value => value.Code)
                .HasName(typeof(TMarker) == typeof(UniqueConstraintName) ? "uq_changed" : "uq_parents_code");
            parent
                .Property(static value => value.Id)
                .HasColumnOrder(0);
            parent
                .Property(static value => value.Code)
                .HasColumnName(typeof(TMarker) == typeof(ColumnName) ? "code_changed" : "code")
                .HasColumnType(
                    typeof(TMarker) == typeof(ColumnType) ? "character varying(65)" : "character varying(64)")
                .HasMaxLength(typeof(TMarker) == typeof(ColumnLength) ? 63 : 64)
                .IsUnicode(typeof(TMarker) != typeof(ColumnUnicode))
                .IsFixedLength(typeof(TMarker) == typeof(ColumnFixedLength))
                .UseCollation(typeof(TMarker) == typeof(ColumnCollation) ? "POSIX" : "C")
                .HasComment(typeof(TMarker) == typeof(ColumnComment) ? "changed" : "parent code")
                .HasColumnOrder(typeof(TMarker) == typeof(ColumnOrder) ? 3 : 1)
                .IsRequired();
            parent
                .Property(static value => value.Description)
                .HasColumnType("text")
                .IsRequired(typeof(TMarker) != typeof(ColumnNullability));
            parent
                .Property(static value => value.Amount)
                .HasPrecision(
                    typeof(TMarker) == typeof(ColumnPrecision) ? 11 : 10,
                    typeof(TMarker) == typeof(ColumnScale) ? 3 : 2)
                .HasDefaultValue(typeof(TMarker) == typeof(ColumnDefaultLiteral) ? 2.5m : 1.5m);
            parent
                .Property(static value => value.NormalizedCode)
                .HasComputedColumnSql(
                    typeof(TMarker) == typeof(ColumnComputed) ? "lower(code) || 'x'" : "lower(code)",
                    stored: true);
            parent
                .Property(static value => value.CreatedAt)
                .HasDefaultValueSql(
                    typeof(TMarker) == typeof(ColumnDefaultSql) ? "CURRENT_TIMESTAMP(3)" : "CURRENT_TIMESTAMP");
            parent
                .HasIndex(static value => value.Code)
                .HasDatabaseName(typeof(TMarker) == typeof(IndexName) ? "ix_changed" : "ix_parents_code")
                .IsUnique(typeof(TMarker) == typeof(IndexUnique))
                .HasFilter(typeof(TMarker) == typeof(IndexFilter) ? "code <> 'changed'" : "code <> ''")
                .IsDescending(typeof(TMarker) == typeof(IndexDescending));
            parent.ToTable(table => table.HasCheckConstraint(
                "ck_parents_amount",
                typeof(TMarker) == typeof(CheckSql) ? "amount > 1" : "amount >= 0"));

            var child = modelBuilder.Entity<Child>();
            child.ToTable("children", "review");
            child.HasKey(static value => value.Id);
            child
                .Property(static value => value.Id)
                .ValueGeneratedNever();
            child
                .HasOne<Parent>()
                .WithMany()
                .HasForeignKey(static value => value.ParentId)
                .OnDelete(
                    typeof(TMarker) == typeof(ForeignKeyDelete) ? DeleteBehavior.Restrict : DeleteBehavior.Cascade)
                .HasConstraintName(typeof(TMarker) == typeof(ForeignKeyName) ? "fk_changed" : "fk_children_parents");
            child.InsertUsingStoredProcedure(
                typeof(TMarker) == typeof(StoredProcedureName) ? "insert_child_changed" : "insert_child",
                "review",
                procedure =>
                {
                    procedure.HasParameter(static value => value.Id);
                    procedure.HasParameter(static value => value.ParentId);
                });

            modelBuilder.Entity<ViewRow>(entity =>
            {
                entity.HasNoKey();
                entity.ToView(typeof(TMarker) == typeof(ViewName) ? "parent_view_changed" : "parent_view", "review");
                entity
                    .Property(static value => value.Code)
                    .HasColumnName("code");
            });
            modelBuilder.Entity<QueryRow>(entity =>
            {
                entity.HasNoKey();
                entity.ToSqlQuery(typeof(TMarker) == typeof(QuerySql) ? "SELECT 2 AS value" : "SELECT 1 AS value");
            });

            var method = typeof(PostgreSqlModelFingerprintFacetTests).GetMethod(
                nameof(FingerprintFunction),
                BindingFlags.NonPublic | BindingFlags.Static)!;
            modelBuilder
                .HasDbFunction(method)
                .HasName(typeof(TMarker) == typeof(FunctionName) ? "fingerprint_changed" : "fingerprint")
                .HasSchema("review");
        }
    }

    private static int FingerprintFunction(
        int value
    ) => throw new NotSupportedException(value.ToString(CultureInfo.InvariantCulture));

    private sealed class Parent
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;

        public decimal Amount { get; set; }

        public string? Description { get; set; }

        public string NormalizedCode { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
    }

    private sealed class Child
    {
        public int Id { get; set; }

        public int ParentId { get; set; }
    }

    private sealed class ViewRow
    {
        public string Code { get; set; } = string.Empty;
    }

    private sealed class QueryRow
    {
        public int Value { get; set; }
    }

    private sealed class Baseline;

    private sealed class TableName;

    private sealed class TableSchema;

    private sealed class TableComment;

    private sealed class TableExcluded;

    private sealed class ColumnName;

    private sealed class ColumnType;

    private sealed class ColumnNullability;

    private sealed class ColumnLength;

    private sealed class ColumnPrecision;

    private sealed class ColumnScale;

    private sealed class ColumnUnicode;

    private sealed class ColumnFixedLength;

    private sealed class ColumnCollation;

    private sealed class ColumnComment;

    private sealed class ColumnOrder;

    private sealed class ColumnComputed;

    private sealed class ColumnDefaultSql;

    private sealed class ColumnDefaultLiteral;

    private sealed class PrimaryKeyName;

    private sealed class UniqueConstraintName;

    private sealed class ForeignKeyName;

    private sealed class ForeignKeyDelete;

    private sealed class IndexName;

    private sealed class IndexUnique;

    private sealed class IndexFilter;

    private sealed class IndexDescending;

    private sealed class CheckSql;

    private sealed class TriggerName;

    private sealed class ViewName;

    private sealed class QuerySql;

    private sealed class SequenceIncrement;

    private sealed class FunctionName;

    private sealed class StoredProcedureName;
}
