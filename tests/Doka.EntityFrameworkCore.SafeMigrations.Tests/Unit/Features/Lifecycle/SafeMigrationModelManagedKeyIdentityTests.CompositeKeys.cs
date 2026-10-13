namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedKeyIdentityTests
{
    /// <summary>Projection identifies reordered predicates only when every column retains its paired value.</summary>
    /// <param name="reassociateValues">Whether the later values follow their reordered key labels.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositeModelManagedPostflightPairsLabelsAndValuesIndependentlyOfKeyOrder(bool reassociateValues)
    {
        // Arrange
        var identity = new ColumnKeyNormalizer(insensitive: true);
        MigrationOperation[] operations =
        [
            Operation(CompositeEnsure()),
            Operation(CompositeUpdate(reassociateValues)),
        ];

        // WHY: This tests physical identity reduction, not authored repetition.
        // Repeated keys inside one migration remain contract-validator errors;
        // production cross-migration streams require certified origin boundaries.

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity);

        // Assert
        Assert.Equal(reassociateValues, projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Ordered row evidence recognizes a composite predicate without confusing swapped values.</summary>
    /// <param name="reassociateValues">Whether the update retains the column/value associations.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositeModelManagedPreflightRetainsColumnValueAssociations(bool reassociateValues)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ColumnKeyNormalizer(insensitive: true));

        Accept(projection, Operation(CompositeEnsure()));
        var update = Operation(CompositeUpdate(reassociateValues));

        // Act
        var analysis = projection.Project(update, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(reassociateValues ? SafeMigrationObservedState.TransitionReady
            : SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>Composite reordering cannot merge a different owner, qualifier or exact typed key value.</summary>
    /// <param name="boundary">The independently different row-identity facet.</param>
    [Theory]
    [InlineData("owner")]
    [InlineData("schema")]
    [InlineData("key_type")]
    [InlineData("key_value")]
    public void CompositeModelManagedKeyPermutationRetainsOtherIdentityBoundaries(string boundary)
    {
        // Arrange
        var identity = new ColumnKeyNormalizer(insensitive: true);
        var keyValues = new object?[,]
        {
            { 2, boundary == "key_type" ? (object)1L : boundary == "key_value" ? 9 : 1 },
        };

        var update = new UpdateModelManagedDataIntent(boundary == "owner" ? "items" : "Items",
            ["CODE", "ID"], ["int", "int"], keyValues, ["Value"], ["int"],
            new object?[,] { { 7 } }, new object?[,] { { 8 } },
            boundary == "schema" ? "other" : null, uniqueKeys: null);

        MigrationOperation[] operations = [Operation(CompositeEnsure()), Operation(update)];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity);

        // Assert
        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Canonical predicates retain mixed key types and also ignore authored row-set order.</summary>
    [Fact]
    public void CompositeModelManagedPermutationRetainsMixedTypesAndRowSetIdentity()
    {
        // Arrange
        var identity = new ColumnKeyNormalizer(insensitive: true);
        var ensure = new EnsureModelManagedDataIntent("Items", ["Id", "Code"], ["int", "varchar(64)"],
            ["Id", "Code", "Value"], ["int", "varchar(64)", "int"],
            new object?[,] { { 1, "first", 7 }, { 2, "second", 7 } }, schema: null, uniqueKeys: null);

        var update = new UpdateModelManagedDataIntent("Items", ["CODE", "ID"], ["varchar(64)", "int"],
            new object?[,] { { "second", 2 }, { "first", 1 } }, ["Value"], ["int"],
            new object?[,] { { 7 }, { 7 } }, new object?[,] { { 8 }, { 8 } }, schema: null, uniqueKeys: null);

        MigrationOperation[] operations = [Operation(ensure), Operation(update)];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity);

        // Assert
        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Pending uniqueness proof depends on the same column set, not the requested index key order.</summary>
    /// <param name="differentColumn">Whether the index targets a different, unproven composite key.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositeModelManagedUniqueProofRetainsColumnSetIdentity(bool differentColumn)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ColumnKeyNormalizer(insensitive: true));

        Accept(projection, Operation(new EnsureTableIntent(new ExpectedTableDefinition("Items",
            [Column("Id"), Column("Code"), Column("Value")]), SafeMigrationTableMode.StrictDefinition)));
        Accept(projection, Operation(new EnsureModelManagedDataIntent("Items", ["Id", "Code"], ["int", "int"],
            ["Id", "Code", "Value"], ["int", "int", "int"], new object?[,] { { 1, 2, 7 }, { 3, 2, 7 } },
            schema: null, uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Id", "Code"])])));
        var index = Operation(new EnsureIndexIntent(new ExpectedIndexDefinition("pending_pair", "Items",
            [new ExpectedIndexKeyDefinition("CODE"), new ExpectedIndexKeyDefinition(differentColumn ? "Value" : "ID")],
            unique: true)));

        // Act
        var analysis = projection.Project(index, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(differentColumn ? SafeMigrationObservedState.PrerequisiteMissing
            : SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Already canonical and single-member keys need no permutation array or input snapshot.</summary>
    /// <param name="shape">The authored key-label order.</param>
    [Theory]
    [InlineData("single")]
    [InlineData("sorted")]
    [InlineData("unsorted")]
    public void CompositeKeyCanonicalPermutationReusesAlreadyOrderedInput(string shape)
    {
        // Arrange
        string[] columns = shape == "single" ? ["Id"] : shape == "sorted" ? ["Code", "Id"] : ["Id", "Code"];
        string[] expected = shape == "single" ? ["Id"] : ["Code", "Id"];

        // Act
        var canonical = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(columns, normalizer: null,
            out var ordinals);

        var labels = Enumerable.Range(0, canonical.Count)
            .Select(index => canonical[ordinals?[index] ?? index]).ToArray();

        // Assert
        Assert.Same(columns, canonical);
        Assert.Equal(shape == "unsorted", ordinals is not null);
        Assert.Equal(expected, labels);
    }

    /// <summary>Composite normalization and ordinal sorting are cached once for all rows of each intent.</summary>
    [Fact]
    public void CompositeKeyCanonicalPermutationDoesNotNormalizeOncePerRow()
    {
        // Arrange
        var identity = new ColumnKeyNormalizer(insensitive: true);
        var seed = new EnsureModelManagedDataIntent("Items", ["Id", "Code"], ["int", "int"],
            ["Id", "Code", "Value"], ["int", "int", "int"],
            new object?[,] { { 1, 2, 7 }, { 3, 4, 7 }, { 5, 6, 7 } }, schema: null, uniqueKeys: null);

        var update = new UpdateModelManagedDataIntent("Items", ["CODE", "ID"], ["int", "int"],
            new object?[,] { { 6, 5 }, { 2, 1 }, { 4, 3 } }, ["Value"], ["int"],
            new object?[,] { { 7 }, { 7 }, { 7 } }, new object?[,] { { 8 }, { 8 }, { 8 } },
            schema: null, uniqueKeys: null);

        MigrationOperation[] operations = [Operation(seed), Operation(update)];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity);

        // Assert
        Assert.Equal(4, identity.ColumnNormalizationCalls);
        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Canonical key orders allocate no permutation or comparer, including after garbage collection.</summary>
    /// <param name="singleMember">Whether the input contains only one key member.</param>
    /// <param name="collect">Whether to collect after warming the canonicalization path.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompositeKeyAlreadyOrderedPathAvoidsAllocations(bool singleMember, bool collect)
    {
        // Arrange
        string[] columns = singleMember ? ["Id"] : ["Code", "Id"];
        for (var iteration = 0; iteration < 256; iteration++)
        {
            _ = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(columns, normalizer: null, out _);
        }

        if (collect)
        {
            // WHY: The ordered path must not depend on a weak runtime cache
            // retaining an earlier comparer or permutation allocation.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        IReadOnlyList<string> canonical = columns;
        int[]? ordinals = null;

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 512; iteration++)
        {
            canonical = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(columns, normalizer: null,
                out ordinals);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0L, allocated);
        Assert.Same(columns, canonical);
        Assert.Null(ordinals);
    }

    /// <summary>Creates known composite row evidence without claiming a one-migration repetition exemption.</summary>
    private static EnsureModelManagedDataIntent CompositeEnsure() => new("Items", ["Id", "Code"], ["int", "int"],
        ["Id", "Code", "Value"], ["int", "int", "int"], new object?[,] { { 1, 2, 7 } },
        schema: null, uniqueKeys: null);

    /// <summary>Reorders labels while independently controlling whether their paired values follow.</summary>
    private static UpdateModelManagedDataIntent CompositeUpdate(bool reassociateValues) =>
        new("Items", ["CODE", "ID"], ["int", "int"],
            reassociateValues ? new object?[,] { { 2, 1 } } : new object?[,] { { 1, 2 } }, ["Value"], ["int"],
            new object?[,] { { 7 } }, new object?[,] { { 8 } }, schema: null, uniqueKeys: null);
}
