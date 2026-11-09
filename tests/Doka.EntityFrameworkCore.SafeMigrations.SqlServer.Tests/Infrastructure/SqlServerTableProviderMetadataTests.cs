namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Prevents foreign table metadata from becoming an unqualified SQL Server contract.</summary>
public sealed class SqlServerTableProviderMetadataTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1;Database=table_metadata;User ID=test;Password=unused;TrustServerCertificate=True";

    /// <summary>Ordinary table definitions retain their existing strict and convergence support.</summary>
    /// <param name="mode">The table comparison contract.</param>
    [Theory]
    [InlineData(SafeMigrationTableMode.StrictDefinition)]
    [InlineData(SafeMigrationTableMode.ConvergenceContainer)]
    public void UnannotatedTableRemainsSupported(
        SafeMigrationTableMode mode
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString, registerSafeMigrations: false);
        var operation = CreateOperation(mode, annotation: null, normalizeExpression: false);

        // Act
        var plan = CreateCatalog(context)
            .Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Null(plan.UnsupportedCode);
        Assert.Contains("sys.", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>Neither a known foreign engine nor unknown metadata is accepted in either table mode.</summary>
    /// <param name="mode">The table comparison contract.</param>
    /// <param name="annotation">The provider-owned metadata key.</param>
    [Theory]
    [InlineData(SafeMigrationTableMode.StrictDefinition, "Doka:MySql:StorageEngine")]
    [InlineData(SafeMigrationTableMode.ConvergenceContainer, "Doka:MySql:StorageEngine")]
    [InlineData(SafeMigrationTableMode.StrictDefinition, "Unknown:TableFacet")]
    [InlineData(SafeMigrationTableMode.ConvergenceContainer, "Unknown:TableFacet")]
    public void TableMetadataIsRejectedBeforeCatalogClassification(
        SafeMigrationTableMode mode,
        string annotation
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString, registerSafeMigrations: false);
        var operation = CreateOperation(mode, annotation, normalizeExpression: false);

        // Act
        var plan = CreateCatalog(context)
            .Build(operation);

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("table_annotation", plan.UnsupportedCode);
        Assert.Equal("N'unsupported'", plan.StateExpression);
    }

    /// <summary>Native expression normalization preserves table-owned metadata at the provider boundary.</summary>
    /// <param name="mode">The table comparison contract.</param>
    [Theory]
    [InlineData(SafeMigrationTableMode.StrictDefinition)]
    [InlineData(SafeMigrationTableMode.ConvergenceContainer)]
    public void ExpressionNormalizationRetainsMetadataRejection(
        SafeMigrationTableMode mode
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString, registerSafeMigrations: false);
        var operation = CreateOperation(mode, "Doka:MySql:StorageEngine", normalizeExpression: true);
        var definition = ((EnsureTableIntent)operation.Intent).Definition;
        var normalized = SafeMigrationCapturedExpressionNormalization.NormalizeTable(definition, true);
        var normalizedOperation = new SafeMigrationOperation(new EnsureTableIntent(normalized, mode), operation.Policy);

        // Act
        var plan = CreateCatalog(context)
            .Build(normalizedOperation);

        // Assert
        Assert.NotSame(definition, normalized);
        Assert.Single(normalized.ProviderAnnotations);
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("table_annotation", plan.UnsupportedCode);
    }

    /// <summary>Uses the actual provider mappings without opening the deliberately unreachable connection.</summary>
    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Captures a table-only annotation independently of operation-level annotations.</summary>
    private static SafeMigrationOperation CreateOperation(
        SafeMigrationTableMode mode,
        string? annotation,
        bool normalizeExpression
    )
    {
        var metadata = new CreateTableOperation();
        if (annotation is not null)
        {
            metadata[annotation] = "InnoDB";
        }

        List<ExpectedColumnDefinition> columns =
        [
            new("source", typeof(int), false, "int"),
        ];

        if (normalizeExpression)
        {
            columns.Add(new ExpectedColumnDefinition(
                "derived",
                typeof(int),
                true,
                "int",
                isStored: true,
                computedExpression: SafeMigrationSql.Identifier("SOURCE"))
            {
                CapturedComputedSql = "SOURCE",
            });
        }

        var definition = new ExpectedTableDefinition("table_metadata", columns)
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(metadata),
        };

        return new SafeMigrationOperation(
            new EnsureTableIntent(definition, mode),
            SafeMigrationPolicy.ThrowIfDifferent);
    }
}
