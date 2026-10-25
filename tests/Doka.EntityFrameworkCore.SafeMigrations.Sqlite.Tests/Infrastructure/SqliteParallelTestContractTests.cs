namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Guards discovery boundaries and independently owned native state without timing assumptions.</summary>
public sealed class SqliteParallelTestContractTests : SqliteIntegrationTestBase
{
    /// <summary>Ordinary features get independent collections while process measurements are exclusive.</summary>
    [Fact]
    public void FeatureClasses_UseIndependentCollectionsExceptTheProcessMeasurement()
    {
        // Arrange
        var assembly = typeof(SqliteSafeMigrationCatalogIntegrationTests).Assembly;
        var behavior = assembly.GetCustomAttribute<CollectionBehaviorAttribute>();
        var registration = Assert.Single(assembly.GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionBehaviorAttribute));
        var features = CatalogFeatureClasses();
        var exclusive = typeof(SqliteSafeMigrationPerformanceIntegrationTests);
        var ordinary = features.Where(type => type != exclusive).ToArray();

        // Act
        var exclusiveRegistration = Assert.Single(exclusive.GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionAttribute));
        var collectionName = Assert.IsType<string>(Assert.Single(exclusiveRegistration.ConstructorArguments).Value);
        var definition = typeof(ProcessWideTestGroup).GetCustomAttribute<CollectionDefinitionAttribute>();

        // Assert
        Assert.NotNull(behavior);
        Assert.Equal((int)CollectionBehavior.CollectionPerClass,
            Convert.ToInt32(Assert.Single(registration.ConstructorArguments).Value, CultureInfo.InvariantCulture));
        Assert.False(behavior.DisableTestParallelization);
        Assert.Equal(0, behavior.MaxParallelThreads);
        Assert.Equal(22, features.Length);
        Assert.Equal(21, ordinary.Select(type => type.FullName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ordinary, type => Assert.Null(type.GetCustomAttribute<CollectionAttribute>()));
        Assert.Equal(ProcessWideTestGroup.Name, collectionName);
        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);
        Assert.Contains(typeof(SqliteSafeMigrationIndexesAndCatalogIntegrationTests), ordinary);
        Assert.Contains(typeof(SqliteSafeMigrationExecutionIntegrationTests), ordinary);
    }

    /// <summary>Abstract shared ownership must never multiply inherited facts across concrete feature leaves.</summary>
    [Fact]
    public void CatalogModelOwner_HasNoTestsAndEveryOriginalMethodHasOneConcreteOwner()
    {
        // Arrange
        var owner = typeof(SqliteSafeMigrationCatalogIntegrationTests);
        var features = CatalogFeatureClasses();
        var ownerMethods = owner.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        // Act
        var featureTests = features.SelectMany(type => type.GetMethods()
            .Where(method => method.GetCustomAttribute<FactAttribute>() is not null)
            .Select(method => (Feature: type, Method: method))).ToArray();

        // Assert
        Assert.True(owner.IsAbstract);
        Assert.DoesNotContain(ownerMethods, method => method.GetCustomAttribute<FactAttribute>() is not null);
        // WHY: Four additional methods expose the original three aggregate
        // contracts independently without adding runnable feature classes.
        Assert.Equal(199, featureTests.Length);
        Assert.All(featureTests, test => Assert.Equal(test.Feature, test.Method.DeclaringType));
        Assert.All(featureTests.GroupBy(test => test.Method.Name, StringComparer.Ordinal), group =>
            Assert.Single(group));
        Assert.Contains(featureTests, test => test.Method.Name
            == "ModelManagedData_UniqueCollisionFailsClosedWithoutMutation");
        Assert.Contains(featureTests, test => test.Method.Name
            == "ModelManagedData_UnmodeledDependencyDeletionFailsClosedWithoutMutation");
        Assert.Contains(featureTests, test => test.Method.Name
            == "GeneratedArithmeticColumn_IsComparedByPhysicalFacet");
        Assert.Contains(featureTests, test => test.Method.Name
            == "StringDefaultAndCollation_AreComparedByPhysicalFacet");
        Assert.Contains(featureTests, test => test.Method.Name == "SchemaIntent_MainIsNoOp");
        Assert.Contains(featureTests, test => test.Method.Name == "SchemaIntent_ForeignSchemaIsRejected");
        Assert.Contains(featureTests, test => test.Method.Name == "SchemaIntent_DropSchemaIsRejected");
        Assert.Equal(
            typeof(SqliteSafeMigrationPerformanceIntegrationTests),
            Assert.Single(featureTests, test => test.Method.Name
                == "ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently").Feature);
    }

    /// <summary>Moving test discovery must not change EF model cache identities or nested entity ownership.</summary>
    /// <param name="modelName">The nested model whose original CLR identity must remain stable.</param>
    [Theory]
    [InlineData("SqliteConstraintShapeContext")]
    [InlineData("SqliteUniqueConstraintShapeContext")]
    [InlineData("SqliteCheckConstraintShapeContext")]
    [InlineData("SqliteForeignKeyConstraintShapeContext")]
    [InlineData("SqliteProjectedSeparateAddContext")]
    [InlineData("SqliteProjectedForeignKeyNullDefaultContext")]
    [InlineData("SqliteProjectedCompositeForeignKeyContext")]
    public void NestedEfContexts_RetainTheOriginalDeclaringType(string modelName)
    {
        // Arrange
        var owner = typeof(SqliteSafeMigrationCatalogIntegrationTests);

        // Act
        var model = owner.GetNestedType(modelName, BindingFlags.NonPublic);

        // Assert
        Assert.NotNull(model);
        Assert.Equal(owner, model.DeclaringType);
        Assert.Equal(owner.FullName + "+" + modelName, model.FullName);
        Assert.True(typeof(DbContext).IsAssignableFrom(model));
    }

    /// <summary>A rejected command on one in-memory connection cannot mutate a neighboring test's state.</summary>
    [Fact]
    public async Task InMemoryConnections_KeepOverlappingNamesAndFailuresIndependent()
    {
        // Arrange
        await using var first = await OpenConnectionAsync();
        await using var second = await OpenConnectionAsync();
        await ExecuteSqlAsync(first,
            "CREATE TABLE parallel_owned_rows (Id INTEGER); INSERT INTO parallel_owned_rows VALUES (11);");
        await ExecuteSqlAsync(second,
            "CREATE TABLE parallel_owned_rows (Id INTEGER); INSERT INTO parallel_owned_rows VALUES (22);");

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteSqlAsync(first,
            "INSERT INTO nonexistent_parallel_table VALUES (33);"));
        var firstValue = await ScalarIntAsync(first, "SELECT Id FROM parallel_owned_rows;");
        var secondValue = await ScalarIntAsync(second, "SELECT Id FROM parallel_owned_rows;");

        // Assert
        Assert.NotSame(first, second);
        Assert.IsType<SqliteException>(exception);
        Assert.Equal(11, firstValue);
        Assert.Equal(22, secondValue);
    }

    /// <summary>Private caches do not isolate files: independently allocated paths prevent shared rows.</summary>
    /// <param name="independentFiles">Whether this case proves isolation or the shared-file control.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnedFileConnections_IsolateOnlyIndependentPaths(bool independentFiles)
    {
        // Arrange
        var firstPath = SqliteSafeMigrationExecutionIntegrationTests.CreateOwnedDatabasePath();
        var secondPath = SqliteSafeMigrationExecutionIntegrationTests.CreateOwnedDatabasePath();
        var firstString = SqliteSafeMigrationExecutionIntegrationTests.CreateOwnedFileConnectionString(firstPath);
        var secondString = SqliteSafeMigrationExecutionIntegrationTests.CreateOwnedFileConnectionString(
            independentFiles ? secondPath : firstPath);

        try
        {
            await using var second = new SqliteConnection(secondString);
            await second.OpenAsync(CancellationToken.None);

            // Act
            await using (var first = new SqliteConnection(firstString))
            {
                await first.OpenAsync(CancellationToken.None);
                await ExecuteSqlAsync(first,
                    "PRAGMA journal_mode=WAL; CREATE TABLE parallel_owned_rows (Id INTEGER); "
                    + "INSERT INTO parallel_owned_rows VALUES (11);");
            }

            var observedTables = await ScalarIntAsync(second,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'parallel_owned_rows';");
            await ExecuteSqlAsync(second, "CREATE TABLE surviving_connection (Id INTEGER);");

            // Assert
            Assert.NotEqual(firstPath, secondPath);
            Assert.False(new SqliteConnectionStringBuilder(firstString).Pooling);
            Assert.False(new SqliteConnectionStringBuilder(secondString).Pooling);
            Assert.Equal(independentFiles ? 0 : 1, observedTables);
            Assert.Equal(1, await ScalarIntAsync(second,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'surviving_connection';"));
        }
        finally
        {
            DeleteOwnedFile(firstPath);
            DeleteOwnedFile(secondPath);
        }
    }

    /// <summary>Finds independent catalog leaves, excluding models and unrelated standalone suites.</summary>
    /// <returns>The concrete classes whose discovery contract this metadata suite protects.</returns>
    private static Type[] CatalogFeatureClasses() => typeof(SqliteSafeMigrationCatalogIntegrationTests).Assembly
        .GetTypes()
        .Where(type => type.IsClass && !type.IsAbstract
            && typeof(SqliteSafeMigrationCatalogIntegrationTests).IsAssignableFrom(type))
        .ToArray();

    /// <summary>Deletes only this test's allocated paths after the owning connections are disposed.</summary>
    /// <param name="databasePath">The invocation-owned native database path.</param>
    private static void DeleteOwnedFile(string databasePath)
    {
        File.Delete(databasePath);
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");
    }
}
