namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationStandardOperationFactoryTests
{
    /// <summary>
    /// Rejects a functional index when the generic factory has no certified provider-owned baseline renderer.
    /// </summary>
    [Fact]
    public void FunctionalIndexRequiresProviderOwnedBaselineRendering()
    {
        // Arrange
        var definition = new ExpectedIndexDefinition(
            "ix_items_lower_name",
            "items",
            [new ExpectedIndexKeyDefinition(expression: "lower(name)")]);

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationStandardOperationFactory.Create(new EnsureIndexIntent(definition)));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
    }
}
