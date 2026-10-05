namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed class SqlServerSafeMigrationsBaselineGenerator<TGenerator> : ISqlServerSafeMigrationsBaselineGenerator
    where TGenerator : class, IMigrationsSqlGenerator
{
    private readonly TGenerator _generator;

    public SqlServerSafeMigrationsBaselineGenerator(
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
