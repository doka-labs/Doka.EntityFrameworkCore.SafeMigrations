namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies authored inline key limits before any SQL Server baseline is generated.
/// </summary>
public sealed class SqlServerInlineKeySqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=inline_keys;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Accepts the declared-byte limit and rejects the first width above it for each key family.
    /// </summary>
    [Theory]
    [InlineData(true, 450, false)]
    [InlineData(true, 451, true)]
    [InlineData(false, 850, false)]
    [InlineData(false, 851, true)]
    public void InlineKey_DeclaredWidthBoundaryIsProvenBeforeDdl(
        bool primaryKey,
        int length,
        bool unsupported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var definition = new ExpectedTableDefinition("inline_keys",
            [new ExpectedColumnDefinition("Value", typeof(string), false, $"nvarchar({length})", maxLength: length)],
            primaryKey: primaryKey ? new ExpectedPrimaryKeyDefinition("PK_inline", "inline_keys", ["Value"]) : null,
            uniqueConstraints: primaryKey
                ? [] : [new ExpectedUniqueConstraintDefinition("UQ_inline", "inline_keys", ["Value"])]);

        builder.EnsureTable(definition, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build((SafeMigrationOperation)builder.Operations[0]);
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model));

        // Assert
        Assert.Equal(unsupported, plan.IsStaticallyUnsupported);
        Assert.Equal(unsupported ? "key_unproven_width" : null, plan.UnsupportedCode);
        if (unsupported)
        {
            Assert.IsType<NotSupportedException>(exception);
        }
        else
        {
            Assert.Null(exception);
        }
    }

    /// <summary>
    /// Accepts thirty-two declared key columns and rejects the first additional column.
    /// </summary>
    [Theory]
    [InlineData(true, 32, false)]
    [InlineData(true, 33, true)]
    [InlineData(false, 32, false)]
    [InlineData(false, 33, true)]
    public void InlineKey_ColumnCountBoundaryIsProvenBeforeDdl(
        bool primaryKey,
        int count,
        bool unsupported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var names = Enumerable.Range(0, count).Select(static ordinal => $"Column{ordinal:D2}").ToArray();
        var definition = new ExpectedTableDefinition("inline_keys",
            names.Select(static name => new ExpectedColumnDefinition(name, typeof(int), false, "int")),
            primaryKey: primaryKey ? new ExpectedPrimaryKeyDefinition("PK_inline", "inline_keys", names) : null,
            uniqueConstraints: primaryKey
                ? [] : [new ExpectedUniqueConstraintDefinition("UQ_inline", "inline_keys", names)]);

        builder.EnsureTable(definition, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build((SafeMigrationOperation)builder.Operations[0]);
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model));

        // Assert
        Assert.Equal(unsupported, plan.IsStaticallyUnsupported);
        Assert.Equal(unsupported ? "key_unproven_width" : null, plan.UnsupportedCode);
        if (unsupported)
        {
            Assert.IsType<NotSupportedException>(exception);
        }
        else
        {
            Assert.Null(exception);
        }
    }
}
