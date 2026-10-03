namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Selects bounded catalog work without changing its original result identity.</summary>
internal static class SafeMigrationCatalogWorkOrder
{
    /// <summary>Creates the ascending local ordinals that still require live classification.</summary>
    /// <param name="count">The operation count in one bounded plan capture.</param>
    /// <param name="include">The predicate evaluated exactly once for every local ordinal.</param>
    /// <param name="cancellationToken">The token that cancels selection before further work is submitted.</param>
    /// <returns>The selected original ordinals, without compacting their result identities.</returns>
    public static int[] Create(
        int count,
        Func<int, bool> include,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(include);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            count,
            SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture);

        cancellationToken.ThrowIfCancellationRequested();

        // WHY: A capture is bounded to 512 operations. Stack storage avoids a
        // second managed allocation when most entries are already resolved.
        Span<int> selected = stackalloc int[count];
        var selectedCount = 0;
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (include(ordinal))
            {
                selected[selectedCount++] = ordinal;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        return selected[..selectedCount].ToArray();
    }

    /// <summary>Checks that a returned row belongs to the next submitted operation.</summary>
    /// <param name="ordinal">The original operation ordinal returned by the provider.</param>
    /// <param name="submittedOrdinals">The ordered operation identities owned by this batch.</param>
    /// <param name="consumed">The number of validated rows, incremented only after a valid match.</param>
    /// <exception cref="InvalidOperationException">
    /// The result is duplicated, reordered, extra, or not owned by this batch.
    /// </exception>
    public static void ValidateResultOrdinal(
        int ordinal,
        IReadOnlyList<int> submittedOrdinals,
        ref int consumed
    )
    {
        ArgumentNullException.ThrowIfNull(submittedOrdinals);

        // WHY: A gap can belong to a local short-circuit result, not this
        // database batch. Bounds alone cannot prove result ownership.
        if ((uint)consumed >= (uint)submittedOrdinals.Count
            || ordinal != submittedOrdinals[consumed])
        {
            throw new InvalidOperationException(
                "The SafeMigrations catalog classifier returned an unsubmitted or out-of-order ordinal.");
        }

        consumed++;
    }

    /// <summary>Requires one returned result for every operation submitted in the batch.</summary>
    /// <param name="consumed">The number of rows accepted by <see cref="ValidateResultOrdinal"/>.</param>
    /// <param name="submittedOrdinals">The operation identities owned by this batch.</param>
    /// <exception cref="InvalidOperationException">The provider omitted or added classifier results.</exception>
    public static void ValidateCompletion(
        int consumed,
        IReadOnlyList<int> submittedOrdinals
    )
    {
        ArgumentNullException.ThrowIfNull(submittedOrdinals);
        if (consumed != submittedOrdinals.Count)
        {
            throw new InvalidOperationException(
                "The SafeMigrations catalog classifier returned an inconsistent submitted row count.");
        }
    }
}
