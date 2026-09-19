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
        IRelationalCommandDiagnosticsLogger logger
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
            return ExecuteInCurrentTransaction(connection, parameterValues);
        }

        using var localTransaction = connection.BeginTransaction();
        try
        {
            var affected = ExecuteInCurrentTransaction(connection, parameterValues);
            localTransaction.Commit();

            return affected;
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
                if (foreignKeysEnabled)
                {
                    var postSnapshot = _executionState.GetSnapshot(connection);
                    VerifyForeignKeys(
                        connection.DbConnection,
                        localTransaction.GetDbTransaction(),
                        ReadForeignKeyValidationTables(postSnapshot));
                }

                localTransaction.Commit();

                return affected;
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

    private int ExecuteInCurrentTransaction(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues
    )
    {
        var transaction = connection.CurrentTransaction?.GetDbTransaction();
        var snapshot = _executionState.GetSnapshot(connection);
        var projection = new SafeMigrationPreflightProjection(
            _analyzer,
            projectedKeyAnalyzer: null,
            _analyzer);

        var acceptedOperations = new List<MigrationOperation>(_operations.Count);

        foreach (var operation in _operations)
        {
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
                throw CreateBlockedException(safeOperation, analysis, decision);
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
            return 0;
        }

        var commands = _baselineGenerator.Generate(acceptedOperations, _model, _options);
        var affected = ExecuteCommands(connection, parameterValues, commands);

        _executionState.Invalidate();
        var postSnapshot = _executionState.GetSnapshot(connection);
        var postflight = new SafeMigrationPostflightProjection(
            _operations,
            _analyzer);

        for (var index = 0; index < _operations.Count; index++)
        {
            if (_operations[index] is not SafeMigrationOperation operation)
            {
                continue;
            }

            var postcondition = _analyzer.Analyze(
                postSnapshot,
                connection.DbConnection,
                transaction,
                operation,
                _rebuildArtifacts);

            if (!postcondition.PostconditionSatisfied && !postflight.IsSuperseded(index))
            {
                throw new InvalidOperationException(
                    $"SQLite SafeMigrations postcondition failed for {operation.Intent.Kind} "
                    + $"'{operation.Intent.ObjectName}' with analysis code '{postcondition.Code}'.");
            }
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

        var result = new HashSet<string>(rebuilt, SqliteIdentifierComparer.Instance);
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
        HashSet<string> tables
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

            var table = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
            var rowId = reader.IsDBNull(1)
                ? "without-rowid"
                : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "unknown";

            var principal = reader.IsDBNull(2) ? "unknown" : reader.GetString(2);

            throw new InvalidOperationException(
                $"SQLite SafeMigrations foreign-key validation failed after rebuilding table '{table}' "
                + $"at row '{rowId}' against '{principal}'.");
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

    private static InvalidOperationException CreateBlockedException(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    ) => new(
        $"SQLite SafeMigrations blocked {operation.Intent.Kind} '{operation.Intent.ObjectName}': "
        + $"analysis={analysis.Code}, decision={decision.Code}.");
}
