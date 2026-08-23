namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed class PostgreSqlSafeMigrationsBaselineGenerator<TGenerator> : IPostgreSqlSafeMigrationsBaselineGenerator
    where TGenerator : class, IMigrationsSqlGenerator
{
    private readonly TGenerator _generator;

    public PostgreSqlSafeMigrationsBaselineGenerator(
        TGenerator generator
    )
    {
        ArgumentNullException.ThrowIfNull(generator);

        _generator = generator;
    }

    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    ) => _generator.Generate(operations, model, options);
}
