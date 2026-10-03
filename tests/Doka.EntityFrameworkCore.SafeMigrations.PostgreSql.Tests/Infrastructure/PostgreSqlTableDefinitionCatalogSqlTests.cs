namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>
/// Pins the set-oriented table-definition classification without requiring a live engine.
/// </summary>
/// <remarks>
/// These tests exist because the per-column and per-constraint classifiers were replaced by
/// set-oriented forms while the live suite stayed green. The replacement can silently weaken two
/// guarantees that no behavioural test exercised: an unknown facet verdict must stay a mismatch,
/// and the expected name must resolve as a catalog name. Both are asserted here directly.
/// </remarks>
public sealed class PostgreSqlTableDefinitionCatalogSqlTests
{
    private const string OfflineConnectionString =
        "Host=127.0.0.1;Port=1;Database=table_definition_shape;Username=test;Password=test";

    /// <summary>Column verification keeps its catalog joins constant as the column count grows.</summary>
    [Fact]
    public void ColumnVerification_DoesNotGrowCatalogJoinsWithColumnCount()
    {
        // Arrange
        using var context = CreateContext();
        var narrow = BuildStateExpression(context, ColumnCount(2), uniqueConstraints: []);
        var wide = BuildStateExpression(context, ColumnCount(6), uniqueConstraints: []);

        // Act
        var narrowJoins = Occurrences(narrow, "pg_catalog.pg_class");
        var wideJoins = Occurrences(wide, "pg_catalog.pg_class");

        // Assert
        Assert.Equal(narrowJoins, wideJoins);
    }

    /// <summary>An unknown column facet verdict stays a mismatch instead of being accepted.</summary>
    /// <remarks>
    /// WHY: The per-column form returned no row when a facet predicate evaluated to NULL and
    /// therefore reported a mismatch. The set-oriented form inverts that unless the absent test
    /// coalesces to FALSE, so the guard is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void ColumnVerification_TreatsAnUnknownFacetVerdictAsMismatch()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, ColumnCount(3), uniqueConstraints: []);

        // Assert
        Assert.Contains("NOT COALESCE(CASE expected.attnum", state, StringComparison.Ordinal);
    }

    /// <summary>The expected column name is compared as the catalog's own name type.</summary>
    /// <remarks>
    /// WHY: The per-column form compared against an unknown-typed literal, which resolves to the
    /// catalog name type. A VALUES column is text, so without the cast the comparison would use a
    /// different operator than the form it replaces.
    /// </remarks>
    [Fact]
    public void ColumnVerification_ComparesTheExpectedNameAsACatalogName()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, ColumnCount(2), uniqueConstraints: []);

        // Assert
        Assert.Contains("AS pg_catalog.name", state, StringComparison.Ordinal);
    }

    /// <summary>Column verification still requires each column at its expected position.</summary>
    [Fact]
    public void ColumnVerification_RequiresTheExpectedColumnPosition()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, ColumnCount(4), uniqueConstraints: []);

        // Assert
        Assert.Contains("a.attnum <> expected.attnum", state, StringComparison.Ordinal);
    }

    /// <summary>Required unique constraints keep their catalog scans constant as the count grows.</summary>
    [Fact]
    public void RequiredUniqueConstraints_DoNotGrowCatalogScansWithConstraintCount()
    {
        // Arrange
        using var context = CreateContext();
        var single = BuildStateExpression(context, ColumnCount(3), UniqueConstraints(1));
        var several = BuildStateExpression(context, ColumnCount(3), UniqueConstraints(3));

        // Act
        var singleScans = Occurrences(single, "pg_catalog.pg_constraint");
        var severalScans = Occurrences(several, "pg_catalog.pg_constraint");

        // Assert
        Assert.Equal(singleScans, severalScans);
    }

    /// <summary>An unknown unique-constraint verdict stays a mismatch instead of being accepted.</summary>
    [Fact]
    public void RequiredUniqueConstraints_TreatAnUnknownVerdictAsMismatch()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, ColumnCount(3), UniqueConstraints(2));

        // Assert
        Assert.Contains("AS required(conname, columns) WHERE NOT COALESCE(", state, StringComparison.Ordinal);
    }

    /// <summary>A required unique constraint is still accepted under a differing catalog name.</summary>
    /// <remarks>
    /// WHY: The per-constraint form accepted a semantic alias when no constraint carried the
    /// expected name. Collapsing the three scans must keep that third branch.
    /// </remarks>
    [Fact]
    public void RequiredUniqueConstraints_RetainTheSemanticAliasBranch()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, ColumnCount(3), UniqueConstraints(1));

        // Assert
        Assert.Contains("co.conname <> required.conname", state, StringComparison.Ordinal);
    }

    /// <summary>Required check constraints keep their catalog scans constant as the count grows.</summary>
    [Fact]
    public void RequiredCheckConstraints_DoNotGrowCatalogScansWithConstraintCount()
    {
        // Arrange
        using var context = CreateContext();
        var single = BuildStateExpression(context, ColumnCount(3), [], CheckConstraints(1));
        var several = BuildStateExpression(context, ColumnCount(3), [], CheckConstraints(3));

        // Act
        var singleScans = Occurrences(single, "pg_catalog.pg_constraint");
        var severalScans = Occurrences(several, "pg_catalog.pg_constraint");

        // Assert
        Assert.Equal(singleScans, severalScans);
    }

    /// <summary>An unknown check-constraint verdict stays a mismatch instead of being accepted.</summary>
    [Fact]
    public void RequiredCheckConstraints_TreatAnUnknownVerdictAsMismatch()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, ColumnCount(3), [], CheckConstraints(2));

        // Assert
        Assert.Contains("AS required(ordinal, conname) WHERE NOT COALESCE(", state, StringComparison.Ordinal);
    }

    /// <summary>A required check constraint is still accepted under a differing catalog name.</summary>
    [Fact]
    public void RequiredCheckConstraints_RetainTheSemanticAliasBranch()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, ColumnCount(3), [], CheckConstraints(1));

        // Assert
        Assert.Contains("co.conname <> required.conname", state, StringComparison.Ordinal);
    }

    /// <summary>Required foreign keys keep their catalog scans constant as the count grows.</summary>
    [Fact]
    public void RequiredForeignKeys_DoNotGrowCatalogScansWithKeyCount()
    {
        // Arrange
        using var context = CreateContext();
        var single = BuildStateExpression(context, ColumnCount(3), [], null, ForeignKeys(1));
        var several = BuildStateExpression(context, ColumnCount(3), [], null, ForeignKeys(3));

        // Act
        var singleScans = Occurrences(single, "pg_catalog.pg_constraint");
        var severalScans = Occurrences(several, "pg_catalog.pg_constraint");

        // Assert
        Assert.Equal(singleScans, severalScans);
    }

    /// <summary>An unknown foreign-key verdict stays a mismatch instead of being accepted.</summary>
    [Fact]
    public void RequiredForeignKeys_TreatAnUnknownVerdictAsMismatch()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, ColumnCount(3), [], null, ForeignKeys(2));

        // Assert
        Assert.Contains(
            "confupdtype, confdeltype) WHERE NOT COALESCE(", state, StringComparison.Ordinal);
    }

    /// <summary>The principal relation is still resolved through the catalog resolver.</summary>
    /// <remarks>
    /// WHY: The per-key form compared the referenced relation through to_regclass on a literal
    /// name. Carrying the name in a VALUES row must keep that resolution rather than degrade into
    /// a text comparison, which would not follow search_path or quoting the same way.
    /// </remarks>
    [Fact]
    public void RequiredForeignKeys_ResolveThePrincipalRelationThroughTheCatalogResolver()
    {
        // Arrange
        using var context = CreateContext();

        // Act
        var state = BuildStateExpression(context, ColumnCount(3), [], null, ForeignKeys(1));

        // Assert
        Assert.Contains("pg_catalog.to_regclass(required.principal)", state, StringComparison.Ordinal);
    }

    private static SafeMigrationDbContext CreateContext() =>
        new(OfflineConnectionString, registerSafeMigrations: false);

    private static ExpectedColumnDefinition[] ColumnCount(
        int count
    ) => Enumerable.Range(0, count)
        .Select(static index => new ExpectedColumnDefinition(
            "column_" + index.ToString(CultureInfo.InvariantCulture), typeof(int), false, "integer"))
        .ToArray();

    private static ExpectedUniqueConstraintDefinition[] UniqueConstraints(
        int count
    ) => Enumerable.Range(0, count)
        .Select(static index => new ExpectedUniqueConstraintDefinition(
            "uq_shape_" + index.ToString(CultureInfo.InvariantCulture),
            "shape_table",
            ["column_" + index.ToString(CultureInfo.InvariantCulture)]))
        .ToArray();

    private static string BuildStateExpression(
        SafeMigrationDbContext context,
        ExpectedColumnDefinition[] columns,
        ExpectedUniqueConstraintDefinition[] uniqueConstraints,
        ExpectedCheckConstraintDefinition[]? checkConstraints = null,
        ExpectedForeignKeyDefinition[]? foreignKeys = null
    )
    {
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var migration = new MigrationBuilder(context.Database.ProviderName!);
        migration.EnsureTable(
            new ExpectedTableDefinition(
                "shape_table",
                columns,
                uniqueConstraints: uniqueConstraints,
                checkConstraints: checkConstraints ?? [],
                foreignKeys: foreignKeys ?? []),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        return builder.Build((SafeMigrationOperation)migration.Operations[0]).StateExpression;
    }

    // WHY: A raw-SQL check constraint makes the whole table operation statically unsupported, so
    // the state expression collapses to 'unsupported' and a shape assertion would pass vacuously.
    // The structured expression is the form the classifier actually renders.
    private static ExpectedCheckConstraintDefinition[] CheckConstraints(
        int count
    ) => Enumerable.Range(0, count)
        .Select(static index => ExpectedCheckConstraintDefinition.FromExpression(
            "ck_shape_" + index.ToString(CultureInfo.InvariantCulture),
            "shape_table",
            SafeMigrationSql.Binary(
                SafeMigrationSql.Identifier("column_" + index.ToString(CultureInfo.InvariantCulture)),
                SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                SafeMigrationSql.Literal(0))))
        .ToArray();

    private static ExpectedForeignKeyDefinition[] ForeignKeys(
        int count
    ) => Enumerable.Range(0, count)
        .Select(static index => new ExpectedForeignKeyDefinition(
            "fk_shape_" + index.ToString(CultureInfo.InvariantCulture),
            "shape_table",
            ["column_" + index.ToString(CultureInfo.InvariantCulture)],
            "shape_parent",
            ["id"],
            onUpdate: ReferentialAction.NoAction,
            onDelete: ReferentialAction.NoAction))
        .ToArray();

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
