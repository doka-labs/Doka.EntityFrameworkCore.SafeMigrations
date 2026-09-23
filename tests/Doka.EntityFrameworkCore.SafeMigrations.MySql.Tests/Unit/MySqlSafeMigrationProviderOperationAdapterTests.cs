namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlSafeMigrationProviderOperationAdapterTests
{
    public static TheoryData<AlterDatabaseOperation, bool>
        AlterDatabaseCompatibilityCases
    {
        get
        {
            var supported = new AlterDatabaseOperation { Collation = "utf8mb4_unicode_ci" };
            supported["Doka:MySql:CharSet"] = "utf8mb4";

            var unsupportedAnnotation = new AlterDatabaseOperation { Collation = "utf8mb4_unicode_ci" };
            unsupportedAnnotation["Provider:Unknown"] = "value";

            return new TheoryData<AlterDatabaseOperation, bool>
            {
                { supported, true },
                { unsupportedAnnotation, false },
                { new AlterDatabaseOperation { Collation = " " }, false },
            };
        }
    }

    [Fact]
    public void Normalize_ConvertsAnnotatedHistoricalDropColumn()
    {
        // Arrange
        var operation = new DropColumnOperation
        {
            Name = "category_id",
            Table = "certificate_types",
        };

        operation["Doka:MySql:GuidFormat"] = DokaMySqlGuidFormat.Char36;
        operation["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.None;
        operation["Doka:MySql:CharSet"] = "utf8mb4";
        operation["Relational:Collation"] = "utf8mb4_unicode_ci";

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        var safeOperation = Assert.IsType<SafeMigrationOperation>(result.Operation);

        Assert.Equal(SafeMigrationOperationCompatibilityKind.Safe, result.Kind);
        var intent = Assert.IsType<DropColumnIntent>(safeOperation.Intent);
        Assert.Equal("category_id", intent.Name);
        Assert.Equal("certificate_types", intent.Table);
    }

    [Fact]
    public void Normalize_RejectsUnknownHistoricalDropColumnMetadata()
    {
        // Arrange
        var operation = new DropColumnOperation
        {
            Name = "category_id",
            Table = "certificate_types",
        };

        operation["Provider:Unknown"] = true;

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        Assert.Equal(SafeMigrationOperationCompatibilityKind.Unsupported, result.Kind);
    }

    [Fact]
    public void Normalize_ConvertsAnnotatedHistoricalRenameColumn()
    {
        // Arrange
        var operation = new RenameColumnOperation
        {
            Name = "old_code",
            NewName = "renamed_code",
            Table = "users",
        };

        operation["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.None;
        operation["Doka:MySql:CharSet"] = "utf8mb4";
        operation["Relational:Collation"] = "utf8mb4_unicode_ci";

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        var safeOperation = Assert.IsType<SafeMigrationOperation>(result.Operation);
        var intent = Assert.IsType<RenameColumnIntent>(safeOperation.Intent);

        Assert.Equal(SafeMigrationOperationCompatibilityKind.Safe, result.Kind);
        Assert.Equal("old_code", intent.Name);
        Assert.Equal("renamed_code", intent.NewName);
        Assert.Equal("users", intent.Table);
    }

    [Fact]
    public void Normalize_RejectsUnknownHistoricalRenameColumnMetadata()
    {
        // Arrange
        var operation = new RenameColumnOperation
        {
            Name = "old_code",
            NewName = "renamed_code",
            Table = "users",
        };

        operation["Provider:Unknown"] = true;

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        Assert.Equal(SafeMigrationOperationCompatibilityKind.Unsupported, result.Kind);
    }

    [Theory]
    [InlineData("Doka:MySql:CharSet", "")]
    [InlineData("Doka:MySql:CharSet", " ")]
    [InlineData("Relational:Collation", "")]
    [InlineData("Relational:Collation", " ")]
    public void Normalize_RejectsInvalidColumnCharacterMetadata(
        string annotationName,
        string annotationValue
    )
    {
        // Arrange
        var operation = new RenameColumnOperation
        {
            Name = "old_code",
            NewName = "renamed_code",
            Table = "users",
        };

        operation[annotationName] = annotationValue;

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        Assert.Equal(SafeMigrationOperationCompatibilityKind.Unsupported, result.Kind);
    }

    [Fact]
    public void Normalize_ProjectsIndexPrefixMetadataIntoTheSafeDefinition()
    {
        // Arrange
        var operation = new CreateIndexOperation
        {
            Name = "ix_logs_property",
            Table = "logs",
            Columns = ["property"],
        };

        operation["Doka:MySql:IndexPrefixLength"] = new[] { 768 };

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        var safeOperation = Assert.IsType<SafeMigrationOperation>(result.Operation);
        var intent = Assert.IsType<EnsureIndexIntent>(safeOperation.Intent);

        Assert.Equal(768, Assert.Single(intent.Definition.Keys).PrefixLength);
    }

    [Theory]
    [MemberData(nameof(AlterDatabaseCompatibilityCases))]
    public void Normalize_CertifiesOnlyBoundedAlterDatabaseMetadata(
        AlterDatabaseOperation operation,
        bool expectedCertified
    )
    {
        // Arrange
        var adapter = MySqlSafeMigrationProviderOperationAdapter.Instance;

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            adapter);

        // Assert
        Assert.Equal(
            expectedCertified
                ? SafeMigrationOperationCompatibilityKind.CertifiedProvider
                : SafeMigrationOperationCompatibilityKind.Unsupported,
            result.Kind);
    }
}
