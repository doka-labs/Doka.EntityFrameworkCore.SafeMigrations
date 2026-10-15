namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Checks runnable selection activation and non-owning fixture injection.</summary>
public sealed class SharedContainerFixtureSelectionTests
{
    /// <summary>Only runnable concrete classes with the exact public fixture parameter activate resources.</summary>
    /// <param name="testClass">The selected class shape.</param>
    /// <param name="skipReason">The selected case's skip reason.</param>
    /// <param name="expected">Whether this selection needs the fixture.</param>
    [Theory]
    [InlineData(typeof(FixtureConsumer), null, true)]
    [InlineData(typeof(FixtureConsumer), "", true)]
    [InlineData(typeof(FixtureConsumer), "intentionally skipped", false)]
    [InlineData(typeof(FixtureConsumer), " ", false)]
    [InlineData(typeof(UnitOnlyConsumer), null, false)]
    [InlineData(typeof(PrivateConstructorConsumer), null, false)]
    [InlineData(typeof(AbstractConsumer), null, false)]
    [InlineData(typeof(DifferentFixtureConsumer), null, false)]
    public void RequiresFixture_UsesSelectedConcretePublicConstructor(
        Type testClass,
        string? skipReason,
        bool expected
    )
    {
        // Arrange
        var selectedClass = testClass;

        // Act
        var requiresFixture = SharedContainerFixtureSelection.RequiresFixture<SelectionFixture>(
            selectedClass, skipReason);

        // Assert
        Assert.Equal(expected, requiresFixture);
    }

    /// <summary>Class injection must not add the shared resource to collection teardown ownership.</summary>
    [Fact]
    public void Merge_AddsSharedInstanceWithoutMutatingCollectionOwnership()
    {
        // Arrange
        var fixture = new SelectionFixture();
        var unrelated = new object();
        var collectionOwned = new Dictionary<Type, object>
        {
            [typeof(object)] = unrelated,
        };

        // Act
        var firstClass = SharedContainerFixtureMappings.Merge(collectionOwned, fixture);
        var secondClass = SharedContainerFixtureMappings.Merge(collectionOwned, fixture);

        // Assert
        Assert.NotSame(collectionOwned, firstClass);
        Assert.NotSame(firstClass, secondClass);
        Assert.Same(fixture, firstClass[typeof(SelectionFixture)]);
        Assert.Same(fixture, secondClass[typeof(SelectionFixture)]);
        Assert.Same(unrelated, firstClass[typeof(object)]);
        Assert.Single(collectionOwned);
        Assert.False(collectionOwned.ContainsKey(typeof(SelectionFixture)));
    }

    /// <summary>A unit-only class gets ordinary collection mappings without a fabricated shared fixture.</summary>
    [Fact]
    public void Merge_WithoutSharedFixture_PreservesOnlyOrdinaryMappings()
    {
        // Arrange
        var unrelated = new object();
        var collectionOwned = new Dictionary<Type, object>
        {
            [typeof(object)] = unrelated,
        };

        // Act
        var classMappings = SharedContainerFixtureMappings.Merge<SelectionFixture>(collectionOwned, null);

        // Assert
        Assert.NotSame(collectionOwned, classMappings);
        Assert.Same(unrelated, Assert.Single(classMappings).Value);
        Assert.False(classMappings.ContainsKey(typeof(SelectionFixture)));
    }

    private sealed class SelectionFixture { }

    private sealed class FixtureConsumer
    {
        public FixtureConsumer(
            SelectionFixture fixture
        ) => ArgumentNullException.ThrowIfNull(fixture);
    }

    private sealed class UnitOnlyConsumer { }

    private sealed class PrivateConstructorConsumer
    {
        private PrivateConstructorConsumer(
            SelectionFixture fixture
        ) => ArgumentNullException.ThrowIfNull(fixture);
    }

    private abstract class AbstractConsumer
    {
        protected AbstractConsumer(
            SelectionFixture fixture
        ) => ArgumentNullException.ThrowIfNull(fixture);
    }

    private sealed class DifferentFixtureConsumer
    {
        public DifferentFixtureConsumer(
            object fixture
        ) => ArgumentNullException.ThrowIfNull(fixture);
    }
}
