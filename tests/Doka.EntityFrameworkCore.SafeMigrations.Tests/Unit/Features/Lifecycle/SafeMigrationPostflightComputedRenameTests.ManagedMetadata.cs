namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPostflightComputedRenameTests
{
    /// <summary>Value-column and candidate-key metadata change without copying or altering stored row values.</summary>
    /// <param name="kind">The managed write whose non-key column is renamed.</param>
    [Theory]
    [InlineData("ensure")]
    [InlineData("update")]
    public void ManagedValueRenameRetainsUniqueKeyAndImmutableMatrices(string kind)
    {
        // Arrange
        var source = ManagedRenameIntent(kind, "source");
        MigrationOperation[] operations =
        [
            Safe(source),
            Safe(new RenameColumnIntent("payload", "source_nodes", "renamed_payload")),
        ];

        // Act
        var probes = ManagedProbes(operations);

        // Assert
        var rebound = Assert.IsAssignableFrom<ModelManagedDataIntent>(Assert.Single(probes!).Operation.Intent);
        Assert.Equal(["source"], rebound.KeyColumns);
        Assert.Contains("renamed_payload", rebound.Columns);
        Assert.Same(source.KeyValues, rebound.KeyValues);
        if (source is EnsureModelManagedDataIntent ensure)
        {
            var actual = Assert.IsType<EnsureModelManagedDataIntent>(rebound);
            Assert.Same(ensure.Values, actual.Values);
            Assert.Equal(["renamed_payload"], Assert.Single(actual.UniqueKeys).Columns);
        }
        else
        {
            var update = Assert.IsType<UpdateModelManagedDataIntent>(source);
            var actual = Assert.IsType<UpdateModelManagedDataIntent>(rebound);
            Assert.Same(update.OldValues, actual.OldValues);
            Assert.Same(update.NewValues, actual.NewValues);
            Assert.Equal(["renamed_payload"], Assert.Single(actual.UniqueKeys).Columns);
        }
    }

    /// <summary>Incoming dependent-column roles are rebound independently of the managed principal owner.</summary>
    [Fact]
    public void ManagedDeleteRebindsOnlyRenamedDependentRole()
    {
        // Arrange
        var source = Assert.IsType<DeleteModelManagedDataIntent>(ManagedRenameIntent("delete", "source"));
        MigrationOperation[] operations =
        [
            Safe(source),
            Safe(new RenameColumnIntent("owner", "dependents", "renamed_owner")),
        ];

        // Act
        var probes = ManagedProbes(operations);

        // Assert
        var actual = Assert.IsType<DeleteModelManagedDataIntent>(Assert.Single(probes!).Operation.Intent);
        var foreignKey = Assert.Single(actual.ForeignKeys);
        Assert.Equal("dependents", foreignKey.Table);
        Assert.Null(foreignKey.Schema);
        Assert.Equal(["renamed_owner"], foreignKey.Columns);
        Assert.Equal(["source"], foreignKey.PrincipalColumns);
        Assert.Same(source.Columns, actual.Columns);
        Assert.Same(source.KeyValues, actual.KeyValues);
        Assert.Same(source.OldValues, actual.OldValues);
    }

    /// <summary>Successive verified renames retain one final row probe with unchanged matrices and policy.</summary>
    [Fact]
    public void SuccessiveManagedRenamesProduceOneFinalProbe()
    {
        // Arrange
        var source = ManagedRenameIntent("ensure", "source");
        var operation = Safe(source);
        operation.AddAnnotation("Test:CapturedMetadata", "retained");
        MigrationOperation[] operations =
        [
            operation,
            Safe(new RenameColumnIntent("source", "source_nodes", "middle")),
            Safe(new RenameColumnIntent("middle", "source_nodes", "final")),
        ];

        // Act
        var probes = ManagedProbes(operations);

        // Assert
        var probe = Assert.Single(probes!);
        var actual = Assert.IsType<EnsureModelManagedDataIntent>(probe.Operation.Intent);
        Assert.Equal(["final"], actual.KeyColumns);
        Assert.Equal(["final", "payload"], actual.Columns);
        Assert.Same(source.KeyValues, actual.KeyValues);
        Assert.Same(Assert.IsType<EnsureModelManagedDataIntent>(source).Values, actual.Values);
        Assert.Equal(operation.Policy, probe.Operation.Policy);
        Assert.Equal("retained", probe.Operation["Test:CapturedMetadata"]);
        Assert.Equal(0, probe.OperationOrdinal);
        Assert.Equal(0, probe.SafeOrdinal);
    }

    /// <summary>A collision, another owner or later opaque work cannot certify a transformed row contract.</summary>
    /// <param name="boundary">The independent reason no historical row probe can be retained.</param>
    [Theory]
    [InlineData("collision")]
    [InlineData("other_owner")]
    [InlineData("later_sql")]
    [InlineData("later_structure")]
    public void UnprovenManagedRenameMetadataDoesNotProduceProbe(string boundary)
    {
        // Arrange
        var operations = new List<MigrationOperation>
        {
            Safe(ManagedRenameIntent("ensure", "source")),
            Safe(new RenameColumnIntent("source", boundary == "other_owner" ? "other_nodes" : "source_nodes",
                boundary == "collision" ? "payload" : "renamed")),
        };

        if (boundary == "later_sql")
        {
            operations.Add(new SqlOperation { Sql = "SELECT 1;", });
        }
        else if (boundary == "later_structure")
        {
            operations.Add(Safe(new DropColumnIntent("payload", "source_nodes")));
        }

        // Act
        var probes = ManagedProbes(operations);

        // Assert
        Assert.Null(probes);
    }

    private static List<SafeMigrationPostflightRenameProbe>? ManagedProbes(
        IReadOnlyList<MigrationOperation> operations
    ) => SafeMigrationPostflightProjection.CreateRenamedManagedDataProbes(operations,
        operations.OfType<SafeMigrationOperation>().Select(static _ =>
            new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None, true, "classified_matching")).ToArray(),
        new FinalCatalogAnalyzer(Table("source")));
}
