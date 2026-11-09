namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Prevents foreign table metadata from becoming an unqualified PostgreSQL contract.</summary>
public sealed class PostgreSqlTableProviderMetadataTests
{
    private const string ConnectionString =
        "Host=127.0.0.1;Port=1;Database=table_metadata;Username=test;Password=unused";

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
        Assert.Contains("pg_catalog", plan.StateExpression, StringComparison.Ordinal);
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
        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("table_annotation", plan.UnsupportedCode);
        Assert.Equal("table_annotation", catalog.GetUnsupportedFeature(operation.Intent));
        Assert.Equal("'unsupported'", plan.StateExpression);
    }

    /// <summary>A copied native expression view cannot erase the foreign table metadata refusal.</summary>
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

        // Act
        var plan = CreateCatalog(context)
            .Build(operation);

        // Assert
        Assert.NotSame(definition, normalized);
        Assert.Single(normalized.ProviderAnnotations);
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("table_annotation", plan.UnsupportedCode);
    }

    /// <summary>Uses the actual provider mappings without opening the deliberately unreachable connection.</summary>
    private static PostgreSqlSafeMigrationCatalogSqlBuilder CreateCatalog(
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
            new("source", typeof(int), false, "integer"),
        ];

        if (normalizeExpression)
        {
            columns.Add(new ExpectedColumnDefinition(
                "derived",
                typeof(int),
                true,
                "integer",
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
