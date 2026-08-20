namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationStandardOperationFactoryTests
{
    [Fact]
    public void FactoryCoversEveryClosedIntentWithAStandardEfOperation()
    {
        foreach (var intent in CreateIntents())
        {
            var operation = SafeMigrationStandardOperationFactory.Create(intent);

            Assert.NotNull(operation);
            Assert.NotEqual(typeof(SafeMigrationOperation), operation.GetType());
        }
    }
}
