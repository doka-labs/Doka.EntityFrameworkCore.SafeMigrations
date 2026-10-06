namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Reduces same-operation transport exchanges without changing SQL or cleanup boundaries.</summary>
internal static class MySqlSafeMigrationSetupCommandCompactor
{
    private const int MaximumOriginalFragmentCount = 128;
    private const int MaximumOriginalTextLength = 1_048_576;
    /// <summary>Bounds grouping copies and identifies fragments that need no deferred descriptor snapshot.</summary>
    internal const int MaximumGroupedPayloadBytes = 4096;

    /// <summary>
    /// Groups owned setup fragments in order while preserving opaque provider fragments
    /// and the original payload ceiling.
    /// </summary>
    /// <param name="setupCommands">The complete, ordered setup sequence.</param>
    /// <param name="bodyCommand">The independently executed guarded body.</param>
    /// <param name="cleanupCommands">The independently attempted cleanup sequence.</param>
    /// <param name="providerSetupOffset">The first opaque provider setup fragment.</param>
    /// <param name="providerSetupCount">The number of consecutive opaque provider setup fragments.</param>
    /// <param name="fusedSetupFragmentCount">
    /// The original control fragments already emitted directly into owned assignment strings.
    /// </param>
    /// <returns>The compacted sequence, or the original sequence when no grouping is possible.</returns>
    internal static IReadOnlyList<string> Compact(
        IReadOnlyList<string> setupCommands,
        string bodyCommand,
        IReadOnlyList<string> cleanupCommands,
        int providerSetupOffset,
        int providerSetupCount,
        int fusedSetupFragmentCount = 0
    )
    {
        ArgumentNullException.ThrowIfNull(setupCommands);

        return CompactCore(
            new StringSource(setupCommands),
            bodyCommand,
            cleanupCommands,
            providerSetupOffset,
            providerSetupCount,
            fusedSetupFragmentCount);
    }

    /// <summary>Writes deferred owned SQL directly into its final bounded transport groups.</summary>
    /// <param name="setupCommands">The complete, ordered deferred setup sequence.</param>
    /// <param name="bodyCommand">The independently executed guarded body.</param>
    /// <param name="cleanupCommands">The independently attempted cleanup sequence.</param>
    /// <param name="providerSetupOffset">The first opaque provider setup fragment.</param>
    /// <param name="providerSetupCount">The number of consecutive opaque provider setup fragments.</param>
    /// <param name="fusedSetupFragmentCount">The original controls already included in owned fragments.</param>
    /// <returns>The exact final setup sequence with unchanged provider and scope boundaries.</returns>
    internal static IReadOnlyList<string> Compact(
        IReadOnlyList<MySqlSafeMigrationSetupFragment> setupCommands,
        string bodyCommand,
        IReadOnlyList<string> cleanupCommands,
        int providerSetupOffset,
        int providerSetupCount,
        int fusedSetupFragmentCount = 0
    )
    {
        ArgumentNullException.ThrowIfNull(setupCommands);

        return CompactCore(
            new DeferredSource(setupCommands),
            bodyCommand,
            cleanupCommands,
            providerSetupOffset,
            providerSetupCount,
            fusedSetupFragmentCount);
    }

    /// <summary>Applies one grouping contract to materialized and deferred SQL without boxing either source.</summary>
    private static IReadOnlyList<string> CompactCore<TSource>(
        TSource setupCommands,
        string bodyCommand,
        IReadOnlyList<string> cleanupCommands,
        int providerSetupOffset,
        int providerSetupCount,
        int fusedSetupFragmentCount
    ) where TSource : struct, IFragmentSource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyCommand);
        ArgumentNullException.ThrowIfNull(cleanupCommands);
        ArgumentOutOfRangeException.ThrowIfNegative(providerSetupOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(providerSetupOffset, setupCommands.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(providerSetupCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(providerSetupCount, setupCommands.Count - providerSetupOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(fusedSetupFragmentCount);

        if (fusedSetupFragmentCount > 0
            && (long)setupCommands.Count + cleanupCommands.Count + 1 + fusedSetupFragmentCount
            > MaximumOriginalFragmentCount)
        {
            // WHY: Returning an already-fused list would let CreateScoped accept an originally
            // oversized scope. Reject before compaction; the physical count cannot prove admission.
            throw new ArgumentException(
                "The original MySQL migration scope exceeds the supported fragment count.",
                nameof(setupCommands));
        }

        if (!FitsOriginalScope(setupCommands, bodyCommand, cleanupCommands))
        {
            // WHY: Grouping must not hide a scope that the provider would reject before optimization.
            // Return its original inputs so CreateScoped retains authoritative validation and diagnostics.
            return setupCommands.Materialize();
        }

        // WHY: A small fixed copy budget removes setup round trips without copying unbounded
        // classifiers. Also retain the largest original payload ceiling so grouping alone cannot
        // make a previously accepted command exceed the server's packet-size limit.
        var maximumBytes = bodyCommand.Length >= MaximumGroupedPayloadBytes
            ? MaximumGroupedPayloadBytes
            : Math.Min(MaximumGroupedPayloadBytes, Encoding.UTF8.GetByteCount(bodyCommand));

        // WHY: The inputs are indexable; interface enumeration boxes a List enumerator
        // on every operation even when its first fragment already settles the payload bound.
        for (var index = 0; index < setupCommands.Count; index++)
        {
            var command = setupCommands[index];
            if (maximumBytes == MaximumGroupedPayloadBytes || command.Length >= MaximumGroupedPayloadBytes)
            {
                maximumBytes = MaximumGroupedPayloadBytes;

                break;
            }

            maximumBytes = Math.Max(
                maximumBytes,
                Math.Min(MaximumGroupedPayloadBytes, command.Utf8ByteCount));
        }

        List<string>? compacted = null;
        var providerSetupEnd = providerSetupOffset + providerSetupCount;
        var offset = 0;
        while (offset < setupCommands.Count)
        {
            if ((offset >= providerSetupOffset && offset < providerSetupEnd)
                || setupCommands[offset].Length >= MaximumGroupedPayloadBytes)
            {
                // WHY: Provider setup remains opaque, and large owned strings need no additional copy.
                compacted?.Add(setupCommands[offset].ToSql());
                offset++;

                continue;
            }

            var end = offset + 1;
            var length = setupCommands[offset].Length;
            var bytes = setupCommands[offset].Utf8ByteCount;
            while (end < setupCommands.Count
                   && (end < providerSetupOffset || end >= providerSetupEnd))
            {
                var next = setupCommands[end];
                if (next.Length > maximumBytes - bytes)
                {
                    break;
                }

                var nextBytes = next.Utf8ByteCount;
                if (nextBytes > maximumBytes - bytes)
                {
                    break;
                }

                length += next.Length;
                bytes += nextBytes;
                end++;
            }

            if (end == offset + 1)
            {
                compacted?.Add(setupCommands[offset].ToSql());
            }
            else
            {
                if (compacted is null)
                {
                    // WHY: This first completed group proves a smaller upper bound; later groups
                    // can only reduce the result further, so no original-sized backing array is needed.
                    compacted = new List<string>(setupCommands.Count - (end - offset - 1));
                    for (var index = 0; index < offset; index++)
                    {
                        compacted.Add(setupCommands[index].ToSql());
                    }
                }

                // WHY: Doka's original script concatenates fragments without separators. Copy exactly
                // those bytes; adding SQL or delimiters would alter the certified execution contract.
                // Deferred pieces write into this final allocation, avoiding the intermediate
                // assignment strings that would otherwise be immediately copied into the group.
                compacted.Add(string.Create(
                    length,
                    (Commands: setupCommands, Offset: offset, End: end),
                    static (destination, state) =>
                    {
                        var position = 0;
                        for (var index = state.Offset; index < state.End; index++)
                        {
                            var command = state.Commands[index];
                            command.WriteTo(destination.Slice(position, command.Length));
                            position += command.Length;
                        }
                    }));
            }

            offset = end;
        }

        return compacted ?? setupCommands.Materialize();
    }

    /// <summary>Retains the existing provider scope bounds before reducing its fragment count.</summary>
    private static bool FitsOriginalScope<TSource>(
        TSource setupCommands,
        string bodyCommand,
        IReadOnlyList<string> cleanupCommands
    ) where TSource : struct, IFragmentSource
    {
        if ((long)setupCommands.Count + cleanupCommands.Count + 1 > MaximumOriginalFragmentCount)
        {
            return false;
        }

        var length = (long)bodyCommand.Length;
        // WHY: Indexing keeps validation allocation-free for List and read-only array inputs.
        for (var index = 0; index < setupCommands.Count; index++)
        {
            length += setupCommands[index].Length;
        }

        for (var index = 0; index < cleanupCommands.Count; index++)
        {
            length += cleanupCommands[index].Length;
        }

        return length <= MaximumOriginalTextLength;
    }

    /// <summary>Keeps source-specific ownership outside the single shared grouping algorithm.</summary>
    private interface IFragmentSource
    {
        /// <summary>Gets the original setup fragment count before any grouping.</summary>
        int Count { get; }

        /// <summary>Gets one immutable fragment descriptor without materializing its SQL.</summary>
        MySqlSafeMigrationSetupFragment this[int index] { get; }

        /// <summary>Returns unchanged fragment boundaries when grouping is inapplicable.</summary>
        IReadOnlyList<string> Materialize();
    }

    /// <summary>Preserves original string and collection identities for already materialized callers.</summary>
    private readonly struct StringSource(IReadOnlyList<string> commands) : IFragmentSource
    {
        /// <inheritdoc />
        public int Count => commands.Count;

        /// <inheritdoc />
        public MySqlSafeMigrationSetupFragment this[int index] => commands[index];

        /// <inheritdoc />
        public IReadOnlyList<string> Materialize() => commands;
    }

    /// <summary>Materializes deferred SQL only when its independent final output slot is known.</summary>
    private readonly struct DeferredSource(IReadOnlyList<MySqlSafeMigrationSetupFragment> commands) : IFragmentSource
    {
        /// <inheritdoc />
        public int Count => commands.Count;

        /// <inheritdoc />
        public MySqlSafeMigrationSetupFragment this[int index] => commands[index];

        /// <inheritdoc />
        public IReadOnlyList<string> Materialize()
        {
            if (commands.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[commands.Count];
            for (var index = 0; index < commands.Count; index++)
            {
                result[index] = commands[index].ToSql();
            }

            return result;
        }
    }
}
