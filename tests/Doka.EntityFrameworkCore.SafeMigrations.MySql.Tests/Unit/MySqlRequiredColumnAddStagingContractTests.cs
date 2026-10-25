namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies complete nullable staging copies without broadening the public definition surface.</summary>
public sealed class MySqlRequiredColumnAddStagingContractTests
{
    /// <summary>
    /// Every current facet survives staging; a newly added property requires an explicit test decision.
    /// </summary>
    /// <param name="expressionKind">The mutually exclusive default or computed-expression contract.</param>
    [Theory]
    [InlineData("default")]
    [InlineData("opaque")]
    [InlineData("structured")]
    [InlineData("captured")]
    public void NullableStagingCopiesEveryDefinitionFacet(string expressionKind)
    {
        // Arrange
        var annotated = new AddColumnOperation();
        annotated["Doka:MySql:RequiresExplicitBackfill"] = true;
        annotated["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.None;
        var definition = new ExpectedColumnDefinition(
            name: "complete_value",
            clrType: typeof(string),
            isNullable: false,
            storeType: "varchar(31)",
            isUnicode: true,
            maxLength: 31,
            isFixedLength: true,
            isRowVersion: true,
            precision: 7,
            scale: 2,
            collation: new SafeMigrationCollationIdentifier("utf8mb4_unicode_ci"),
            comment: "preserved staging comment",
            defaultValue: expressionKind == "default" ? SafeMigrationDefaultValue.Literal("kept") : null,
            computedColumnSql: expressionKind == "opaque" ? "`source_value`" : null,
            isStored: expressionKind == "default" ? null : true,
            computedExpression: expressionKind is "structured" or "captured"
                ? new SafeMigrationSqlIdentifierExpression(["source_value"])
                : null)
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(annotated),
            CapturedComputedSql = expressionKind == "captured" ? "`source_value`" : null,
        };

        string[] expectedProperties =
        [
            "CapturedComputedSql", "ClrType", "Collation", "Comment", "ComputedColumnSql", "ComputedExpression",
            "DefaultValue",
            "IsFixedLength", "IsNullable", "IsRowVersion", "IsStored", "IsUnicode", "MaxLength", "Name",
            "Precision", "ProviderAnnotations", "Scale", "StoreType",
        ];

        var properties = typeof(ExpectedColumnDefinition).GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // Act
        var staging = MySqlSafeMigrationCatalogSqlBuilder.CreateRequiredColumnAddNullableTarget(definition);

        // Assert
        // WHY: Equality alone could miss a new optional property whose fixture
        // and forgotten copy both default to null. The property inventory makes
        // every future facet an explicit copy-and-fixture maintenance decision.
        Assert.Equal(expectedProperties, properties.Select(static property => property.Name)
            .Order(StringComparer.Ordinal));
        Assert.NotSame(definition, staging);
        Assert.False(definition.IsNullable);
        Assert.True(staging.IsNullable);
        Assert.Same(definition.ProviderAnnotations, staging.ProviderAnnotations);
        Assert.Same(definition.DefaultValue, staging.DefaultValue);
        Assert.Same(definition.ComputedExpression, staging.ComputedExpression);
        Assert.Same(definition.CapturedComputedSql, staging.CapturedComputedSql);
        Assert.All(properties.Where(static property => property.Name != nameof(ExpectedColumnDefinition.IsNullable)),
            property => Assert.Equal(property.GetValue(definition), property.GetValue(staging)));
    }

    /// <summary>An absent authored definition cannot produce a partial staging target.</summary>
    [Fact]
    public void NullableStagingRejectsMissingDefinition()
    {
        // Arrange
        ExpectedColumnDefinition? definition = null;

        // Act
        var exception = Record.Exception(() =>
            MySqlSafeMigrationCatalogSqlBuilder.CreateRequiredColumnAddNullableTarget(definition!));

        // Assert
        Assert.Equal("definition", Assert.IsType<ArgumentNullException>(exception).ParamName);
    }
}
