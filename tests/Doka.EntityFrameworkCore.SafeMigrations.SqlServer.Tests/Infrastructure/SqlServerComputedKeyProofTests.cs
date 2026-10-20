namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies that native computed-index facts cannot outlive their exact operand lineage.</summary>
public sealed class SqlServerComputedKeyProofTests
{
    /// <summary>Native type and precision evidence cannot escape its qualified owner.</summary>
    /// <param name="table">The requested physical owner.</param>
    /// <param name="schema">The requested schema, including the implicit dbo spelling.</param>
    /// <param name="expected">Whether the native proof belongs to that exact owner.</param>
    [Theory]
    [InlineData("nodes", null, true)]
    [InlineData("nodes", "dbo", true)]
    [InlineData("other_nodes", null, false)]
    [InlineData("nodes", "other_schema", false)]
    [InlineData("Nodes", null, false)]
    public void NativeProofRequiresItsQualifiedOwner(string table, string? schema, bool expected)
    {
        // Arrange
        var shape = new SqlServerProjectedKeyColumn("bit", 1, true, true, true, null, true);
        var proof = new SqlServerProjectedComputedKeyProof(shape,
            new Dictionary<string, ExpectedColumnDefinition?> { ["L"] = null }, "nodes")
        {
            LiveOperandIdentities = new Dictionary<string, int> { ["L"] = 7 },
            CurrentColumnBindings = new Dictionary<string, int> { ["L"] = 7 },
        };

        var source = new Source(null, recreated: false);

        // Act
        var matches = proof.Matches(source, table, schema);

        // Assert
        Assert.Equal(expected, matches);
    }

    /// <summary>Shared definitions and identical table-local ids do not lend another owner's precision.</summary>
    [Fact]
    public void ReusedDefinitionRetainsIndependentNativePrecisionPerOwner()
    {
        // Arrange
        var shape = new SqlServerProjectedKeyColumn("bit", 1, true, true, true, null, true);
        var precise = new SqlServerProjectedComputedKeyProof(shape,
            new Dictionary<string, ExpectedColumnDefinition?> { ["L"] = null }, "precise_nodes")
        {
            LiveOperandIdentities = new Dictionary<string, int> { ["L"] = 7 },
            CurrentColumnBindings = new Dictionary<string, int> { ["L"] = 7 },
        };

        var imprecise = precise with { Table = "imprecise_nodes", Shape = shape with { KeyEligible = false, }, };
        SqlServerProjectedComputedKeyProof[] captured = [precise, imprecise];
        var source = new Source(null, recreated: false);

        // Act
        var bound = captured.FirstOrDefault(proof => proof.Matches(source, "imprecise_nodes", null));

        // Assert
        Assert.True(precise.Matches(source, "precise_nodes", null));
        Assert.NotNull(bound);
        Assert.Same(imprecise, bound);
        Assert.False(bound.Shape.KeyEligible);
    }

    /// <summary>A replacement operand must not inherit the old expression's type and precision certificate.</summary>
    /// <param name="sameBinding">Whether the projected operand is the original immutable definition.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AuthoredProofRequiresIdenticalOperandBindings(bool sameBinding)
    {
        // Arrange
        var original = new ExpectedColumnDefinition("L", typeof(long), false, "bigint");
        var replacement = new ExpectedColumnDefinition("L", typeof(long), true, "bigint");
        var shape = new SqlServerProjectedKeyColumn("bit", 1, true, true, true, null, true);
        var proof = new SqlServerProjectedComputedKeyProof(shape,
            new Dictionary<string, ExpectedColumnDefinition?> { ["L"] = original }, "nodes");

        var source = new Source(sameBinding ? original : replacement, recreated: false);

        // Act
        var matches = proof.Matches(source, "nodes", null);

        // Assert
        Assert.Equal(sameBinding, matches);
    }

    /// <summary>Live metadata cannot certify a recreated owner or a newly authored operand.</summary>
    /// <param name="recreated">Whether an accepted operation recreated the owner.</param>
    /// <param name="projectedOperand">Whether an accepted operation supplied a different operand.</param>
    /// <param name="expected">Whether the captured immutable live operand remains applicable.</param>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void LiveProofDoesNotSurviveOwnerOrOperandReplacement(
        bool recreated,
        bool projectedOperand,
        bool expected
    )
    {
        // Arrange
        var shape = new SqlServerProjectedKeyColumn("bit", 1, true, true, true, null, true);
        var proof = new SqlServerProjectedComputedKeyProof(shape,
            new Dictionary<string, ExpectedColumnDefinition?> { ["L"] = null }, "nodes");

        var source = new Source(projectedOperand
            ? new ExpectedColumnDefinition("L", typeof(double), true, "float") : null, recreated);

        // Act
        var matches = proof.Matches(source, "nodes", null);

        // Assert
        Assert.Equal(expected, matches);
    }

    /// <summary>Missing projected metadata cannot resurrect a live operand after structural uncertainty.</summary>
    /// <param name="unknown">Whether an accepted operation invalidated the owner shape.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveProofRequiresKnownOwnerWhenNoProjectedOperandExists(bool unknown)
    {
        // Arrange
        var shape = new SqlServerProjectedKeyColumn("bit", 1, true, true, true, null, true);
        var proof = new SqlServerProjectedComputedKeyProof(shape,
            new Dictionary<string, ExpectedColumnDefinition?> { ["L"] = null }, "nodes");

        var source = new Source(null, recreated: false, unknown);

        // Act
        var matches = proof.Matches(source, "nodes", null);

        // Assert
        Assert.Equal(!unknown, matches);
    }

    /// <summary>Native physical identity survives sibling changes but not removal or same-name replacement.</summary>
    /// <param name="currentIdentity">The current physical identity, or zero after removal.</param>
    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8)]
    public void CapturedPhysicalSourceRequiresItsExactRemainingIdentity(int currentIdentity)
    {
        // Arrange
        var current = new Dictionary<string, int>(StringComparer.Ordinal);
        if (currentIdentity != 0)
        {
            current.Add("L", currentIdentity);
        }

        var shape = new SqlServerProjectedKeyColumn("bit", 1, true, true, true, null, true);
        var proof = new SqlServerProjectedComputedKeyProof(shape,
            new Dictionary<string, ExpectedColumnDefinition?> { ["L"] = null }, "nodes")
        {
            LiveOperandIdentities = new Dictionary<string, int> { ["L"] = 7 },
            CurrentColumnBindings = current,
        };

        var source = new Source(null, recreated: false, unknown: true);

        // Act
        var matches = proof.Matches(source, "nodes", null);

        // Assert
        Assert.Equal(currentIdentity == 7, matches);
    }

    /// <summary>Independent rename copies require an explicit lineage marker and complete facet equality.</summary>
    /// <param name="renamed">Whether native capture observed the preceding authored rename.</param>
    /// <param name="changed">Whether the accepted copy has a different nullability facet.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RenamedProofRequiresExplicitLineageAndIdenticalFacets(bool renamed, bool changed)
    {
        // Arrange
        var native = new ExpectedColumnDefinition("L", typeof(long), false, "bigint");
        var accepted = new ExpectedColumnDefinition("L", typeof(long), changed, "bigint");
        var shape = new SqlServerProjectedKeyColumn("bit", 1, true, true, true, null, true);
        var proof = new SqlServerProjectedComputedKeyProof(shape,
            new Dictionary<string, ExpectedColumnDefinition?> { ["L"] = native }, "nodes")
        {
            RenamedOperandDefinitions = renamed ? new HashSet<ExpectedColumnDefinition> { native } : null,
        };

        var source = new Source(accepted, recreated: true);

        // Act
        var matches = proof.Matches(source, "nodes", null);

        // Assert
        Assert.Equal(renamed && !changed, matches);
    }

    /// <summary>A compiler failure preserves the declared mapping but never invents index eligibility.</summary>
    /// <param name="nativeProof">The bounded native result: rejected, non-null, or nullable result.</param>
    /// <param name="keyEligible">Whether the native result proves an eligible computed key operand.</param>
    /// <param name="includeEligible">Whether the native result proves an eligible computed INCLUDE operand.</param>
    /// <param name="nullable">Whether the compiler reported physically nullable generated metadata.</param>
    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(1, true, true, false)]
    [InlineData(2, true, true, true)]
    [InlineData(3, false, false, false)]
    [InlineData(4, false, false, true)]
    [InlineData(5, false, true, false)]
    [InlineData(6, false, true, true)]
    public void NativeProofOwnsEligibilityAndPhysicalNullability(
        int nativeProof,
        bool keyEligible,
        bool includeEligible,
        bool nullable
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1433;Database=proof;User ID=sa;Password=unused;TrustServerCertificate=True");

        var definition = SqlServerGeneratedColumnSqlTests.Flag();

        // Act
        var shape = SqlServerProjectedKeyShape.CreateComputedColumnProof(definition,
            context.GetService<IRelationalTypeMappingSource>(), nativeProof);

        // Assert
        Assert.NotNull(shape);
        Assert.Equal(keyEligible, shape.KeyEligible);
        Assert.Equal(includeEligible, shape.IncludeEligible);
        Assert.Equal(nullable, shape.IsNullable);
        Assert.Equal(1, shape.DeclaredBytes);
    }

    private sealed class Source(ExpectedColumnDefinition? definition, bool recreated, bool unknown = false)
        : ISafeMigrationProjectedColumnSource, ISafeMigrationProjectedTableSource
    {
        public bool TryGetProjectedColumn(string table, string? schema, string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? result)
        {
            result = definition?.Name == column ? definition : null;

            return result is not null;
        }

        public bool TryGetProjectedTableState(string table, string? schema, out SafeMigrationProjectedTableState result)
        {
            result = new SafeMigrationProjectedTableState(recreated, false, false, unknown);

            return true;
        }
    }
}
