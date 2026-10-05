namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>
/// Pins the set-oriented verification of plain index keys without requiring a live engine.
/// </summary>
/// <remarks>
/// These tests exist because the per-key classifier opened its own catalog lookups for every
/// index key, and collapsing them can silently change two things no behavioural test covered:
/// the zero-based catalog vectors must keep their offset against the one-based property
/// positions, and a key carrying an operator class must stay out of the correlated set.
/// </remarks>
public sealed class PostgreSqlIndexKeyCatalogSqlTests
{
    private const string OfflineConnectionString =
        "Host=127.0.0.1;Port=1;Database=index_key_shape;Username=test;Password=test";

    /// <summary>Plain index keys keep their catalog lookups constant as the key count grows.</summary>
    [Fact]
    public void PlainIndexKeys_DoNotGrowCatalogLookupsWithKeyCount()
    {
        // Arrange
        using var context = CreateContext();
        var narrow = BuildStateExpression(context, PlainKeys(2));
        var wide = BuildStateExpression(context, PlainKeys(6));

        // Act
        var narrowLookups = Occurrences(narrow, "pg_catalog.pg_opclass");
        var wideLookups = Occurrences(wide, "pg_catalog.pg_opclass");

        // Assert
        Assert.Equal(narrowLookups, wideLookups);
    }

    /// <summary>An unknown index-key verdict stays a mismatch instead of being accepted.</summary>
    [Fact]
    public void PlainIndexKeys_TreatAnUnknownVerdictAsMismatch()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, PlainKeys(3));

        // Assert
        Assert.Contains("null_property, null_default) WHERE NOT COALESCE(", state, StringComparison.Ordinal);
    }

    /// <summary>The zero-based catalog vectors keep their offset from the one-based positions.</summary>
    /// <remarks>
    /// WHY: indkey and indclass are zero-based while pg_index_column_has_property and
    /// pg_get_indexdef take one-based positions. Carrying one position per row means exactly one
    /// of the two uses must subtract, and dropping that offset would compare the wrong key.
    /// </remarks>
    [Fact]
    public void PlainIndexKeys_OffsetTheZeroBasedCatalogVectors()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, PlainKeys(2));

        // Assert
        Assert.Contains("i.indkey[key.pos - 1]", state, StringComparison.Ordinal);
        Assert.Contains("i.indclass[key.pos - 1]", state, StringComparison.Ordinal);
        Assert.Contains("i.indcollation[key.pos - 1] = key_attribute.attcollation", state, StringComparison.Ordinal);
    }

    /// <summary>A key carrying an operator class keeps its own per-key catalog lookup.</summary>
    [Fact]
    public void IndexKeyWithOperatorClass_StaysOutOfTheCorrelatedSet()
    {
        // Arrange
        using var context = CreateContext();
        ExpectedIndexKeyDefinition[] keys =
        [
            new("column_0"),
            new("column_1", operatorClass: "text_pattern_ops"),
        ];

        // Act
        var state = BuildStateExpression(context, keys);

        // Assert
        Assert.Contains("i.indclass[1]", state, StringComparison.Ordinal);
        Assert.Contains("i.indclass[key.pos - 1]", state, StringComparison.Ordinal);
        Assert.Contains("i.indcollation[1] = key_attribute.attcollation", state, StringComparison.Ordinal);
    }

    /// <summary>A namespaced explicit collation keeps its physical lookup between collapsed key positions.</summary>
    [Fact]
    public void IndexKeyWithExplicitCollation_StaysOutOfTheCorrelatedSet()
    {
        // Arrange
        using var context = CreateContext();
        ExpectedIndexKeyDefinition[] keys =
        [
            new("column_0"),
            new("column_1", collation: new SafeMigrationCollationIdentifier("C", "pg_catalog")),
            new("column_2"),
        ];

        // Act
        var state = BuildStateExpression(context, keys);

        // Assert
        Assert.Contains("coll.oid = i.indcollation[1]", state, StringComparison.Ordinal);
        Assert.Contains("coll.collname = 'C'", state, StringComparison.Ordinal);
        Assert.Contains("'pg_catalog'", state, StringComparison.Ordinal);
        Assert.DoesNotContain("i.indcollation[1] = key_attribute.attcollation", state, StringComparison.Ordinal);
        Assert.Contains("i.indcollation[key.pos - 1] = key_attribute.attcollation", state, StringComparison.Ordinal);
    }

    /// <summary>Expression-default collation uses an empty typed subquery rather than a data scan.</summary>
    [Fact]
    public void ExpressionKeyDefaultCollation_UsesMetadataOnlyScalarSubquery()
    {
        // Arrange
        using var context = CreateContext();
        ExpectedIndexKeyDefinition[] keys =
        [new(structuredExpression: SafeMigrationSql.Function("lower", SafeMigrationSql.Identifier("column_0")))];

        // Act
        var state = BuildStateExpression(context, keys);

        // Assert
        Assert.Contains("CASE WHEN i.indcollation[0] = 0 THEN TRUE ELSE", state, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.pg_collation_for((SELECT lower(column_0)", state, StringComparison.Ordinal);
        Assert.Contains("FROM shape_table LIMIT 0)))::oid", state, StringComparison.Ordinal);
    }

    private static SafeMigrationDbContext CreateContext() =>
        new(OfflineConnectionString, registerSafeMigrations: false);

    private static ExpectedIndexKeyDefinition[] PlainKeys(
        int count
    ) => Enumerable.Range(0, count)
        .Select(static index => new ExpectedIndexKeyDefinition(
            "column_" + index.ToString(CultureInfo.InvariantCulture)))
        .ToArray();

    private static string BuildStateExpression(
        SafeMigrationDbContext context,
        ExpectedIndexKeyDefinition[] keys
    )
    {
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var migration = new MigrationBuilder(context.Database.ProviderName!);
        migration.EnsureIndex(
            new ExpectedIndexDefinition("ix_shape", "shape_table", keys),
            SafeMigrationPolicy.ThrowIfDifferent);

        return builder.Build((SafeMigrationOperation)migration.Operations[0]).StateExpression;
    }

    private static int Occurrences(
        string text,
        string value
    )
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
