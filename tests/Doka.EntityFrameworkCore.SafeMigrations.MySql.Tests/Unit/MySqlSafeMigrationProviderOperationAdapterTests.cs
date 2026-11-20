namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects historical provider operation normalization and unsupported metadata rejection.</summary>
public sealed class MySqlSafeMigrationProviderOperationAdapterTests
{
    /// <summary>Normalizes supported annotated historical column drops into the guarded intent contract.</summary>
    [Fact]
    public void Normalize_ConvertsAnnotatedHistoricalDropColumn()
    {
        // Arrange
        var operation = new DropColumnOperation
        {
            Name = "category_id",
            Table = "certificate_types",
            ["Doka:MySql:GuidFormat"] = DokaMySqlGuidFormat.Char36,
            ["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.None,
            ["Doka:MySql:CharSet"] = "utf8mb4",
            ["Relational:Collation"] = "utf8mb4_unicode_ci",
        };

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

    /// <summary>Rejects unknown metadata on a historical column drop rather than silently discarding it.</summary>
    [Fact]
    public void Normalize_RejectsUnknownHistoricalDropColumnMetadata()
    {
        // Arrange
        var operation = new DropColumnOperation
        {
            Name = "category_id",
            Table = "certificate_types",
            ["Provider:Unknown"] = true,
        };

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        Assert.Equal(SafeMigrationOperationCompatibilityKind.Unsupported, result.Kind);
    }

    /// <summary>
    /// Normalizes supported annotated historical column renames without changing their source or target identity.
    /// </summary>
    [Fact]
    public void Normalize_ConvertsAnnotatedHistoricalRenameColumn()
    {
        // Arrange
        var operation = new RenameColumnOperation
        {
            Name = "old_code",
            NewName = "renamed_code",
            Table = "users",
            ["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.None,
            ["Doka:MySql:CharSet"] = "utf8mb4",
            ["Relational:Collation"] = "utf8mb4_unicode_ci",
        };

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

    /// <summary>Rejects unknown metadata on a historical column rename before creating a guarded intent.</summary>
    [Fact]
    public void Normalize_RejectsUnknownHistoricalRenameColumnMetadata()
    {
        // Arrange
        var operation = new RenameColumnOperation
        {
            Name = "old_code",
            NewName = "renamed_code",
            Table = "users",
            ["Provider:Unknown"] = true,
        };

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        Assert.Equal(SafeMigrationOperationCompatibilityKind.Unsupported, result.Kind);
    }

    /// <summary>Rejects contradictory or invalid column character-set metadata during normalization.</summary>
    /// <param name="annotationName">The provider annotation whose independent capability boundary is exercised.</param>
    /// <param name="annotationValue">The authored provider annotation value under test.</param>
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
            [annotationName] = annotationValue,
        };

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        Assert.Equal(SafeMigrationOperationCompatibilityKind.Unsupported, result.Kind);
    }

    /// <summary>Preserves provider index-prefix metadata in the normalized guarded index definition.</summary>
    [Fact]
    public void Normalize_ProjectsIndexPrefixMetadataIntoTheSafeDefinition()
    {
        // Arrange
        var operation = new CreateIndexOperation
        {
            Name = "ix_logs_property",
            Table = "logs",
            Columns = ["property"],
            ["Doka:MySql:IndexPrefixLength"] = new[] { 768 },
        };

        // Act
        var result = SafeMigrationOperationCompatibility.Normalize(
            operation,
            MySqlSafeMigrationProviderOperationAdapter.Instance);

        // Assert
        var safeOperation = Assert.IsType<SafeMigrationOperation>(result.Operation);
        var intent = Assert.IsType<EnsureIndexIntent>(safeOperation.Intent);

        Assert.Equal(768,
            Assert.Single(intent.Definition.Keys)
                .PrefixLength);
    }

    /// <summary>Certifies only the supported bounded database-alter metadata contract.</summary>
    /// <param name="shape">The serializable identity of the exact native operation constructed during Arrange.</param>
    /// <param name="expectedCertified">Whether this exact native shape must receive a preservation certificate.</param>
    [Theory]
    [InlineData("supported", true)]
    [InlineData("unknown-annotation", false)]
    [InlineData("blank-collation", false)]
    public void Normalize_CertifiesOnlyBoundedAlterDatabaseMetadata(
        string shape,
        bool expectedCertified
    )
    {
        // Arrange
        var operation = shape switch
        {
            "supported" => new AlterDatabaseOperation
            {
                Collation = "utf8mb4_unicode_ci",
                ["Doka:MySql:CharSet"] = "utf8mb4",
            },
            "unknown-annotation" => new AlterDatabaseOperation
            {
                Collation = "utf8mb4_unicode_ci",
                ["Provider:Unknown"] = "value",
            },
            "blank-collation" => new AlterDatabaseOperation { Collation = " " },
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

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
