namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies that inferred column facets agree with explicit SQL Server store types.
/// </summary>
public sealed class SqlServerColumnFacetSqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=facets;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Resolves length, Unicode, fixed-length, precision, and scale without an explicit store type.
    /// </summary>
    [Theory]
    [InlineData(typeof(string), "varchar(80)", false, 80, false, null, null)]
    [InlineData(typeof(string), "nchar(80)", true, 80, true, null, null)]
    [InlineData(typeof(string), "nvarchar(80)", true, 80, false, null, null)]
    [InlineData(typeof(byte[]), "varbinary(80)", null, 80, false, null, null)]
    [InlineData(typeof(decimal), "decimal(12,3)", null, null, null, 12, 3)]
    [InlineData(typeof(DateTime), "datetime2(3)", null, null, null, 3, null)]
    public void InferredFacets_ProduceTheExplicitStoreTypeCatalogPredicate(
        Type clrType,
        string storeType,
        bool? unicode,
        int? length,
        bool? fixedLength,
        int? precision,
        int? scale
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource>(),
            context.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>());

        var inferred = new ExpectedColumnDefinition(
            "Value", clrType, true, isUnicode: unicode, maxLength: length,
            isFixedLength: fixedLength, precision: precision, scale: scale);

        var explicitType = new ExpectedColumnDefinition(
            "Value", clrType, true, storeType, unicode, length, fixedLength,
            precision: precision, scale: scale);

        // Act
        var inferredPlan = catalog.Build(new SafeMigrationOperation(
            new EnsureColumnIntent("facet_items", inferred), SafeMigrationPolicy.ThrowIfDifferent));

        var explicitPlan = catalog.Build(new SafeMigrationOperation(
            new EnsureColumnIntent("facet_items", explicitType), SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.False(inferredPlan.IsStaticallyUnsupported);
        Assert.Equal(explicitPlan.StateExpression, inferredPlan.StateExpression);
        Assert.Equal(explicitPlan.Postcondition, inferredPlan.Postcondition);
    }

    /// <summary>
    /// Requires ordinary columns to exclude provider-owned storage attributes.
    /// </summary>
    [Fact]
    public void OrdinaryColumn_RequiresUnmodeledPhysicalAttributesToBeAbsent()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource>(),
            context.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>());

        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("facet_items", new ExpectedColumnDefinition("Value", typeof(int), true, "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.Contains("c.is_sparse = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("c.is_rowguidcol = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("c.is_filestream = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("c.is_hidden = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("c.is_column_set = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("c.generated_always_type = 0", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects an authored nullable inline primary-key column before baseline SQL generation.
    /// </summary>
    [Fact]
    public void InlinePrimaryKey_NullableAuthoredColumnIsStaticallyUnsupported()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource>(),
            context.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>());

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("nullable_key",
            [new ExpectedColumnDefinition("Id", typeof(int), true, "int")],
            primaryKey: new ExpectedPrimaryKeyDefinition("PK_nullable_key", "nullable_key", ["Id"])),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        var operation = (SafeMigrationOperation)builder.Operations[0];

        // Act
        var plan = catalog.Build(operation);
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("primary_key_nullable_column", plan.UnsupportedCode);
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>
    /// Requires SQL Server column collations to be unqualified in every expected and source definition.
    /// </summary>
    [Theory]
    [InlineData("column", false)]
    [InlineData("column", true)]
    [InlineData("table", true)]
    [InlineData("alter-old", true)]
    [InlineData("alter-new", true)]
    public void ColumnCollation_QualifiedIdentityIsStaticallyUnsupported(
        string operationKind,
        bool qualified
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var collation = new SafeMigrationCollationIdentifier(
            "Latin1_General_100_CI_AS", qualified ? "application" : null);

        var definition = new ExpectedColumnDefinition(
            "Value", typeof(string), true, "varchar(80)", collation: collation);

        switch (operationKind)
        {
            case "column":
                builder.EnsureColumn("collation_items", definition, SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "table":
                builder.EnsureTable(new ExpectedTableDefinition("collation_items", [definition]),
                    SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "alter-old":
                builder.AlterColumnIfDifferent("collation_items",
                    new ExpectedColumnDefinition("Value", typeof(string), true, "varchar(80)"),
                    new ExpectedColumnDefinition("Value", typeof(string), true, "varchar(40)", collation: collation),
                    SafeMigrationPolicy.RepairIfSafe);
                break;
            case "alter-new":
                builder.AlterColumnIfDifferent("collation_items", definition,
                    new ExpectedColumnDefinition("Value", typeof(string), true, "varchar(40)"),
                    SafeMigrationPolicy.RepairIfSafe);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operationKind));
        }

        // Act
        var plan = catalog.Build((SafeMigrationOperation)builder.Operations[0]);
        var exception = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model));

        // Assert
        Assert.Equal(qualified, plan.IsStaticallyUnsupported);
        Assert.Equal(qualified ? "column_collation_unproven" : null, plan.UnsupportedCode);
        if (qualified)
        {
            Assert.IsType<NotSupportedException>(exception);
        }
        else
        {
            Assert.Null(exception);
        }
    }
}
