namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationStandardOperationFactoryTests
{
    [Fact]
    public void FunctionalIndexRequiresProviderOwnedBaselineRendering()
    {
        var definition = new ExpectedIndexDefinition(
            "ix_items_lower_name",
            "items",
            [new ExpectedIndexKeyDefinition(expression: "lower(name)")]);

        Assert.Throws<NotSupportedException>(() =>
            SafeMigrationStandardOperationFactory.Create(new EnsureIndexIntent(definition)));
    }
}
