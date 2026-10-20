namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Holds pending traversal work without consuming the process call stack.</summary>
/// <typeparam name="T">The traversal frame type.</typeparam>
/// <remarks>
/// Keep an owning stack scoped to one traversal and pass it by reference rather than copying ownership.
/// </remarks>
internal ref struct SafeMigrationWorkStack<T>
{
    private readonly ArrayPool<T>? _pool;
    private T[]? _items;
    private int _count;

    /// <summary>Uses a supplied pool for deterministic ownership and cleanup verification.</summary>
    /// <param name="pool">The pool that owns every rented buffer.</param>
    internal SafeMigrationWorkStack(
        ArrayPool<T> pool
    )
    {
        ArgumentNullException.ThrowIfNull(pool);

        _pool = pool;
    }

    /// <summary>Adds pending work, renting only when traversal actually branches.</summary>
    /// <param name="item">The pending traversal frame.</param>
    public void Push(
        T item
    )
    {
        if (_items is null
            || _count == _items.Length)
        {
            Grow();
        }

        _items![_count++] = item;
    }

    /// <summary>Removes the most recently added frame and releases its stored references immediately.</summary>
    /// <param name="item">The removed frame, or its default value when no work remains.</param>
    /// <returns>Whether a frame was available.</returns>
    public bool TryPop(
        [MaybeNullWhen(false)] out T item
    )
    {
        if (_count == 0)
        {
            item = default;

            return false;
        }

        item = _items![--_count];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _items[_count] = default!;
        }

        return true;
    }

    /// <summary>Returns the owned buffer once, including when traversal exits early or throws.</summary>
    public void Dispose()
    {
        var items = _items;

        _items = null;
        _count = 0;
        if (items is not null)
        {
            Pool.Return(items, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }

    /// <summary>Gets the shared pool without requiring an initialized stack or a default-path allocation.</summary>
    private readonly ArrayPool<T> Pool => _pool ?? ArrayPool<T>.Shared;

    /// <summary>Grows geometrically so an arbitrarily deep traversal remains linear in work and storage.</summary>
    private void Grow()
    {
        var previous = _items;
        var capacity = previous is null ? 16 : checked(previous.Length * 2);
        var replacement = Pool.Rent(capacity);

        if (previous is not null)
        {
            previous
                .AsSpan(0, _count)
                .CopyTo(replacement);
            Pool.Return(previous, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }

        _items = replacement;
    }
}
