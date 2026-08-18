namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Creates and validates deterministic fingerprints of EF target models.</summary>
public static class SafeMigrationModelFingerprint
{
    /// <summary>Creates a lowercase SHA-256 fingerprint of the complete EF model debug view.</summary>
    public static string Create(
        IModel model
    )
    {
        ArgumentNullException.ThrowIfNull(model);
        var canonical = model
            .ToDebugString(MetadataDebugStringOptions.LongDefault)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Validates an expected fingerprint when one is supplied.</summary>
    public static void ValidateExpected(
        string actualFingerprint,
        string? expectedFingerprint
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actualFingerprint);
        if (expectedFingerprint is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        if (!StringComparer.OrdinalIgnoreCase.Equals(actualFingerprint, expectedFingerprint))
        {
            throw new SafeMigrationModelMismatchException(expectedFingerprint, actualFingerprint);
        }
    }
}

/// <summary>Indicates that a runtime context does not match the canonical Core model.</summary>
public sealed class SafeMigrationModelMismatchException : InvalidOperationException
{
    /// <summary>Initializes the mismatch exception.</summary>
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
