namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Generates ordinary SQLite commands through the configured EF Core provider.</summary>
internal interface ISqliteSafeMigrationsBaselineGenerator
{
    /// <summary>Generates baseline commands for operations not rendered by SafeMigrations.</summary>
    /// <param name="operations">The ordered provider operations.</param>
    /// <param name="model">The target model, when available.</param>
    /// <param name="options">The native SQL generation options.</param>
    /// <param name="guardedOperations">
    /// The original guarded stream, or null for ordinary provider generation. Its operation identities preserve
    /// ordinary fresh creators even when adjacent safe operations require complete generated-column facets.
    /// Explicit consumer generators remain delegated unchanged and must satisfy the runtime postconditions.
    /// </param>
    /// <returns>The executable native migration commands.</returns>
    IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default,
        IReadOnlyList<MigrationOperation>? guardedOperations = null
    );
}
