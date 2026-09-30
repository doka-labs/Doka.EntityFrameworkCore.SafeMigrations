namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies SQL Server schema invariants without generating destructive baseline commands.
/// </summary>
public sealed class SqlServerSchemaSqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=schemas;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Rejects a reserved schema before ordinary SQL generation can produce DROP SCHEMA.
    /// </summary>
    [Theory]
    [InlineData("dbo")]
    [InlineData("guest")]
    [InlineData("sys")]
    [InlineData("INFORMATION_SCHEMA")]
    public void ReservedDropSchema_IsStaticallyUnsupported(
        string schema
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropSchemaIfExists(schema);

        // Act
        var plan = catalog.Build((SafeMigrationOperation)builder.Operations[0]);
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("reserved_schema", plan.UnsupportedCode);
        Assert.IsType<NotSupportedException>(exception);
    }
}
