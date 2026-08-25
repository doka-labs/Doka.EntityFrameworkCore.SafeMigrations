namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SymbolReadbackManifestBuilderTests
{
    private const string PackageId = "Doka.EntityFrameworkCore.SafeMigrations";
    private const string Version = "10.0.0-test";

    [Fact]
    public async Task Candidate_assembly_produces_checksum_bound_public_symbol_probe()
    {
        var packageDirectory = CreatePackageDirectory();

        try
        {
            await WritePackagePairAsync(
                packageDirectory,
                typeof(SafeMigrationOperation).Assembly.Location,
                corruptSymbols: false);

            var symbol = SymbolReadbackManifestBuilder.BuildEntry(
                packageDirectory,
                PackageId,
                Version);

            Assert.Equal(PackageId, symbol.PackageId);
            Assert.Equal(Version, symbol.PackageVersion);
            Assert.Equal($"{PackageId}.pdb", symbol.PdbName);
            Assert.Matches("^[0-9a-f]{32}FFFFFFFF$", symbol.SymbolKey);
            Assert.EndsWith("FFFFFFFF", symbol.SymbolKey, StringComparison.Ordinal);
            Assert.StartsWith("SHA256:", symbol.ChecksumHeader, StringComparison.Ordinal);
            Assert.Equal(71, symbol.ChecksumHeader.Length);
            Assert.Equal(
                $"https://symbols.nuget.org/download/symbols/{symbol.PdbName}/{symbol.SymbolKey}/{symbol.PdbName}",
                symbol.SymbolUrl);
            Assert.Matches("^[0-9a-f]{64}$", symbol.Sha256);
        }
        finally
        {
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    [Fact]
    public void Candidate_primary_package_must_exist()
    {
        var packageDirectory = CreatePackageDirectory();

        try
        {
            var exception = Assert.Throws<FileNotFoundException>(() =>
                SymbolReadbackManifestBuilder.BuildEntry(
                    packageDirectory,
                    PackageId,
                    Version));

            Assert.EndsWith(
                $"{PackageId}.{Version}.nupkg",
                exception.FileName,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Candidate_primary_package_must_contain_one_exact_assembly_entry()
    {
        var packageDirectory = CreatePackageDirectory();
        var assemblyPath = typeof(SafeMigrationOperation).Assembly.Location;

        try
        {
            await WritePackagePairAsync(
                packageDirectory,
                assemblyPath,
                corruptSymbols: false);

            using (var package = ZipFile.Open(
                       Path.Combine(packageDirectory, $"{PackageId}.{Version}.nupkg"),
                       ZipArchiveMode.Update))
            {
                package.CreateEntryFromFile(
                    assemblyPath,
                    $"lib/net10.0/{PackageId}.dll");
            }

            var exception = Assert.Throws<InvalidDataException>(() =>
                SymbolReadbackManifestBuilder.BuildEntry(
                    packageDirectory,
                    PackageId,
                    Version));

            Assert.Contains("must contain exactly one", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Candidate_symbols_must_match_the_checksum_sealed_into_the_assembly()
    {
        var packageDirectory = CreatePackageDirectory();

        try
        {
            await WritePackagePairAsync(
                packageDirectory,
                typeof(SafeMigrationOperation).Assembly.Location,
                corruptSymbols: true);

            var exception = Assert.Throws<InvalidDataException>(() =>
                SymbolReadbackManifestBuilder.BuildEntry(
                    packageDirectory,
                    PackageId,
                    Version));

            Assert.Contains(
                "does not match the checksum sealed into its assembly",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    private static string CreatePackageDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"safe-migrations-symbol-readback-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        return directory;
    }

    private static async Task WritePackagePairAsync(
        string packageDirectory,
        string assemblyPath,
        bool corruptSymbols
    )
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        var assemblyEntry = $"lib/net10.0/{PackageId}.dll";
        var pdbEntry = $"lib/net10.0/{PackageId}.pdb";

        using (var package = ZipFile.Open(
                   Path.Combine(packageDirectory, $"{PackageId}.{Version}.nupkg"),
                   ZipArchiveMode.Create))
        {
            package.CreateEntryFromFile(assemblyPath, assemblyEntry);
        }

        var pdb = await File.ReadAllBytesAsync(pdbPath, CancellationToken.None);

        if (corruptSymbols)
        {
            pdb[^1] ^= 0xff;
        }

        using var symbols = ZipFile.Open(
            Path.Combine(packageDirectory, $"{PackageId}.{Version}.snupkg"),
            ZipArchiveMode.Create);

        var entry = symbols.CreateEntry(pdbEntry);
        await using var stream = entry.Open();
        await stream.WriteAsync(pdb, CancellationToken.None);
    }
}
