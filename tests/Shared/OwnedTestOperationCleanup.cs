using System.Runtime.ExceptionServices;

namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Finishes borrowed asynchronous test work before its caller releases the work's owners.</summary>
internal static class OwnedTestOperationCleanup
{
    /// <summary>Releases an independent barrier, requests owned cancellation and observes all pending work.</summary>
    /// <param name="primaryFailure">The original failure already being rethrown by the caller, or null.</param>
    /// <param name="pendingWork">The actual operation, not a timeout or cancellation wrapper around it.</param>
    /// <param name="releaseBarrier">
    /// An optional independently executable unblocker. It must not use the connection still executing pending work.
    /// </param>
    /// <param name="cancellationSource">
    /// The caller-owned cancellation source, or null when work needs no cancellation.
    /// </param>
    /// <param name="afterDrain">
    /// Optional final cleanup that may use the operation's session only after its actual work has ended.
    /// </param>
    /// <returns>Completion only after all work is observed; owners remain the caller's responsibility.</returns>
    /// <exception cref="Exception">One new cleanup failure is rethrown without replacing its identity.</exception>
    /// <exception cref="AggregateException">Additional failures retain the original primary failure first.</exception>
    internal static async Task CompleteAsync(
        Exception? primaryFailure,
        Task? pendingWork,
        Func<Task>? releaseBarrier,
        CancellationTokenSource? cancellationSource,
        Func<Task>? afterDrain = null
    )
    {
        List<Exception>? cleanupFailures = null;
        var primaryLeaves = primaryFailure is AggregateException
            ? GetFailureLeaves(primaryFailure)
            : null;

        Task? releaseWork = null;
        try
        {
            if (releaseBarrier is not null)
            {
                releaseWork = releaseBarrier();
                await releaseWork.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            RecordTaskFailures(releaseWork, exception, primaryFailure, primaryLeaves, ref cleanupFailures);
        }

        CancellationToken? ownedToken = null;
        Task? cancellationWork = null;
        try
        {
            if (pendingWork is { IsCompleted: false }
                && cancellationSource is not null)
            {
                ownedToken = cancellationSource.Token;
                cancellationWork = cancellationSource.CancelAsync();
                await cancellationWork.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // WHY: A release or cancellation callback can throw without stopping
            // borrowed work. Its failure must not bypass the independent drain.
            // An unavailable token cannot certify cancellation; do not retry its getter for each fault.
            ownedToken ??= CancellationToken.None;
            RecordTaskFailures(cancellationWork, exception, primaryFailure, primaryLeaves, ref cleanupFailures);
        }

        if (pendingWork is not null)
        {
            try
            {
                // WHY: Waiting for an observer with a timeout does not complete
                // the operation it observes. Owners must survive the real task,
                // including a task that faulted before cleanup began.
                await pendingWork.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var pendingFailures = GetFailureLeaves(pendingWork.Exception ?? exception);

                // WHY: Every actual fault is observed in authored leaf order, including empty aggregates.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < pendingFailures.Count; index++)
                {
                    RecordPendingFailure(
                        pendingFailures[index],
                        pendingWork,
                        primaryFailure,
                        primaryLeaves,
                        cancellationSource,
                        ref ownedToken,
                        ref cleanupFailures);
                }
            }
        }

        Task? finalCleanup = null;
        try
        {
            if (afterDrain is not null)
            {
                // WHY: Same-session cleanup cannot overlap its borrowed command,
                // but prior release, cancellation or execution faults must not skip it.
                finalCleanup = afterDrain();
                await finalCleanup.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            RecordTaskFailures(finalCleanup, exception, primaryFailure, primaryLeaves, ref cleanupFailures);
        }

        if (cleanupFailures is null)
        {
            return;
        }

        if (primaryFailure is null
            && cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo
                .Capture(cleanupFailures[0])
                .Throw();
        }

        if (primaryFailure is not null)
        {
            cleanupFailures.Insert(0, primaryFailure);
        }

        throw new AggregateException("Owned test operation cleanup failed.", cleanupFailures);
    }

    /// <summary>Retains every fault from an awaited composite task instead of only its first await exception.</summary>
    private static void RecordTaskFailures(
        Task? task,
        Exception observedFailure,
        Exception? primaryFailure,
        List<Exception>? primaryLeaves,
        ref List<Exception>? cleanupFailures
    )
    {
        var failures = GetFailureLeaves(task?.Exception ?? observedFailure);

        // WHY: Keep the same leaf order for synchronously thrown and task-retained failures.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < failures.Count; index++)
        {
            RecordFailure(failures[index], primaryFailure, primaryLeaves, ref cleanupFailures);
        }
    }

    /// <summary>Retains ordinary faults and empty aggregates in authored depth-first sibling order.</summary>
    /// <param name="failure">The actual observed exception or task-retained aggregate root.</param>
    /// <returns>The original exception objects without recursive traversal or a depth cutoff.</returns>
    private static List<Exception> GetFailureLeaves(
        Exception failure
    )
    {
        if (failure is not AggregateException { InnerExceptions.Count: > 0 })
        {
            return [failure];
        }

        List<Exception> leaves = [];
        var pending = new Stack<Exception>();
        pending.Push(failure);

        while (pending.TryPop(out var current))
        {
            if (current is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            {
                // WHY: Flatten drops empty aggregates and changes nested sibling order.
                // Reverse push preserves authored depth-first order without recursion.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else
            {
                leaves.Add(current);
            }
        }

        return leaves;
    }

    /// <summary>Distinguishes reobserved failures and proved owned cancellation from unrelated cancellation.</summary>
    private static void RecordPendingFailure(
        Exception failure,
        Task pendingWork,
        Exception? primaryFailure,
        List<Exception>? primaryLeaves,
        CancellationTokenSource? cancellationSource,
        ref CancellationToken? ownedToken,
        ref List<Exception>? cleanupFailures
    )
    {
        if (failure is TaskCanceledException pendingCancellation
            && ReferenceEquals(pendingCancellation.Task, pendingWork)
            && ((primaryFailure is TaskCanceledException primaryCancellation
                    && ReferenceEquals(primaryCancellation.Task, pendingWork))
                || ContainsCanceledTask(primaryLeaves, pendingWork)))
        {
            return;
        }

        if (failure is OperationCanceledException cancellation
            && cancellationSource is not null)
        {
            try
            {
                ownedToken ??= cancellationSource.Token;
                if (ownedToken.Value.IsCancellationRequested
                    && cancellation.CancellationToken == ownedToken.Value)
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                ownedToken ??= CancellationToken.None;
                RecordFailure(exception, primaryFailure, primaryLeaves, ref cleanupFailures);
            }
        }

        RecordFailure(failure, primaryFailure, primaryLeaves, ref cleanupFailures);
    }

    /// <summary>Deduplicates errors by reference while preserving the original primary object.</summary>
    private static void RecordFailure(
        Exception failure,
        Exception? primaryFailure,
        List<Exception>? primaryLeaves,
        ref List<Exception>? cleanupFailures
    )
    {
        if (ReferenceEquals(failure, primaryFailure)
            || ContainsReference(primaryLeaves, failure)
            || ContainsReference(cleanupFailures, failure))
        {
            return;
        }

        (cleanupFailures ??= []).Add(failure);
    }

    /// <summary>Checks identity without invoking an exception type's possibly overridden equality.</summary>
    private static bool ContainsReference(
        List<Exception>? failures,
        Exception failure
    )
    {
        if (failures is null)
        {
            return false;
        }

        // WHY: Preserve reference identity and stop at the first duplicate without a delegate or hash-set allocation.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < failures.Count; index++)
        {
            if (ReferenceEquals(failures[index], failure))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Recognizes a canceled task already retained by the caller's primary aggregate.</summary>
    private static bool ContainsCanceledTask(
        List<Exception>? failures,
        Task pendingWork
    )
    {
        if (failures is null)
        {
            return false;
        }

        // WHY: A canceled task may create a fresh await exception each time; its task identity stays stable.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < failures.Count; index++)
        {
            if (failures[index] is TaskCanceledException cancellation
                && ReferenceEquals(cancellation.Task, pendingWork))
            {
                return true;
            }
        }

        return false;
    }
}
