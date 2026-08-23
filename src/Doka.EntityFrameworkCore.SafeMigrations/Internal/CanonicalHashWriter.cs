namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed class CanonicalHashWriter : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(256);
    private bool _completed;

    public void Add(
        string? value
    )
    {
        if (value is null)
        {
            Add(-1);
            return;
        }

        var maximumLength = Encoding.UTF8.GetMaxByteCount(value.Length);
        EnsureBufferSize(maximumLength);

        var length = Encoding.UTF8.GetBytes(value, _buffer);
        Add(length);
        _hash.AppendData(_buffer.AsSpan(0, length));
    }

    public void Add(
        Type? value
    ) => Add(value?.AssemblyQualifiedName);

    public void Add(
        int value
    )
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    public void Add(
        int? value
    )
    {
        Add(value.HasValue);
        if (value.HasValue)
        {
            Add(value.Value);
        }
    }

    public void Add(
        long value
    )
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    public void Add(
        long? value
    )
    {
        Add(value.HasValue);
        if (value.HasValue)
        {
            Add(value.Value);
        }
    }

    public void Add(
        bool value
    ) => Add(value ? 1 : 0);

    public void Add(
        bool? value
    )
    {
        Add(value.HasValue);
        if (value.HasValue)
        {
            Add(value.Value);
        }
    }

    public void Add(
        byte[] bytes
    )
    {
        ArgumentNullException.ThrowIfNull(bytes);

        Add(bytes.Length);
        _hash.AppendData(bytes);
    }

    public string GetHash()
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);

        if (_completed)
        {
            throw new InvalidOperationException("The canonical fingerprint has already been finalized.");
        }

        _completed = true;
        return Convert.ToHexStringLower(_hash.GetHashAndReset());
    }

    public void Dispose()
    {
        _hash.Dispose();

        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = [];
        }
    }

    private void EnsureBufferSize(
        int requiredLength
    )
    {
        if (_buffer.Length >= requiredLength)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(requiredLength);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = replacement;
    }
}
