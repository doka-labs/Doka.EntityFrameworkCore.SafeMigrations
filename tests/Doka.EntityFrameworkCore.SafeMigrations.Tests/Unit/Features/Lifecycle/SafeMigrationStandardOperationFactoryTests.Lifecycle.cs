namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationStandardOperationFactoryTests
{
    /// <summary>
    /// Reconstructs each factory-supported structural intent as an ordinary EF operation, never another safe
    /// envelope.
    /// </summary>
    /// <param name="ordinal">The independently reconstructed structural intent's fixture ordinal.</param>
    [Theory]
    [MemberData(nameof(StandardIntentOrdinals))]
    public void FactoryCoversEveryClosedIntentWithAStandardEfOperation(
        int ordinal
    )
    {
        // Arrange
        var intent = CreateIntents()[ordinal];

        // Act
        var operation = SafeMigrationStandardOperationFactory.Create(intent);

        // Assert
        Assert.NotNull(operation);
        Assert.NotEqual(typeof(SafeMigrationOperation), operation.GetType());
    }

    /// <summary>Enumerates serializable independent case identities without sharing mutable intent instances.</summary>
    /// <returns>Every structural-intent fixture ordinal.</returns>
    public static IEnumerable<object[]> StandardIntentOrdinals()
    {
        var intents = CreateIntents();

        // WHY: Discovery receives serializable ordinals; each test constructs its own immutable intent fixture.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < intents.Count; ordinal++)
        {
            yield return [ordinal];
        }
    }

    /// <summary>
    /// Prevents missing or duplicated structural factory fixtures from making the case matrix incomplete.
    /// </summary>
    [Fact]
    public void FactoryFixturesCoverEveryStructuralOperationKind()
    {
        // Arrange
        var expectedKinds = Enum
            .GetValues<SafeMigrationOperationKind>()
            .Where(static kind => kind is not (SafeMigrationOperationKind.EnsureModelManagedData
                or SafeMigrationOperationKind.UpdateModelManagedData
                or SafeMigrationOperationKind.DeleteModelManagedData))
            .Order();

        // Act
        var kinds = CreateIntents()
            .Select(static intent => intent.Kind)
            .Order();

        // Assert
        Assert.Equal(expectedKinds, kinds);
    }
}
