namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Supplies real SQLite model services with controlled connection ownership and asynchronous close.</summary>
internal sealed class SqliteRunnerCleanupConnection : DbConnection
{
    private readonly Task _closeCompletion;
    private ConnectionState _state;

    /// <summary>Creates a resource-free connection with independently controlled close completion.</summary>
    /// <param name="closeCompletion">The actual close operation, not a cancelable observer.</param>
    /// <param name="alreadyOpen">Whether the caller already owns the open connection.</param>
    internal SqliteRunnerCleanupConnection(
        Task closeCompletion,
        bool alreadyOpen = false
    )
    {
        _closeCompletion = closeCompletion;
        _state = alreadyOpen
            ? ConnectionState.Open
            : ConnectionState.Closed;
    }

    /// <summary>Gets the signal that the runner has actually started closing its connection.</summary>
    internal TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets the native asynchronous open count.</summary>
    internal int OpenCount { get; private set; }

    /// <summary>Gets the native asynchronous close count.</summary>
    internal int CloseCount { get; private set; }

    /// <summary>Gets unexpected synchronous open or close calls.</summary>
    internal int SynchronousCalls { get; private set; }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString { get; set; } = "Data Source=:memory:";

    /// <inheritdoc />
    public override string Database => "main";

    /// <inheritdoc />
    public override string DataSource => ":memory:";

    /// <inheritdoc />
    public override string ServerVersion => "3.46.0";

    /// <inheritdoc />
    // WHY: Keep this fake connection's getter-only state observation distinct from its explicit Open/Close mutations.
    // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override Task OpenAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenCount++;
        _state = ConnectionState.Open;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task CloseAsync()
    {
        CloseCount++;
        CloseEntered.TrySetResult();

        return _closeCompletion;
    }

    /// <inheritdoc />
    public override void Open()
    {
        SynchronousCalls++;
        _state = ConnectionState.Open;
    }

    /// <inheritdoc />
    public override void Close()
    {
        SynchronousCalls++;
        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    public override void ChangeDatabase(
        string databaseName
    ) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(
        IsolationLevel isolationLevel
    ) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
}
