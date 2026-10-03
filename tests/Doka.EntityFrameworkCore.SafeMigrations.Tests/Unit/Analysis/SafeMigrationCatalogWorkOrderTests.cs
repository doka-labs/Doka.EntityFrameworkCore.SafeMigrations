namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies bounded scheduling and strict ownership of sparse catalog results.</summary>
public sealed class SafeMigrationCatalogWorkOrderTests
{
    /// <summary>Resolved gaps do not split live work or renumber operation identities.</summary>
    [Fact]
    public void Create_PacksUnresolvedWorkAcrossGapsWithoutRenumbering()
    {
        var calls = new int[12];

        var ordinals = SafeMigrationCatalogWorkOrder.Create(
            calls.Length,
            ordinal =>
            {
                calls[ordinal]++;

                return ordinal % 3 != 1;
            });

        Assert.Equal([0, 2, 3, 5, 6, 8, 9, 11], ordinals);
        Assert.All(calls, static count => Assert.Equal(1, count));
    }

    /// <summary>Empty and fully resolved captures produce no submitted work.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    public void Create_ReturnsEmptyForResolvedCaptures(
        int count
    )
    {
        var calls = 0;

        var ordinals = SafeMigrationCatalogWorkOrder.Create(
            count,
            _ =>
            {
                calls++;

                return false;
            });

        Assert.Empty(ordinals);
        Assert.Equal(count, calls);
    }

    /// <summary>The largest supported capture preserves every original ordinal.</summary>
    [Fact]
    public void Create_AcceptsTheExactCaptureBound()
    {
        var count = SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture;

        var ordinals = SafeMigrationCatalogWorkOrder.Create(count, static _ => true);

        Assert.Equal(Enumerable.Range(0, count), ordinals);
    }

    /// <summary>Unbounded or invalid captures are rejected before invoking the predicate.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(513)]
    [InlineData(int.MaxValue)]
    public void Create_RejectsInvalidCaptureBoundsBeforeInvokingThePredicate(
        int count
    )
    {
        var calls = 0;

        var exception = Record.Exception(() => SafeMigrationCatalogWorkOrder.Create(count, _ => ++calls > 0));

        Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal(0, calls);
    }

    /// <summary>A missing predicate cannot silently select no work.</summary>
    [Fact]
    public void Create_RejectsANullPredicate()
    {
        var exception = Record.Exception(() => SafeMigrationCatalogWorkOrder.Create(0, null!));

        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>A cancelled capture invokes no predicates, including for an empty capture.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    public void Create_ObservesCancellationBeforeSelection(
        int count
    )
    {
        using var cancellation = new System.Threading.CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;

        var exception = Record.Exception(() => SafeMigrationCatalogWorkOrder.Create(
            count,
            _ => ++calls > 0,
            cancellation.Token));

        Assert.IsType<OperationCanceledException>(exception);
        Assert.Equal(0, calls);
    }

    /// <summary>Cancellation during selection prevents submitting the partially selected batch.</summary>
    [Fact]
    public void Create_ObservesCancellationDuringSelection()
    {
        using var cancellation = new System.Threading.CancellationTokenSource();
        var calls = 0;

        var exception = Record.Exception(() => SafeMigrationCatalogWorkOrder.Create(
            512,
            ordinal =>
            {
                calls++;
                if (ordinal == 7)
                {
                    cancellation.Cancel();
                }

                return true;
            },
            cancellation.Token));

        Assert.IsType<OperationCanceledException>(exception);
        Assert.Equal(8, calls);
    }

    /// <summary>Cancellation by the final predicate cannot publish a fully selected capture.</summary>
    [Fact]
    public void Create_ObservesCancellationAfterTheFinalPredicate()
    {
        using var cancellation = new System.Threading.CancellationTokenSource();
        var calls = 0;

        var exception = Record.Exception(() => SafeMigrationCatalogWorkOrder.Create(
            1,
            _ =>
            {
                calls++;
                cancellation.Cancel();

                return true;
            },
            cancellation.Token));

        Assert.IsType<OperationCanceledException>(exception);
        Assert.Equal(1, calls);
    }

    /// <summary>Identity validation accepts sparse submitted ordinals in their original order.</summary>
    [Fact]
    public void ValidateResultOrdinal_AcceptsTheNextSubmittedIdentity()
    {
        int[] submitted = [512, 514, 519];
        var consumed = 1;

        SafeMigrationCatalogWorkOrder.ValidateResultOrdinal(514, submitted, ref consumed);

        Assert.Equal(2, consumed);
    }

    /// <summary>A short-circuit slot, duplicate, reordered result, or extra row cannot steal evidence.</summary>
    [Theory]
    [InlineData(513, 1)]
    [InlineData(512, 1)]
    [InlineData(519, 1)]
    [InlineData(519, 3)]
    [InlineData(-1, 0)]
    [InlineData(512, -1)]
    public void ValidateResultOrdinal_RejectsUnownedResultsWithoutAdvancing(
        int ordinal,
        int initialConsumed
    )
    {
        int[] submitted = [512, 514, 519];
        var consumed = initialConsumed;

        var exception = Record.Exception(() =>
            SafeMigrationCatalogWorkOrder.ValidateResultOrdinal(ordinal, submitted, ref consumed));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(initialConsumed, consumed);
    }

    /// <summary>Completion requires precisely the number of submitted results.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ValidateCompletion_AcceptsExactCounts(
        int count
    )
    {
        var submitted = Enumerable.Range(0, count).ToArray();

        var exception = Record.Exception(() => SafeMigrationCatalogWorkOrder.ValidateCompletion(count, submitted));

        Assert.Null(exception);
    }

    /// <summary>Missing or extra results fail closed even when their ordinals were individually valid.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void ValidateCompletion_RejectsMissingOrExtraRows(
        int consumed
    )
    {
        int[] submitted = [512, 514, 519];

        var exception = Record.Exception(() => SafeMigrationCatalogWorkOrder.ValidateCompletion(consumed, submitted));

        Assert.IsType<InvalidOperationException>(exception);
    }
}
