namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Pins narrow process-wide exclusions while ordinary Core classes retain independent scheduling.</summary>
public sealed class CoreSchedulingContractTests
{
    /// <summary>Verifies that the copied runner configuration preserves the repository scheduling contract.</summary>
    [Fact]
    public void CopiedRunnerConfigurationPinsRepositorySchedulingContract()
    {
        // Arrange
        // WHY: Inspect the actual test output so a missing or stale configuration copy fails existing Core CI.
        var path = Path.Combine(AppContext.BaseDirectory, "xunit.runner.json");

        // Act
        using var configuration = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = configuration.RootElement;

        // Assert
        Assert.True(root.GetProperty("parallelizeAssembly").GetBoolean());
        Assert.True(root.GetProperty("parallelizeTestCollections").GetBoolean());
        Assert.Equal("conservative", root.GetProperty("parallelAlgorithm").GetString());
        Assert.False(root.TryGetProperty("parallelizeAssemblies", out _));
        Assert.False(root.TryGetProperty("maxParallelThreads", out _));
    }

    /// <summary>Rejects missing exclusions and blanket serialization of additional Core classes.</summary>
    [Fact]
    public void ProcessWideCollectionContainsOnlyReviewedClasses()
    {
        // Arrange
        Type[] expected =
        [
            typeof(SafeMigrationTelemetryMeasurementTests),
            typeof(SafeMigrationOperationIdentityAllocationTests),
            typeof(SafeMigrationRuntimeDiagnosticContextGarbageCollectionTests),
            typeof(SafeMigrationDiagnosticTruncationAllocationTests),
            typeof(SafeMigrationDiagnosticMessagesAllocationTests),
            typeof(SafeMigrationModelManagedKeyIdentityAllocationTests),
            typeof(Infrastructure.BenchmarkRunnerMeasurementTests),
        ];

        // Act
        var actual = typeof(CoreSchedulingContractTests).Assembly.GetTypes()
            .Where(static type => CollectionName(type) == ProcessWideTestGroup.Name);

        var definition = typeof(ProcessWideTestGroup).GetCustomAttribute<CollectionDefinitionAttribute>();

        // Assert
        Assert.Equal(expected.OrderBy(static type => type.FullName, StringComparer.Ordinal),
            actual.OrderBy(static type => type.FullName, StringComparer.Ordinal));

        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);
    }

    /// <summary>Rejects unrelated methods entering the exclusive classes and inherited test duplication.</summary>
    [Fact]
    public void ExclusiveClassesContainOnlyReviewedMethods()
    {
        // Arrange
        var expected = new Dictionary<Type, string[]>
        {
            [typeof(SafeMigrationTelemetryMeasurementTests)] =
            [
                nameof(SafeMigrationTelemetryMeasurementTests.FailureTelemetry_ContainsOnlyBoundedLowCardinalityTags),
                nameof(SafeMigrationTelemetryMeasurementTests
                    .SuccessfulTelemetry_UsesStableDatabaseSemanticConventionTags),
            ],
            [typeof(SafeMigrationOperationIdentityAllocationTests)] =
            [
                nameof(SafeMigrationOperationIdentityAllocationTests
                    .SingleColumnProductionReducesRetainedMetadataAllocation),
                nameof(SafeMigrationOperationIdentityAllocationTests
                    .OrdinaryColumnProductionReducesRetainedMetadataAllocation),
            ],
            [typeof(SafeMigrationRuntimeDiagnosticContextGarbageCollectionTests)] =
            [
                nameof(SafeMigrationRuntimeDiagnosticContextGarbageCollectionTests
                    .CapturedContextDoesNotRetainModelManagedValue),
            ],
            [typeof(SafeMigrationDiagnosticTruncationAllocationTests)] =
            [
                nameof(SafeMigrationDiagnosticTruncationAllocationTests.DisplayBoundarySelectionDoesNotAllocate),
            ],
            [typeof(SafeMigrationDiagnosticMessagesAllocationTests)] =
            [
                nameof(SafeMigrationDiagnosticMessagesAllocationTests
                    .ExplanationLookupDoesNotAllocateAfterGarbageCollection),
            ],
            [typeof(SafeMigrationModelManagedKeyIdentityAllocationTests)] =
            [
                nameof(SafeMigrationModelManagedKeyIdentityAllocationTests
                    .CompositeKeyAlreadyOrderedPathAvoidsAllocations),
            ],
            [typeof(Infrastructure.BenchmarkRunnerMeasurementTests)] =
            [
                nameof(Infrastructure.BenchmarkRunnerMeasurementTests.CompletePreservesExceededBudgetEvidence),
                nameof(Infrastructure.BenchmarkRunnerMeasurementTests.CompleteAcceptsMeasurementsWithinBudgets),
                nameof(Infrastructure.BenchmarkRunnerMeasurementTests.MeasureRejectsDuplicateBenchmarkNames),
                nameof(Infrastructure.BenchmarkRunnerMeasurementTests.MeasurePropagatesExecutionFailures),
                nameof(Infrastructure.BenchmarkRunnerMeasurementTests.CompletePropagatesOutputFailures),
                nameof(Infrastructure.BenchmarkRunnerMeasurementTests.CompleteHonorsCommandLineBudgetPolicy),
            ],
        };

        // Act
        var actual = expected.Keys.ToDictionary(static type => type,
            static type => type.GetMethods().Where(static method => method.IsDefined(typeof(FactAttribute), true))
                .Select(static method => method.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray());

        // Assert
        foreach (var (type, methods) in expected)
        {
            Assert.Equal(typeof(object), type.BaseType);
            Assert.Equal(methods.OrderBy(static name => name, StringComparer.Ordinal), actual[type]);
        }

        Assert.DoesNotContain(typeof(Infrastructure.BenchmarkFixture).GetMethods(),
            static method => method.IsDefined(typeof(FactAttribute), true));
    }

    /// <summary>
    /// Rejects blanket exclusion of ordinary source classes, including trace-filtered Activity tests.
    /// </summary>
    [Fact]
    public void OrdinaryCoreClassesRemainParallelEligible()
    {
        // Arrange
        Type[] ordinary =
        [
            typeof(SafeMigrationRunContractTests),
            typeof(SafeMigrationRuntimeDiagnosticContextTests),
            typeof(SafeMigrationDiagnosticTruncationTests),
            typeof(SafeMigrationDiagnosticMessagesTests),
            typeof(SafeMigrationModelManagedKeyIdentityTests),
            typeof(SafeMigrationAnalysisTelemetryTests),
            typeof(Infrastructure.BenchmarkRunnerTests),
        ];

        // Act
        var collections = ordinary.Select(static type => type.GetCustomAttribute<CollectionAttribute>()).ToArray();

        // Assert
        Assert.All(collections, static collection => Assert.Null(collection));
        Assert.All(ordinary, static type => Assert.Contains(type.GetMethods(),
            static method => method.IsDefined(typeof(FactAttribute), true)));
    }

    /// <summary>
    /// Preserves the previously verified planner collection without adding its test to ordinary scheduling.
    /// </summary>
    [Fact]
    public void ExistingPlannerGarbageCollectionGroupRemainsExclusive()
    {
        // Arrange
        var planner = typeof(SafeMigrationDecisionPlannerGarbageCollectionTests);
        var collectionDefinition = typeof(SafeMigrationDecisionPlannerGarbageCollectionDefinition);

        // Act
        var collection = planner.GetCustomAttribute<CollectionAttribute>();
        var collectionName = CollectionName(planner);
        var definition = collectionDefinition.GetCustomAttribute<CollectionDefinitionAttribute>();
        var methods = planner.GetMethods().Where(static method => method.IsDefined(typeof(FactAttribute), true))
            .Select(static method => method.Name).ToArray();

        // Assert
        Assert.NotNull(collection);
        Assert.Equal(collectionDefinition.Name, collectionName);
        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);
        Assert.Equal(
            [nameof(SafeMigrationDecisionPlannerGarbageCollectionTests
                .ActionOnly_DoesNotAllocateAfterGarbageCollection)],
            methods);
    }

    /// <summary>Reads the constructor metadata used by xUnit v2 collection discovery.</summary>
    /// <param name="type">The test class whose declared collection is inspected.</param>
    /// <returns>The declared collection name, or null when no named collection is declared.</returns>
    private static string? CollectionName(Type type)
    {
        var collection = type.GetCustomAttributesData()
            .SingleOrDefault(static attribute => attribute.AttributeType == typeof(CollectionAttribute));

        // WHY: xUnit v2 exposes collection identity through discovery metadata, not a public Name property.
        return collection?.ConstructorArguments[0].Value as string;
    }
}
