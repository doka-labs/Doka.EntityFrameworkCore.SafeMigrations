namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Removes only an unrelated member while retaining the complete generated-column target.</summary>
[DbContext(typeof(GeneratedFacetPendingDropDbContext))]
[Migration(GeneratedFacetPendingDropDbContext.MigrationId)]
public sealed class GeneratedFacetPendingDropMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumnIfExists("unrelated", GeneratedFacetPendingDropDbContext.TableName);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.EnsureColumn(GeneratedFacetPendingDropDbContext.TableName,
            new ExpectedColumnDefinition("unrelated", typeof(int), true, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);

    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
        => GeneratedFacetPendingDropDbContext.ConfigureModel(modelBuilder);
}
