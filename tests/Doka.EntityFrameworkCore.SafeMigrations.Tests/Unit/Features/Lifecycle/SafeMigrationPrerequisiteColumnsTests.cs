namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Preserves complete prerequisite roles while avoiding storage for proven-empty column definitions.</summary>
public sealed class SafeMigrationPrerequisiteColumnsTests
{
    /// <summary>Returns no dependencies without allocating a set when every structured column role is absent.</summary>
    /// <param name="scenario">The independently selectable empty source/target shape.</param>
    [Theory]
    [InlineData("ensure_plain")]
    [InlineData("ensure_literal")]
    [InlineData("ensure_opaque_default")]
    [InlineData("ensure_opaque_computed")]
    [InlineData("alter_plain")]
    [InlineData("alter_missing_old")]
    [InlineData("alter_literal")]
    public void Local_ProvenEmptyColumnRolesDoNotAllocate(
        string scenario
    )
    {
        // Arrange
        var intent = EmptyIntent(scenario);
        for (var warmup = 0; warmup < 128; warmup++)
        {
            _ = SafeMigrationPrerequisiteColumns.Local(intent);
        }

        const int count = 4096;
        var checksum = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        for (var iteration = 0; iteration < count; iteration++)
        {
            checksum += SafeMigrationPrerequisiteColumns.Local(intent).Count;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0, checksum);
        Assert.Equal(0, allocated);
    }

    /// <summary>Reads old and target expression roles instead of treating an empty opposite side as proof.</summary>
    /// <param name="scenario">The independently selectable expression role.</param>
    [Theory]
    [InlineData("ensure_computed")]
    [InlineData("ensure_default")]
    [InlineData("alter_old_computed")]
    [InlineData("alter_old_default")]
    [InlineData("alter_target_computed")]
    [InlineData("alter_target_default")]
    public void Local_NonemptyColumnRoleCannotUseEmptyFastPath(
        string scenario
    )
    {
        // Arrange
        var intent = ReferencingIntent(scenario);

        // Act
        var columns = SafeMigrationPrerequisiteColumns.Local(intent);

        // Assert
        Assert.Equal(["source"], columns);
    }

    /// <summary>Retains ordinal sorting, case identity and deduplication across old/new expression roles.</summary>
    [Fact]
    public void Local_CombinesOldAndTargetRolesWithoutChangingIdentity()
    {
        // Arrange
        var oldExpression = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("a"),
            SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Identifier("A"));

        var targetExpression = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("a"),
            SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Identifier("z"));

        var intent = new AlterColumnIntent("rows",
            new ExpectedColumnDefinition("value", typeof(int), true,
                defaultValue: SafeMigrationDefaultValue.Sql(targetExpression)),
            new ExpectedColumnDefinition("value", typeof(int), true, computedExpression: oldExpression));

        // Act
        var columns = SafeMigrationPrerequisiteColumns.Local(intent);

        // Assert
        Assert.Equal(["A", "a", "z"], columns);
    }

    /// <summary>Retains expression, key, include and filter columns across non-column operation consumers.</summary>
    [Fact]
    public void Local_IndexRetainsAllDependencyRoles()
    {
        // Arrange
        var intent = new EnsureIndexIntent(new ExpectedIndexDefinition("ix", "rows",
            [new ExpectedIndexKeyDefinition("z"), new ExpectedIndexKeyDefinition(
                structuredExpression: SafeMigrationSql.Identifier("a"))],
            includedColumns: ["A"], structuredFilter: SafeMigrationSql.Identifier("z")));

        // Act
        var columns = SafeMigrationPrerequisiteColumns.Local(intent);

        // Assert
        Assert.Equal(["A", "a", "z"], columns);
    }

    /// <summary>The empty fast path must not turn a null intent into a successful empty result.</summary>
    [Fact]
    public void Local_RejectsNullBeforeConsideringEmptyRoles()
    {
        // Arrange
        SafeMigrationIntent intent = null!;

        // Act
        var exception = Assert.Throws<ArgumentNullException>(() => SafeMigrationPrerequisiteColumns.Local(intent));

        // Assert
        Assert.Equal("intent", exception.ParamName);
    }

    /// <summary>Builds each proven-empty role without conflating opaque SQL with expression support.</summary>
    /// <param name="scenario">The stable discovery identifier.</param>
    /// <returns>The prepared immutable operation intent.</returns>
    private static SafeMigrationIntent EmptyIntent(
        string scenario
    )
    {
        var plain = new ExpectedColumnDefinition("value", typeof(int), true);
        var literal = new ExpectedColumnDefinition("value", typeof(int), true,
            defaultValue: SafeMigrationDefaultValue.Literal(7));

        return scenario switch
        {
            "ensure_plain" => new EnsureColumnIntent("rows", plain),
            "ensure_literal" => new EnsureColumnIntent("rows", literal),
            "ensure_opaque_default" => new EnsureColumnIntent("rows", new ExpectedColumnDefinition(
                "value", typeof(int), true, defaultValue: SafeMigrationDefaultValue.Sql("legacy_function()"))),
            "ensure_opaque_computed" => new EnsureColumnIntent("rows", new ExpectedColumnDefinition(
                "value", typeof(int), true, computedColumnSql: "legacy_source + 1")),
            "alter_plain" => new AlterColumnIntent("rows", plain, plain),
            "alter_missing_old" => new AlterColumnIntent("rows", plain),
            "alter_literal" => new AlterColumnIntent("rows", literal, literal),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    /// <summary>Exercises each of the four column expression slots independently.</summary>
    /// <param name="scenario">The stable discovery identifier.</param>
    /// <returns>The prepared source/target intent with one nonempty dependency role.</returns>
    private static SafeMigrationIntent ReferencingIntent(
        string scenario
    )
    {
        var plain = new ExpectedColumnDefinition("value", typeof(int), true);
        var computed = new ExpectedColumnDefinition("value", typeof(int), true,
            computedExpression: SafeMigrationSql.Identifier("source"));

        var defaulted = new ExpectedColumnDefinition("value", typeof(int), true,
            defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Identifier("source")));

        return scenario switch
        {
            "ensure_computed" => new EnsureColumnIntent("rows", computed),
            "ensure_default" => new EnsureColumnIntent("rows", defaulted),
            "alter_old_computed" => new AlterColumnIntent("rows", plain, computed),
            "alter_old_default" => new AlterColumnIntent("rows", plain, defaulted),
            "alter_target_computed" => new AlterColumnIntent("rows", computed, plain),
            "alter_target_default" => new AlterColumnIntent("rows", defaulted, plain),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }
}
