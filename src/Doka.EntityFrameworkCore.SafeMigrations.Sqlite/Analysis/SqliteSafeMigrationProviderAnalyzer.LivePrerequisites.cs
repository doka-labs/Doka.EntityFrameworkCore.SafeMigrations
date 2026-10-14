namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    private SqliteLivePrerequisiteSource? _livePrerequisiteSource;

    /// <inheritdoc />
    public bool HasLiveColumn(string table, string? schema, string column) =>
        _livePrerequisiteSource?.HasLiveColumn(table, schema, column) == true;

    /// <inheritdoc />
    public bool RequiresLiveForeignKeyQualification(EnsureForeignKeyIntent intent) =>
        _livePrerequisiteSource?.RequiresLiveForeignKeyQualification(intent) == true;

    /// <inheritdoc />
    public IReadOnlyList<SafeMigrationLiveCandidateKey>? GetLiveForeignKeyCandidateKeys(
        EnsureForeignKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns) =>
        _livePrerequisiteSource?.GetLiveForeignKeyCandidateKeys(intent, columns);
}
