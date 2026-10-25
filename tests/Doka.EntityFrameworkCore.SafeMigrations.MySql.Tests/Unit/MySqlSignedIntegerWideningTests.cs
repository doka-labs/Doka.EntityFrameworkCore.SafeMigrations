namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Fixes the exact injective signed integer domain independently of native catalog execution.</summary>
public sealed class MySqlSignedIntegerWideningTests
{
    /// <summary>Only strictly larger signed built-in domains with compatible CLR declarations qualify.</summary>
    /// <param name="sourceType">The exact authored native source type.</param>
    /// <param name="targetType">The exact reviewed native target type.</param>
    /// <param name="sourceClr">The declared source CLR domain.</param>
    /// <param name="targetClr">The declared target CLR domain.</param>
    /// <param name="expected">Whether the complete transition qualifies.</param>
    [Theory]
    [InlineData("tinyint", "smallint", typeof(sbyte), typeof(short), true)]
    [InlineData("smallint", "int", typeof(short), typeof(int), true)]
    [InlineData("mediumint", "int", typeof(int), typeof(int), true)]
    [InlineData("int", "bigint", typeof(int), typeof(long), true)]
    [InlineData("integer", "BIGINT", typeof(int), typeof(long), true)]
    [InlineData(" INT ", " BIGINT ", typeof(int), typeof(long), true)]
    [InlineData("bigint", "int", typeof(long), typeof(int), false)]
    [InlineData("int", "int", typeof(int), typeof(int), false)]
    [InlineData("int unsigned", "bigint", typeof(uint), typeof(long), false)]
    [InlineData("int", "bigint unsigned", typeof(int), typeof(ulong), false)]
    [InlineData("int(11)", "bigint", typeof(int), typeof(long), false)]
    [InlineData("int", "decimal(20)", typeof(int), typeof(decimal), false)]
    [InlineData("custom_int", "bigint", typeof(int), typeof(long), false)]
    [InlineData("tinyint", "bigint", typeof(bool), typeof(long), false)]
    [InlineData("int", "bigint", typeof(int), typeof(int), false)]
    public void SignedStorageDomainsRemainExact(string sourceType, string targetType, Type sourceClr,
        Type targetClr, bool expected)
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", sourceClr, false, sourceType);
        var target = new ExpectedColumnDefinition("value", targetClr, false, targetType);

        // Act
        var result = MySqlSignedIntegerWidening.IsSafe(source, target);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>A type widening does not grant permission to replace unrelated intrinsic facets.</summary>
    /// <param name="facet">The deliberately changed non-type facet.</param>
    [Theory]
    [InlineData("nullable")]
    [InlineData("comment")]
    [InlineData("collation")]
    [InlineData("default")]
    [InlineData("sql_default")]
    [InlineData("generation")]
    [InlineData("computed")]
    [InlineData("precision")]
    public void NonTypeFacetChangesAreRejected(string facet)
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(int), false, "int");
        var target = new ExpectedColumnDefinition("value", typeof(long), facet == "nullable", "bigint",
            precision: facet == "precision" ? 19 : null,
            collation: facet == "collation" ? new SafeMigrationCollationIdentifier("utf8mb4_unicode_ci") : null,
            comment: facet == "comment" ? "changed" : null,
            defaultValue: facet == "default" ? SafeMigrationDefaultValue.Literal(1L)
                : facet == "sql_default" ? SafeMigrationDefaultValue.Sql("1") : null,
            computedColumnSql: facet == "computed" ? "1" : null)
        {
            ProviderAnnotations = facet == "generation"
                ? AutoIncrementMetadata() : [],
        };

        // Act
        var result = MySqlSignedIntegerWidening.IsSafe(source, target);

        // Assert
        Assert.False(result);
    }

    /// <summary>Equal representable signed literal defaults retain their behavior after CLR widening.</summary>
    [Fact]
    public void EqualSignedLiteralDefaultsArePreserved()
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(int), false, "int",
            defaultValue: SafeMigrationDefaultValue.Literal(int.MinValue));
        var target = new ExpectedColumnDefinition("value", typeof(long), false, "bigint",
            defaultValue: SafeMigrationDefaultValue.Literal((long)int.MinValue));

        // Act
        var result = MySqlSignedIntegerWidening.IsSafe(source, target);

        // Assert
        Assert.True(result);
    }

    private static IReadOnlyList<SafeMigrationProviderAnnotation> AutoIncrementMetadata()
    {
        var operation = new AddColumnOperation();
        operation["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.AutoIncrement;

        return SafeMigrationProviderAnnotation.Capture(operation);
    }
}
