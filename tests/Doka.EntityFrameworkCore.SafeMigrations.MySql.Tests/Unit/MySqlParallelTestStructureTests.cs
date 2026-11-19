namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Locks the scheduling and ownership boundaries without constructing a native engine.</summary>
public sealed class MySqlParallelTestStructureTests
{
    /// <summary>
    /// The runnable families are independent collections, not inherited copies of abstract-base facts.
    /// </summary>
    [Fact]
    public void FeatureClassesRetainFixtureContractWithoutInheritedRunnableMethods()
    {
        // Arrange
        var helperOwner = typeof(MySqlSafeMigrationIntegrationTests);
        var featureClasses = helperOwner
            .Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && type.IsSubclassOf(helperOwner))

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        // Act
        var inheritedTests = helperOwner
            .GetMethods()
            .Where(method => method
                .GetCustomAttributes<FactAttribute>()
                .Any())

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var declaredTests = featureClasses
            .SelectMany(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method
                .GetCustomAttributes<FactAttribute>()
                .Any())

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var checkBypassMethod = typeof(MySqlColumnsAtomicRequiredAddConcurrencyIntegrationTests).GetMethod(
            nameof(MySqlColumnsAtomicRequiredAddConcurrencyIntegrationTests
                .AtomicRequiredAddConcurrencyPreservesNullAndBarrierAfterWriterCheckBypass));

        var projectedProtectionMethod = typeof(MySqlColumnsRequiredDefaultsProjectedAddsIntegrationTests).GetMethod(
            nameof(MySqlColumnsRequiredDefaultsProjectedAddsIntegrationTests
                .RequiredDefaultProjectedNoDefaultAddRejectsDisabledMariaCheckProtection));

        var unrelatedTimeoutMethod = typeof(MySqlPerformanceIntegrationTests).GetMethod(
            nameof(MySqlPerformanceIntegrationTests.Analyzer_CommandTimeoutAssertionRejectsUnrelatedNativeFailure));

        // Assert
        Assert.True(helperOwner.IsAbstract);
        Assert.Empty(inheritedTests);

        // WHY: Exact feature-family coverage includes custom Fact subclasses; approximate counts can hide omissions.
        Assert.Equal(84, featureClasses.Length);

        // WHY: The native timeout's unrelated-error negative control adds one selectable method;
        // theory case splits retain the original method identities rather than multiplying this count.
        // Generated tooling facts are not integration-helper subclasses and do not belong to this count.
        Assert.Equal(659, declaredTests.Length);
        Assert.Equal(659,
            declaredTests
                .Select(method => method.Name)
                .Distinct(StringComparer.Ordinal)
                .Count());

        Assert.NotNull(checkBypassMethod);
        Assert.Equal(typeof(MySqlColumnsAtomicRequiredAddConcurrencyIntegrationTests), checkBypassMethod.DeclaringType);
        Assert.NotNull(projectedProtectionMethod);
        Assert.Equal(
            typeof(MySqlColumnsRequiredDefaultsProjectedAddsIntegrationTests),
            projectedProtectionMethod.DeclaringType);

        Assert.NotNull(unrelatedTimeoutMethod);
        Assert.Equal(typeof(MySqlPerformanceIntegrationTests), unrelatedTimeoutMethod.DeclaringType);

        Assert.Contains(typeof(MySqlTablesIntegrationTests), featureClasses);
        Assert.Contains(typeof(MySqlInnoDbTableIntegrationTests), featureClasses);
        Assert.Contains(typeof(MySqlColumnsIntegrationTests), featureClasses);
        Assert.Contains(typeof(MySqlPerformanceIntegrationTests), featureClasses);
        Assert.Contains(typeof(MySqlGeneratedCaseIntegrationTests), featureClasses);
        Assert.Contains(typeof(MySqlProvider106IntegrationTests), featureClasses);
        Assert.Contains(typeof(MySqlIndependentColumnDropIntegrationTests), featureClasses);
        Assert.Contains(typeof(MySqlIndexDropIntegerWideningIntegrationTests), featureClasses);
        Assert.All(
            featureClasses,
            type =>
            {
                Assert.Contains(typeof(IClassFixture<MySqlEngineContainerFixture>), type.GetInterfaces());
                Assert.NotNull(type.GetConstructor([typeof(MySqlEngineContainerFixture)]));
                Assert.All(
                    type
                        .GetMethods()
                        .Where(method => method
                            .GetCustomAttributes<FactAttribute>()
                            .Any()),
                    method => Assert.Equal(type, method.DeclaringType));
            });
    }

    /// <summary>
    /// Selected runnable fixture consumers activate the engine; skipped, offline and abstract cases do not.
    /// </summary>
    /// <param name="testClass">The candidate selected class.</param>
    /// <param name="skipReason">The actual selected case's skip reason.</param>
    /// <param name="expected">Whether this selection owns a native startup.</param>
    [Theory]
    [InlineData(typeof(MySqlTablesIntegrationTests), null, true)]
    [InlineData(typeof(MySqlInnoDbTableIntegrationTests), null, true)]
    [InlineData(typeof(MySqlInnoDbTableIntegrationTests), "native unavailable", false)]
    [InlineData(typeof(MySqlColumnsIntegrationTests), "", true)]
    [InlineData(typeof(MySqlIndexDropIntegerWideningIntegrationTests), null, true)]
    [InlineData(typeof(MySqlIndexDropIntegerWideningIntegrationTests), "native unavailable", false)]
    [InlineData(typeof(MySqlTablesIntegrationTests), "native unavailable", false)]
    [InlineData(typeof(MySqlRuntimeDiagnosticsTests), null, false)]
    [InlineData(typeof(MySqlSafeMigrationIntegrationTests), null, false)]
    public void NativeFixtureActivationMatchesActualSelectedRunnableClass(
        Type testClass,
        string? skipReason,
        bool expected
    )
    {
        // Arrange
        var concreteClass = testClass;

        // Act
        var required = SharedContainerFixtureSelection.RequiresFixture<MySqlEngineContainerFixture>(
            concreteClass,
            skipReason);

        // Assert
        Assert.Equal(expected, required);
    }

    /// <summary>
    /// Only process-wide retention and process-total measurement contracts opt out of class parallelism.
    /// </summary>
    [Fact]
    public void OnlyProvenProcessWideContractsUseExclusiveCollection()
    {
        // Arrange
        var assembly = typeof(MySqlTestFramework).Assembly;
        var behavior = assembly.GetCustomAttribute<CollectionBehaviorAttribute>();
        var definition = typeof(ProcessWideTestGroup).GetCustomAttribute<CollectionDefinitionAttribute>();

        // Act
        var exclusiveClasses = assembly
            .GetTypes()
            .Where(type => type
                .GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType == typeof(CollectionAttribute))
                .Select(attribute => attribute.ConstructorArguments[0].Value as string)
                .Contains(ProcessWideTestGroup.Name))

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        // Assert
        Assert.NotNull(behavior);
        Assert.False(behavior.DisableTestParallelization);
        Assert.Equal(4, behavior.MaxParallelThreads);
        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);
        Assert.Equal(
            new[] { typeof(MySqlProcessWidePerformanceIntegrationTests), typeof(MySqlRuntimeDiagnosticsRetentionTests) }
                .OrderBy(type => type.Name, StringComparer.Ordinal),
            exclusiveClasses.OrderBy(type => type.Name, StringComparer.Ordinal));

        Assert.Equal(
            nameof(MySqlProcessWidePerformanceIntegrationTests
                .ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently),
            Assert.Single(
                    typeof(MySqlProcessWidePerformanceIntegrationTests).GetMethods(),
                    method => method
                        .GetCustomAttributes<FactAttribute>()
                        .Any())
                .Name);

        Assert.Null(typeof(MySqlPerformanceIntegrationTests).GetCustomAttribute<CollectionAttribute>());
        Assert.Null(typeof(MySqlRuntimeDiagnosticsTests).GetCustomAttribute<CollectionAttribute>());
    }
}
