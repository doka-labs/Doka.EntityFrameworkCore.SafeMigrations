using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies that SQL Server INCLUDE metadata survives EF differ and SafeMigrations scaffolding.
/// </summary>
public sealed class SqlServerIncludedIndexScaffoldingTests
{
    /// <summary>
    /// Renders included columns as typed SafeMigrations arguments without a duplicated annotation.
    /// </summary>
    [Fact]
    public void IncludedIndex_FromEfModel_RendersTypedSafeOperation()
    {
        // Arrange
        using var context = new SqlServerSafeMigrationScaffoldingDbContext(
            "Server=127.0.0.1,1433;Database=scaffolding;User ID=sa;Password=unused;TrustServerCertificate=True");

        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var operations = differ.GetDifferences(source: null, model);
        var index = operations.OfType<CreateIndexOperation>().Single();
        var sourceBuilder = new IndentedStringBuilder();
        var generator = CreateGenerator(context.GetService<ITypeMappingSource>());

        // Act
        generator.Generate("migrationBuilder", [index], sourceBuilder);
        var projection = new SqlServerSafeMigrationCreateIndexScaffoldingProjector().Project(index);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.CreateIndexWithIncludesIfNotExistsFromModel(
            index.Name,
            index.Table,
            index.Columns[0],
            projection.IncludedColumns!.ToArray());
        var safeOperation = (SafeMigrationOperation)migrationBuilder.Operations.Single();
        var intent = (EnsureIndexIntent)safeOperation.Intent;

        // Assert
        Assert.Single(operations.OfType<CreateIndexOperation>());
        Assert.IsType<SafeMigrationOperation>(Assert.Single(migrationBuilder.Operations));
        Assert.IsType<EnsureIndexIntent>(safeOperation.Intent);
        Assert.Equal(["DisplayName"], Assert.IsType<string[]>(index["SqlServer:Include"]));
        Assert.Contains("includedColumns: [\"DisplayName\"]", sourceBuilder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(".Annotation(\"SqlServer:Include\"", sourceBuilder.ToString(), StringComparison.Ordinal);
        Assert.Equal(["DisplayName"], intent.Definition.IncludedColumns);
        Assert.Empty(safeOperation.GetAnnotations());
    }

    /// <summary>
    /// Leaves a plain EF index unchanged when it has no provider metadata to project.
    /// </summary>
    [Fact]
    public void IndexWithoutIncludeMetadata_PreservesOperation()
    {
        // Arrange
        var operation = CreateIndex();
        var projector = new SqlServerSafeMigrationCreateIndexScaffoldingProjector();

        // Act
        var projection = projector.Project(operation);

        // Assert
        Assert.Same(operation, projection.Operation);
        Assert.Null(projection.IncludedColumns);
    }

    /// <summary>
    /// Rejects unknown annotations and malformed INCLUDE metadata before generating migration source.
    /// </summary>
    [Theory]
    [InlineData("foreign")]
    [InlineData("wrong-type")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("key-column")]
    public void MalformedIncludeMetadata_FailsClosed(
        string caseName
    )
    {
        // Arrange
        var operation = CreateIndex();

        switch (caseName)
        {
            case "foreign":
                operation["Foreign:Facet"] = true;
                break;
            case "wrong-type":
                operation["SqlServer:Include"] = "DisplayName";
                break;
            case "empty":
                operation["SqlServer:Include"] = Array.Empty<string>();
                break;
            case "duplicate":
                operation["SqlServer:Include"] = new[] { "DisplayName", "displayname" };
                break;
            case "key-column":
                operation["SqlServer:Include"] = new[] { "email" };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName));
        }

        var projector = new SqlServerSafeMigrationCreateIndexScaffoldingProjector();

        // Act
        var exception = Record.Exception(() => projector.Project(operation));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    private static CreateIndexOperation CreateIndex() => new()
    {
        Name = "IX_scaffolding_users_Email",
        Table = "scaffolding_users",
        Columns = ["Email"],
    };

    private static SafeMigrationCSharpMigrationOperationGenerator CreateGenerator(
        ITypeMappingSource typeMappingSource
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeMappingSource);
        services.AddEntityFrameworkDesignTimeServices();

        using var provider = services.BuildServiceProvider();
        var dependencies = provider.GetRequiredService<CSharpMigrationOperationGeneratorDependencies>();

        return new SafeMigrationCSharpMigrationOperationGenerator(
            dependencies,
            new SafeMigrationScaffoldingConfiguration(
                true,
                SafeMigrationScaffoldingMode.Strict,
                SafeMigrationPolicy.ThrowIfDifferent),
            [new SqlServerSafeMigrationCreateIndexScaffoldingProjector()],
            []);
    }
}
