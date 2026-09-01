namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Projects provider-owned create-index metadata into provider-neutral
/// SafeMigrations scaffolding input.
/// </summary>
internal interface ISafeMigrationCreateIndexScaffoldingProjector
{
    /// <summary>Projects one EF Core create-index operation.</summary>
    /// <param name="operation">The original provider-produced operation.</param>
    /// <returns>The sanitized operation and provider-neutral index metadata.</returns>
    SafeMigrationCreateIndexScaffoldingProjection Project(CreateIndexOperation operation);
}

/// <summary>Contains one immutable create-index scaffolding projection.</summary>
/// <param name="Operation">The operation whose remaining metadata EF Core should render.</param>
/// <param name="PrefixLengths">
/// Ordered key prefix lengths, where zero means the complete key, or null when absent.
/// </param>
internal sealed record SafeMigrationCreateIndexScaffoldingProjection(
    CreateIndexOperation Operation,
    IReadOnlyList<int>? PrefixLengths
);
