namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies SQL Server fixture selection and scheduling without constructing a container.</summary>
public sealed class SqlServerParallelSchedulingTests
{
    /// <summary>Uses the common assembly lifecycle and bounded independent class collections.</summary>
    [Fact]
    public void Framework_UsesSharedAssemblyLifetimeAndFourIndependentCollections()
    {
        // Arrange
        var assembly = typeof(SqlServerTestFramework).Assembly;

        // Act
        var behavior = Assert.Single(assembly.GetCustomAttributes(typeof(CollectionBehaviorAttribute), false)
            .Cast<CollectionBehaviorAttribute>());
        var behaviorData = Assert.Single(assembly.GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionBehaviorAttribute));
        var framework = Assert.Single(assembly.GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(TestFrameworkAttribute));

        // Assert
        Assert.False(behavior.DisableTestParallelization);
        Assert.Equal(4, behavior.MaxParallelThreads);
        Assert.Equal((int)CollectionBehavior.CollectionPerClass, behaviorData.ConstructorArguments[0].Value);
        Assert.Equal(typeof(SqlServerTestFramework).FullName, framework.ConstructorArguments[0].Value);
        Assert.Equal(assembly.GetName().Name, framework.ConstructorArguments[1].Value);
        Assert.Equal(typeof(SharedContainerTestFramework<SqlServerContainerFixture>),
            typeof(SqlServerTestFramework).BaseType);
    }

    /// <summary>Restricts exclusivity to the process-total allocation case rather than all live tests.</summary>
    [Fact]
    public void LiveClasses_BorrowFixtureMetadataWithoutBlanketCollectionSerialization()
    {
        // Arrange
        var assemblyTypes = typeof(SqlServerTestFramework).Assembly.GetTypes();

        // Act
        var liveClasses = assemblyTypes.Where(type => !type.IsAbstract
            && typeof(SqlServerIntegrationTestBase).IsAssignableFrom(type)).ToArray();
        var exclusiveClasses = liveClasses.Where(type => type
            .GetCustomAttributes(typeof(CollectionAttribute), false).Length != 0).ToArray();
        var collectionOwners = assemblyTypes.Where(type =>
            typeof(ICollectionFixture<SqlServerContainerFixture>).IsAssignableFrom(type)).ToArray();
        var allocationCollection = Assert.Single(typeof(SqlServerSafeMigrationAllocationIntegrationTests)
            .GetCustomAttributesData(), attribute => attribute.AttributeType == typeof(CollectionAttribute));
        var exclusiveDefinition = Assert.Single(typeof(ProcessWideTestGroup)
            .GetCustomAttributes(typeof(CollectionDefinitionAttribute), false).Cast<CollectionDefinitionAttribute>());

        // Assert
        Assert.True(liveClasses.Length >= 52);
        Assert.All(liveClasses, type => Assert.True(
            typeof(IClassFixture<SqlServerContainerFixture>).IsAssignableFrom(type)));
        Assert.Equal([typeof(SqlServerSafeMigrationAllocationIntegrationTests)], exclusiveClasses);
        Assert.Equal(ProcessWideTestGroup.Name, allocationCollection.ConstructorArguments[0].Value);
        Assert.True(exclusiveDefinition.DisableParallelization);
        Assert.Empty(collectionOwners);
    }

    /// <summary>Prevents abstract helper ownership from rediscovering the original cases in every leaf.</summary>
    [Fact]
    public void HelperBase_DeclaresNoTestCases()
    {
        // Arrange
        var helperBase = typeof(SqlServerSafeMigrationIntegrationTests);

        // Act
        var declaredTests = helperBase.GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(FactAttribute), true).Length != 0).ToArray();

        // Assert
        Assert.True(helperBase.IsAbstract);
        Assert.Empty(declaredTests);
    }

    /// <summary>Activates only concrete selected live cases with no effective skip reason.</summary>
    /// <param name="testClass">The selected test class.</param>
    /// <param name="skipReason">The selected case's effective skip reason.</param>
    /// <param name="expected">Whether the assembly must initialize its fixture.</param>
    [Theory]
    [InlineData(typeof(SqlServerSafeMigrationBasicIntegrationTests), null, true)]
    [InlineData(typeof(SqlServerSafeMigrationBasicIntegrationTests), "", true)]
    [InlineData(typeof(SqlServerSafeMigrationBasicIntegrationTests), "unsupported architecture", false)]
    [InlineData(typeof(SqlServerOperationMatrixCoverageTests), null, false)]
    [InlineData(typeof(SqlServerSafeMigrationIntegrationTests), null, false)]
    public void FixtureSelection_RequiresOnlyConcreteExecutableLiveCases(
        Type testClass,
        string? skipReason,
        bool expected
    )
    {
        // Arrange
        var selectedClass = testClass;

        // Act
        var required = SharedContainerFixtureSelection.RequiresFixture<SqlServerContainerFixture>(
            selectedClass, skipReason);

        // Assert
        Assert.Equal(expected, required);
    }

    /// <summary>Never even constructs SQL Server's container for offline or all-skipped selections.</summary>
    /// <param name="allSkippedLiveCases">Whether selection contains only skipped live cases or only offline cases.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveSelection_DoesNotConstructOrDisposeAContainer(
        bool allSkippedLiveCases
    )
    {
        // Arrange
        var constructionCount = 0;
        var lifecycle = new SharedContainerFixtureLifecycle<SqlServerContainerFixture>(() =>
        {
            constructionCount++;

            throw new InvalidOperationException("An inactive selection constructed a SQL Server container.");
        });
        var selectedClass = allSkippedLiveCases
            ? typeof(SqlServerSafeMigrationBasicIntegrationTests)
            : typeof(SqlServerOperationMatrixCoverageTests);
        var skipReason = allSkippedLiveCases ? "unsupported architecture" : null;
        var required = SharedContainerFixtureSelection.RequiresFixture<SqlServerContainerFixture>(
            selectedClass, skipReason);

        // Act
        await Task.WhenAll(lifecycle.InitializeAsync(required), lifecycle.InitializeAsync(required));
        await Task.WhenAll(lifecycle.DisposeAsync(), lifecycle.DisposeAsync());

        // Assert
        Assert.False(required);
        Assert.Equal(0, constructionCount);
        Assert.Null(lifecycle.Fixture);
        Assert.Empty(lifecycle.Failures);
    }

    /// <summary>Native architecture skip metadata controls activation before fixture construction.</summary>
    [Fact]
    public void NativeSkipMetadata_MatchesFixtureActivation()
    {
        // Arrange
        var fact = new SqlServerLiveFactAttribute();
        var theory = new SqlServerLiveTheoryAttribute();
        var nativeHost = RuntimeInformation.OSArchitecture == Architecture.X64;

        // Act
        var factRequiresFixture = SharedContainerFixtureSelection.RequiresFixture<SqlServerContainerFixture>(
            typeof(SqlServerSafeMigrationBasicIntegrationTests), fact.Skip);
        var theoryRequiresFixture = SharedContainerFixtureSelection.RequiresFixture<SqlServerContainerFixture>(
            typeof(SqlServerSafeMigrationColumnLayoutIntegrationTests), theory.Skip);

        // Assert
        Assert.Equal(nativeHost, factRequiresFixture);
        Assert.Equal(nativeHost, theoryRequiresFixture);
        Assert.Equal(fact.Skip, theory.Skip);
    }
}
