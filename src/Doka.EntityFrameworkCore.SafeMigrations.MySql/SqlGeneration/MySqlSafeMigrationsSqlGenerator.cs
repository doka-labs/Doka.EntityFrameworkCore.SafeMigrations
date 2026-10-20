namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Carries one complete ordered migration contract across Doka's
/// per-operation handler calls while delegating SQL rendering unchanged.
/// </summary>
internal sealed class MySqlSafeMigrationsSqlGenerator : IMigrationsSqlGenerator
{
    private readonly IMigrationsSqlGenerator _providerGenerator;
    private readonly MySqlSafeMigrationPlanCapture _planCapture;
    private readonly MigrationsSqlGeneratorDependencies? _dependencies;

    public MySqlSafeMigrationsSqlGenerator(
        IMigrationsSqlGenerator providerGenerator,
        MySqlSafeMigrationPlanCapture planCapture,
        MigrationsSqlGeneratorDependencies? dependencies = null
    )
    {
        ArgumentNullException.ThrowIfNull(providerGenerator);
        ArgumentNullException.ThrowIfNull(planCapture);

        _providerGenerator = providerGenerator;
        _planCapture = planCapture;
        _dependencies = dependencies;
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

        var generatedCandidates = MySqlGeneratedColumnModelCandidates.CaptureTargets(
            operations,
            null,
            model,
            _dependencies?.CurrentContext.Context.GetService<IModelRuntimeInitializer>());

        using var generation = _planCapture.BeginGeneration(operations, generatedCandidates);
        var commands = _providerGenerator.Generate(operations, model, options);
        if ((options & MigrationsSqlGenerationOptions.Script) != 0)
        {
            return commands;
        }

        MigrationCommand[]? decorated = null;
        IRelationalCommand? metadataCommand = null;
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            var diagnostic = _planCapture.FindRuntimeDiagnostic(command.CommandText);
            if (diagnostic is null)
            {
                continue;
            }

            var dependencies = _dependencies
                ?? throw new InvalidOperationException(
                    "MySQL runtime diagnostics require the registered EF migrations command services.");

            // WHY: The native scoped command remains the execution owner.
            // A shared empty base command avoids duplicating every SQL payload.
            decorated ??= commands.ToArray();
            metadataCommand ??= dependencies
                .CommandBuilderFactory
                .Create()
                .Build();

            decorated[index] = new MySqlSafeMigrationDiagnosticCommand(
                command,
                diagnostic,
                dependencies,
                metadataCommand);
        }

        return decorated ?? commands;
    }
}
