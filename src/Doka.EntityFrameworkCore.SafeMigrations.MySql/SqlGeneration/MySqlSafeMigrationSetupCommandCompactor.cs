namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Reduces same-operation transport exchanges without changing SQL or cleanup boundaries.</summary>
internal static class MySqlSafeMigrationSetupCommandCompactor
{
    private const int MaximumOriginalFragmentCount = 128;
    private const int MaximumOriginalTextLength = 1_048_576;
    private const int MaximumGroupedPayloadBytes = 256;

    /// <summary>
    /// Groups owned setup fragments in order while preserving opaque provider fragments
    /// and the original payload ceiling.
    /// </summary>
    /// <param name="setupCommands">The complete, ordered setup sequence.</param>
    /// <param name="bodyCommand">The independently executed guarded body.</param>
    /// <param name="cleanupCommands">The independently attempted cleanup sequence.</param>
    /// <param name="providerSetupOffset">The first opaque provider setup fragment.</param>
    /// <param name="providerSetupCount">The number of consecutive opaque provider setup fragments.</param>
    /// <returns>The compacted sequence, or the original sequence when no grouping is possible.</returns>
    internal static IReadOnlyList<string> Compact(
        IReadOnlyList<string> setupCommands,
        string bodyCommand,
        IReadOnlyList<string> cleanupCommands,
        int providerSetupOffset,
        int providerSetupCount
    )
    {
        ArgumentNullException.ThrowIfNull(setupCommands);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyCommand);
        ArgumentNullException.ThrowIfNull(cleanupCommands);
        ArgumentOutOfRangeException.ThrowIfNegative(providerSetupOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(providerSetupOffset, setupCommands.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(providerSetupCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(providerSetupCount, setupCommands.Count - providerSetupOffset);

        if (!FitsOriginalScope(setupCommands, bodyCommand, cleanupCommands))
        {
            // WHY: Grouping must not hide a scope that the provider would reject before optimization.
            // Return its original inputs so CreateScoped retains authoritative validation and diagnostics.
            return setupCommands;
        }

        // WHY: Large classifier/DDL assignments already dominate the payload. Copying them to
        // remove one small exchange raised measured generation allocations. Group only short
        // control fragments; retain large strings by reference and cap each new UTF-16 copy.
        var maximumBytes = bodyCommand.Length >= MaximumGroupedPayloadBytes
            ? MaximumGroupedPayloadBytes
            : Math.Min(MaximumGroupedPayloadBytes, Encoding.UTF8.GetByteCount(bodyCommand));

        foreach (var command in setupCommands)
        {
            if (maximumBytes == MaximumGroupedPayloadBytes || command.Length >= MaximumGroupedPayloadBytes)
            {
                maximumBytes = MaximumGroupedPayloadBytes;

                break;
            }

            maximumBytes = Math.Max(
                maximumBytes,
                Math.Min(MaximumGroupedPayloadBytes, Encoding.UTF8.GetByteCount(command)));
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
                compacted?.Add(setupCommands[offset]);
                offset++;

                continue;
            }

            var end = offset + 1;
            var length = setupCommands[offset].Length;
            var bytes = Encoding.UTF8.GetByteCount(setupCommands[offset]);
            while (end < setupCommands.Count
                   && (end < providerSetupOffset || end >= providerSetupEnd))
            {
                var next = setupCommands[end];
                if (next.Length > maximumBytes - bytes)
                {
                    break;
                }

                var nextBytes = Encoding.UTF8.GetByteCount(next);
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
                compacted?.Add(setupCommands[offset]);
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
                        compacted.Add(setupCommands[index]);
                    }
                }

                // WHY: Doka's original script concatenates fragments without separators. Copy exactly
                // those bytes; adding SQL or delimiters would alter the certified execution contract.
                compacted.Add(string.Create(
                    length,
                    (Commands: setupCommands, Offset: offset, End: end),
                    static (destination, state) =>
                    {
                        var position = 0;
                        for (var index = state.Offset; index < state.End; index++)
                        {
                            var command = state.Commands[index];
                            command.AsSpan().CopyTo(destination[position..]);
                            position += command.Length;
                        }
                    }));
            }

            offset = end;
        }

        return compacted ?? setupCommands;
    }

    /// <summary>Retains the existing provider scope bounds before reducing its fragment count.</summary>
    private static bool FitsOriginalScope(
        IReadOnlyList<string> setupCommands,
        string bodyCommand,
        IReadOnlyList<string> cleanupCommands
    )
    {
        if ((long)setupCommands.Count + cleanupCommands.Count + 1 > MaximumOriginalFragmentCount)
        {
            return false;
        }

        var length = (long)bodyCommand.Length;
        foreach (var command in setupCommands)
        {
            length += command.Length;
        }

        foreach (var command in cleanupCommands)
        {
            length += command.Length;
        }

        return length <= MaximumOriginalTextLength;
    }
}
