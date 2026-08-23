namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Creates and validates deterministic fingerprints of relational EF target models.</summary>
public static partial class SafeMigrationModelFingerprint
{
    private const string Algorithm = "sha256";
    private const string Format = "safe-relational-model";
    private const string Version = "v1";

    /// <summary>Creates a versioned SHA-256 fingerprint of the relational design-time model.</summary>
    /// <param name="model">The EF Core design-time target model.</param>
    /// <param name="providerContract">The stable provider contract identifier.</param>
    /// <returns>The versioned lowercase SHA-256 fingerprint.</returns>
    /// <exception cref="NotSupportedException">
    /// A provider annotation has a value that cannot be represented by this fingerprint version.
    /// </exception>
    public static string Create(
        IModel model,
        string providerContract
    )
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateProviderContract(providerContract);

        using var writer = new CanonicalHashWriter();
        writer.Add(Format);
        writer.Add(Version);
        writer.Add(providerContract);
        WriteRelationalModel(writer, model.GetRelationalModel());

        return string.Join(':', Format, Version, providerContract, Algorithm, writer.GetHash());
    }

    /// <summary>Validates an expected fingerprint when one is supplied.</summary>
    /// <param name="actualFingerprint">The fingerprint computed from the runtime model.</param>
    /// <param name="expectedFingerprint">The canonical fingerprint that the runtime model must match.</param>
    /// <exception cref="ArgumentException">A supplied fingerprint does not use the current wire format.</exception>
    /// <exception cref="SafeMigrationModelMismatchException">The fingerprints differ.</exception>
    public static void ValidateExpected(
        string actualFingerprint,
        string? expectedFingerprint
    )
    {
        ValidateFingerprint(actualFingerprint, nameof(actualFingerprint));

        if (expectedFingerprint is null)
        {
            return;
        }

        ValidateFingerprint(expectedFingerprint, nameof(expectedFingerprint));

        if (!StringComparer.Ordinal.Equals(actualFingerprint, expectedFingerprint))
        {
            throw new SafeMigrationModelMismatchException(expectedFingerprint, actualFingerprint);
        }
    }

    internal static void ValidateFingerprint(
        string fingerprint,
        string parameterName
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint, parameterName);

        var parts = fingerprint.Split(':');
        if (parts.Length != 5
            || !StringComparer.Ordinal.Equals(parts[0], Format)
            || !StringComparer.Ordinal.Equals(parts[1], Version)
            || string.IsNullOrWhiteSpace(parts[2])
            || !StringComparer.Ordinal.Equals(parts[3], Algorithm)
            || parts[4].Length != 64
            || parts[4]
                .Any(static value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The model fingerprint must use the safe-relational-model:v1:<provider>:sha256:<lowercase-hex> format.",
                parameterName);
        }
    }

    private static void ValidateProviderContract(
        string providerContract
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerContract);

        if (providerContract.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The provider contract identifier cannot contain a colon.",
                nameof(providerContract));
        }
    }
}

/// <summary>Indicates that a runtime context does not match the canonical Core model.</summary>
public sealed class SafeMigrationModelMismatchException : InvalidOperationException
{
    /// <summary>Initializes the mismatch exception.</summary>
    /// <param name="expectedFingerprint">The canonical fingerprint that the runtime model must match.</param>
    /// <param name="actualFingerprint">The fingerprint computed from the runtime model.</param>
    public SafeMigrationModelMismatchException(
        string expectedFingerprint,
        string actualFingerprint
    ) : base("The runtime DbContext model does not match the canonical migration model fingerprint.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualFingerprint);

        ExpectedFingerprint = expectedFingerprint;
        ActualFingerprint = actualFingerprint;
    }

    /// <summary>Gets the expected fingerprint.</summary>
    public string ExpectedFingerprint { get; }

    /// <summary>Gets the actual fingerprint.</summary>
    public string ActualFingerprint { get; }
}
