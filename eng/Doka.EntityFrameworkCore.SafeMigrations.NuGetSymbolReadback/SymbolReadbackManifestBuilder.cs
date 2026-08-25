namespace Doka.EntityFrameworkCore.SafeMigrations.NuGetSymbolReadback;

internal static class SymbolReadbackManifestBuilder
{
    private const string SymbolServer = "https://symbols.nuget.org/download/symbols";

    private static readonly string[] s_packageIds =
    [
        "Doka.EntityFrameworkCore.SafeMigrations",
        "Doka.EntityFrameworkCore.SafeMigrations.MySql",
        "Doka.EntityFrameworkCore.SafeMigrations.PostgreSql",
    ];

    internal static SymbolReadbackManifest Build(
        string packageDirectory,
        string version
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var packageRoot = Path.GetFullPath(packageDirectory);
        var symbols = s_packageIds
            .Select(packageId => BuildEntry(packageRoot, packageId, version))
            .ToArray();

        return new SymbolReadbackManifest(1, version, symbols);
    }

    internal static SymbolReadbackEntry BuildEntry(
        string packageDirectory,
        string packageId,
        string version
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var packageRoot = Path.GetFullPath(packageDirectory);
        var primaryPath = Path.Combine(packageRoot, $"{packageId}.{version}.nupkg");
        var symbolsPath = Path.Combine(packageRoot, $"{packageId}.{version}.snupkg");
        var assemblyEntryName = $"lib/net10.0/{packageId}.dll";
        var pdbEntryName = $"lib/net10.0/{packageId}.pdb";

        var assembly = ReadExactPackageEntry(primaryPath, assemblyEntryName);
        var pdb = ReadExactPackageEntry(symbolsPath, pdbEntryName);

        using var assemblyStream = new MemoryStream(assembly, writable: false);
        using var peReader = new PEReader(assemblyStream);
        var debugEntries = peReader.ReadDebugDirectory();
        var codeViewEntries = debugEntries
            .Where(entry => entry.Type == DebugDirectoryEntryType.CodeView && entry.IsPortableCodeView)
            .ToArray();

        var checksumEntries = debugEntries
            .Where(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum)
            .ToArray();

        if (codeViewEntries.Length != 1
            || checksumEntries.Length != 1)
        {
            throw new InvalidDataException(
                $"{assemblyEntryName} must contain one Portable PDB identity and checksum.");
        }

        var codeView = peReader.ReadCodeViewDebugDirectoryData(codeViewEntries[0]);
        var checksum = peReader.ReadPdbChecksumDebugDirectoryData(checksumEntries[0]);
        var pdbName = Path.GetFileName(codeView.Path);

        if (!StringComparer.Ordinal.Equals(pdbName, Path.GetFileName(pdbEntryName)))
        {
            throw new InvalidDataException(
                $"{assemblyEntryName} identifies unexpected symbols '{pdbName}'.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(checksum.AlgorithmName, "SHA256"))
        {
            throw new InvalidDataException(
                $"{assemblyEntryName} does not carry the required SHA-256 PDB checksum.");
        }

        var pdbSha256 = SHA256.HashData(pdb);
        MetadataReaderProvider? metadataProvider;

        try
        {
            if (!peReader.TryOpenAssociatedPortablePdb(
                    assemblyEntryName,
                    _ => new MemoryStream(pdb, writable: false),
                    out metadataProvider,
                    out _)
                || metadataProvider is null)
            {
                throw new InvalidDataException(
                    $"{pdbEntryName} does not match the identity and checksum sealed into its assembly.");
            }
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidDataException(
                $"{pdbEntryName} does not match the checksum sealed into its assembly.",
                exception);
        }

        using (metadataProvider)
        {
            ValidatePortablePdb(
                metadataProvider,
                codeView.Guid,
                checksum.Checksum.AsSpan(),
                pdb,
                pdbEntryName);
        }

        // Portable PDB symbol-store keys use UInt32.MaxValue as their age.
        var symbolKey = $"{codeView.Guid:N}FFFFFFFF";
        var sha256 = Convert
            .ToHexString(pdbSha256)
            .ToLowerInvariant();

        var checksumHeader = $"{checksum.AlgorithmName}:"
            + Convert
                .ToHexString(checksum.Checksum.AsSpan())
                .ToLowerInvariant();

        return new SymbolReadbackEntry(
            packageId,
            version,
            pdbName,
            symbolKey,
            $"{SymbolServer}/{pdbName}/{symbolKey}/{pdbName}",
            checksumHeader,
            sha256);
    }

    private static void ValidatePortablePdb(
        MetadataReaderProvider metadataProvider,
        Guid expectedGuid,
        ReadOnlySpan<byte> expectedChecksum,
        byte[] pdb,
        string pdbEntryName
    )
    {
        var metadataHeader = metadataProvider.GetMetadataReader()
            .DebugMetadataHeader;

        if (metadataHeader is null)
        {
            throw new InvalidDataException($"{pdbEntryName} has no Portable PDB metadata header.");
        }

        var metadataId = new BlobContentId(metadataHeader.Id);

        if (metadataId.Guid != expectedGuid)
        {
            throw new InvalidDataException(
                $"{pdbEntryName} does not match the Portable PDB identity sealed into its assembly.");
        }

        // The deterministic content ID is zeroed for the whole-file checksum.
        var actualChecksum = CalculatePortablePdbChecksum(
            pdb,
            metadataHeader.IdStartOffset,
            metadataHeader.Id.Length);

        if (!actualChecksum.AsSpan().SequenceEqual(expectedChecksum))
        {
            throw new InvalidDataException(
                $"{pdbEntryName} does not match the checksum sealed into its assembly.");
        }
    }

    private static byte[] CalculatePortablePdbChecksum(
        byte[] pdb,
        int idOffset,
        int idLength
    )
    {
        if (idLength != 20
            || idOffset < 0
            || idOffset > pdb.Length - idLength)
        {
            throw new InvalidDataException("Portable PDB contains an invalid content ID range.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(pdb.AsSpan(0, idOffset));
        hash.AppendData(stackalloc byte[20]);
        hash.AppendData(pdb.AsSpan(idOffset + idLength));

        return hash.GetHashAndReset();
    }

    private static byte[] ReadExactPackageEntry(
        string packagePath,
        string entryName
    )
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("Candidate package is missing.", packagePath);
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var matches = archive
            .Entries.Where(entry => StringComparer.Ordinal.Equals(entry.FullName, entryName))
            .ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(packagePath)} must contain exactly one {entryName} entry.");
        }

        using var stream = matches[0].Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }
}
