namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class PostgreSqlSafeMigrationsBaselineGenerator<TGenerator> : IPostgreSqlSafeMigrationsBaselineGenerator
    where TGenerator : class, IMigrationsSqlGenerator
{
    private readonly TGenerator _generator;

    /// <summary>Retains the actual baseline generator selected by dependency injection.</summary>
    /// <param name="generator">The caller-configured native generator instance.</param>
    public PostgreSqlSafeMigrationsBaselineGenerator(
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
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    ) => _generator.Generate(operations, model, options);
}
