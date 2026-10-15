namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Adds actionable context only after the original Doka command has completed its cleanup.</summary>
internal sealed class MySqlSafeMigrationDiagnosticCommand : MigrationCommand
{
    private readonly MySqlSafeMigrationRuntimeDiagnostic _diagnostic;

    /// <summary>Retains scoped native execution instead of copying its body into an ordinary EF command.</summary>
    /// <param name="original">The native command whose resource and transaction ownership remains authoritative.</param>
    /// <param name="diagnostic">The bounded authored operation and correlation.</param>
    /// <param name="dependencies">The existing EF metadata services.</param>
    /// <param name="metadataCommand">A shared empty base command, not a copy of the native SQL.</param>
    internal MySqlSafeMigrationDiagnosticCommand(
        MigrationCommand original,
        MySqlSafeMigrationRuntimeDiagnostic diagnostic,
        MigrationsSqlGeneratorDependencies dependencies,
        IRelationalCommand metadataCommand
    ) : base(metadataCommand, dependencies.CurrentContext.Context, original.CommandLogger,
        original.TransactionSuppressed)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(metadataCommand);

        OriginalCommand = original;
        _diagnostic = diagnostic;
    }

    /// <summary>Gets the untouched native resource owner delegated by every metadata and execution member.</summary>
    internal MigrationCommand OriginalCommand { get; }

    /// <inheritdoc />
    public override string CommandText => OriginalCommand.CommandText;

    /// <inheritdoc />
    public override bool TransactionSuppressed => OriginalCommand.TransactionSuppressed;

    /// <inheritdoc />
    public override IRelationalCommandDiagnosticsLogger CommandLogger => OriginalCommand.CommandLogger;

    /// <inheritdoc />
    public override int ExecuteNonQuery(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null
    )
    {
        try
        {
            return OriginalCommand.ExecuteNonQuery(connection, parameterValues);
        }
        catch (MySqlException exception) when (_diagnostic.TryReadFailure(exception, out _, out _))
        {
            // WHY: Doka's finally/recovery has finished before this catch. Its
            // recovery exception, cancellation and unrelated native failures
            // bypass this exact main-guard filter and retain their identity.
            throw CreateFailure(exception);
        }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await OriginalCommand.ExecuteNonQueryAsync(connection, parameterValues, cancellationToken);
        }
        catch (MySqlException exception) when (_diagnostic.TryReadFailure(exception, out _, out _))
        {
            throw CreateFailure(exception);
        }
    }

    /// <summary>Formats context once on failure; successful execution does not build human-readable messages.</summary>
    private SafeMigrationExecutionException CreateFailure(MySqlException exception)
    {
        _diagnostic.TryReadFailure(exception, out var guardCode, out var reasonSlot);

        return new SafeMigrationExecutionException(_diagnostic.Context, guardCode, _diagnostic.Ordinal,
            exception, _diagnostic.GetReasonCode(reasonSlot));
    }
}
