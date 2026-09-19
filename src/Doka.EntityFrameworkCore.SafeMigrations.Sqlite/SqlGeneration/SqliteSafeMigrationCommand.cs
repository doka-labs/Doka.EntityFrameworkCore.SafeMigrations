namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Executes one non-structural SQLite safe operation with runtime guards.</summary>
internal sealed class SqliteSafeMigrationCommand : MigrationCommand
{
    private readonly SqliteSafeMigrationProviderAnalyzer _analyzer;
    private readonly SqliteSafeMigrationExecutionState _executionState;
    private readonly SqliteRebuildArtifactContract _rebuildArtifacts;
    private readonly SafeMigrationOperation _operation;
    private readonly IReadOnlyList<MigrationCommand> _applyCommands;

    /// <summary>Initializes a guarded SQLite migration command.</summary>
    public SqliteSafeMigrationCommand(
        SafeMigrationOperation operation,
        IReadOnlyList<MigrationCommand> applyCommands,
        SqliteSafeMigrationProviderAnalyzer analyzer,
        SqliteSafeMigrationExecutionState executionState,
        SqliteRebuildArtifactContract rebuildArtifacts,
        IRelationalCommand placeholder,
        DbContext context,
        IRelationalCommandDiagnosticsLogger logger
    ) : base(placeholder, context, logger, transactionSuppressed: false)
    {
        _operation = operation;
        _applyCommands = applyCommands;
        _analyzer = analyzer;
        _executionState = executionState;
        _rebuildArtifacts = rebuildArtifacts;
    }

    /// <inheritdoc />
    public override string CommandText =>
        $"-- SQLite SafeMigrations: {_operation.Intent.Kind} {_operation.Intent.ObjectName}";

    /// <inheritdoc />
    public override int ExecuteNonQuery(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.CurrentTransaction is null)
        {
            using var localTransaction = connection.BeginTransaction();
            try
            {
                var localAffected = ExecuteNonQuery(connection, parameterValues);
                localTransaction.Commit();

                return localAffected;
            }
            catch
            {
                localTransaction.Rollback();
                throw;
            }
        }

        var transaction = connection.CurrentTransaction?.GetDbTransaction();
        var snapshot = _executionState.GetSnapshot(connection);
        var analysis = _analyzer.Analyze(snapshot, connection.DbConnection, transaction, _operation, _rebuildArtifacts);

        var decision = SafeMigrationDecisionPlanner.Plan(
            _operation.Intent.Kind,
            analysis.ObservedState,
            _operation.Policy,
            analysis.RepairCapability);

        if (decision.Action.RejectsExecution())
        {
            throw CreateBlockedException(analysis, decision);
        }

        if (decision.Action == SafeMigrationAction.NoOp)
        {
            return 0;
        }

        var affected = _operation.Intent switch
        {
            ModelManagedDataIntent modelManagedData =>
                ExecuteModelManagedData(connection.DbConnection, transaction, modelManagedData),
            RenameIndexIntent renameIndex => ExecuteRenameIndex(
                connection.DbConnection,
                transaction
                ?? throw new InvalidOperationException(
                    "SQLite SafeMigrations index renames require an active transaction."),
                snapshot,
                renameIndex),
            _ => ExecuteCommands(connection, parameterValues, _applyCommands),
        };

        if (_operation.Intent is not ModelManagedDataIntent)
        {
            _executionState.Invalidate();
        }

        var postSnapshot = _executionState.GetSnapshot(connection);
        var postcondition = _analyzer.Analyze(
            postSnapshot,
            connection.DbConnection,
            transaction,
            _operation,
            _rebuildArtifacts);

        if (!postcondition.PostconditionSatisfied)
        {
            throw new InvalidOperationException(
                $"SQLite SafeMigrations postcondition failed for {_operation.Intent.Kind} "
                + $"'{_operation.Intent.ObjectName}' with analysis code '{postcondition.Code}'.");
        }

        return affected;
    }

    /// <inheritdoc />
    public override Task<int> ExecuteNonQueryAsync(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // WHY: Microsoft.Data.Sqlite executes asynchronous ADO.NET calls
        // synchronously. Keeping one synchronous path avoids duplicate safety
        // logic and preserves the connection's single-threaded transaction ownership.
        return Task.FromResult(ExecuteNonQuery(connection, parameterValues));
    }

    private static int ExecuteCommands(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues,
        IReadOnlyList<MigrationCommand> commands
    )
    {
        if (commands.Count == 0)
        {
            throw new InvalidOperationException(
                "SQLite SafeMigrations accepted an operation without an executable baseline.");
        }

        if (commands.Any(static command => command.TransactionSuppressed))
        {
            throw new NotSupportedException(
                "SQLite SafeMigrations cannot guard an unexpected transaction-suppressed baseline command.");
        }

        var affected = 0;
        foreach (var command in commands)
        {
            affected += command.ExecuteNonQuery(connection, parameterValues);
        }

        return affected;
    }

    private static int ExecuteRenameIndex(
        DbConnection connection,
        DbTransaction transaction,
        SqliteCatalogSnapshot snapshot,
        RenameIndexIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            throw new InvalidOperationException("The accepted SQLite index rename lost its table prerequisite.");
        }

        var source = table.Indexes.FirstOrDefault(index =>
            SqliteIdentifierComparer.Instance.Equals(index.Name, intent.Name));

        if (source is null
            || source.Origin != "c")
        {
            throw new InvalidOperationException("The accepted SQLite index rename lost its source index.");
        }

        var keys = source
            .Keys
            .Where(static key => key.IsKey)
            .OrderBy(static key => key.Ordinal)
            .Select(RenderExistingIndexKey);

        var createSql = "CREATE "
            + (source.Unique ? "UNIQUE " : string.Empty)
            + "INDEX "
            + Delimit(intent.NewName)
            + " ON "
            + Delimit(intent.Table)
            + " ("
            + string.Join(", ", keys)
            + ")"
            + (source.Filter is null ? string.Empty : " WHERE " + source.Filter)
            + ";";

        var dropSql = "DROP INDEX " + Delimit(intent.Name) + ";";

        return ExecuteSql(connection, transaction, createSql) + ExecuteSql(connection, transaction, dropSql);
    }

    private static string RenderExistingIndexKey(
        SqliteIndexKeySnapshot key
    )
    {
        var result = key.Column is null
            ? key.Expression ?? throw new InvalidOperationException("SQLite returned an index key without identity.")
            : Delimit(key.Column);

        if (!StringComparer.OrdinalIgnoreCase.Equals(key.Collation, "BINARY"))
        {
            result += " COLLATE " + Delimit(key.Collation);
        }

        if (key.Descending)
        {
            result += " DESC";
        }

        return result;
    }

    private static int ExecuteSql(
        DbConnection connection,
        DbTransaction transaction,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return command.ExecuteNonQuery();
    }

    private static int ExecuteModelManagedData(
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent
    ) => intent switch
    {
        EnsureModelManagedDataIntent value => InsertMissingRows(connection, transaction, value),
        UpdateModelManagedDataIntent value => UpdateSourceRows(connection, transaction, value),
        DeleteModelManagedDataIntent value => DeleteSourceRows(connection, transaction, value),
        _ => throw new UnreachableException(),
    };

    private static int InsertMissingRows(
        DbConnection connection,
        DbTransaction? transaction,
        EnsureModelManagedDataIntent intent
    )
    {
        var affected = 0;
        var parameterCountPerRow = intent.KeyColumns.Count + intent.Columns.Count;
        var batchSize = Math.Max(1, SqliteSafeMigrationLimits.MaximumParametersPerCommand / parameterCountPerRow);

        for (var offset = 0; offset < intent.RowCount; offset += batchSize)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var count = Math.Min(batchSize, intent.RowCount - offset);
            var cteColumns = Enumerable
                .Range(0, intent.KeyColumns.Count)
                .Select(static index => $"key_{index}")
                .Concat(
                    Enumerable
                        .Range(0, intent.Columns.Count)
                        .Select(static index => $"value_{index}"))
                .ToArray();

            var rows = new string[count];
            for (var localRow = 0; localRow < count; localRow++)
            {
                var row = offset + localRow;
                var parameters = new string[parameterCountPerRow];
                for (var column = 0; column < intent.KeyColumns.Count; column++)
                {
                    var name = $"$r{localRow}_k{column}";
                    parameters[column] = name;
                    AddParameter(command, name, intent.KeyValues.GetUnsafeValue(row, column));
                }

                for (var column = 0; column < intent.Columns.Count; column++)
                {
                    var name = $"$r{localRow}_v{column}";
                    parameters[intent.KeyColumns.Count + column] = name;
                    AddParameter(command, name, intent.Values.GetUnsafeValue(row, column));
                }

                rows[localRow] = "(" + string.Join(", ", parameters) + ")";
            }

            var keyPredicate = string.Join(
                " AND ",
                intent.KeyColumns.Select((
                        column,
                        index
                    ) => $"stored.{Delimit(column)} IS incoming.{Delimit($"key_{index}")}"));

            command.CommandText = $"WITH incoming ({string.Join(", ", cteColumns.Select(Delimit))}) AS ("
                + $"VALUES {string.Join(", ", rows)}) INSERT INTO {Delimit(intent.Table)} "
                + $"({string.Join(", ", intent.Columns.Select(Delimit))}) "
                + "SELECT "
                + string.Join(
                    ", ",
                    Enumerable
                        .Range(0, intent.Columns.Count)
                        .Select(static index => $"incoming.\"value_{index}\""))
                + " FROM incoming WHERE NOT EXISTS ("
                + $"SELECT 1 FROM {Delimit(intent.Table)} AS stored WHERE {keyPredicate});";
            affected += command.ExecuteNonQuery();
        }

        return affected;
    }

    private static int UpdateSourceRows(
        DbConnection connection,
        DbTransaction? transaction,
        UpdateModelManagedDataIntent intent
    )
    {
        var affected = 0;
        var parameterCountPerRow = intent.KeyColumns.Count + (intent.Columns.Count * 2);
        var batchSize = Math.Max(1, SqliteSafeMigrationLimits.MaximumParametersPerCommand / parameterCountPerRow);

        for (var offset = 0; offset < intent.RowCount; offset += batchSize)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var count = Math.Min(batchSize, intent.RowCount - offset);
            var cteColumns = Enumerable
                .Range(0, intent.KeyColumns.Count)
                .Select(static index => $"key_{index}")
                .Concat(
                    Enumerable
                        .Range(0, intent.Columns.Count)
                        .Select(static index => $"source_{index}"))
                .Concat(
                    Enumerable
                        .Range(0, intent.Columns.Count)
                        .Select(static index => $"target_{index}"))
                .ToArray();

            var rows = new string[count];
            for (var localRow = 0; localRow < count; localRow++)
            {
                var row = offset + localRow;
                var parameters = new string[parameterCountPerRow];
                var parameterIndex = 0;
                for (var column = 0; column < intent.KeyColumns.Count; column++)
                {
                    var name = $"$r{localRow}_k{column}";
                    parameters[parameterIndex++] = name;
                    AddParameter(command, name, intent.KeyValues.GetUnsafeValue(row, column));
                }

                for (var column = 0; column < intent.Columns.Count; column++)
                {
                    var name = $"$r{localRow}_s{column}";
                    parameters[parameterIndex++] = name;
                    AddParameter(command, name, intent.OldValues.GetUnsafeValue(row, column));
                }

                for (var column = 0; column < intent.Columns.Count; column++)
                {
                    var name = $"$r{localRow}_t{column}";
                    parameters[parameterIndex++] = name;
                    AddParameter(command, name, intent.NewValues.GetUnsafeValue(row, column));
                }

                rows[localRow] = "(" + string.Join(", ", parameters) + ")";
            }

            var matchPredicate = ModelManagedMatchPredicate(intent, "stored", "incoming", includeSource: true);
            var assignments = intent.Columns.Select((
                    column,
                    index
                ) => $"{Delimit(column)} = (SELECT incoming.{Delimit($"target_{index}")} "
                + $"FROM incoming WHERE {matchPredicate} LIMIT 1)");

            command.CommandText = $"WITH incoming ({string.Join(", ", cteColumns.Select(Delimit))}) AS ("
                + $"VALUES {string.Join(", ", rows)}) UPDATE {Delimit(intent.Table)} AS stored "
                + $"SET {string.Join(", ", assignments)} WHERE EXISTS ("
                + $"SELECT 1 FROM incoming WHERE {matchPredicate});";
            affected += command.ExecuteNonQuery();
        }

        return affected;
    }

    private static int DeleteSourceRows(
        DbConnection connection,
        DbTransaction? transaction,
        DeleteModelManagedDataIntent intent
    )
    {
        var affected = 0;
        var parameterCountPerRow = intent.KeyColumns.Count + intent.Columns.Count;
        var batchSize = Math.Max(1, SqliteSafeMigrationLimits.MaximumParametersPerCommand / parameterCountPerRow);

        for (var offset = 0; offset < intent.RowCount; offset += batchSize)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var count = Math.Min(batchSize, intent.RowCount - offset);
            var cteColumns = Enumerable
                .Range(0, intent.KeyColumns.Count)
                .Select(static index => $"key_{index}")
                .Concat(
                    Enumerable
                        .Range(0, intent.Columns.Count)
                        .Select(static index => $"source_{index}"))
                .ToArray();

            var rows = new string[count];
            for (var localRow = 0; localRow < count; localRow++)
            {
                var row = offset + localRow;
                var parameters = new string[parameterCountPerRow];
                var parameterIndex = 0;
                for (var column = 0; column < intent.KeyColumns.Count; column++)
                {
                    var name = $"$r{localRow}_k{column}";
                    parameters[parameterIndex++] = name;
                    AddParameter(command, name, intent.KeyValues.GetUnsafeValue(row, column));
                }

                for (var column = 0; column < intent.Columns.Count; column++)
                {
                    var name = $"$r{localRow}_s{column}";
                    parameters[parameterIndex++] = name;
                    AddParameter(command, name, intent.OldValues.GetUnsafeValue(row, column));
                }

                rows[localRow] = "(" + string.Join(", ", parameters) + ")";
            }

            var matchPredicate = ModelManagedMatchPredicate(intent, "stored", "incoming", includeSource: true);

            command.CommandText = $"WITH incoming ({string.Join(", ", cteColumns.Select(Delimit))}) AS ("
                + $"VALUES {string.Join(", ", rows)}) DELETE FROM {Delimit(intent.Table)} AS stored "
                + $"WHERE EXISTS (SELECT 1 FROM incoming WHERE {matchPredicate});";
            affected += command.ExecuteNonQuery();
        }

        return affected;
    }

    private static string ModelManagedMatchPredicate(
        ModelManagedDataIntent intent,
        string storedAlias,
        string incomingAlias,
        bool includeSource
    )
    {
        var predicates = new List<string>(intent.KeyColumns.Count + (includeSource ? intent.Columns.Count : 0));
        predicates.AddRange(
            intent.KeyColumns.Select((
                    column,
                    index
                ) => $"{storedAlias}.{Delimit(column)} IS {incomingAlias}.{Delimit($"key_{index}")}"));
        if (includeSource)
        {
            predicates.AddRange(
                intent.Columns.Select((
                        column,
                        index
                    ) => $"{storedAlias}.{Delimit(column)} IS {incomingAlias}.{Delimit($"source_{index}")}"));
        }

        return string.Join(" AND ", predicates);
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }

    private static string Delimit(
        string identifier
    ) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private InvalidOperationException CreateBlockedException(
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    ) => new(
        $"SQLite SafeMigrations blocked {_operation.Intent.Kind} '{_operation.Intent.ObjectName}': "
        + $"analysis={analysis.Code}, decision={decision.Code}.");
}
