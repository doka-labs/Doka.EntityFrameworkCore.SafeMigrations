namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Checks the bounded value-domain proof independently from catalog source matching.</summary>
public sealed class SqliteColumnRepairProofTests
{
    /// <summary>Non-nullability/default metadata is insufficient to certify any value-domain change.</summary>
    /// <param name="facet">The independently changed storage, CLR or generation facet.</param>
    [Theory]
    [InlineData("store-type")]
    [InlineData("clr-type")]
    [InlineData("length")]
    [InlineData("precision")]
    [InlineData("scale")]
    [InlineData("unicode")]
    [InlineData("fixed-length")]
    [InlineData("collation")]
    [InlineData("computed")]
    [InlineData("stored")]
    [InlineData("autoincrement")]
    public void DomainChangesDoNotAuthorizeRepairs(string facet)
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(long), true, storeType: "INTEGER",
            computedColumnSql: facet == "stored" ? "1 + 1" : null,
            isStored: facet == "stored" ? false : null);

        var target = new ExpectedColumnDefinition("value", facet == "clr-type" ? typeof(int) : typeof(long), false,
            storeType: facet == "store-type" ? "REAL" : "INTEGER",
            isUnicode: facet == "unicode" ? true : null,
            maxLength: facet == "length" ? 10 : null,
            isFixedLength: facet == "fixed-length" ? true : null,
            precision: facet == "precision" ? 10 : null,
            scale: facet == "scale" ? 2 : null,
            collation: facet == "collation" ? new SafeMigrationCollationIdentifier("NOCASE") : null,
            computedColumnSql: facet is "computed" or "stored" ? "1 + 1" : null,
            isStored: facet == "stored" ? true : null)
        {
            ProviderAnnotations = facet == "autoincrement" ? AutoincrementAnnotations() : [],
        };

        // Act
        var repairIsLossless = SqliteColumnRepairProof.HasLosslessShape(source, target);

        // Assert
        Assert.False(repairIsLossless);
    }

    /// <summary>Generation expression removal and mutation cannot replace copied existing values.</summary>
    /// <param name="removeExpression">Whether the target removes rather than changes the expression.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedValueDomainChangesDoNotAuthorizeRepairs(bool removeExpression)
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(long), false, storeType: "INTEGER",
            computedColumnSql: "1 + 1");

        var target = new ExpectedColumnDefinition("value", typeof(long), false, storeType: "INTEGER",
            computedColumnSql: removeExpression ? null : "1 + 2");

        // Act
        var repairIsLossless = SqliteColumnRepairProof.HasLosslessShape(source, target);

        // Assert
        Assert.False(repairIsLossless);
    }

    /// <summary>Defaults and nullability do not alter copied non-null values with unchanged shape.</summary>
    /// <param name="sourceNullable">Whether the source accepts nulls.</param>
    /// <param name="targetNullable">Whether the target accepts nulls.</param>
    /// <param name="changeDefault">Whether the target changes the future-row default.</param>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void SameShapeRetainsNullabilityAndDefaultRepairs(
        bool sourceNullable,
        bool targetNullable,
        bool changeDefault)
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(long), sourceNullable, storeType: "INTEGER");
        var target = new ExpectedColumnDefinition("value", typeof(long), targetNullable, storeType: "INTEGER",
            defaultValue: changeDefault ? SafeMigrationDefaultValue.Literal(7L) : null);

        // Act
        var repairIsLossless = SqliteColumnRepairProof.HasLosslessShape(source, target);

        // Assert
        Assert.True(repairIsLossless);
    }

    /// <summary>Generation annotations are compared in both directions, including unchanged generation.</summary>
    /// <param name="sourceAutoincrement">Whether the source owns automatic rowid generation.</param>
    /// <param name="targetAutoincrement">Whether the target owns automatic rowid generation.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AutoincrementIdentityIsPreserved(bool sourceAutoincrement, bool targetAutoincrement)
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(long), true, storeType: "INTEGER")
        {
            ProviderAnnotations = sourceAutoincrement ? AutoincrementAnnotations() : [],
        };

        var target = new ExpectedColumnDefinition("value", typeof(long), false, storeType: "INTEGER")
        {
            ProviderAnnotations = targetAutoincrement ? AutoincrementAnnotations() : [],
        };

        // Act
        var repairIsLossless = SqliteColumnRepairProof.HasLosslessShape(source, target);

        // Assert
        Assert.Equal(sourceAutoincrement == targetAutoincrement, repairIsLossless);
    }

    /// <summary>Captures the provider-owned generation annotation using the production capture boundary.</summary>
    private static IReadOnlyList<SafeMigrationProviderAnnotation> AutoincrementAnnotations()
    {
        var operation = new AddColumnOperation();
        operation.AddAnnotation("Sqlite:Autoincrement", true);

        return SafeMigrationProviderAnnotation.Capture(operation);
    }
}
