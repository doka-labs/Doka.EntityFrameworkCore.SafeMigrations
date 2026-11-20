namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Pins the actual measured deep-tree fixture without enforcing numeric performance budgets.</summary>
public sealed class CoreDeepEqualityBenchmarkTests
{
    /// <summary>The producer retains the former depth and independent inputs for both native cases.</summary>
    [Fact]
    public void PreparedTreesRetainEveryBranchAndTheirIndependentFinalLeaf()
    {
        // Arrange
        const int firstValue = 1;
        const int secondValue = 2;

        // Act
        var first = CoreDeepEqualityBenchmarks.CreateExpression(firstValue);
        var second = CoreDeepEqualityBenchmarks.CreateExpression(secondValue);

        // Assert
        Assert.NotSame(first, second);
        Assert.Equal(firstValue, ReadLeaf(first));
        Assert.Equal(secondValue, ReadLeaf(second));
    }

    /// <summary>Repeated actual workload calls preserve matching and late-mismatch results.</summary>
    /// <param name="equivalent">Whether the selected fixture has matching final leaves.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MeasuredMethodPreservesItsSelectedFunctionalResult(
        bool equivalent
    )
    {
        // Arrange
        var benchmark = new CoreDeepEqualityBenchmarks { EquivalentTrees = equivalent };
        benchmark.Setup();

        // Act
        var first = benchmark.CompareDeepTrees();
        var repeated = benchmark.CompareDeepTrees();
        var cleanupFailure = Record.Exception(benchmark.VerifyWorkload);

        // Assert
        Assert.Equal(equivalent, first);
        Assert.Equal(equivalent, repeated);
        Assert.Null(cleanupFailure);
    }

    /// <summary>Uninitialized fixtures cannot pass the unmeasured semantic guard.</summary>
    /// <param name="equivalent">The selected case before any trees have been prepared.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingSetupIsRejected(
        bool equivalent
    )
    {
        // Arrange
        var benchmark = new CoreDeepEqualityBenchmarks { EquivalentTrees = equivalent };

        // Act
        var failure = Record.Exception(benchmark.VerifyWorkload);

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>The semantic guard detects a case that no longer describes its prepared trees.</summary>
    /// <param name="equivalent">The case used when the trees were prepared.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedCaseCannotSilentlyMeasureTheFormerWorkload(
        bool equivalent
    )
    {
        // Arrange
        var benchmark = new CoreDeepEqualityBenchmarks { EquivalentTrees = equivalent };
        benchmark.Setup();
        benchmark.EquivalentTrees = !equivalent;

        // Act
        var failure = Record.Exception(benchmark.VerifyWorkload);

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Native discovery retains both real workloads and their unmeasured setup and cleanup.</summary>
    [Fact]
    public void BenchmarkDotNetDiscoversBothDeepTraversalCases()
    {
        // Arrange
        var fixture = typeof(CoreDeepEqualityBenchmarks);

        // Act
        var cases = BenchmarkConverter.TypeToBenchmarks(fixture, DefaultConfig.Instance)
            .BenchmarksCases;

        // Assert
        Assert.Equal(2, cases.Length);
        Assert.All(cases,
            benchmark =>
            {
                Assert.Equal(nameof(CoreDeepEqualityBenchmarks.CompareDeepTrees),
                    benchmark.Descriptor.WorkloadMethod.Name);

                Assert.Equal(nameof(CoreDeepEqualityBenchmarks.Setup), benchmark.Descriptor.GlobalSetupMethod?.Name);
                Assert.Equal(
                    nameof(CoreDeepEqualityBenchmarks.VerifyWorkload),
                    benchmark.Descriptor.GlobalCleanupMethod?.Name);
            });

        Assert.Equal(
            new object[] { false, true },
            cases
                .Select(benchmark => Assert.Single(benchmark.Parameters.Items)
                    .Value)
                .OrderBy(value => value));
    }

    /// <summary>Reads the exact fixed-depth shape with an oracle independent of the production comparer.</summary>
    /// <param name="expression">The actual immutable benchmark tree.</param>
    /// <returns>The final integer leaf after every expected addition branch has been checked.</returns>
    private static int ReadLeaf(
        SafeMigrationSqlExpression expression
    )
    {
        for (var depth = 0; depth < 1_024; depth++)
        {
            var branch = Assert.IsType<SafeMigrationSqlBinaryExpression>(expression);
            Assert.Equal(SafeMigrationSqlBinaryOperator.Add, branch.Operator);
            Assert.Equal(0,
                Assert.IsType<SafeMigrationSqlLiteralExpression>(branch.Right)
                    .Value);

            expression = branch.Left;
        }

        return Assert.IsType<int>(Assert.IsType<SafeMigrationSqlLiteralExpression>(expression)
            .Value);
    }
}
