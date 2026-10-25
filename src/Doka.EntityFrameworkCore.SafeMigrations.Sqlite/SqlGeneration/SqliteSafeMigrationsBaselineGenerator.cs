namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Adapts the configured EF Core SQLite generator to the provider wrapper.</summary>
internal sealed class SqliteSafeMigrationsBaselineGenerator<TGenerator> : ISqliteSafeMigrationsBaselineGenerator
    where TGenerator : class, IMigrationsSqlGenerator
{
    private readonly TGenerator _generator;

    /// <summary>Initializes the baseline-generator adapter.</summary>
    public SqliteSafeMigrationsBaselineGenerator(
        TGenerator generator
    )
    {
        ArgumentNullException.ThrowIfNull(generator);

        _generator = generator;
    }

    /// <inheritdoc />
    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default,
        IReadOnlyList<MigrationOperation>? guardedOperations = null
    ) => guardedOperations is not null && _generator is SqliteSafeMigrationsBaselineSqlGenerator native
        ? native.GenerateSafe(operations, model, options, guardedOperations)
        : _generator.Generate(operations, model, options);
}
