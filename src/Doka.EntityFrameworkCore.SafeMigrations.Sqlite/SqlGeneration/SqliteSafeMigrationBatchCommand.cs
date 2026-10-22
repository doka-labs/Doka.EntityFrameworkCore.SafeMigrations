namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Executes one ordered SQLite structural segment as an atomic guarded batch.</summary>
internal sealed class SqliteSafeMigrationBatchCommand : MigrationCommand
{
    private readonly ISqliteSafeMigrationsBaselineGenerator _baselineGenerator;
    private readonly SqliteSafeMigrationProviderAnalyzer _analyzer;
    private readonly SqliteSafeMigrationExecutionState _executionState;
    private readonly SqliteSafeMigrationSqlExpressionRenderer _expressionRenderer;
    private readonly IModel? _model;
    private readonly IReadOnlyList<MigrationOperation> _operations;
    private readonly MigrationsSqlGenerationOptions _options;
    private readonly SqliteRebuildArtifactContract _rebuildArtifacts;
    private readonly bool _requiresForeignKeySuspension;
    private readonly int _firstOperationOrdinal;

    /// <summary>Initializes an ordered SQLite structural batch.</summary>
    public SqliteSafeMigrationBatchCommand(
        IReadOnlyList<MigrationOperation> operations,
        ISqliteSafeMigrationsBaselineGenerator baselineGenerator,
        SqliteSafeMigrationProviderAnalyzer analyzer,
        SqliteSafeMigrationExecutionState executionState,
        SqliteSafeMigrationSqlExpressionRenderer expressionRenderer,
        SqliteRebuildArtifactContract rebuildArtifacts,
        IModel? model,
        MigrationsSqlGenerationOptions options,
        IRelationalCommand placeholder,
        DbContext context,
        IRelationalCommandDiagnosticsLogger logger,
        int firstOperationOrdinal
    ) : base(placeholder, context, logger, RequiresForeignKeySuspension(operations))
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(baselineGenerator);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(executionState);
        ArgumentNullException.ThrowIfNull(expressionRenderer);
        ArgumentNullException.ThrowIfNull(rebuildArtifacts);

        if (operations.Count == 0)
        {
            throw new ArgumentException("A SQLite SafeMigrations batch cannot be empty.", nameof(operations));
        }

        _operations = operations;
        _baselineGenerator = baselineGenerator;
        _analyzer = analyzer;
        _executionState = executionState;
        _expressionRenderer = expressionRenderer;
        _rebuildArtifacts = rebuildArtifacts;
        _model = model;
        _options = options;
        _requiresForeignKeySuspension = RequiresForeignKeySuspension(operations);
        _firstOperationOrdinal = firstOperationOrdinal;
    }

    /// <inheritdoc />
    public override string CommandText => $"-- SQLite SafeMigrations batch: {_operations.Count}";

    /// <inheritdoc />
    public override int ExecuteNonQuery(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        // WHY: SQLite cannot change PRAGMA foreign_keys inside a transaction;
        // rebuild batches therefore own a transaction-suppressed outer boundary.
        return _requiresForeignKeySuspension
            ? ExecuteRebuildBatch(connection, parameterValues)
            : ExecuteTransactionalBatch(connection, parameterValues);
    }

    private int ExecuteTransactionalBatch(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues
    )
    {
        if (connection.CurrentTransaction is not null)
        {
            return ExecuteInCurrentTransaction(connection, parameterValues).Affected;
        }

        using var localTransaction = connection.BeginTransaction();
        try
        {
            var affected = ExecuteInCurrentTransaction(connection, parameterValues);
            localTransaction.Commit();

            return affected.Affected;
        }
        catch
        {
            localTransaction.Rollback();
            throw;
        }
    }

    private int ExecuteRebuildBatch(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues
    )
    {
        if (connection.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "SQLite SafeMigrations table rebuilds require a transaction-suppressed command boundary.");
        }

        var foreignKeysEnabled = ReadForeignKeysEnabled(connection.DbConnection);

        try
        {
            if (foreignKeysEnabled)
            {
                SetForeignKeys(connection.DbConnection, enabled: false);
            }

            using var localTransaction = connection.BeginTransaction();
            try
            {
                var affected = ExecuteInCurrentTransaction(connection, parameterValues);
                // WHY: A batch containing only accepted no-ops never rebuilds
                // an owner. Auditing unrelated pre-existing orphans would turn
                // an idempotent absence into a rejection without any mutation.
                // A real DDL statement can report zero affected rows, so use
                // actual accepted execution rather than the provider row count.
                if (foreignKeysEnabled && affected.Executed)
                {
                    var postSnapshot = _executionState.GetSnapshot(connection);
                    VerifyForeignKeys(
                        connection.DbConnection,
                        localTransaction.GetDbTransaction(),
                        ReadForeignKeyValidationTables(postSnapshot),
                        postSnapshot);
                }

                localTransaction.Commit();

                return affected.Affected;
            }
            catch
            {
                localTransaction.Rollback();
                throw;
            }
        }
        finally
        {
            if (foreignKeysEnabled)
            {
                SetForeignKeys(connection.DbConnection, enabled: true);
            }
        }
    }

    private (int Affected, bool Executed) ExecuteInCurrentTransaction(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues
    )
    {
        var transaction = connection.CurrentTransaction?.GetDbTransaction();
        var snapshot = _executionState.GetSnapshot(connection);
        _analyzer.ResetDropOnlyProjection(snapshot);
        var projection = new SafeMigrationPreflightProjection(
            _analyzer,
            projectedKeyAnalyzer: null,
            _analyzer,
            projectedDependencyAnalyzer: _analyzer,
            projectedColumnAnalyzer: _analyzer,
            livePrerequisiteSource: new SqliteLivePrerequisiteSource(snapshot));

        var acceptedOperations = new List<MigrationOperation>(_operations.Count);

        // WHY: Keep operation ordinals aligned with projection and postflight result ownership.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var operationIndex = 0; operationIndex < _operations.Count; operationIndex++)
        {
            var operation = _operations[operationIndex];

            if (operation is not SafeMigrationOperation safeOperation)
            {
                acceptedOperations.Add(operation);
                projection.ObserveProviderPostcondition(operation);
                continue;
            }

            var liveAnalysis = _analyzer.Analyze(
                snapshot,
                connection.DbConnection,
                transaction,
                safeOperation,
                _rebuildArtifacts);

            var analysis = projection.Project(safeOperation, liveAnalysis);
            var decision = SafeMigrationDecisionPlanner.Plan(
                safeOperation.Intent.Kind,
                analysis.ObservedState,
                safeOperation.Policy,
                analysis.RepairCapability);

            if (decision.Action.RejectsExecution())
            {
                throw new InvalidOperationException(SqliteSafeMigrationDiagnosticMessages.FormatBlocked(
                    safeOperation, analysis, decision, _firstOperationOrdinal + operationIndex));
            }

            projection.Observe(safeOperation, liveAnalysis, analysis, decision);
            if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair)
            {
                acceptedOperations.Add(
                    SafeMigrationStandardOperationFactory.Create(
                        safeOperation.Intent,
                        _expressionRenderer.Render,
                        static collation => collation.Schema is null ? collation.Name : null));
            }
        }

        if (acceptedOperations.Count == 0)
        {
            return (0, false);
        }

        var affected = ExecuteAcceptedOperations(connection, parameterValues, acceptedOperations);

        _executionState.Invalidate();
        var postSnapshot = _executionState.GetSnapshot(connection);
        var postconditions = new List<SafeMigrationProviderAnalysis>(_operations.Count);
        foreach (var candidate in _operations)
        {
            if (candidate is SafeMigrationOperation safeOperation)
            {
                postconditions.Add(_analyzer.Analyze(
                    postSnapshot,
                    connection.DbConnection,
                    transaction,
                    safeOperation,
                    _rebuildArtifacts));
            }
        }

        var renamedTables = SafeMigrationPostflightProjection.CreateRenamedTableProbes(
            _operations, postconditions, _analyzer);

        if (renamedTables is not null)
        {
            // WHY: This batch verifies its final state after all accepted DDL,
            // unlike per-command guards. A verified rename changes the earlier
            // complete table contract, not its unrelated facets. Reuse the fresh
            // final snapshot so the stronger proof adds no catalog roundtrip.
            foreach (var probe in renamedTables)
            {
                postconditions[probe.SafeOrdinal] = _analyzer.Analyze(
                    postSnapshot, connection.DbConnection, transaction, probe.Operation, _rebuildArtifacts);
            }
        }

        // WHY: PK alias identity requires the fresh final-catalog proof. Reuse
        // these classifications for verification instead of querying twice.
        var postflight = new SafeMigrationPostflightProjection(
            _operations,
            _analyzer,
            liveAnalyses: postconditions);

        var safeOrdinal = 0;

        // WHY: Keep operation ordinals aligned with projection and postflight result ownership.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < _operations.Count; index++)
        {
            if (_operations[index] is not SafeMigrationOperation operation)
            {
                continue;
            }

            var postcondition = postconditions[safeOrdinal++];

            if (!postcondition.PostconditionSatisfied && !postflight.IsSuperseded(index))
            {
                throw new InvalidOperationException(
                    SafeMigrationDiagnosticMessages.FormatPostcondition(
                        operation, postcondition, _firstOperationOrdinal + index));
            }
        }

        return (affected, true);
    }

    /// <summary>Preserves prerequisite column order when EF folds standalone FKs into new table operations.</summary>
    private int ExecuteAcceptedOperations(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues,
        List<MigrationOperation> operations)
    {
        Dictionary<string, CreateTableOperation>? creators = null;
        var windowStart = 0;
        var affected = 0;

        // WHY: Retain stream ordinals when folding an FK into its correct preceding table creator.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal] is CreateTableOperation create)
            {
                // WHY: EF's folding selects the first preceding exact table
                // spelling, not the latest model or projected column state.
                creators ??= new Dictionary<string, CreateTableOperation>(StringComparer.Ordinal);
                _ = creators.TryAdd(create.Name, create);
            }
            else if (operations[ordinal] is AddForeignKeyOperation foreignKey
                     && creators?.TryGetValue(foreignKey.Table, out var creator) == true
                     && !CreatorContainsForeignKeyColumns(creator, foreignKey))
            {
                // WHY: EF 10's private rewrite otherwise embeds the FK before
                // its separately authored ADD column, yielding invalid DDL.
                // Flush only the baseline-generation window, within this same
                // owned transaction; final snapshot and integrity checks stay
                // shared and strict table column contracts remain unchanged.
                affected += ExecuteAcceptedWindow(
                    connection, parameterValues, operations, windowStart, ordinal - windowStart);
                windowStart = ordinal;
                creators.Clear();
            }
        }

        return affected + ExecuteAcceptedWindow(
            connection, parameterValues, operations, windowStart, operations.Count - windowStart);
    }

    /// <summary>Executes one baseline window without allocating a slice on the normal unsplit path.</summary>
    private int ExecuteAcceptedWindow(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues,
        List<MigrationOperation> operations,
        int start,
        int count)
    {
        IReadOnlyList<MigrationOperation> window = start == 0 && count == operations.Count
            ? operations : operations.GetRange(start, count);

        var commands = _baselineGenerator.Generate(window, _model, _options, guardedOperations: _operations);

        return ExecuteCommands(connection, parameterValues, commands);
    }

    /// <summary>Checks only declared local creator columns, never future ADDs or target-model wishes.</summary>
    private static bool CreatorContainsForeignKeyColumns(
        CreateTableOperation creator,
        AddForeignKeyOperation foreignKey)
    {
        // WHY: Check each required FK member against the creator's explicitly indexed column list.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < foreignKey.Columns.Length; ordinal++)
        {
            var present = false;

            // WHY: Check each required FK member against the creator's explicitly indexed column list.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var candidate = 0; candidate < creator.Columns.Count; candidate++)
            {
                if (SqliteIdentifierComparer.Instance.Equals(
                        creator.Columns[candidate].Name, foreignKey.Columns[ordinal]))
                {
                    present = true;
                    break;
                }
            }

            if (!present)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override Task<int> ExecuteNonQueryAsync(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // WHY: Microsoft.Data.Sqlite implements asynchronous ADO.NET calls
        // synchronously. One execution path keeps the guarded batch and its
        // transaction ownership identical.
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
                "SQLite SafeMigrations accepted a structural batch without an executable baseline.");
        }

        if (commands.Any(command => command.TransactionSuppressed && !IsForeignKeyPragma(command)))
        {
            throw new NotSupportedException(
                "SQLite SafeMigrations cannot guard an unexpected transaction-suppressed baseline command.");
        }

        var affected = 0;
        foreach (var command in commands)
        {
            if (!IsForeignKeyPragma(command))
            {
                affected += command.ExecuteNonQuery(connection, parameterValues);
            }
        }

        return affected;
    }

    private static bool RequiresForeignKeySuspension(
        IReadOnlyList<MigrationOperation> operations
    ) => operations.Any(static operation =>
        SqliteSafeMigrationOperationClassifier.RequiresTableRebuild(operation));

    private HashSet<string> ReadForeignKeyValidationTables(
        SqliteCatalogSnapshot snapshot
    )
    {
        var rebuilt = _operations
            .Where(SqliteSafeMigrationOperationClassifier.RequiresTableRebuild)
            .Select(SqliteSafeMigrationOperationClassifier.TableName)
            .Where(static table => table is not null)
            .Select(static table => table!)
            .ToHashSet(SqliteIdentifierComparer.Instance);

        foreach (var operation in _operations)
        {
            switch (operation)
            {
                case SafeMigrationOperation { Intent: RenameTableIntent value }
                    when rebuilt.Remove(value.Name):
                    _ = rebuilt.Add(value.NewName ?? value.Name);
                    break;
                case RenameTableOperation value when rebuilt.Remove(value.Name):
                    _ = rebuilt.Add(value.NewName ?? value.Name);
                    break;
                case SafeMigrationOperation { Intent: DropTableIntent value }:
                    _ = rebuilt.Remove(value.Table);
                    break;
                case DropTableOperation value:
                    _ = rebuilt.Remove(value.Name);
                    break;
            }
        }

        var result = new HashSet<string>(SqliteIdentifierComparer.Instance);
        foreach (var table in rebuilt)
        {
            if (snapshot.Tables.ContainsKey(table))
            {
                _ = result.Add(table);
            }
        }

        // WHY: An absent DROP is a no-op, not a surviving rebuild owner on
        // which foreign_key_check can execute. Keep the original principal set
        // below so retained children of removed owners are still validated.
        foreach (var table in snapshot.Tables.Values)
        {
            if (table.ForeignKeys.Any(foreignKey => rebuilt.Contains(foreignKey.PrincipalTable)))
            {
                _ = result.Add(table.Name);
            }
        }

        return result;
    }

    private static bool ReadForeignKeysEnabled(
        DbConnection connection
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void SetForeignKeys(
        DbConnection connection,
        bool enabled
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = enabled
            ? "PRAGMA foreign_keys = ON;"
            : "PRAGMA foreign_keys = OFF;";
        _ = command.ExecuteNonQuery();

        if (ReadForeignKeysEnabled(connection) != enabled)
        {
            throw new InvalidOperationException(
                "SQLite did not apply the required foreign-key enforcement mode outside the rebuild transaction.");
        }
    }

    private static void VerifyForeignKeys(
        DbConnection connection,
        DbTransaction transaction,
        HashSet<string> tables,
        SqliteCatalogSnapshot snapshot
    )
    {
        foreach (var validationTable in tables.OrderBy(static table => table, SqliteIdentifierComparer.Instance))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA foreign_key_check(" + Delimit(validationTable) + ");";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                continue;
            }

            // WHY: PRAGMA's second field is a row key, not structural context.
            // Keep integrity failures actionable without disclosing that value.
            var principal = reader.IsDBNull(2) ? null : reader.GetString(2);
            var foreignKeyOrdinal = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
            var foreignKey = snapshot.Tables.TryGetValue(validationTable, out var owner)
                ? owner.ForeignKeys.FirstOrDefault(candidate => candidate.Id == foreignKeyOrdinal)
                : null;

            throw new InvalidOperationException(
                SqliteSafeMigrationDiagnosticMessages.FormatForeignKeyValidation(
                    validationTable, principal, foreignKeyOrdinal, foreignKey));
        }
    }

    private static string Delimit(
        string identifier
    ) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool IsForeignKeyPragma(
        MigrationCommand command
    ) => command.CommandText.AsSpan().Trim().StartsWith(
        "PRAGMA foreign_keys",
        StringComparison.OrdinalIgnoreCase);
}
