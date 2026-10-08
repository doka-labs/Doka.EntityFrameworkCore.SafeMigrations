namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Proves empty-baseline projection exceptions against the provider's actual generated SQL.</summary>
public sealed class PostgreSqlEventTriggerProjectionTests
{
    /// <summary>Supplies only operations that Npgsql certifies as mutation-free.</summary>
    /// <returns>The original immutable operation fixtures.</returns>
    public static IEnumerable<object[]> EmptyBaselines()
    {
        yield return [new EnsureSchemaOperation { Name = "public" }];
        yield return [new RenameTableOperation { Name = "values", NewName = "values", Schema = "public" }];
        yield return [new RenameTableOperation { Name = "values", Schema = "public", NewSchema = "public" }];
        yield return [new RenameIndexOperation { Name = "ix_values", NewName = "ix_values", Table = "values" }];
        yield return [new RenameSequenceOperation { Name = "value_sequence", NewName = "value_sequence" }];
        yield return [new InsertDataOperation
        {
            Table = "values",
            Columns = ["id"],
            ColumnTypes = ["integer"],
            Values = new object[0, 1],
        }];

        yield return [new UpdateDataOperation
        {
            Table = "values",
            KeyColumns = ["id"],
            KeyColumnTypes = ["integer"],
            KeyValues = new object[0, 1],
            Columns = ["id"],
            ColumnTypes = ["integer"],
            Values = new object[0, 1],
        }];

        yield return [new DeleteDataOperation
        {
            Table = "values",
            KeyColumns = ["id"],
            KeyColumnTypes = ["integer"],
            KeyValues = new object[0, 1],
        }];
    }

    /// <summary>Certified empty baselines preserve all captured evidence because no trigger can fire.</summary>
    /// <param name="operation">The unchanged ordinary provider operation.</param>
    [Theory]
    [MemberData(nameof(EmptyBaselines))]
    public void EmptyBaseline_PreservesEvidenceOnlyWhenNpgsqlEmitsNoSql(MigrationOperation operation)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=localhost;Database=event_projection;Username=unused;Password=unused", registerSafeMigrations: false);
        var analyzer = new PostgreSqlSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var preserves = analyzer.PreservesExistingTableState(operation);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        // Assert
        Assert.True(preserves);
        Assert.Empty(commands);
    }

    /// <summary>An unchanged column rename still emits DDL and must not acquire the empty-baseline exception.</summary>
    [Fact]
    public void ColumnRename_IdenticalNamesDoNotProveNoDdl()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=localhost;Database=event_projection;Username=unused;Password=unused", registerSafeMigrations: false);
        var analyzer = new PostgreSqlSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());
        var operation = new RenameColumnOperation { Table = "values", Name = "id", NewName = "id" };

        // Act
        var preserves = analyzer.PreservesExistingTableState(operation);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        // Assert
        Assert.False(preserves);
        Assert.Contains("ALTER TABLE", Assert.Single(commands).CommandText, StringComparison.Ordinal);
    }

    /// <summary>An empty identity INSERT can bump its sequence without changing captured table state.</summary>
    [Fact]
    public void EmptyIdentityInsert_PreservesTableEvidenceDespiteSequenceCommand()
    {
        // Arrange
        using var context = new IdentitySeedDbContext();
        var analyzer = new PostgreSqlSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());
        var operation = new InsertDataOperation
        {
            Table = "identity_seed_values",
            Columns = ["Id"],
            ColumnTypes = ["integer"],
            Values = new object[0, 1],
        };

        // Act
        var preserves = analyzer.PreservesExistingTableState(operation);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        // Assert
        Assert.True(preserves);
        var command = Assert.Single(commands).CommandText;

        Assert.Contains("setval(", command, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", command, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE", command, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER", command, StringComparison.Ordinal);
    }

    /// <summary>Supplies identity metadata to Npgsql without opening a database connection.</summary>
    private sealed class IdentitySeedDbContext : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseNpgsql("Host=localhost;Database=event_projection;Username=unused;Password=unused");

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<IdentitySeedValue>().ToTable("identity_seed_values")
                .Property(value => value.Id).UseIdentityByDefaultColumn();
        }
    }

    /// <summary>Represents only the identity column consumed by Npgsql's sequence-bump generation.</summary>
    private sealed class IdentitySeedValue
    {
        /// <summary>Gets or sets the generated identity key.</summary>
        public int Id { get; set; }
    }
}
