namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Matches native constraint vectors without reusing an authored or required physical slot.</summary>
internal static class SqliteConstraintShapeCapacity
{
    /// <summary>Proves allowed shape capacity and all untouched required constraint witnesses.</summary>
    /// <param name="actual">The independently captured native constraint vector.</param>
    /// <param name="allowed">The complete authorized transitional shape vector.</param>
    /// <param name="required">The untouched required initial shape vector.</param>
    /// <param name="matches">An exact semantic equality predicate, never a subsumption predicate.</param>
    /// <param name="coalesceRequiredShapes">Whether native equivalent UQ declarations share one autoindex.</param>
    /// <returns>Whether every native and required constraint has an authorized semantic witness.</returns>
    public static bool Matches<TActual, TExpected>(
        IReadOnlyList<TActual> actual,
        IReadOnlyList<TExpected> allowed,
        IReadOnlyList<TExpected> required,
        Func<TActual, TExpected, bool> matches,
        bool coalesceRequiredShapes = false)
    {
        if (actual.Count > allowed.Count || (!coalesceRequiredShapes && required.Count > actual.Count))
        {
            return false;
        }

        if (actual.Count == 0)
        {
            return required.Count == 0;
        }

        var length = Math.Max(allowed.Count, actual.Count);
        byte[]? rented = null;
        var consumed = length <= 256
            ? stackalloc byte[length]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);

        consumed.Clear();
        try
        {
            // WHY: Shape equality partitions both vectors into equivalence
            // classes, so greedy slot consumption is injective without a
            // general graph matcher. Usually-small vectors remain on stack;
            // exceptional large vectors rent bounded synchronous scratch.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < actual.Count; ordinal++)
            {
                var witness = -1;
                // WHY: Retain candidate positions so witness reuse follows the constraint-capacity policy.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var candidate = 0; candidate < allowed.Count; candidate++)
                {
                    if (consumed[candidate] == 0 && matches(actual[ordinal], allowed[candidate]))
                    {
                        witness = candidate;
                        break;
                    }
                }

                if (witness < 0)
                {
                    return false;
                }

                consumed[witness] = 1;
            }

            consumed.Clear();

            // WHY: Retain candidate positions so witness reuse follows the constraint-capacity policy.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < required.Count; ordinal++)
            {
                var witness = -1;
                // WHY: Retain candidate positions so witness reuse follows the constraint-capacity policy.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var candidate = 0; candidate < actual.Count; candidate++)
                {
                    if ((coalesceRequiredShapes || consumed[candidate] == 0)
                        && matches(actual[candidate], required[ordinal]))
                    {
                        witness = candidate;
                        break;
                    }
                }

                if (witness < 0)
                {
                    return false;
                }

                // WHY: SQLite coalesces equivalent UNIQUE declarations onto
                // one physical autoindex. Their required semantic aliases may
                // share that witness; FK/check declarations retain distinct
                // records and therefore require distinct native capacity.
                if (!coalesceRequiredShapes)
                {
                    consumed[witness] = 1;
                }

            }

            return true;
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }
}
