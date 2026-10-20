namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationProviderTargetModelAnalyzer
{
    private IReadOnlyList<MigrationOperation>? _analysisOperationStream;
    private IReadOnlyList<IModel?>? _analysisTargetModels;

    /// <inheritdoc />
    async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> ISafeMigrationProviderTargetModelAnalyzer.AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<IModel?>? targetModels,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);
        if (targetModels is not null
            && targetModels.Count != operations.Count)
        {
            throw new ArgumentException("The target-model list must match the operation stream.", nameof(targetModels));
        }

        if (_analysisOperationStream is not null)
        {
            throw new InvalidOperationException("A MySQL ordered target-model analysis invocation is already active.");
        }

        // WHY: Pending migrations supply the historical target at each exact
        // operation. A current context model is neither that target nor proof
        // of native state; ordinary SQL boundaries must also remain visible.
        _analysisOperationStream = operations;
        _analysisTargetModels = targetModels;
        try
        {
            return await AnalyzeCoreAsync(
                context,
                operations
                    .OfType<SafeMigrationOperation>()
                    .ToArray(),
                captureRenameSources: true,
                cancellationToken);
        }
        finally
        {
            _analysisTargetModels = null;
            _analysisOperationStream = null;
        }
    }
}
