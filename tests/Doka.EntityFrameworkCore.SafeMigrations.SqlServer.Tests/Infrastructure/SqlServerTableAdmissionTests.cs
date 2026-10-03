namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies ordinary table column-count and non-overflowable row-layout admission.</summary>
public sealed class SqlServerTableAdmissionTests
{
    /// <summary>Counts row metadata, packed bits, and fixed storage without rejecting variable row overflow.</summary>
    [Theory]
    [InlineData("int", 1024, 0, true, null)]
    [InlineData("int", 1025, 0, false, "table_column_limit")]
    [InlineData("bit", 1024, 0, true, null)]
    [InlineData("char", 2, 4000, true, null)]
    [InlineData("char", 2, 5000, false, "table_fixed_row_limit")]
    [InlineData("varchar", 2, 5000, true, null)]
    [InlineData("nvarchar", 2, 4000, true, null)]
    public void OrdinaryTable_RequiresAdmissibleFixedLayout(
        string type,
        int count,
        int length,
        bool supported,
        string? code
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1433;Database=table_layout;User ID=sa;Password=unused;TrustServerCertificate=True");

        var store = length == 0 ? type : type + "(" + length.ToString(CultureInfo.InvariantCulture) + ")";
        var clr = type == "int" ? typeof(int) : type == "bit" ? typeof(bool) : typeof(string);
        var columns = Enumerable.Range(0, count).Select(index => new ExpectedColumnDefinition(
            "C" + index.ToString(CultureInfo.InvariantCulture), clr, false, store)).ToArray();

        var operation = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
            "layout_items", columns), SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.Equal(!supported, plan.IsStaticallyUnsupported);
        Assert.Equal(code, plan.UnsupportedCode);
    }

    /// <summary>Includes the seven-byte header and null bitmap at the fixed 8,060-byte boundary.</summary>
    [Theory]
    [InlineData(53, true)]
    [InlineData(54, false)]
    public void FixedRows_IncludeTheirMandatoryRecordMetadata(
        int secondLength,
        bool supported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1433;Database=table_layout;User ID=sa;Password=unused;TrustServerCertificate=True");

        var operation = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("layout_items",
            [new ExpectedColumnDefinition("First", typeof(string), false, "char(8000)"),
                new ExpectedColumnDefinition("Second", typeof(string), false,
                    "char(" + secondLength.ToString(CultureInfo.InvariantCulture) + ")")]),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.Equal(!supported, plan.IsStaticallyUnsupported);
        Assert.Equal(supported ? null : "table_fixed_row_limit", plan.UnsupportedCode);
    }

    /// <summary>Does not confuse a variable maximum-row warning with a CREATE TABLE admission failure.</summary>
    [Fact]
    public void FixedBoundaryPlusVariableColumn_RemainsAnAdmissibleDefinition()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1433;Database=table_layout;User ID=sa;Password=unused;TrustServerCertificate=True");

        var definition = new ExpectedTableDefinition("layout_items",
            [new ExpectedColumnDefinition("First", typeof(string), false, "char(8000)"),
                new ExpectedColumnDefinition("Second", typeof(string), false, "char(53)"),
                new ExpectedColumnDefinition("Variable", typeof(string), true, "varchar(1)")]);

        var operation = new SafeMigrationOperation(new EnsureTableIntent(definition,
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Null(plan.UnsupportedCode);
    }
}
