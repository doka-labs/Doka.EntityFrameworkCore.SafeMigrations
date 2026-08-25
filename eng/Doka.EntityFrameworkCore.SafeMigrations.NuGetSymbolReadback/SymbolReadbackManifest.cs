namespace Doka.EntityFrameworkCore.SafeMigrations.NuGetSymbolReadback;

internal sealed record SymbolReadbackManifest(
    int SchemaVersion,
    string ReleaseVersion,
    IReadOnlyList<SymbolReadbackEntry> Symbols
);

internal sealed record SymbolReadbackEntry(
    string PackageId,
    string PackageVersion,
    string PdbName,
    string SymbolKey,
    string SymbolUrl,
    string ChecksumHeader,
    string Sha256
);
