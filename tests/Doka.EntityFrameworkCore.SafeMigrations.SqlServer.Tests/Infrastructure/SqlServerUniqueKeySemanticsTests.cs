namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies that default unique-key definitions retain duplicate-write failure semantics.
/// </summary>
public sealed class SqlServerUniqueKeySemanticsTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=unique_key_semantics;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Requires duplicate-key rejection in both classification and postcondition predicates.
    /// </summary>
    [Theory]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("index")]
    public void UniqueKey_DefaultSemanticsRequireIgnoreDuplicateKeyOff(
        string family
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        SafeMigrationIntent intent = family switch
        {
            "primary" => new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("PK_unique_key_semantics", "unique_key_semantics", ["Id"])),
            "unique" => new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("UQ_unique_key_semantics", "unique_key_semantics", ["Id"])),
            "index" => new EnsureIndexIntent(new ExpectedIndexDefinition(
                "IX_unique_key_semantics", "unique_key_semantics",
                [new ExpectedIndexKeyDefinition("Id")], unique: true)),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains("i.ignore_dup_key = 0", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("i.ignore_dup_key = 0", plan.Postcondition, StringComparison.Ordinal);
    }
}
