namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Captures complete provider ADD and ALTER operations in generated safe entry points.</summary>
    [Fact]
    public void ColumnGenerationCapturesCompleteProviderOperationsInsideSafeCalls()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var addColumn = new AddColumnOperation
        {
            Name = "caption",
            Table = "items",
            ClrType = typeof(string),
            ColumnType = "varchar(200)",
            MaxLength = 200,
            IsNullable = false,
        };

        addColumn["Provider:Facet"] = "value";

        var alterColumn = new AlterColumnOperation
        {
            Name = "caption",
            Table = "items",
            ClrType = typeof(string),
            ColumnType = "varchar(400)",
            MaxLength = 400,
            IsNullable = false,
            OldColumn = new AddColumnOperation
            {
                Name = "caption",
                Table = "items",
                ClrType = typeof(string),
                ColumnType = "varchar(200)",
                MaxLength = 200,
                IsNullable = false,
            },
        };

        // Act
        generator.Generate("migrationBuilder", [addColumn, alterColumn], builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains("migrationBuilder.AddColumnIfNotExistsFromModel(", source, StringComparison.Ordinal);
        Assert.Contains(
            "operationFactory: static operationBuilder => operationBuilder.AddColumn<string>(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(".Annotation(\"Provider:Facet\", \"value\")", source, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.AlterColumnIfDifferentFromModel(", source, StringComparison.Ordinal);
        Assert.Contains("SafeMigrationPolicy.RepairIfSafe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.AddColumn<string>(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.AlterColumn<string>(", source, StringComparison.Ordinal);
    }

    /// <summary>Keeps target and source annotations distinct when capturing a generated ALTER.</summary>
    [Fact]
    public void GeneratedColumnCapturePreservesTargetAndOldProviderAnnotations()
    {
        // Arrange
        var builder = new MigrationBuilder("test");

        // Act
        _ = builder.AlterColumnIfDifferentFromModel(
            operationBuilder => operationBuilder
                .AlterColumn<string>(
                    name: "caption",
                    table: "items",
                    type: "varchar(400)",
                    maxLength: 400,
                    nullable: false,
                    oldClrType: typeof(string),
                    oldType: "varchar(200)",
                    oldMaxLength: 200)
                .Annotation("Provider:Target", "target")
                .OldAnnotation("Provider:Old", "old"));

        // Assert
        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        var intent = Assert.IsType<AlterColumnIntent>(operation.Intent);

        Assert.Equal("target", Assert.Single(intent.Definition.ProviderAnnotations).Value);
        Assert.Equal("old", Assert.Single(intent.OldDefinition!.ProviderAnnotations).Value);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, operation.Policy);
    }

    /// <summary>Rejects a column callback that produces no operation.</summary>
    [Fact]
    public void GeneratedColumnCapture_RejectsCallbackWithoutAnOperation()
    {
        // Arrange
        var builder = new MigrationBuilder("test");

        // Act
        var exception = Record.Exception(() => builder.AddColumnIfNotExistsFromModel(
            static _ => { }));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(builder.Operations);
    }

    /// <summary>Rejects a column callback that produces the wrong operation kind.</summary>
    [Fact]
    public void GeneratedColumnCapture_RejectsCallbackWithTheWrongOperationType()
    {
        // Arrange
        var builder = new MigrationBuilder("test");

        // Act
        var exception = Record.Exception(() => builder.AddColumnIfNotExistsFromModel(
            static builder => builder.DropColumn("legacy", "items")));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(builder.Operations);
    }

    /// <summary>Rejects a column callback that produces multiple operations atomically.</summary>
    [Fact]
    public void GeneratedColumnCapture_RejectsCallbackWithMultipleOperations()
    {
        // Arrange
        var builder = new MigrationBuilder("test");

        // Act
        var exception = Record.Exception(() => builder.AddColumnIfNotExistsFromModel(
            static builder =>
            {
                builder.AddColumn<int>("left", "items", nullable: false);
                builder.AddColumn<int>("right", "items", nullable: false);
            }));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(builder.Operations);
    }

    /// <summary>Removes a partially captured operation when its callback fails.</summary>
    [Fact]
    public void GeneratedColumnCapture_RollsBackAnOperationWhenTheCallbackThrows()
    {
        // Arrange
        var builder = new MigrationBuilder("test");

        // Act
        var exception = Record.Exception(() => builder.AddColumnIfNotExistsFromModel(
            static migrationBuilder =>
            {
                migrationBuilder.AddColumn<int>("caption", "items", nullable: false);

                throw new InvalidOperationException("callback failed");
            }));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("callback failed", invalidOperation.Message);
        Assert.Empty(builder.Operations);
    }
}
