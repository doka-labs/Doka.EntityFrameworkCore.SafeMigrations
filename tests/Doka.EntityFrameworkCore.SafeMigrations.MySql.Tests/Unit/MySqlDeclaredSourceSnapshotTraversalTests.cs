namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Guards complete immutable source scans independently from native database execution.</summary>
public sealed class MySqlDeclaredSourceSnapshotTraversalTests
{
    /// <summary>Last, missing, ambiguous and nullable sources retain distinct binding and nullability verdicts.</summary>
    /// <param name="width">The number of independently declared ordinary columns.</param>
    /// <param name="missing">Whether the expression references an undeclared source.</param>
    /// <param name="ambiguous">Whether a second case-insensitive source alias exists.</param>
    /// <param name="nullable">Whether the declared last source permits null values.</param>
    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(16, false, false, false)]
    [InlineData(128, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(128, true, false, false)]
    [InlineData(1, false, true, false)]
    [InlineData(128, false, true, false)]
    [InlineData(1, false, false, true)]
    [InlineData(128, false, false, true)]
    public void CompleteSourceScanRetainsIndependentProofs(int width, bool missing, bool ambiguous, bool nullable)
    {
        // Arrange
        var sourceName = "c" + (width - 1).ToString(CultureInfo.InvariantCulture);
        var columns = Enumerable.Range(0, width)
            .Select(index => new ExpectedColumnDefinition("c" + index.ToString(CultureInfo.InvariantCulture),
                typeof(int), index == width - 1 && nullable, "int"))
            .ToArray();

        var declared = ambiguous
            ? columns.Append(new ExpectedColumnDefinition(sourceName.ToUpperInvariant(), typeof(int), false, "int"))
                .ToArray()
            : columns;

        var table = new ExpectedTableDefinition("items", declared);
        var generated = new ExpectedColumnDefinition("derived", typeof(int), false, "int",
            computedExpression: SafeMigrationSql.Identifier(missing ? "absent" : sourceName));

        // WHY: A cached scan length is valid only for the owned snapshot, never the caller's mutable array.
        declared[^1] = new ExpectedColumnDefinition("caller_mutation", typeof(int), true, "int");

        // Act
        var sourcesExist = MySqlGeneratedColumnDependencies.DeclaredSourcesExist(generated, table);
        var requiredResult = MariaDbGeneratedColumnNullability.CanProveForTable(generated, table);

        // Assert
        Assert.Equal(!missing && !ambiguous, sourcesExist);
        Assert.Equal(!missing && !ambiguous && !nullable, requiredResult);
        Assert.Equal(width + (ambiguous ? 1 : 0), table.Columns.Count);
        Assert.DoesNotContain(table.Columns, static column => column.Name == "caller_mutation");
    }
}
