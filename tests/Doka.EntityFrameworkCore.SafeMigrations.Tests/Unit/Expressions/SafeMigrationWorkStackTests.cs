namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies pooled traversal storage ownership independently of expression consumers.</summary>
public sealed class SafeMigrationWorkStackTests
{
    /// <summary>An unused stack never rents storage, and repeated disposal has no ownership effect.</summary>
    [Fact]
    public void EmptyStackDoesNotRentAndDisposalIsIdempotent()
    {
        // Arrange
        var pool = new RecordingPool<object>();
        var stack = new SafeMigrationWorkStack<object>(pool);

        // Act
        var popped = stack.TryPop(out var item);
        stack.Dispose();
        stack.Dispose();

        // Assert
        Assert.False(popped);
        Assert.Null(item);
        Assert.Empty(pool.Rented);
        Assert.Empty(pool.Returned);
    }

    /// <summary>Growth preserves LIFO order and releases every reference buffer exactly once with clearing.</summary>
    [Fact]
    public void GrowthPreservesOrderAndClearsEveryReturnedReferenceBuffer()
    {
        // Arrange
        var pool = new RecordingPool<object>();
        var stack = new SafeMigrationWorkStack<object>(pool);
        var values = Enumerable
            .Range(0, 65)
            .Select(static _ => new object())
            .ToArray();

        var actual = new List<object>();

        // Act
        foreach (var value in values)
        {
            stack.Push(value);
        }

        while (stack.TryPop(out var value))
        {
            actual.Add(value);
        }

        stack.Dispose();
        stack.Dispose();

        // Assert
        Assert.Equal(values.Reverse(), actual);
        Assert.Equal(4, pool.Rented.Count);
        Assert.Equal(pool.Rented, pool.Returned);
        Assert.All(pool.Returned, static buffer => Assert.All(buffer, Assert.Null));
        Assert.All(pool.ClearRequests, Assert.True);
    }

    /// <summary>Popping a value frame clears its backing-slot reference before the buffer is returned.</summary>
    [Fact]
    public void PopImmediatelyReleasesReferencesIncludingReferencesInsideValueFrames()
    {
        // Arrange
        var pool = new RecordingPool<ReferenceFrame>();
        var stack = new SafeMigrationWorkStack<ReferenceFrame>(pool);
        var value = new object();

        // Act
        stack.Push(new ReferenceFrame(value));
        var popped = stack.TryPop(out var actual);
        var retained = pool
            .Rented[0][0].Value;

        stack.Dispose();

        // Assert
        Assert.True(popped);
        Assert.Same(value, actual.Value);
        Assert.Null(retained);
        Assert.True(Assert.Single(pool.ClearRequests));
    }

    /// <summary>Reference-free frames return their buffer without requesting unnecessary clearing.</summary>
    [Fact]
    public void ReferenceFreeFramesDoNotRequestUnnecessaryBufferClearing()
    {
        // Arrange
        var pool = new RecordingPool<int>();
        var stack = new SafeMigrationWorkStack<int>(pool);

        // Act
        stack.Push(42);
        var popped = stack.TryPop(out var value);
        stack.Dispose();

        // Assert
        Assert.True(popped);
        Assert.Equal(42, value);
        Assert.False(Assert.Single(pool.ClearRequests));
    }

    /// <summary>Releasing one stack cannot clear another owner's buffer containing the same payload.</summary>
    [Fact]
    public void DisposingOneStackDoesNotClearAnotherStacksAliasedPayload()
    {
        // Arrange
        var pool = new RecordingPool<object>();
        var first = new SafeMigrationWorkStack<object>(pool);
        var second = new SafeMigrationWorkStack<object>(pool);
        var sharedValue = new object();

        // Act
        first.Push(sharedValue);
        second.Push(sharedValue);
        first.Dispose();
        var popped = second.TryPop(out var actual);
        second.Dispose();

        // Assert
        Assert.True(popped);
        Assert.Same(sharedValue, actual);
        Assert.NotSame(pool.Rented[0], pool.Rented[1]);
        Assert.Equal(2, pool.Returned.Count);
        Assert.All(pool.Returned, static buffer => Assert.All(buffer, Assert.Null));
    }

    /// <summary>Exception unwinding returns and clears pending storage despite repeated cleanup.</summary>
    [Fact]
    public void ExceptionalExitReturnsPendingReferenceFramesExactlyOnce()
    {
        // Arrange
        var pool = new RecordingPool<object>();

        // Act
        var failure = Record.Exception(() => ThrowWithPendingWork(pool));

        // Assert
        Assert.Equal("expected traversal failure",
            Assert.IsType<InvalidOperationException>(failure)
                .Message);

        Assert.Single(pool.Rented);
        Assert.Single(pool.Returned);
        Assert.All(pool.Returned[0], Assert.Null);
    }

    /// <summary>A failed replacement rent leaves the original buffer owned until deterministic disposal.</summary>
    [Fact]
    public void FailedGrowthRetainsTheOriginalBufferForDisposal()
    {
        // Arrange
        var pool = new RecordingPool<object> { FailRentNumber = 2 };

        // Act
        var failure = Record.Exception(() => ExhaustPool(pool));

        // Assert
        Assert.Equal("expected rent failure",
            Assert.IsType<InvalidOperationException>(failure)
                .Message);

        Assert.Single(pool.Rented);
        Assert.Single(pool.Returned);
        Assert.All(pool.Returned[0], Assert.Null);
    }

    /// <summary>Exercises real exception unwinding without capturing a ref struct in a delegate.</summary>
    private static void ThrowWithPendingWork(
        RecordingPool<object> pool
    )
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
    private static void ExhaustPool(
        RecordingPool<object> pool
    )
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
        public override T[] Rent(
            int minimumLength
        )
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
        public override void Return(
            T[] array,
            bool clearArray = false
        )
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
