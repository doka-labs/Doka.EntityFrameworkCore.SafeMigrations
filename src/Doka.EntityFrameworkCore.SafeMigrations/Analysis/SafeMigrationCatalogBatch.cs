namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Executes bounded catalog statements through provider batching when
/// available and through sequential commands for compatible wrappers.
/// </summary>
internal sealed class SafeMigrationCatalogBatch : IAsyncDisposable
{
    private readonly DbBatch? _providerBatch;
    private readonly List<DbCommand>? _sequentialCommands;
    private readonly DbConnection _connection;
    private readonly int? _commandTimeout;
    private bool _disposed;

    /// <summary>Creates one bounded catalog transport for the supplied connection.</summary>
    /// <param name="connection">The open provider or compatible wrapper connection.</param>
    /// <param name="commandTimeout">The optional timeout applied to every catalog statement.</param>
    public SafeMigrationCatalogBatch(
        DbConnection connection,
        int? commandTimeout
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _commandTimeout = commandTimeout;
        if (connection.CanCreateBatch)
        {
            _providerBatch = connection.CreateBatch();
            if (commandTimeout is not null)
            {
                _providerBatch.Timeout = commandTimeout.Value;
            }
        }
        else
        {
            // ADO.NET wrappers inherit CanCreateBatch=false unless they
            // deliberately forward DbBatch. Sequential bounded statements
            // preserve correctness and compatibility without rebuilding a
            // provider-specific multi-statement protocol in SafeMigrations.
            _sequentialCommands = [];
        }
    }

    /// <summary>Gets the number of catalog statements currently owned by this transport.</summary>
    public int Count => _providerBatch?.BatchCommands.Count ?? _sequentialCommands!.Count;

    /// <summary>Creates and registers the next ordered catalog statement.</summary>
    /// <returns>A provider-neutral command adapter owned by this transport.</returns>
    public SafeMigrationCatalogCommand CreateCommand()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_providerBatch is not null)
        {
            var command = _providerBatch.CreateBatchCommand();
            _providerBatch.BatchCommands.Add(command);

            return new SafeMigrationCatalogCommand(this, command);
        }

        var sequentialCommand = _connection.CreateCommand();
        if (_commandTimeout is not null)
        {
            sequentialCommand.CommandTimeout = _commandTimeout.Value;
        }

        _sequentialCommands!.Add(sequentialCommand);

        return new SafeMigrationCatalogCommand(this, sequentialCommand);
    }

    /// <summary>Removes an unexecuted trailing statement that exceeded a payload budget.</summary>
    /// <param name="command">The trailing command returned by <see cref="CreateCommand"/>.</param>
    /// <exception cref="InvalidOperationException">
    /// The command is not the last statement owned by this transport.
    /// </exception>
    public void RemoveLastCommand(
        SafeMigrationCatalogCommand command
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!ReferenceEquals(command.Owner, this)
            || Count == 0)
        {
            throw new InvalidOperationException("The catalog command does not belong to this batch.");
        }

        if (_providerBatch is not null)
        {
            var providerCommand = command.ProviderCommand
                ?? throw new InvalidOperationException("The catalog command has an invalid provider shape.");

            if (!ReferenceEquals(_providerBatch.BatchCommands[^1], providerCommand))
            {
                throw new InvalidOperationException("Only the last catalog command can be removed.");
            }

            _providerBatch.BatchCommands.RemoveAt(_providerBatch.BatchCommands.Count - 1);

            return;
        }

        var sequentialCommand = command.SequentialCommand
            ?? throw new InvalidOperationException("The catalog command has an invalid sequential shape.");

        if (!ReferenceEquals(_sequentialCommands![^1], sequentialCommand))
        {
            throw new InvalidOperationException("Only the last catalog command can be removed.");
        }

        _sequentialCommands.RemoveAt(_sequentialCommands.Count - 1);
        sequentialCommand.Dispose();
    }

    /// <summary>Reads every result set in statement order.</summary>
    /// <param name="readResultSet">The asynchronous result-set consumer.</param>
    /// <param name="cancellationToken">The token that cancels execution and reading.</param>
    /// <returns>A task that completes after all result sets have been consumed.</returns>
    public async Task ForEachResultSetAsync(
        Func<DbDataReader, CancellationToken, Task> readResultSet,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(readResultSet);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_providerBatch is not null)
        {
            await using var reader = await _providerBatch.ExecuteReaderAsync(cancellationToken);
            await ReadResultSetsAsync(reader, readResultSet, cancellationToken);

            return;
        }

        foreach (var command in _sequentialCommands!)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await ReadResultSetsAsync(reader, readResultSet, cancellationToken);
        }
    }

    /// <summary>Disposes the native batch or every sequential command.</summary>
    /// <returns>A value task that represents asynchronous disposal.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_providerBatch is not null)
        {
            await _providerBatch.DisposeAsync();

            return;
        }

        foreach (var command in _sequentialCommands!)
        {
            await command.DisposeAsync();
        }
    }

    private static async Task ReadResultSetsAsync(
        DbDataReader reader,
        Func<DbDataReader, CancellationToken, Task> readResultSet,
        CancellationToken cancellationToken
    )
    {
        do
        {
            await readResultSet(reader, cancellationToken);
        } while (await reader.NextResultAsync(cancellationToken));
    }
}

/// <summary>Provides one provider-neutral catalog command under batch ownership.</summary>
internal sealed class SafeMigrationCatalogCommand
{
    private readonly DbBatchCommand? _providerCommand;
    private readonly DbCommand? _sequentialCommand;

    internal SafeMigrationCatalogCommand(
        SafeMigrationCatalogBatch owner,
        DbBatchCommand command
    )
    {
        Owner = owner;
        _providerCommand = command;
    }

    internal SafeMigrationCatalogCommand(
        SafeMigrationCatalogBatch owner,
        DbCommand command
    )
    {
        Owner = owner;
        _sequentialCommand = command;
    }

    /// <summary>Gets the parameters owned by this statement.</summary>
    public DbParameterCollection Parameters => _providerCommand?.Parameters ?? _sequentialCommand!.Parameters;

    /// <summary>Gets or sets the SQL text for this statement.</summary>
    public string CommandText
    {
        get => _providerCommand?.CommandText ?? _sequentialCommand!.CommandText;
        set
        {
            if (_providerCommand is not null)
            {
                _providerCommand.CommandText = value;
            }
            else
            {
                _sequentialCommand!.CommandText = value;
            }
        }
    }

    /// <summary>Creates a provider parameter compatible with this statement.</summary>
    /// <returns>A provider-specific parameter that is not yet registered.</returns>
    public DbParameter CreateParameter() =>
        _providerCommand?.CreateParameter() ?? _sequentialCommand!.CreateParameter();

    internal SafeMigrationCatalogBatch Owner { get; }

    internal DbBatchCommand? ProviderCommand => _providerCommand;

    internal DbCommand? SequentialCommand => _sequentialCommand;
}
