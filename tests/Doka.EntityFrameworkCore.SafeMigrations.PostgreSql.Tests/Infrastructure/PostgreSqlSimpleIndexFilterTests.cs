namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies bounded recognition of EF's PostgreSQL null-test index predicates.</summary>
public sealed class PostgreSqlSimpleIndexFilterTests
{
    /// <summary>Canonical null tests preserve the immutable contract and exact physical prerequisites.</summary>
    /// <param name="filter">The canonical provider predicate.</param>
    /// <param name="column">The exact physical predicate column.</param>
    [Theory]
    [InlineData("parent IS NULL", "parent")]
    [InlineData("parent IS NOT NULL", "parent")]
    [InlineData("\"Parent\" IS NULL", "Parent")]
    [InlineData("\"Parent\" IS NOT NULL", "Parent")]
    [InlineData("\"parent\"\"entry\" IS NULL", "parent\"entry")]
    [InlineData("\"parent entry\" IS NOT NULL", "parent entry")]
    [InlineData("  parent IS NULL  ", "parent")]
    [InlineData("\t\nparent IS NOT NULL\r\f\v", "parent")]
    public void CanonicalNullTestIsSupportedWithoutChangingContract(
        string filter,
        string column
    )
    {
        // Arrange
        using var context = Context();
        var definition = new ExpectedIndexDefinition(
            "lookup",
            "nodes",
            [new ExpectedIndexKeyDefinition("key", sortOrder: SafeMigrationIndexSortOrder.Descending)],
            schema: "hierarchy",
            filter: filter,
            method: "btree",
            includedColumns: ["payload"]);

        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var fingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var catalog = Catalog(context);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Null(plan.UnsupportedCode);
        Assert.Contains(
            "a.attname IN ('key', 'payload', '" + column.Replace("'", "''", StringComparison.Ordinal) + "')",
            plan.PrerequisiteExpression,
            StringComparison.Ordinal);
        Assert.Same(definition, ((EnsureIndexIntent)operation.Intent).Definition);
        Assert.Equal(filter, definition.Filter);
        Assert.Null(definition.StructuredFilter);
        Assert.Equal("hierarchy", definition.Schema);
        Assert.Equal("btree", definition.Method);
        Assert.Equal(["payload"], definition.IncludedColumns);
        Assert.Equal(SafeMigrationIndexSortOrder.Descending, definition.Keys[0].SortOrder);
        Assert.Equal(fingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>Expressions outside the exact single-column null-test contract remain opaque.</summary>
    /// <param name="filter">The predicate outside the bounded recognition contract.</param>
    [Theory]
    [InlineData("parent IS NULL OR true")]
    [InlineData("parent IS NULL; SELECT 1")]
    [InlineData("parent IS NULL -- comment")]
    [InlineData("parent IS NULL /* comment */")]
    [InlineData("lower(parent) IS NULL")]
    [InlineData("parent + 1 IS NULL")]
    [InlineData("nodes.parent IS NULL")]
    [InlineData("[parent] IS NULL")]
    [InlineData("`parent` IS NULL")]
    [InlineData("Parent IS NULL")]
    [InlineData("\"parent\" IS NULL")]
    [InlineData("(parent IS NULL)")]
    [InlineData("parent is null")]
    [InlineData("parent\nIS NULL")]
    [InlineData("\u00A0parent IS NULL")]
    [InlineData("parent IS NULL\u00A0")]
    [InlineData("\u0085parent IS NULL")]
    public void NonCanonicalOrComplexFilterRemainsRejected(
        string filter
    )
    {
        // Arrange
        using var context = Context();
        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(new ExpectedIndexDefinition(
                "lookup",
                "nodes",
                [new ExpectedIndexKeyDefinition("key")],
                filter: filter)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var catalog = Catalog(context);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("opaque_sql_expression", plan.UnsupportedCode);
    }

    /// <summary>Structured filters retain their existing dependency extraction without duplicate columns.</summary>
    [Fact]
    public void StructuredNullTestRetainsExistingPrerequisites()
    {
        // Arrange
        using var context = Context();
        var operation = new SafeMigrationOperation(
            new EnsureIndexIntent(new ExpectedIndexDefinition(
                "lookup",
                "nodes",
                [new ExpectedIndexKeyDefinition("parent")],
                structuredFilter: SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("parent")))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var catalog = Catalog(context);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("a.attname IN ('parent')", plan.PrerequisiteExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("'parent', 'parent'", plan.PrerequisiteExpression, StringComparison.Ordinal);
    }

    /// <summary>Provider-specific dependencies use exactly the same recognition as runtime catalog SQL.</summary>
    /// <param name="filter">The canonical filter predicate.</param>
    /// <param name="column">The exact physical predicate column.</param>
    [Theory]
    [InlineData("parent IS NULL", "parent")]
    [InlineData("\"Parent\" IS NOT NULL", "Parent")]
    public void ProviderProjectionUsesCatalogPrerequisites(
        string filter,
        string column
    )
    {
        // Arrange
        using var context = Context();
        var intent = new EnsureIndexIntent(new ExpectedIndexDefinition(
            "lookup",
            "nodes",
            [new ExpectedIndexKeyDefinition("key")],
            filter: filter));

        var analyzer = new PostgreSqlSafeMigrationProviderAnalyzer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var columns = ((ISafeMigrationIndexPrerequisiteSource)analyzer).GetIndexPrerequisiteColumns(intent);

        // Assert
        Assert.Equal(["key", column], columns);
    }

    /// <summary>Null-test predicates already present among key columns do not create duplicate prerequisites.</summary>
    [Fact]
    public void CanonicalFilterKeyColumnIsNotDuplicated()
    {
        // Arrange
        using var context = Context();
        var intent = new EnsureIndexIntent(new ExpectedIndexDefinition(
            "lookup",
            "nodes",
            [new ExpectedIndexKeyDefinition("parent")],
            filter: "parent IS NOT NULL"));

        var catalog = Catalog(context);

        // Act
        var columns = catalog.IndexPrerequisiteColumns(intent);

        // Assert
        Assert.Equal(["parent"], columns);
    }

    /// <summary>Model-captured raw filters keep their authored bytes and contract fingerprint.</summary>
    [Fact]
    public void ModelCapturedCanonicalFilterRetainsAuthoredDefinition()
    {
        // Arrange
        using var context = Context();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateCompositeIndexIfNotExistsFromModel(
            "lookup",
            "nodes",
            ["key", "parent"],
            schema: "hierarchy",
            descending: [false, true],
            filter: "parent IS NOT NULL");

        var operation = (SafeMigrationOperation)builder.Operations[0];
        var fingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var catalog = Catalog(context);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        var definition = Assert.IsType<EnsureIndexIntent>(operation.Intent).Definition;

        Assert.Equal("parent IS NOT NULL", definition.Filter);
        Assert.Null(definition.StructuredFilter);
        Assert.Equal(SafeMigrationIndexSortOrder.Descending, definition.Keys[1].SortOrder);
        Assert.Equal(fingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>Creates the catalog builder from the configured provider's exact rendering services.</summary>
    private static PostgreSqlSafeMigrationCatalogSqlBuilder Catalog(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Creates provider services without opening a connection or changing database state.</summary>
    private static DbContext Context()
    {
        var options = new DbContextOptionsBuilder()
            .UseNpgsql("Host=127.0.0.1;Port=1;Username=test;Database=test")
            .UsePostgreSqlSafeMigrations();

        return new DbContext(options.Options);
    }
}
