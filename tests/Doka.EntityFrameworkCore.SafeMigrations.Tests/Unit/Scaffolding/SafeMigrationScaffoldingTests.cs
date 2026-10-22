namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Delegates table creation unchanged when safe scaffolding is disabled.</summary>
    [Fact]
    public void DisabledScaffoldingDelegatesToEfCoreUnchanged()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict, isEnabled: false);
        var builder = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [CreateTable()], builder);

        // Assert
        Assert.Contains("migrationBuilder.CreateTable(", builder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTableIfNotExists", builder.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Delegates every supported ordinary operation unchanged when disabled.</summary>
    /// <param name="operation">The ordinary EF operation supplied by this case.</param>
    /// <param name="expectedIntentType">The expected normalized safe intent type.</param>
    [Theory]
    [MemberData(
        nameof(SafeMigrationOperationCompatibilityTests.SupportedOperations),
        MemberType = typeof(SafeMigrationOperationCompatibilityTests))]
    public void DisabledScaffoldingDelegatesEverySupportedStandardOperationToEfCore(
        MigrationOperation operation,
        Type expectedIntentType
    )
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict, isEnabled: false);
        var builder = new IndentedStringBuilder();
        var efMethod = operation.GetType().Name.Replace("Operation", string.Empty, StringComparison.Ordinal);
        var normalized = SafeMigrationOperationCompatibility.Normalize(operation, providerAdapter: null);

        // Act
        generator.Generate("migrationBuilder", [operation], builder);

        // Assert
        Assert.Equal(expectedIntentType, Assert.IsType<SafeMigrationOperation>(normalized.Operation).Intent.GetType());
        Assert.Contains($"migrationBuilder.{efMethod}", builder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Doka.EntityFrameworkCore.SafeMigrations", builder.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Delegates otherwise unsupported standard operations unchanged when disabled.</summary>
    /// <param name="operation">The ordinary EF operation supplied by this case.</param>
    [Theory]
    [MemberData(nameof(DisabledUnsupportedOperations))]
    public void DisabledScaffoldingDelegatesUnsupportedStandardOperationsToEfCore(
        MigrationOperation operation
    )
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict, isEnabled: false);
        var builder = new IndentedStringBuilder();
        var efMethod = operation.GetType().Name.Replace("Operation", string.Empty, StringComparison.Ordinal);

        // Act
        generator.Generate("migrationBuilder", [operation], builder);

        // Assert
        Assert.Contains($"migrationBuilder.{efMethod}", builder.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Doka.EntityFrameworkCore.SafeMigrations", builder.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Rejects an unknown operation without publishing a partial source buffer.</summary>
    [Fact]
    public void UnknownOperationStopsGenerationBeforePublishingSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();

        // Act
        var exception = Record.Exception(() => generator.Generate(
            "migrationBuilder",
            [new UnknownMigrationOperation()],
            builder));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Equal(string.Empty, builder.ToString());
    }

    /// <summary>Uses safe schema operations in both supported scaffolding modes.</summary>
    /// <param name="mode">The selected scaffolding mode.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence)]
    public void SchemaGenerationUsesSafeOperationsForBothScaffoldingModes(
        SafeMigrationScaffoldingMode mode
    )
    {
        // Arrange
        var generator = CreateOperationGenerator(mode);
        var builder = new IndentedStringBuilder();
        MigrationOperation[] operations =
        [
            new EnsureSchemaOperation { Name = "application" },
            new DropSchemaOperation { Name = "archive" },
        ];

        // Act
        generator.Generate("migrationBuilder", operations, builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains(".EnsureSchemaExists(", source, StringComparison.Ordinal);
        Assert.Contains("name: \"application\"", source, StringComparison.Ordinal);
        Assert.Contains(".DropSchemaIfExists(", source, StringComparison.Ordinal);
        Assert.Contains("name: \"archive\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".EnsureSchema(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".DropSchema(", source, StringComparison.Ordinal);
    }

    /// <summary>Preserves provider schema operations while safe scaffolding is disabled.</summary>
    [Fact]
    public void DisabledScaffoldingPreservesProviderSchemaOperations()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict, isEnabled: false);
        var builder = new IndentedStringBuilder();
        MigrationOperation[] operations =
        [
            new EnsureSchemaOperation { Name = "application" },
            new DropSchemaOperation { Name = "archive" },
        ];

        // Act
        generator.Generate("migrationBuilder", operations, builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains(".EnsureSchema(", source, StringComparison.Ordinal);
        Assert.Contains("name: \"application\"", source, StringComparison.Ordinal);
        Assert.Contains(".DropSchema(", source, StringComparison.Ordinal);
        Assert.Contains("name: \"archive\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".EnsureSchemaExists(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".DropSchemaIfExists(", source, StringComparison.Ordinal);
    }

    /// <summary>Emits closed safe DROP and RENAME operations instead of ordinary EF calls.</summary>
    [Fact]
    public void DropAndRenameGenerationUsesClosedSafeOperations()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        MigrationOperation[] operations =
        [
            new DropColumnOperation { Name = "legacy", Table = "items" },
            new RenameColumnOperation { Name = "caption", Table = "items", NewName = "name" },
            new RenameTableOperation { Name = "items", NewName = "products" },
            new RenameIndexOperation
            {
                Name = "ix_items_caption",
                Table = "items",
                NewName = "ix_items_name",
            },
        ];

        // Act
        generator.Generate("migrationBuilder", operations, builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains(".DropColumnIfExists(", source, StringComparison.Ordinal);
        Assert.Contains(".RenameColumnIfExists(", source, StringComparison.Ordinal);
        Assert.Contains(".RenameTableIfExists(", source, StringComparison.Ordinal);
        Assert.Contains(".RenameIndexIfExists(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.DropColumn(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.RenameColumn(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.RenameTable(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.RenameIndex(", source, StringComparison.Ordinal);
    }

    /// <summary>Rejects each unsupported standard operation before publishing source.</summary>
    /// <param name="operation">The ordinary EF operation supplied by this case.</param>
    [Theory]
    [MemberData(nameof(UnsupportedScaffoldingOperations))]
    public void EnabledScaffoldingRejectsOperationsWithoutSafeContracts(
        MigrationOperation operation
    )
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();

        // Act
        var exception = Record.Exception(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        var unsupported = Assert.IsType<NotSupportedException>(exception);

        Assert.Contains(operation.GetType().FullName!, unsupported.Message, StringComparison.Ordinal);
        Assert.Empty(builder.ToString());
    }

    /// <summary>Does not publish a valid prefix when a later operation is unsupported.</summary>
    [Fact]
    public void EnabledScaffolding_DoesNotPublishAValidPrefixBeforeAnUnsupportedOperation()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        MigrationOperation[] operations =
        [
            new DropColumnOperation { Name = "legacy", Table = "items" },
            new SqlOperation { Sql = "DELETE FROM items;" },
        ];

        // Act
        var exception = Record.Exception(() => generator.Generate("migrationBuilder", operations, builder));

        // Assert
        var unsupported = Assert.IsType<NotSupportedException>(exception);

        Assert.Contains(typeof(SqlOperation).FullName!, unsupported.Message, StringComparison.Ordinal);
        Assert.Empty(builder.ToString());
    }

    /// <summary>Covers every typed EF generation handler with an explicit safe override.</summary>
    [Fact]
    public void GeneratorOverridesEveryEfCoreTypedOperationHandler()
    {
        // Arrange
        var baseOperationTypes = typeof(CSharpMigrationOperationGenerator)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(static method => method.Name == "Generate")
            .Where(static method => method.GetParameters() is
                [
                    { ParameterType: var operationType },
                    { ParameterType: var builderType },
                ]
                && typeof(MigrationOperation).IsAssignableFrom(operationType)
                && builderType == typeof(IndentedStringBuilder))
            .Select(static method => method.GetParameters()[0].ParameterType)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        // Act
        var overriddenOperationTypes = typeof(SafeMigrationCSharpMigrationOperationGenerator)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(static method => method.Name == "Generate")
            .Where(static method => method.GetParameters() is
                [
                    { ParameterType: var operationType },
                    { ParameterType: var builderType },
                ]
                && typeof(MigrationOperation).IsAssignableFrom(operationType)
                && builderType == typeof(IndentedStringBuilder))
            .Select(static method => method.GetParameters()[0].ParameterType)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(baseOperationTypes, overriddenOperationTypes);
    }

    /// <summary>Expands typed convergence into table, column and constraint contracts in order.</summary>
    [Fact]
    public void TypedConvergenceExpandsIntoContainerColumnsAndConstraints()
    {
        // Arrange
        var builder = new MigrationBuilder("test");

        // Act
        builder.ConvergeTableFromModel(
            "users",
            table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                Email = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_users", value => value.Id);
                table.UniqueConstraint("uq_users_email", value => value.Email);
            });

        // Assert
        Assert.Collection(
            builder.Operations,
            operation =>
            {
                var safe = Assert.IsType<SafeMigrationOperation>(operation);
                var intent = Assert.IsType<EnsureTableIntent>(safe.Intent);

                Assert.Equal(SafeMigrationTableMode.ConvergenceContainer, intent.Mode);
                Assert.Equal(SafeMigrationPolicy.ExistenceOnly, safe.Policy);
            },
            operation => Assert.IsType<EnsureColumnIntent>(Assert.IsType<SafeMigrationOperation>(operation).Intent),
            operation => Assert.IsType<EnsureColumnIntent>(Assert.IsType<SafeMigrationOperation>(operation).Intent),
            operation => Assert.IsType<EnsurePrimaryKeyIntent>(Assert.IsType<SafeMigrationOperation>(operation).Intent),
            operation => Assert.IsType<EnsureUniqueConstraintIntent>(
                Assert.IsType<SafeMigrationOperation>(operation).Intent));
    }
}
