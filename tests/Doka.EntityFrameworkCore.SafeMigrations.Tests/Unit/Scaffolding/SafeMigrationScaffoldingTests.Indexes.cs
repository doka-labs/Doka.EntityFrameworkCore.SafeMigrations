namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Selects the safe or ordinary DROP INDEX entry point according to configuration.</summary>
    /// <param name="isEnabled">Whether safe scaffolding is enabled.</param>
    /// <param name="expectedMethod">The exact generated migration entry point.</param>
    [Theory]
    [InlineData(true, ".DropIndexIfExists(")]
    [InlineData(false, ".DropIndex(")]
    public void DropIndexGenerationFollowsTheSelectedScaffoldingContract(
        bool isEnabled,
        string expectedMethod
    )
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict, isEnabled: isEnabled);
        var builder = new IndentedStringBuilder();
        var operation = new DropIndexOperation
        {
            Name = "ix_users_email",
            Table = "users",
        };

        // Act
        generator.Generate("migrationBuilder", [operation], builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains(expectedMethod, source, StringComparison.Ordinal);
        Assert.Equal(isEnabled, source.Contains(".DropIndexIfExists(", StringComparison.Ordinal));
    }

    /// <summary>Preserves single and composite index shape in generated safe entry points.</summary>
    [Fact]
    public void IndexGenerationSelectsSingleAndCompositeSafeEntryPoints()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var single = new CreateIndexOperation
        {
            Name = "ix_users_email",
            Table = "users",
            Columns = ["email"],
        };

        var composite = new CreateIndexOperation
        {
            Name = "ix_users_tenant_email",
            Table = "users",
            Columns = ["tenant_id", "email"],
            IsDescending = [false, true],
        };

        // Act
        generator.Generate("migrationBuilder", [single, composite], builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains(".CreateIndexIfNotExistsFromModel(", source, StringComparison.Ordinal);
        Assert.Contains(".CreateCompositeIndexIfNotExistsFromModel(", source, StringComparison.Ordinal);
        Assert.Contains("columns: [\"tenant_id\", \"email\"]", source, StringComparison.Ordinal);
        Assert.Contains("descending: [false, true]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.CreateIndex(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new[]", source, StringComparison.Ordinal);
    }

    /// <summary>Rejects unprojected index metadata before publishing source.</summary>
    [Fact]
    public void IndexGeneration_RejectsUnprojectedProviderMetadataBeforeEmittingSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = new CreateIndexOperation
        {
            Name = "ix_users_email",
            Table = "users",
            Columns = ["email"],
        };

        operation["Provider:Unknown"] = "value";

        // Act
        var exception = Record.Exception(() => generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        var unsupported = Assert.IsType<NotSupportedException>(exception);
        Assert.Contains(typeof(CreateIndexOperation).FullName!, unsupported.Message, StringComparison.Ordinal);
        Assert.Empty(builder.ToString());
    }

    /// <summary>Keeps prefix metadata in key order in source and the captured contract.</summary>
    [Fact]
    public void ProjectedIndexPrefixesAreRenderedAndCapturedInKeyOrder()
    {
        // Arrange
        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.Strict,
            createIndexProjectors: [new TestIndexProjector([0, 64])]);

        var sourceBuilder = new IndentedStringBuilder();
        var operation = new CreateIndexOperation
        {
            Name = "ix_users_tenant_email",
            Table = "users",
            Columns = ["tenant_id", "email"],
        };

        var migrationBuilder = new MigrationBuilder("test");

        // Act
        generator.Generate("migrationBuilder", [operation], sourceBuilder);
        var source = sourceBuilder.ToString();
        _ = migrationBuilder.CreateCompositeIndexWithPrefixesIfNotExistsFromModel(
            operation.Name,
            operation.Table,
            operation.Columns,
            [0, 64]);

        // Assert
        var safeOperation = Assert.IsType<SafeMigrationOperation>(Assert.Single(migrationBuilder.Operations));
        var intent = Assert.IsType<EnsureIndexIntent>(safeOperation.Intent);

        Assert.Contains(".CreateCompositeIndexWithPrefixesIfNotExistsFromModel(", source, StringComparison.Ordinal);
        Assert.Contains(
            string.Concat(",", Environment.NewLine, "    prefixLengths: [0, 64]"),
            source,
            StringComparison.Ordinal);
        Assert.Null(intent.Definition.Keys[0].PrefixLength);
        Assert.Equal(64, intent.Definition.Keys[1].PrefixLength);
        Assert.Empty(safeOperation.GetAnnotations());
    }

    /// <summary>Preserves included columns in generated source and the captured index contract.</summary>
    [Fact]
    public void ProjectedIndexIncludesAreRenderedAndCapturedAsNonKeyColumns()
    {
        // Arrange
        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.Strict,
            createIndexProjectors: [new TestIncludedIndexProjector(["display_name"])]);

        var sourceBuilder = new IndentedStringBuilder();
        var operation = new CreateIndexOperation
        {
            Name = "ix_users_tenant_email",
            Table = "users",
            Columns = ["tenant_id", "email"],
        };

        var migrationBuilder = new MigrationBuilder("test");

        // Act
        generator.Generate("migrationBuilder", [operation], sourceBuilder);
        _ = migrationBuilder.CreateCompositeIndexWithIncludesIfNotExistsFromModel(
            operation.Name,
            operation.Table,
            operation.Columns,
            ["display_name"]);

        // Assert
        var safeOperation = Assert.IsType<SafeMigrationOperation>(Assert.Single(migrationBuilder.Operations));
        var intent = Assert.IsType<EnsureIndexIntent>(safeOperation.Intent);

        Assert.Contains(
            ".CreateCompositeIndexWithIncludesIfNotExistsFromModel(",
            sourceBuilder.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("includedColumns: [\"display_name\"]", sourceBuilder.ToString(), StringComparison.Ordinal);
        Assert.Equal("display_name", Assert.Single(intent.Definition.IncludedColumns));
        Assert.Empty(safeOperation.GetAnnotations());
    }

    /// <summary>Rejects an index key repeated as an included column without adding an operation.</summary>
    [Fact]
    public void IndexIncludeProjectionRejectsKeyColumnAsInclude()
    {
        // Arrange
        var migrationBuilder = new MigrationBuilder("test");

        // Act
        var exception = Record.Exception(() => migrationBuilder.CreateIndexWithIncludesIfNotExistsFromModel(
            "ix_users_email",
            "users",
            "email",
            ["email"]));

        // Assert
        Assert.IsType<ArgumentException>(exception);
        Assert.Empty(migrationBuilder.Operations);
    }

    /// <summary>Rejects a provider projection that mixes prefix and include metadata.</summary>
    [Fact]
    public void IndexGenerationRejectsAmbiguousPrefixAndIncludeProjection()
    {
        // Arrange
        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.Strict,
            createIndexProjectors: [new TestIndexProjector([16], ["display_name"])]);

        var sourceBuilder = new IndentedStringBuilder();
        var operation = new CreateIndexOperation
        {
            Name = "ix_users_email",
            Table = "users",
            Columns = ["email"],
        };

        // Act
        var exception = Record.Exception(() => generator.Generate("migrationBuilder", [operation], sourceBuilder));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(sourceBuilder.ToString());
    }

    /// <summary>Rejects invalid prefix metadata for a single-column index.</summary>
    /// <param name="prefixLengths">The invalid ordered prefix-length input.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0, 64)]
    public void SingleIndexPrefixProjectionRejectsInvalidShapes(
        params int[] prefixLengths
    )
    {
        // Arrange
        var migrationBuilder = new MigrationBuilder("test");

        // Act
        Assert.Throws<ArgumentException>(() =>
            migrationBuilder.CreateIndexWithPrefixesIfNotExistsFromModel(
                "ix_users_email",
                "users",
                "email",
                prefixLengths));
        // Assert
        Assert.Empty(migrationBuilder.Operations);
    }

    /// <summary>Rejects absent prefix metadata for both index entry-point shapes.</summary>
    /// <param name="composite">Whether the composite rather than single-column entry point is used.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexPrefixProjectionRejectsMissingRequiredMetadata(
        bool composite
    )
    {
        // Arrange
        var migrationBuilder = new MigrationBuilder("test");

        // Act
        var exception = composite
            ? Assert.Throws<ArgumentNullException>(() =>
                migrationBuilder.CreateCompositeIndexWithPrefixesIfNotExistsFromModel(
                    "ix_users_tenant_email",
                    "users",
                    ["tenant_id", "email"],
                    prefixLengths: null!))
            : Assert.Throws<ArgumentNullException>(() =>
                migrationBuilder.CreateIndexWithPrefixesIfNotExistsFromModel(
                    "ix_users_email",
                    "users",
                    "email",
                    prefixLengths: null!));

        // Assert
        Assert.Equal("prefixLengths", exception.ParamName);
        Assert.Empty(migrationBuilder.Operations);
    }
}
