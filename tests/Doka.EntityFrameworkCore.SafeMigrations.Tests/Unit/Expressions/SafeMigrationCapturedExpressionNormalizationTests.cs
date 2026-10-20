namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies provider-native projection views without changing source spelling or column facets.</summary>
public sealed class SafeMigrationCapturedExpressionNormalizationTests
{
    /// <summary>
    /// Normalization preserves complete column metadata while folding only native unquoted dependencies.
    /// </summary>
    [Fact]
    public void NormalizedColumnPreservesEveryFacetAndNativeSource()
    {
        // Arrange
        const string sql = "CASE WHEN VALUE > 0 THEN \"QuotedValue\" ELSE VALUE + 2147483648 END";
        var operation = new AddColumnOperation
        {
            Name = "computed_value", ClrType = typeof(string), ColumnType = "varchar(80)", IsNullable = true,
            IsUnicode = false, MaxLength = 80, IsFixedLength = true, IsRowVersion = true, Precision = 15,
            Scale = 3, Collation = "C", Comment = "keep", ComputedColumnSql = sql, IsStored = true,
        };

        operation.AddAnnotation("Provider:Facet", 1);
        var original = SafeMigrationExpectedDefinitionFactory.From(operation);

        // Act
        var normalized = SafeMigrationCapturedExpressionNormalization.NormalizeColumn(original,
            foldUnquotedIdentifiers: true);

        // Assert
        Assert.NotSame(original, normalized);
        Assert.Same(sql, normalized.CapturedComputedSql);
        Assert.Same(original.ProviderAnnotations, normalized.ProviderAnnotations);
        Assert.Equal((original.Name, original.ClrType, original.IsNullable, original.StoreType, original.IsUnicode,
                original.MaxLength, original.IsFixedLength, original.IsRowVersion, original.Precision, original.Scale,
                original.Collation, original.Comment, original.DefaultValue, original.ComputedColumnSql,
                original.IsStored),
            (normalized.Name, normalized.ClrType, normalized.IsNullable, normalized.StoreType, normalized.IsUnicode,
                normalized.MaxLength, normalized.IsFixedLength, normalized.IsRowVersion, normalized.Precision,
                normalized.Scale, normalized.Collation, normalized.Comment, normalized.DefaultValue,
                normalized.ComputedColumnSql, normalized.IsStored));

        Assert.True(SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
            normalized.ComputedExpression!, "value", StringComparer.Ordinal));
        Assert.True(SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
            normalized.ComputedExpression!, "QuotedValue", StringComparer.Ordinal));
        Assert.True(SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
            original.ComputedExpression!, "VALUE", StringComparer.Ordinal));
    }

    /// <summary>CHECK normalization retains source identity, ownership, and source token precision.</summary>
    [Fact]
    public void NormalizedCheckPreservesCapturedSourceAndOwner()
    {
        // Arrange
        const string sql = "VALUE > 2147483648";
        var original = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", sql, "app");

        // Act
        var normalized = SafeMigrationCapturedExpressionNormalization.NormalizeCheck(original,
            foldUnquotedIdentifiers: true);

        // Assert
        Assert.NotSame(original, normalized);
        Assert.Same(sql, normalized.CapturedSql);
        Assert.Equal((original.Name, original.Table, original.Schema),
            (normalized.Name, normalized.Table, normalized.Schema));

        var binary = Assert.IsType<SafeMigrationSqlBinaryExpression>(normalized.Expression);
        Assert.Equal("value", Assert.IsType<SafeMigrationSqlIdentifierExpression>(binary.Left).Parts[0]);
        Assert.Equal("2147483648", Assert.IsType<SafeMigrationSqlLiteralExpression>(binary.Right)
            .NumericSourceToken.Span.ToString());
    }

    /// <summary>Unchanged or explicitly non-captured contracts retain their original reference and semantics.</summary>
    [Fact]
    public void NormalizationFastPathsDoNotCopyUnaffectedContracts()
    {
        // Arrange
        var captured = SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "computed_value", ClrType = typeof(int), ComputedColumnSql = "value + 1",
        });

        var rawCheck = new ExpectedCheckConstraintDefinition("ck_items", "items", "VALUE > 0");
        var rawColumn = new ExpectedColumnDefinition("computed_value", typeof(int), true,
            computedColumnSql: "VALUE + 1");

        // Act
        var unchanged = SafeMigrationCapturedExpressionNormalization.NormalizeColumn(captured, true);
        var explicitRaw = SafeMigrationCapturedExpressionNormalization.NormalizeColumn(rawColumn, true);
        var check = SafeMigrationCapturedExpressionNormalization.NormalizeCheck(rawCheck, true);

        // Assert
        Assert.Same(captured, unchanged);
        Assert.Same(rawColumn, explicitRaw);
        Assert.Same(rawCheck, check);
    }
}
