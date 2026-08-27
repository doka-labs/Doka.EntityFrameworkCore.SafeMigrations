namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Creates a deterministic SHA-256 fingerprint of an ordered SafeMigrations
/// intent and expected-definition contract without reflection or JSON.
/// </summary>
public static partial class SafeMigrationContractFingerprint
{
    /// <summary>Creates the lowercase contract fingerprint.</summary>
    /// <remarks>
    /// Safe operations contribute their ordered intents, definitions, policies,
    /// and operation annotations.
    /// Ordinary provider operations contribute only their CLR type name, not their
    /// properties or SQL. Use an independent artifact digest to bind their full content.
    /// </remarks>
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

                var annotations = SafeMigrationProviderAnnotation.Capture(safeOperation);
                writer.Add(annotations.Count);
                foreach (var annotation in annotations)
                {
                    writer.Add(annotation.Name);
                    writer.Add(annotation.Fingerprint);
                }
            }
            else
            {
                writer.Add("provider-owned");
                writer.Add(operation.GetType().FullName ?? operation.GetType().Name);
            }
        }

        return writer.GetHash();
    }

    internal static void Validate(
        string fingerprint,
        string parameterName
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint, parameterName);

        if (fingerprint.Length != 64
            || fingerprint.Any(static value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The contract fingerprint must contain exactly 64 lowercase hexadecimal characters.",
                parameterName);
        }
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
}
