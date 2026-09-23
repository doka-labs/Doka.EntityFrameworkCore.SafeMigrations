namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

internal sealed class SqliteColumnTransitionTestContext : DbContext
{
    private readonly DbConnection _connection;

    public SqliteColumnTransitionTestContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(_connection);
        optionsBuilder.UseSqliteSafeMigrations<SqliteColumnTransitionTestContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        modelBuilder.Entity<ColumnTransitionEntity>(entity =>
        {
            entity.ToTable("column_transition");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.RenamedCode).HasColumnName("renamed_code");
            entity.Property(value => value.AddedCode);
        });

        modelBuilder.Entity<RenameTargetEntity>(entity =>
        {
            entity.ToTable("rename_target");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.Property(value => value.RenamedCode).HasColumnName("renamed_code");
        });
    }

    private sealed class ColumnTransitionEntity
    {
        public int Id { get; set; }

        public string? RenamedCode { get; set; }

        public string? AddedCode { get; set; }
    }

    private sealed class RenameTargetEntity
    {
        public int Id { get; set; }

        public string? RenamedCode { get; set; }
    }
}
