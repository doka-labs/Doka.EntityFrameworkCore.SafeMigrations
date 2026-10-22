namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Guards declared expression-source traversal without a native catalog or connection.</summary>
public sealed class SqliteDeclaredExpressionSourceTraversalTests
{
    /// <summary>Both expression roles retain missing-source rejection and exact immutable snapshot membership.</summary>
    /// <param name="width">The number of ordinary columns before the generated member.</param>
    /// <param name="checkRole">Whether the structured reference belongs to a CHECK rather than a column.</param>
    /// <param name="missing">Whether the referenced physical source is absent.</param>
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(128, false, false)]
    [InlineData(1, false, true)]
    [InlineData(128, false, true)]
    [InlineData(1, true, false)]
    [InlineData(128, true, false)]
    [InlineData(1, true, true)]
    [InlineData(128, true, true)]
    public void BothSourceRolesUseTheCompleteOwnedSnapshot(int width, bool checkRole, bool missing)
    {
        // Arrange
        var sourceName = "c" + (width - 1).ToString(CultureInfo.InvariantCulture);
        var expression = SafeMigrationSql.Identifier(missing ? "absent" : sourceName);
        var columns = Enumerable.Range(0, width)
            .Select(index => new ExpectedColumnDefinition("c" + index.ToString(CultureInfo.InvariantCulture),
                typeof(int), false, "INTEGER"))
            .Append(new ExpectedColumnDefinition("derived", typeof(int), true, "INTEGER",
                computedExpression: checkRole ? null : expression))
            .ToArray();

        var checks = checkRole
            ? new[] { ExpectedCheckConstraintDefinition.FromExpression("ck_items", "items",
                SafeMigrationSql.IsNotNull(expression)), }
            : Array.Empty<ExpectedCheckConstraintDefinition>();

        var table = new ExpectedTableDefinition("items", columns, checkConstraints: checks);
        var lookup = typeof(SqliteSafeMigrationProviderAnalyzer)
            .GetMethod("HasMissingDeclaredExpressionColumns", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<ExpectedTableDefinition, bool>>();

        columns[width - 1] = new ExpectedColumnDefinition("caller_mutation", typeof(int), true, "INTEGER");

        // Act
        var hasMissingSource = lookup(table);

        // Assert
        Assert.Equal(missing, hasMissingSource);
        Assert.Equal(width + 1, table.Columns.Count);
        Assert.Equal(sourceName, table.Columns[width - 1].Name);
    }
}
