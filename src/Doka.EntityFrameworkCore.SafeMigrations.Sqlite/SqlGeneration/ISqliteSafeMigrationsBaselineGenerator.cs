namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Generates ordinary SQLite commands through the configured EF Core provider.</summary>
internal interface ISqliteSafeMigrationsBaselineGenerator
{
    /// <summary>Generates baseline commands for operations not rendered by SafeMigrations.</summary>
    IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    );
}
