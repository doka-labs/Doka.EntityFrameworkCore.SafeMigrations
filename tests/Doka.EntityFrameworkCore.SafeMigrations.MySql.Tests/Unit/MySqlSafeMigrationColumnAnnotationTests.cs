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
}
