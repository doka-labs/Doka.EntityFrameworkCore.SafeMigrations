namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Canonicalizes typed key labels once without changing row values or physical owner identity.</summary>
internal static class SafeMigrationModelManagedKeyIdentity
{
    /// <summary>Captures normalized labels and an optional canonical-to-authored ordinal permutation once.</summary>
    /// <param name="columns">The immutable authored key-column sequence.</param>
    /// <param name="normalizer">The provider's certified column identity, or null for exact authored identity.</param>
    /// <param name="canonicalOrdinals">Null for ordered labels; otherwise canonical-to-authored ordinals.</param>
    /// <returns>Normalized labels in authored order; values are always read through the matching permutation.</returns>
    public static IReadOnlyList<string> CanonicalizeColumns(
        IReadOnlyList<string> columns,
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        out int[]? canonicalOrdinals
    )
    {
        var normalized = NormalizeColumns(columns, normalizer);
        canonicalOrdinals = null;
        var sorted = true;
        // WHY: Adjacent labels determine whether the authored key order already is canonical.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 1; index < normalized.Count; index++)
        {
            if (StringComparer.Ordinal.Compare(normalized[index - 1], normalized[index]) > 0)
            {
                sorted = false;

                break;
            }
        }

        if (sorted)
        {
            return normalized;
        }

        // WHY: A row predicate is an AND of column/value pairs, so its key
        // order is not physical identity. This permutation never normalizes
        // ordered INDEX/FK definitions, nor copies or sorts the row matrices.
        canonicalOrdinals = new int[normalized.Count];
        // WHY: Seed the authored ordinal permutation before sorting labels without moving row values.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < canonicalOrdinals.Length; index++)
        {
            canonicalOrdinals[index] = index;
        }

        canonicalOrdinals.AsSpan().Sort(new KeyOrdinalComparer(normalized));

        return normalized;
    }

    /// <summary>Reuses the immutable input unless at least one canonical column spelling differs.</summary>
    /// <param name="columns">The immutable authored key-column sequence.</param>
    /// <param name="normalizer">The provider's separately certified column identity.</param>
    /// <returns>The original list or one owned canonical array, never a per-row normalization.</returns>
    public static IReadOnlyList<string> NormalizeColumns(
        IReadOnlyList<string> columns,
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer
    )
    {
        if (normalizer is null)
        {
            return columns;
        }

        string[]? normalized = null;
        // WHY: Keep canonical labels aligned with authored ordinals while allocating only after a change.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < columns.Count; index++)
        {
            var canonical = normalizer.NormalizeColumnIdentifier(columns[index]);
            if (normalized is null && !StringComparer.Ordinal.Equals(columns[index], canonical))
            {
                // WHY: Most providers preserve authored spelling. An owned
                // array is needed only after an actual canonicalization change.
                normalized = new string[columns.Count];
                // WHY: Copy only the unchanged prefix when the first normalized label differs.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var prior = 0; prior < index; prior++)
                {
                    normalized[prior] = columns[prior];
                }
            }

            if (normalized is not null)
            {
                normalized[index] = canonical;
            }
        }

        return normalized ?? columns;
    }

    /// <summary>Canonicalizes OrdinalIgnoreCase without accepting Unicode folds the comparer rejects.</summary>
    /// <param name="identifier">A typed column name, not SQL or a row value.</param>
    /// <returns>A deterministic spelling with the same ordinal-ignore-case identity.</returns>
    public static string NormalizeOrdinalIgnoreCase(string identifier)
    {
        var upper = identifier.ToUpperInvariant();
        if (StringComparer.OrdinalIgnoreCase.Equals(identifier, upper))
        {
            return upper;
        }

        // WHY: Invariant casing and OrdinalIgnoreCase can differ at Unicode
        // boundaries. Preserve only rejected rune folds, not the entire name,
        // so an ASCII suffix still canonicalizes consistently in that case.
        Span<char> runeBuffer = stackalloc char[2];
        var length = 0;
        // WHY: Advance by decoded rune width while measuring the exact UTF-16 destination length.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var offset = 0; offset < identifier.Length;)
        {
            length = checked(length + FoldOrdinalRune(identifier.AsSpan(offset), runeBuffer, out var consumed));
            offset += consumed;
        }

        return string.Create(length, identifier, static (destination, source) =>
        {
            Span<char> buffer = stackalloc char[2];
            var written = 0;
            // WHY: Advance source and destination offsets independently when canonical rune widths differ.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var offset = 0; offset < source.Length;)
            {
                var count = FoldOrdinalRune(source.AsSpan(offset), buffer, out var consumed);
                buffer[..count].CopyTo(destination[written..]);
                written += count;
                offset += consumed;
            }
        });
    }

    /// <summary>Retains rejected case mappings and invalid code units without replacement characters.</summary>
    /// <param name="source">The remaining authored UTF-16 identifier.</param>
    /// <param name="destination">Two-code-unit scratch space for one canonical rune or original code unit.</param>
    /// <param name="consumed">The number of authored UTF-16 code units consumed.</param>
    /// <returns>The number of canonical code units written.</returns>
    private static int FoldOrdinalRune(ReadOnlySpan<char> source, Span<char> destination, out int consumed)
    {
        if (Rune.DecodeFromUtf16(source, out var rune, out consumed) != OperationStatus.Done)
        {
            consumed = 1;
            destination[0] = source[0];

            return 1;
        }

        var written = Rune.ToUpperInvariant(rune).EncodeToUtf16(destination);
        if (source[..consumed].Equals(destination[..written], StringComparison.OrdinalIgnoreCase))
        {
            return written;
        }

        source[..consumed].CopyTo(destination);

        return consumed;
    }

    // WHY: The BCL sort consumes IComparer<T>; a reference comparer avoids
    // redundant value-type boxing. This allocation belongs to one unsorted
    // intent, never its individual rows; ordered inputs bypass sorting entirely.
    /// <summary>Orders one cached per-intent ordinal map through the BCL comparer contract.</summary>
    private sealed class KeyOrdinalComparer(IReadOnlyList<string> columns) : IComparer<int>
    {
        /// <inheritdoc />
        public int Compare(int left, int right)
        {
            var comparison = StringComparer.Ordinal.Compare(columns[left], columns[right]);

            // WHY: Valid model keys have unique physical members. Stable ties
            // retain authored associations rather than merging duplicate labels.
            return comparison != 0 ? comparison : left.CompareTo(right);
        }
    }
}
