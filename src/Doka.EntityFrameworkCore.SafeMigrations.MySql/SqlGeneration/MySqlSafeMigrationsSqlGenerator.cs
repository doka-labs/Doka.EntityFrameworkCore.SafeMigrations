namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Carries one complete ordered migration contract across Doka's
/// per-operation handler calls while delegating SQL rendering unchanged.
/// </summary>
internal sealed class MySqlSafeMigrationsSqlGenerator : IMigrationsSqlGenerator
{
    private readonly IMigrationsSqlGenerator _providerGenerator;
    private readonly MySqlSafeMigrationPlanCapture _planCapture;

    public MySqlSafeMigrationsSqlGenerator(
        IMigrationsSqlGenerator providerGenerator,
        MySqlSafeMigrationPlanCapture planCapture
    )
    {
        ArgumentNullException.ThrowIfNull(providerGenerator);
        ArgumentNullException.ThrowIfNull(planCapture);

        _providerGenerator = providerGenerator;
        _planCapture = planCapture;
    }

    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (_planCapture.IsActive
            || _planCapture.HasGenerationContract)
        {
            return _providerGenerator.Generate(operations, model, options);
        }

        using var generation = _planCapture.BeginGeneration(operations);

        return _providerGenerator.Generate(operations, model, options);
    }
}
