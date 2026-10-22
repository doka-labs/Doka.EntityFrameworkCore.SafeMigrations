namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>
/// Compares SQLite identifiers with the engine's ASCII-only case folding.
/// </summary>
internal sealed class SqliteIdentifierComparer : StringComparer
{
    /// <summary>Gets the shared SQLite identifier comparer.</summary>
    public static SqliteIdentifierComparer Instance { get; } = new();

    private SqliteIdentifierComparer() { }

    /// <inheritdoc />
    public override int Compare(
        string? left,
        string? right
    )
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var commonLength = Math.Min(left.Length, right.Length);

        // WHY: Compare ordered identifier members at matching offsets to preserve native identity semantics.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < commonLength; index++)
        {
            var difference = FoldAscii(left[index]) - FoldAscii(right[index]);
            if (difference != 0)
            {
                return difference;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <inheritdoc />
    public override bool Equals(
        string? left,
        string? right
    )
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null
            || right is null
            || left.Length != right.Length)
        {
            return false;
        }

        // WHY: Compare ordered identifier members at matching offsets to preserve native identity semantics.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < left.Length; index++)
        {
            if (FoldAscii(left[index]) != FoldAscii(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode(
        string value
    )
    {
        ArgumentNullException.ThrowIfNull(value);

        var hash = new HashCode();
        foreach (var character in value)
        {
            hash.Add(FoldAscii(character));
        }

        return hash.ToHashCode();
    }

    /// <summary>Normalizes the ASCII case of an identifier without changing non-ASCII characters.</summary>
    /// <param name="value">The identifier to normalize.</param>
    /// <returns>The normalized identifier.</returns>
    public static string Normalize(
        string value
    )
    {
        ArgumentNullException.ThrowIfNull(value);

        var firstUppercase = -1;

        // WHY: Preserve exact UTF-16 positions when folding and copying identifier characters.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is >= 'A' and <= 'Z')
            {
                firstUppercase = index;
                break;
            }
        }

        if (firstUppercase < 0)
        {
            return value;
        }

        return string.Create(
            value.Length,
            value,
            static (destination, source) =>
            {
                // WHY: Preserve exact UTF-16 positions when folding and copying identifier characters.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < source.Length; index++)
                {
                    destination[index] = FoldAscii(source[index]);
                }
            });
    }

    /// <summary>Folds an ASCII uppercase character to lowercase.</summary>
    /// <param name="value">The character to fold.</param>
    /// <returns>The folded character.</returns>
    public static char FoldAscii(
        char value
    ) => value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;
}
