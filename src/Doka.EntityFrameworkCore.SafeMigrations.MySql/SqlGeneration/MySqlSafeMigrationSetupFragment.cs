namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Defers owned setup SQL until its final transport group is known.</summary>
internal readonly struct MySqlSafeMigrationSetupFragment
{
    private readonly string? _text;
    private readonly Part[]? _parts;
    private readonly int _length;
    private readonly int _utf8ByteCount;

    /// <summary>Retains an existing immutable fragment without copying its SQL.</summary>
    /// <param name="text">The complete existing SQL fragment.</param>
    internal MySqlSafeMigrationSetupFragment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _text = text;
    }

    private MySqlSafeMigrationSetupFragment(Part[] parts, int length, int utf8ByteCount)
    {
        _parts = parts;
        _length = length;
        _utf8ByteCount = utf8ByteCount;
    }

    /// <summary>Gets the exact UTF-16 length before allocating the final SQL string.</summary>
    internal int Length => _text?.Length ?? (_parts is not null
        ? _length
        : throw new InvalidOperationException("The MySQL setup fragment is not initialized."));

    /// <summary>Gets the encoded payload length used by the unchanged grouping budget.</summary>
    internal int Utf8ByteCount => _text is { } text
        ? Encoding.UTF8.GetByteCount(text)
        : _parts is not null
            ? _utf8ByteCount
            : throw new InvalidOperationException("The MySQL setup fragment is not initialized.");

    /// <summary>Retains already materialized provider or constant SQL without allocating a wrapper.</summary>
    /// <param name="text">The existing SQL string.</param>
    public static implicit operator MySqlSafeMigrationSetupFragment(string text) => new(text);

    /// <summary>Defers groupable SQL pieces and materializes large independent fragments once.</summary>
    /// <param name="parts">Raw text or independently UTF-8-encoded hexadecimal pieces, in order.</param>
    /// <returns>An immutable fragment; pieces below the grouping ceiling are snapshotted until rendering.</returns>
    internal static MySqlSafeMigrationSetupFragment Compose(params ReadOnlySpan<Part> parts)
    {
        var count = 0;
        var length = 0;
        var bytes = 0;
        var previous = '\0';
        Part single = default;
        Span<char> boundary = stackalloc char[2];
        foreach (var part in parts)
        {
            var text = part.Text;
            if (text.Length == 0)
            {
                continue;
            }

            var partLength = part.Length;
            length = checked(length + partLength);
            bytes = checked(bytes + (part.IsHexadecimal ? partLength : Encoding.UTF8.GetByteCount(text)));
            if (!part.IsHexadecimal && char.IsHighSurrogate(previous) && char.IsLowSurrogate(text[0]))
            {
                // WHY: Separate UTF-8 counts treat split surrogates as fallbacks. The final string
                // joins them into one scalar; calculate the actual encoder delta instead of assuming
                // a fallback size or allocating a combined string merely to measure its payload.
                boundary[0] = previous;
                boundary[1] = text[0];
                bytes += Encoding.UTF8.GetByteCount(boundary)
                    - Encoding.UTF8.GetByteCount(boundary[..1])
                    - Encoding.UTF8.GetByteCount(boundary[1..]);
            }

            previous = part.IsHexadecimal ? '0' : text[^1];
            single = part;
            count++;
        }

        if (count == 0)
        {
            return new MySqlSafeMigrationSetupFragment(string.Empty);
        }

        if (count == 1 && !single.IsHexadecimal)
        {
            return new MySqlSafeMigrationSetupFragment(single.Text);
        }

        if (length >= MySqlSafeMigrationSetupCommandCompactor.MaximumGroupedPayloadBytes)
        {
            // WHY: The compactor passes large fragments through. Write directly from the caller's
            // span before it expires; a deferred descriptor array could not save a later SQL copy.
            return new MySqlSafeMigrationSetupFragment(string.Create(
                length,
                parts,
                static (destination, source) => WriteParts(source, destination)));
        }

        // WHY: Copy only the small descriptor sequence, not SQL text. No caller-owned span or
        // mutable array escapes, and no rented buffer or closure lives until later rendering.
        var snapshot = new Part[count];
        var offset = 0;
        foreach (var part in parts)
        {
            if (part.Text.Length != 0)
            {
                snapshot[offset++] = part;
            }
        }

        return new MySqlSafeMigrationSetupFragment(snapshot, length, bytes);
    }

    /// <summary>Materializes ungrouped SQL once, or returns the original string unchanged.</summary>
    /// <returns>The complete SQL fragment.</returns>
    internal string ToSql() => _text ?? string.Create(Length, this,
        static (destination, fragment) => fragment.WriteTo(destination));

    /// <summary>Writes this fragment directly into its final group without an intermediate string.</summary>
    /// <param name="destination">A destination with at least <see cref="Length" /> characters.</param>
    internal void WriteTo(Span<char> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Length);
        if (_text is { } text)
        {
            text.AsSpan().CopyTo(destination);

            return;
        }

        WriteParts(_parts, destination);
    }

    /// <summary>Writes immutable parts from either a transient caller span or the deferred snapshot.</summary>
    private static void WriteParts(ReadOnlySpan<Part> parts, Span<char> destination)
    {
        var offset = 0;
        foreach (var part in parts)
        {
            var length = part.Length;
            if (length == 0)
            {
                continue;
            }

            if (part.IsHexadecimal)
            {
                WriteHexadecimal(part.Text, destination.Slice(offset, length));
            }
            else
            {
                part.Text.AsSpan().CopyTo(destination[offset..]);
            }

            offset += length;
        }
    }

    /// <summary>Encodes one prepared SQL value with a buffer scoped strictly to the synchronous write.</summary>
    private static void WriteHexadecimal(string text, Span<char> destination)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(destination.Length / 2);
        try
        {
            var count = Encoding.UTF8.GetBytes(text.AsSpan(), buffer);
            if (!Convert.TryToHexString(buffer.AsSpan(0, count), destination, out var written)
                || written != destination.Length)
            {
                throw new InvalidOperationException(
                    "The MySQL prepared statement could not be encoded as hexadecimal UTF-8.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Describes a raw SQL piece or a separately encoded prepared-statement value.</summary>
    internal readonly struct Part
    {
        private readonly string? _text;

        private Part(string text, bool isHexadecimal)
        {
            ArgumentNullException.ThrowIfNull(text);

            _text = text;
            IsHexadecimal = isHexadecimal;
        }

        /// <summary>Gets the immutable source text and rejects uninitialized descriptors.</summary>
        internal string Text => _text
            ?? throw new ArgumentException("The MySQL setup SQL part is not initialized.");

        /// <summary>Gets whether source text is emitted as uppercase hexadecimal UTF-8.</summary>
        internal bool IsHexadecimal { get; }

        /// <summary>Gets the emitted UTF-16 character count.</summary>
        internal int Length => IsHexadecimal ? checked(Encoding.UTF8.GetByteCount(Text) * 2) : Text.Length;

        /// <summary>Describes an independent prepared SQL value without allocating its encoded string.</summary>
        /// <param name="text">The complete value to encode as hexadecimal UTF-8.</param>
        /// <returns>The immutable encoded-value descriptor.</returns>
        internal static Part Hex(string text) => new(text, isHexadecimal: true);

        /// <summary>Retains raw SQL text without allocating a wrapper.</summary>
        /// <param name="text">The immutable raw SQL piece.</param>
        public static implicit operator Part(string text) => new(text, isHexadecimal: false);
    }
}
