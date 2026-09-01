namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlSafeMigrationColumnAnnotationTests
{
    [Fact]
    public void GuidFormat_WithMatchingGuidStoreType_IsSupported()
    {
        var binary = CreateDefinition(
            typeof(Guid),
            "binary(16)",
            DokaMySqlGuidFormat.Binary16);

        var character = CreateDefinition(
            typeof(Guid),
            "char(36)",
            DokaMySqlGuidFormat.Char36);

        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(binary));
        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(character));
        Assert.True(MySqlSafeMigrationColumnMetadata.CanSafelyConverge(binary));
        Assert.True(MySqlSafeMigrationColumnMetadata.CanSafelyConverge(character));
    }

    [Fact]
    public void GuidFormat_WithUnknownOrContradictoryShape_IsRejected()
    {
        var undefinedValue = CreateDefinition(
            typeof(Guid),
            "binary(16)",
            (DokaMySqlGuidFormat)int.MaxValue);

        var binaryWithCharacterStoreType = CreateDefinition(
            typeof(Guid),
            "char(36)",
            DokaMySqlGuidFormat.Binary16);

        var characterWithBinaryStoreType = CreateDefinition(
            typeof(Guid),
            "binary(16)",
            DokaMySqlGuidFormat.Char36);

        var nonGuidClrType = CreateDefinition(
            typeof(string),
            "char(36)",
            DokaMySqlGuidFormat.Char36);

        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(undefinedValue));
        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(binaryWithCharacterStoreType));
        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(characterWithBinaryStoreType));
        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(nonGuidClrType));
        Assert.False(MySqlSafeMigrationColumnMetadata.CanSafelyConverge(undefinedValue));
        Assert.False(MySqlSafeMigrationColumnMetadata.CanSafelyConverge(binaryWithCharacterStoreType));
        Assert.False(MySqlSafeMigrationColumnMetadata.CanSafelyConverge(characterWithBinaryStoreType));
        Assert.False(MySqlSafeMigrationColumnMetadata.CanSafelyConverge(nonGuidClrType));
    }

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
        var definition = CreateDefinition(
            clrType,
            storeType,
            "Doka:MySql:ValueGenerationStrategy",
            MySqlValueGenerationStrategy.None);

        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition));
        Assert.True(MySqlSafeMigrationColumnMetadata.CanSafelyConverge(definition));
    }

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
        var definition = CreateDefinition(
            typeof(long),
            "bigint",
            "Doka:MySql:ValueGenerationStrategy",
            strategy);

        Assert.Equal(expected, MySqlSafeMigrationColumnMetadata.CanSafelyConverge(definition));
        Assert.Equal(!expected, MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition));
    }

    [Fact]
    public void UnknownProviderAnnotation_IsRejected()
    {
        var definition = CreateDefinition(typeof(string), "varchar(80)", "Test:Unknown", true);

        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition));
        Assert.False(MySqlSafeMigrationColumnMetadata.CanSafelyConverge(definition));
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
        };

        operation["Doka:MySql:GuidFormat"] = guidFormat;

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
        };

        operation[annotationName] = annotationValue;

        return SafeMigrationExpectedDefinitionFactory.From(operation);
    }
}
