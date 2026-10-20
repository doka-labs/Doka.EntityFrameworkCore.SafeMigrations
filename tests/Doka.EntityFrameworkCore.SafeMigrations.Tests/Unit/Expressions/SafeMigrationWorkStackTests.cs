namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies pooled traversal storage ownership independently of expression consumers.</summary>
public sealed class SafeMigrationWorkStackTests
{
    [Fact]
    public void EmptyStackDoesNotRentAndDisposalIsIdempotent()
    {
        var pool = new RecordingPool<object>();
        var stack = new SafeMigrationWorkStack<object>(pool);

        var popped = stack.TryPop(out var item);
        stack.Dispose();
        stack.Dispose();

        Assert.False(popped);
        Assert.Null(item);
        Assert.Empty(pool.Rented);
        Assert.Empty(pool.Returned);
    }

    [Fact]
    public void GrowthPreservesOrderAndClearsEveryReturnedReferenceBuffer()
    {
        var pool = new RecordingPool<object>();
        var stack = new SafeMigrationWorkStack<object>(pool);
        var values = Enumerable.Range(0, 65).Select(static _ => new object()).ToArray();

        foreach (var value in values)
        {
            stack.Push(value);
        }

        var actual = new List<object>();
        while (stack.TryPop(out var value))
        {
            actual.Add(value);
        }

        stack.Dispose();
        stack.Dispose();

        Assert.Equal(values.Reverse(), actual);
        Assert.Equal(4, pool.Rented.Count);
        Assert.Equal(pool.Rented, pool.Returned);
        Assert.All(pool.Returned, static buffer => Assert.All(buffer, Assert.Null));
        Assert.All(pool.ClearRequests, Assert.True);
    }

    [Fact]
    public void PopImmediatelyReleasesReferencesIncludingReferencesInsideValueFrames()
    {
        var pool = new RecordingPool<ReferenceFrame>();
        var stack = new SafeMigrationWorkStack<ReferenceFrame>(pool);
        var value = new object();

        stack.Push(new(value));
        var popped = stack.TryPop(out var actual);
        var retained = pool.Rented[0][0].Value;
        stack.Dispose();

        Assert.True(popped);
        Assert.Same(value, actual.Value);
        Assert.Null(retained);
        Assert.True(Assert.Single(pool.ClearRequests));
    }

    [Fact]
    public void ReferenceFreeFramesDoNotRequestUnnecessaryBufferClearing()
    {
        var pool = new RecordingPool<int>();
        var stack = new SafeMigrationWorkStack<int>(pool);

        stack.Push(42);
        var popped = stack.TryPop(out var value);
        stack.Dispose();

        Assert.True(popped);
        Assert.Equal(42, value);
        Assert.False(Assert.Single(pool.ClearRequests));
    }

    [Fact]
    public void DisposingOneStackDoesNotClearAnotherStacksAliasedPayload()
    {
        var pool = new RecordingPool<object>();
        var first = new SafeMigrationWorkStack<object>(pool);
        var second = new SafeMigrationWorkStack<object>(pool);
        var sharedValue = new object();

        first.Push(sharedValue);
        second.Push(sharedValue);
        first.Dispose();
        var popped = second.TryPop(out var actual);
        second.Dispose();

        Assert.True(popped);
        Assert.Same(sharedValue, actual);
        Assert.NotSame(pool.Rented[0], pool.Rented[1]);
        Assert.Equal(2, pool.Returned.Count);
        Assert.All(pool.Returned, static buffer => Assert.All(buffer, Assert.Null));
    }

    [Fact]
    public void ExceptionalExitReturnsPendingReferenceFramesExactlyOnce()
    {
        var pool = new RecordingPool<object>();

        var failure = Assert.Throws<InvalidOperationException>(() => ThrowWithPendingWork(pool));

        Assert.Equal("expected traversal failure", failure.Message);
        Assert.Single(pool.Rented);
        Assert.Single(pool.Returned);
        Assert.All(pool.Returned[0], Assert.Null);
    }

    [Fact]
    public void FailedGrowthRetainsTheOriginalBufferForDisposal()
    {
        var pool = new RecordingPool<object> { FailRentNumber = 2 };

        var failure = Assert.Throws<InvalidOperationException>(() => ExhaustPool(pool));

        Assert.Equal("expected rent failure", failure.Message);
        Assert.Single(pool.Rented);
        Assert.Single(pool.Returned);
        Assert.All(pool.Returned[0], Assert.Null);
    }

    /// <summary>Exercises real exception unwinding without capturing a ref struct in a delegate.</summary>
    private static void ThrowWithPendingWork(RecordingPool<object> pool)
    {
        var stack = new SafeMigrationWorkStack<object>(pool);

        try
        {
            stack.Push(new object());
            throw new InvalidOperationException("expected traversal failure");
        }
        finally
        {
            stack.Dispose();
            stack.Dispose();
        }
    }

    /// <summary>Leaves a full owned buffer pending when the next pool rent fails.</summary>
    private static void ExhaustPool(RecordingPool<object> pool)
    {
        var stack = new SafeMigrationWorkStack<object>(pool);

        try
        {
            for (var index = 0; index < 17; index++)
            {
                stack.Push(new object());
            }
        }
        finally
        {
            stack.Dispose();
        }
    }

    /// <summary>Models a production continuation that holds an expression reference inside a value type.</summary>
    private readonly record struct ReferenceFrame(object? Value);

    /// <summary>Records actual storage and applies the same clear-on-return contract as ArrayPool.</summary>
    private sealed class RecordingPool<T> : ArrayPool<T>
    {
        /// <summary>Gets every successfully rented backing array.</summary>
        public List<T[]> Rented { get; } = [];

        /// <summary>Gets every returned backing array in ownership-release order.</summary>
        public List<T[]> Returned { get; } = [];

        /// <summary>Gets the requested cleanup flags.</summary>
        public List<bool> ClearRequests { get; } = [];

        /// <summary>Gets the optional one-based rent that fails before allocating.</summary>
        public int? FailRentNumber { get; init; }

        /// <inheritdoc />
        public override T[] Rent(int minimumLength)
        {
            if (Rented.Count + 1 == FailRentNumber)
            {
                throw new InvalidOperationException("expected rent failure");
            }

            var buffer = new T[minimumLength];

            Rented.Add(buffer);

            return buffer;
        }

        /// <inheritdoc />
        public override void Return(T[] array, bool clearArray = false)
        {
            Returned.Add(array);
            ClearRequests.Add(clearArray);
            if (clearArray)
            {
                Array.Clear(array);
            }
        }
    }
}
