namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects explicit provider annotation capability and physical column-shape compatibility.</summary>
public sealed class MySqlSafeMigrationColumnAnnotationTests
{
    /// <summary>Accepts GUID-format annotations only when the declared physical store shape agrees.</summary>
    /// <param name="storeType">The physical shape required by the selected GUID format.</param>
    /// <param name="format">The explicitly authored native GUID format.</param>
    [Theory]
    [InlineData("binary(16)", DokaMySqlGuidFormat.Binary16)]
    [InlineData("char(36)", DokaMySqlGuidFormat.Char36)]
    public void GuidFormat_WithMatchingGuidStoreType_IsSupported(
        string storeType,
        DokaMySqlGuidFormat format
    )
    {
        // Arrange
        var definition = CreateDefinition(typeof(Guid), storeType, format);

        // Act
        var unsupported = MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition);
        var repairable = MySqlSafeMigrationColumnMetadata.CanSafelyConverge(definition);

        // Assert
        Assert.False(unsupported);
        Assert.True(repairable);
    }

    /// <summary>Rejects undefined GUID formats and contradictory declared physical shapes.</summary>
    /// <param name="clrType">The CLR domain paired with the authored physical shape.</param>
    /// <param name="storeType">The declared physical mapping challenged by this case.</param>
    /// <param name="format">The undefined or incompatible GUID-format annotation.</param>
    [Theory]
    [InlineData(typeof(Guid), "binary(16)", (DokaMySqlGuidFormat)int.MaxValue)]
    [InlineData(typeof(Guid), "char(36)", DokaMySqlGuidFormat.Binary16)]
    [InlineData(typeof(Guid), "binary(16)", DokaMySqlGuidFormat.Char36)]
    [InlineData(typeof(string), "char(36)", DokaMySqlGuidFormat.Char36)]
    public void GuidFormat_WithUnknownOrContradictoryShape_IsRejected(
        Type clrType,
        string storeType,
        DokaMySqlGuidFormat format
    )
    {
        // Arrange
        var definition = CreateDefinition(clrType, storeType, format);

        // Act
        var unsupported = MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition);
        var repairable = MySqlSafeMigrationColumnMetadata.CanSafelyConverge(definition);

        // Assert
        Assert.True(unsupported);
        Assert.False(repairable);
    }

    /// <summary>Keeps an explicitly non-generated column eligible for safe mutable-facet repair.</summary>
    /// <param name="clrType">The CLR domain paired with the declared physical store type.</param>
    /// <param name="storeType">The exact authored native physical store type.</param>
    [Theory]
    [InlineData(typeof(bool), "tinyint(1)")]
    [InlineData(typeof(string), "varchar(80)")]
    [InlineData(typeof(Guid), "char(36)")]
    [InlineData(typeof(DateTime), "datetime(6)")]
    public void ExplicitNoneValueGeneration_IsRepairable(
        Type clrType,
        string storeType
    )
    {
        // Arrange
        var definition = CreateDefinition(
            clrType,
            storeType,
            "Doka:MySql:ValueGenerationStrategy",
            MySqlValueGenerationStrategy.None);

        // Act
        var hasUnsupportedAnnotation =
            MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition);

        var canConverge = MySqlSafeMigrationColumnMetadata.CanSafelyConverge(definition);

        // Assert
        Assert.False(hasUnsupportedAnnotation);
        Assert.True(canConverge);
    }

    /// <summary>Accepts only the enumerated supported provider value-generation strategies.</summary>
    /// <param name="strategy">The provider value-generation strategy under test.</param>
    /// <param name="expected">The independently specified result for this exact input case.</param>
    [Theory]
    [InlineData(MySqlValueGenerationStrategy.ClientGuid, true)]
    [InlineData(MySqlValueGenerationStrategy.AutoIncrement, true)]
    [InlineData(MySqlValueGenerationStrategy.HiLo, false)]
    [InlineData((MySqlValueGenerationStrategy)int.MaxValue, false)]
    public void ValueGenerationStrategy_UsesExplicitSupportedSet(
        MySqlValueGenerationStrategy strategy,
        bool expected
    )
    {
        // Arrange
        var definition = CreateDefinition(
            typeof(long),
            "bigint",
            "Doka:MySql:ValueGenerationStrategy",
            strategy);

        // Act
        var hasUnsupportedAnnotation =
            MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition);

        var canConverge = MySqlSafeMigrationColumnMetadata.CanSafelyConverge(definition);

        // Assert
        Assert.Equal(expected, canConverge);
        Assert.Equal(!expected, hasUnsupportedAnnotation);
    }

    /// <summary>Rejects unknown provider annotations rather than assuming they have no SQL effect.</summary>
    [Fact]
    public void UnknownProviderAnnotation_IsRejected()
    {
        // Arrange
        var definition = CreateDefinition(typeof(string), "varchar(80)", "Test:Unknown", true);

        // Act
        var hasUnsupportedAnnotation =
            MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition);

        var canConverge = MySqlSafeMigrationColumnMetadata.CanSafelyConverge(definition);

        // Assert
        Assert.True(hasUnsupportedAnnotation);
        Assert.False(canConverge);
    }

    private static ExpectedColumnDefinition CreateDefinition(
        Type clrType,
        string storeType,
        DokaMySqlGuidFormat guidFormat
    )
    {
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "entities",
            ClrType = clrType,
            ColumnType = storeType,
            IsNullable = false,
            ["Doka:MySql:GuidFormat"] = guidFormat,
        };

        return SafeMigrationExpectedDefinitionFactory.From(operation);
    }

    private static ExpectedColumnDefinition CreateDefinition(
        Type clrType,
        string storeType,
        string annotationName,
        object annotationValue
    )
    {
        var operation = new AddColumnOperation
        {
            Name = "value",
            Table = "entities",
            ClrType = clrType,
            ColumnType = storeType,
            IsNullable = false,
            [annotationName] = annotationValue,
        };

        return SafeMigrationExpectedDefinitionFactory.From(operation);
    }
}
