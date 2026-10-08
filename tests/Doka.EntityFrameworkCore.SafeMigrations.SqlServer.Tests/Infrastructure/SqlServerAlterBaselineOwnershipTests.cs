namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies that a guarded ALTER baseline cannot add hidden model-derived mutations.</summary>
public sealed class SqlServerAlterBaselineOwnershipTests
{
    /// <summary>A target-model index does not authorize an implicit drop or recreation during ALTER.</summary>
    [Fact]
    public void AlterColumn_DoesNotRebuildTargetModelIndexes()
    {
        // Arrange
        using var context = new IndexedContext();
        var operation = new SafeMigrationOperation(new AlterColumnIntent("items",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), "dbo"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.GetService<IDesignTimeModel>().Model);

        var sql = string.Join("\n", commands.Select(static command => command.CommandText));

        // Assert
        Assert.Contains("ALTER COLUMN [Value] bigint NOT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX [IX_model_value]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX [IX_model_value]", sql, StringComparison.Ordinal);
        Assert.All(commands, static command => Assert.False(command.TransactionSuppressed));
    }

    /// <summary>Proven NULL absence does not authorize even a zero-row UPDATE that can fire DML triggers.</summary>
    [Fact]
    public void WideningWithNotNullAndDefault_DoesNotEmitImplicitBackfill()
    {
        // Arrange
        using var context = new IndexedContext();
        var source = new ExpectedColumnDefinition("Value", typeof(int), true, "int",
            defaultValue: SafeMigrationDefaultValue.Literal(0));

        var target = new ExpectedColumnDefinition("Value", typeof(long), false, "bigint",
            defaultValue: SafeMigrationDefaultValue.Literal(0L));

        var operation = new SafeMigrationOperation(new AlterColumnIntent("items", target, source, "dbo"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var sql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.GetService<IDesignTimeModel>().Model)
            .Select(static command => command.CommandText));

        // Assert
        Assert.Contains("[Value] IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN [Value] bigint NOT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE [dbo].[items]", sql, StringComparison.Ordinal);
        Assert.Contains("sp_addextendedproperty", sql, StringComparison.Ordinal);
        Assert.True(source.IsNullable);
        Assert.Equal(0, source.DefaultValue.GetLiteralValue());
    }

    /// <summary>Provides a real relational target model with an index absent from the authored ALTER.</summary>
    private sealed class IndexedContext : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer("Server=127.0.0.1,1;Database=baseline_ownership;"
                + "User ID=sa;Password=unused;TrustServerCertificate=True");

            optionsBuilder.UseSqlServerSafeMigrations<IndexedContext>();
        }

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity("BaselineItem");
            entity.ToTable("items", "dbo");
            entity.Property<int>("Id");
            entity.Property<long>("Value").HasColumnType("bigint");
            entity.HasKey("Id");
            entity.HasIndex("Value").HasDatabaseName("IX_model_value").IncludeProperties("Id");
        }
    }
}
