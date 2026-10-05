namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies that filtered-index precedence follows the actual source-typed temporal SQL.</summary>
public sealed class SqlServerTemporalLiteralFilterTypingTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=temporal_filter_typing;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Does not treat a source-typed date as a varchar literal or permit a column-side conversion.</summary>
    [Fact]
    public void UntypedTemporalLiteral_PreservesItsEmittedNaturalTypeAndPrecedence()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var literal = new SafeMigrationSqlLiteralExpression(new DateTime(1753, 1, 1));

        // Act
        var type = catalog.EmittedIndexFilterLiteralStoreType(literal);
        var temporalColumnCompatible = catalog.IsIndexFilterLiteralCompatible(literal, "datetime2(7)");
        var textColumnCompatible = catalog.IsIndexFilterLiteralCompatible(literal, "varchar(40)");

        // Assert
        Assert.Equal("datetime2", type);
        Assert.True(temporalColumnCompatible);
        Assert.False(textColumnCompatible);
    }
}
