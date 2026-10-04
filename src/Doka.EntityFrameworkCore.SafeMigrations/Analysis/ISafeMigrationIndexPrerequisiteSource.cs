namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Resolves index dependencies accepted by a provider's expression contract.</summary>
internal interface ISafeMigrationIndexPrerequisiteSource
{
    /// <summary>Gets every local column required by a supported index definition.</summary>
    /// <param name="intent">The index whose prerequisites are inspected.</param>
    /// <returns>Distinct column names, including provider-recognized raw predicate identifiers.</returns>
    IReadOnlyList<string> GetIndexPrerequisiteColumns(EnsureIndexIntent intent);
}
