namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks the PostgreSQL assembly's scheduling and selected-case activation contract.</summary>
public sealed class PostgreSqlTestParallelismTests
{
    /// <summary>The assembly chooses per-class collections and a bounded shared-fixture framework.</summary>
    [Fact]
    public void AssemblyScheduling_UsesPerClassCollectionsAndFourWorkers()
    {
        // Arrange
        var assembly = typeof(PostgreSqlTestParallelismTests).Assembly;

        // Act
        var behavior = Assert.Single(assembly.GetCustomAttributesData(), static attribute =>
            attribute.AttributeType == typeof(CollectionBehaviorAttribute));
        var framework = Assert.Single(assembly.GetCustomAttributesData(), static attribute =>
            attribute.AttributeType == typeof(TestFrameworkAttribute));

        // Assert
        Assert.Equal((int)CollectionBehavior.CollectionPerClass, behavior.ConstructorArguments[0].Value);
        Assert.Equal(4, Assert.Single(behavior.NamedArguments, static argument =>
            argument.MemberName == nameof(CollectionBehaviorAttribute.MaxParallelThreads)).TypedValue.Value);
        Assert.Equal(typeof(PostgreSqlTestFramework).FullName, framework.ConstructorArguments[0].Value);
        Assert.Equal(typeof(SharedContainerTestFramework<PostgreSqlContainerFixture>),
            typeof(PostgreSqlTestFramework).BaseType);
    }

    /// <summary>Only actual process-total allocation windows share the exclusive collection.</summary>
    [Fact]
    public void ExclusiveCollections_ContainOnlyProcessTotalAllocationCases()
    {
        // Arrange
        var classes = typeof(PostgreSqlTestParallelismTests).Assembly.GetTypes()
            .Where(static type => type.GetCustomAttribute<CollectionAttribute>() is not null)
            .ToArray();

        // Act
        var methods = classes.SelectMany(static type => type.GetMethods())
            .Where(static method => method.IsDefined(typeof(FactAttribute), inherit: true))
            .Select(static method => method.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(2, classes.Length);
        Assert.All(classes, static type => Assert.Equal(ProcessWideTestGroup.Name,
            Assert.Single(type.GetCustomAttributesData(), static attribute =>
                attribute.AttributeType == typeof(CollectionAttribute)).ConstructorArguments[0].Value));
        Assert.Equal(
            ["ColumnAttribution_PairedPlainAndMixedCatalogRuntimeFixtures",
                "ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently"],
            methods);
        Assert.Null(typeof(PostgreSqlPerformanceIntegrationTests).GetCustomAttribute<CollectionAttribute>());
    }

    /// <summary>The original helper/model owner declares no tests that could be inherited twice.</summary>
    [Fact]
    public void FeatureClasses_DeclareIndependentCasesAndPreserveModelOwners()
    {
        // Arrange
        var originalOwner = typeof(PostgreSqlSafeMigrationIntegrationTests);
        var featureClasses = originalOwner.Assembly.GetTypes()
            .Where(type => !type.IsAbstract && originalOwner.IsAssignableFrom(type))
            .ToArray();

        // Act
        var declaredCases = featureClasses.SelectMany(static type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(static method => method.IsDefined(typeof(FactAttribute), inherit: true))
            .ToArray();
        var inheritedCases = originalOwner.GetMethods()
            .Where(static method => method.IsDefined(typeof(FactAttribute), inherit: true))
            .ToArray();
        var nestedTypes = originalOwner.GetNestedTypes(BindingFlags.NonPublic)
            .Where(static type => !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute),
                inherit: false))
            .Select(static type => type.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.True(originalOwner.IsAbstract);
        Assert.Empty(inheritedCases);
        Assert.Equal(53, featureClasses.Length);
        Assert.Equal(325, declaredCases.Length);
        Assert.Equal(325, declaredCases.Select(static method => method.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(featureClasses, static type =>
            Assert.True(typeof(IClassFixture<PostgreSqlContainerFixture>).IsAssignableFrom(type)));
        Assert.Equal(
            ["BlockingProviderAnalyzer", "CountingDbConnection", "DerivedEntity", "MatchingDerivedContext",
                "SchemaChangingDerivedContext", "SnapshotFreeContext", "SnapshotFreeEntry",
                "StandaloneForeignKeyContext", "TransitionChild", "TransitionParent", "UnmappedValue"],
            nestedTypes);
    }

    /// <summary>Offline, abstract and skipped selections do not activate the native fixture.</summary>
    /// <param name="testClass">The selected test class.</param>
    /// <param name="skipReason">The selected case's skip reason.</param>
    /// <param name="requiresFixture">Whether that case requires the shared live container.</param>
    [Theory]
    [InlineData(typeof(PostgreSqlColumnsIntegrationTests), null, true)]
    [InlineData(typeof(PostgreSqlColumnAttributionIntegrationTests), null, true)]
    [InlineData(typeof(PostgreSqlColumnsIntegrationTests), "native prerequisite unavailable", false)]
    [InlineData(typeof(PostgreSqlTestParallelismTests), null, false)]
    [InlineData(typeof(PostgreSqlSafeMigrationIntegrationTests), null, false)]
    public void SelectedCaseActivation_UsesOnlyRunnableConcreteFixtureConsumers(
        Type testClass,
        string? skipReason,
        bool requiresFixture
    )
    {
        // Arrange
        var expected = requiresFixture;

        // Act
        var actual = SharedContainerFixtureSelection.RequiresFixture<PostgreSqlContainerFixture>(testClass, skipReason);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>An offline selection never invokes even a failing fixture factory.</summary>
    [Fact]
    public async Task OfflineSelection_DoesNotConstructAContainer()
    {
        // Arrange
        var constructions = 0;
        var lifecycle = new SharedContainerFixtureLifecycle<PostgreSqlContainerFixture>(() =>
        {
            constructions++;
            throw new InvalidOperationException("offline construction sentinel");
        });
        var required = SharedContainerFixtureSelection.RequiresFixture<PostgreSqlContainerFixture>(
            typeof(PostgreSqlTestParallelismTests), skipReason: null);

        // Act
        await lifecycle.InitializeAsync(required);
        await lifecycle.DisposeAsync();

        // Assert
        Assert.Equal(0, constructions);
        Assert.Null(lifecycle.Fixture);
        Assert.Empty(lifecycle.Failures);
    }
}
