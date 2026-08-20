namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Creates a deterministic SHA-256 fingerprint of an ordered SafeMigrations
/// intent and expected-definition contract without reflection or JSON.
/// </summary>
public static partial class SafeMigrationContractFingerprint
{
    /// <summary>Creates the lowercase contract fingerprint.</summary>
    /// <param name="operations">The ordered migration operations.</param>
    /// <returns>The lowercase SHA-256 fingerprint.</returns>
    public static string Create(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        using var writer = new CanonicalHashWriter();

        // Domain-separate this wire format so a future canonical form cannot
        // collide with fingerprints produced by this contract version.
        writer.Add("safe-migrations-contract-v1");
        writer.Add(operations.Count);
        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);

            if (operation is SafeMigrationOperation safeOperation)
            {
                writer.Add("safe");
                writer.Add((int)safeOperation.Policy);
                WriteIntent(writer, safeOperation.Intent);
            }
            else
            {
                writer.Add("provider-owned");
                writer.Add(operation.GetType().FullName ?? operation.GetType().Name);
            }
        }

        return writer.GetHash();
    }

    private static void WriteIntent(
        CanonicalHashWriter writer,
        SafeMigrationIntent intent
    )
    {
        writer.Add((int)intent.Kind);
        switch (intent)
        {
            case EnsureSchemaIntent value:
                WriteIntent(writer, value);
                break;
            case DropSchemaIntent value:
                WriteIntent(writer, value);
                break;
            case EnsureTableIntent value:
                WriteIntent(writer, value);
                break;
            case DropTableIntent value:
                WriteIntent(writer, value);
                break;
            case RenameTableIntent value:
                WriteIntent(writer, value);
                break;
            case EnsureColumnIntent value:
                WriteIntent(writer, value);
                break;
            case DropColumnIntent value:
                WriteIntent(writer, value);
                break;
            case RenameColumnIntent value:
                WriteIntent(writer, value);
                break;
            case AlterColumnIntent value:
                WriteIntent(writer, value);
                break;
            case EnsureIndexIntent value:
                WriteIntent(writer, value);
                break;
            case DropIndexIntent value:
                WriteIntent(writer, value);
                break;
            case RenameIndexIntent value:
                WriteIntent(writer, value);
                break;
            case EnsurePrimaryKeyIntent value:
                WriteIntent(writer, value);
                break;
            case DropPrimaryKeyIntent value:
                WriteIntent(writer, value);
                break;
            case EnsureUniqueConstraintIntent value:
                WriteIntent(writer, value);
                break;
            case DropUniqueConstraintIntent value:
                WriteIntent(writer, value);
                break;
            case EnsureCheckConstraintIntent value:
                WriteIntent(writer, value);
                break;
            case DropCheckConstraintIntent value:
                WriteIntent(writer, value);
                break;
            case EnsureForeignKeyIntent value:
                WriteIntent(writer, value);
                break;
            case DropForeignKeyIntent value:
                WriteIntent(writer, value);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(intent),
                    intent.GetType().FullName,
                    "Unknown SafeMigrations intent type.");
        }
    }

    private static void WriteTableIdentity(
        CanonicalHashWriter writer,
        string table,
        string? schema
    )
    {
        writer.Add(schema);
        writer.Add(table);
    }

    private static void WriteStrings(
        CanonicalHashWriter writer,
        IReadOnlyList<string> values
    )
    {
        writer.Add(values.Count);
        foreach (var value in values)
        {
            writer.Add(value);
        }
    }

    private sealed class CanonicalHashWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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
            var rented = ArrayPool<byte>.Shared.Rent(maximumLength);
            try
            {
                var length = Encoding.UTF8.GetBytes(value, rented);

                // Length-prefixing makes adjacent strings unambiguous without
                // allocating a delimiter-escaped intermediate representation.
                Add(length);
                _hash.AppendData(rented.AsSpan(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

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
            Add(bytes.Length);
            _hash.AppendData(bytes);
        }

        public string GetHash()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The contract fingerprint has already been finalized.");
            }

            _completed = true;
            return Convert.ToHexStringLower(_hash.GetHashAndReset());
        }

        public void Dispose() => _hash.Dispose();
    }
}
