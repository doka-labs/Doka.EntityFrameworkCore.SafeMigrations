namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>
/// Provides standard PostgreSQL migration SQL that SafeMigrations wraps with its guarded execution contract.
/// </summary>
public interface IPostgreSqlSafeMigrationsBaselineGenerator
{
    /// <summary>Generates commands for ordinary EF Core migration operations.</summary>
    /// <param name="operations">The operations to generate.</param>
    /// <param name="model">The target model when available.</param>
    /// <param name="options">The migration SQL generation options.</param>
    /// <returns>The generated commands without SafeMigrations guards.</returns>
    IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    );
}
