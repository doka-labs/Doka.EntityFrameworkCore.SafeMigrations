namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer
{
    private readonly MySqlSafeMigrationPlanCapture _planCapture;
    private readonly IMigrationsSqlGenerator _sqlGenerator;

    public MySqlSafeMigrationProviderAnalyzer(
        IMigrationsSqlGenerator sqlGenerator,
        MySqlSafeMigrationPlanCapture planCapture
    )
    {
        ArgumentNullException.ThrowIfNull(sqlGenerator);
        ArgumentNullException.ThrowIfNull(planCapture);

        _sqlGenerator = sqlGenerator;
        _planCapture = planCapture;
    }

    public string ProviderId => "doka_mysql";

    public void ValidateContext(
        DbContext context
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        // Validate the current connection instance rather than the immutable
        // options snapshot. EF may reuse an internal service provider after a
        // consumer replaces the relational connection for the same context
        // type, but no SafeMigrations SQL may escape under invalid settings.
        MySqlSafeMigrationConnectionValidator.Validate(context.Database.GetDbConnection());
    }

    public async Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var serverVersion = MySqlServerVersion.AutoDetect(
                connection,
                MySqlServerVersionCompatibilityMode.AllowUnsupported);

            return new SafeMigrationProviderEnvironment(
                ProviderId,
                serverVersion.IsMariaDb ? "mariadb" : "mysql",
                connection.ServerVersion);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var migrationLock = await context
            .GetService<IHistoryRepository>()
            .AcquireDatabaseLockAsync(cancellationToken);

        return new AnalysisScope(migrationLock);
    }

    public async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
        {
            return [];
        }

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            MySqlSafeMigrationConnectionValidator.Validate(connection);

            var commandTimeout = context.Database.GetCommandTimeout();
            var maximumPayloadBytes = await GetMaximumPayloadBytesAsync(connection, commandTimeout, cancellationToken);

            var expectedUniqueIndexes = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes(operations);
            var results = new List<SafeMigrationProviderAnalysis>(operations.Count);
            var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
            var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
            var operationOffset = 0;
            foreach (var operationWindow in operations.Chunk(
                         SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
            {
                var plans = CapturePlans(operationWindow, context.Model, expectedUniqueIndexes);
                var shortCircuitStates = await FindShortCircuitStatesAsync(
                    connection,
                    plans,
                    maximumPayloadBytes,
                    commandTimeout,
                    operationOffset,
                    cancellationToken);

                var ordinal = 0;
                while (ordinal < operationWindow.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    while (ordinal < operationWindow.Length
                           && shortCircuitStates[ordinal] is { } shortCircuitState)
                    {
                        results.Add(ShortCircuitAnalysis(shortCircuitState));
                        ordinal++;
                    }

                    if (ordinal == operationWindow.Length)
                    {
                        break;
                    }

                    await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);

                    var batchParameterCount = 0;
                    var batchPayloadBytes = 0;
                    while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                           && ordinal < operationWindow.Length
                           && shortCircuitStates[ordinal] is null)
                    {
                        var command = batch.CreateCommand();
                        var parameterizer = new MySqlCatalogQueryParameterizer(command);
                        var selections = new List<string>(
                            Math.Min(
                                SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                                operationWindow.Length - ordinal));

                        var sqlBytes = trailerBytes;
                        while (ordinal < operationWindow.Length
                               && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                        {
                            if (shortCircuitStates[ordinal] is not null)
                            {
                                break;
                            }

                            var checkpoint = parameterizer.Capture();
                            var plan = plans[ordinal];
                            var stateExpression = plan.RenderStateExpression(parameterizer.AddString);
                            var postcondition = plan.RenderPostcondition(parameterizer.AddString);
                            var repairPrecondition = plan.RenderRepairPrecondition(parameterizer.AddString);
                            var classificationCode = plan.ClassificationCodeExpression is null
                                ? "NULL"
                                : plan.RenderClassificationCodeExpression(parameterizer.AddString);

                            var resultOrdinal = operationOffset + ordinal;
                            var selection = $"SELECT {resultOrdinal.ToString(CultureInfo.InvariantCulture)}, "
                                + $"({stateExpression}), "
                                + $"COALESCE(({postcondition}), FALSE), "
                                + $"COALESCE(({repairPrecondition}), FALSE), "
                                + $"({classificationCode})";

                            var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                                + (selections.Count == 0 ? 0 : separatorBytes);

                            var statementPayload = sqlBytes + selectionBytes + parameterizer.Utf8PayloadBytes;
                            var prospectiveBatchParameters = batchParameterCount + parameterizer.Count;
                            var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                            if (SafeMigrationCatalogQueryLimits.Exceeded(
                                    prospectiveBatchParameters,
                                    prospectiveBatchPayload,
                                    maximumPayloadBytes))
                            {
                                parameterizer.Rollback(checkpoint);
                                if (selections.Count == 0)
                                {
                                    if (batch.Count == 1)
                                    {
                                        batch.RemoveLastCommand(command);

                                        throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                            operationOffset + ordinal,
                                            prospectiveBatchParameters,
                                            prospectiveBatchPayload);
                                    }

                                    break;
                                }

                                break;
                            }

                            selections.Add(selection);
                            sqlBytes += selectionBytes;
                            ordinal++;
                        }

                        if (selections.Count == 0)
                        {
                            batch.RemoveLastCommand(command);

                            break;
                        }

                        command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                            + SafeMigrationCatalogQueryLimits.Trailer;
                        batchParameterCount += parameterizer.Count;
                        batchPayloadBytes += sqlBytes + parameterizer.Utf8PayloadBytes;
                    }

                    await ReadAnalysisAsync(batch, results, plans, operationOffset, cancellationToken);
                }

                operationOffset += operationWindow.Length;
            }

            if (results.Count != operations.Count)
            {
                throw new InvalidOperationException(
                    "The MySQL SafeMigrations classifier returned an inconsistent row count.");
            }

            return results.AsReadOnly();
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private MySqlSafeMigrationRuntimePlan[] CapturePlans(
        IReadOnlyList<SafeMigrationOperation> operations,
        IModel model,
        IReadOnlyDictionary<string, IReadOnlyList<ExpectedIndexDefinition>> expectedUniqueIndexes
    )
    {
        // Doka returns one placeholder command per captured operation. The
        // caller supplies a bounded window while this complete catalog keeps
        // cross-window unique-index semantics intact.
        using var capture = _planCapture.Begin(operations, expectedUniqueIndexes);
        _ = _sqlGenerator.Generate(operations, model);

        return capture.Complete();
    }

    private static async Task<SafeMigrationObservedState?[]> FindShortCircuitStatesAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        var states = new SafeMigrationObservedState?[plans.Length];

        // The server resolves every relation referenced by one SQL statement
        // before CASE can select a branch. Keep catalog-only prerequisites in
        // their own statement so a data probe is never prepared for a missing
        // table.
        await FindPrerequisiteStatesAsync(
            connection,
            plans,
            states,
            maximumPayloadBytes,
            commandTimeout,
            operationOffset,
            cancellationToken);

        await FindStateEvaluationGuardStatesAsync(
            connection,
            plans,
            states,
            maximumPayloadBytes,
            commandTimeout,
            operationOffset,
            cancellationToken);

        return states;
    }

    private static async Task FindPrerequisiteStatesAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        SafeMigrationObservedState?[] states,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        var rowsRead = 0;
        var ordinal = 0;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (ordinal < plans.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);

            var batchParameterCount = 0;
            var batchPayloadBytes = 0;
            while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                   && ordinal < plans.Length)
            {
                var command = batch.CreateCommand();
                var parameterizer = new MySqlCatalogQueryParameterizer(command);
                var selections = new List<string>(
                    Math.Min(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, plans.Length - ordinal));

                var sqlBytes = trailerBytes;
                while (ordinal < plans.Length
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var checkpoint = parameterizer.Capture();
                    var plan = plans[ordinal];
                    var prerequisite = plan.RenderPrerequisiteExpression(parameterizer.AddString);

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                        + $"WHEN NOT COALESCE(({prerequisite}), FALSE) THEN 'prerequisite_missing' "
                        + "ELSE NULL END";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var statementPayload = sqlBytes + selectionBytes + parameterizer.Utf8PayloadBytes;
                    var prospectiveBatchParameters = batchParameterCount + parameterizer.Count;
                    var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                    if (SafeMigrationCatalogQueryLimits.Exceeded(
                            prospectiveBatchParameters,
                            prospectiveBatchPayload,
                            maximumPayloadBytes))
                    {
                        parameterizer.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            if (batch.Count == 1)
                            {
                                batch.RemoveLastCommand(command);

                                throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                    operationOffset + ordinal,
                                    prospectiveBatchParameters,
                                    prospectiveBatchPayload);
                            }

                            break;
                        }

                        break;
                    }

                    selections.Add(selection);
                    sqlBytes += selectionBytes;
                    ordinal++;
                }

                if (selections.Count == 0)
                {
                    batch.RemoveLastCommand(command);

                    break;
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;
                batchParameterCount += parameterizer.Count;
                batchPayloadBytes += sqlBytes + parameterizer.Utf8PayloadBytes;
            }

            rowsRead = await ReadPrerequisiteBatchAsync(batch, states, rowsRead, cancellationToken);
        }

        if (rowsRead != plans.Length)
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations prerequisite classifier returned an inconsistent row count.");
        }
    }

    private static async Task FindStateEvaluationGuardStatesAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        SafeMigrationObservedState?[] states,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        var ordinal = 0;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (ordinal < plans.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (ordinal < plans.Length
                   && (states[ordinal] is not null || plans[ordinal].StateEvaluationGuardFailureExpression is null))
            {
                ordinal++;
            }

            if (ordinal == plans.Length)
            {
                break;
            }

            await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);

            var selectedOrdinals = new List<int>(
                Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
                    * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch,
                    plans.Length - ordinal));

            var batchParameterCount = 0;
            var batchPayloadBytes = 0;
            while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                   && ordinal < plans.Length)
            {
                var command = batch.CreateCommand();
                var parameterizer = new MySqlCatalogQueryParameterizer(command);
                var selections = new List<string>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                var statementOrdinals = new List<int>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                var sqlBytes = trailerBytes;
                while (ordinal < plans.Length
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    if (states[ordinal] is not null
                        || plans[ordinal].StateEvaluationGuardFailureExpression is null)
                    {
                        ordinal++;

                        continue;
                    }

                    var checkpoint = parameterizer.Capture();
                    var plan = plans[ordinal];
                    var stateEvaluationGuard = plan.RenderStateEvaluationGuardExpression(parameterizer.AddString);
                    var guardFailure = plan.RenderStateEvaluationGuardFailureExpression(parameterizer.AddString);

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                        + $"WHEN NOT COALESCE(({stateEvaluationGuard}), FALSE) THEN ({guardFailure}) "
                        + "ELSE NULL END";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var statementPayload = sqlBytes + selectionBytes + parameterizer.Utf8PayloadBytes;
                    var prospectiveBatchParameters = batchParameterCount + parameterizer.Count;
                    var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                    if (SafeMigrationCatalogQueryLimits.Exceeded(
                            prospectiveBatchParameters,
                            prospectiveBatchPayload,
                            maximumPayloadBytes))
                    {
                        parameterizer.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            if (batch.Count == 1)
                            {
                                batch.RemoveLastCommand(command);

                                throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                    operationOffset + ordinal,
                                    prospectiveBatchParameters,
                                    prospectiveBatchPayload);
                            }
                        }

                        break;
                    }

                    selections.Add(selection);
                    statementOrdinals.Add(ordinal);
                    sqlBytes += selectionBytes;
                    ordinal++;
                }

                if (selections.Count == 0)
                {
                    batch.RemoveLastCommand(command);

                    break;
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;
                batchParameterCount += parameterizer.Count;
                batchPayloadBytes += sqlBytes + parameterizer.Utf8PayloadBytes;
                selectedOrdinals.AddRange(statementOrdinals);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            await ReadStateEvaluationGuardBatchAsync(batch, states, selectedOrdinals, cancellationToken);
        }
    }

    private static SafeMigrationProviderAnalysis ShortCircuitAnalysis(
        SafeMigrationObservedState state
    ) => new(state, SafeMigrationRepairCapability.None, false, $"classified_{StateCode(state)}");

    private static async Task<int> GetMaximumPayloadBytesAsync(
        DbConnection connection,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = "SELECT @@max_allowed_packet;";
        var raw = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("MySQL did not return max_allowed_packet.");

        var maximumPacket = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        try
        {
            return SafeMigrationCatalogQueryLimits.MySqlMaximumUtf8PayloadBytes(maximumPacket);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException("MySQL returned an invalid max_allowed_packet value.", exception);
        }
    }

    private static async Task ReadAnalysisAsync(
        SafeMigrationCatalogBatch batch,
        List<SafeMigrationProviderAnalysis> results,
        MySqlSafeMigrationRuntimePlan[] plans,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        await batch.ForEachResultSetAsync(
            async (
                reader,
                token
            ) =>
            {
                while (await reader.ReadAsync(token))
                {
                    var ordinal = reader.GetInt32(0);
                    if (ordinal != results.Count)
                    {
                        throw new InvalidOperationException(
                            "The MySQL SafeMigrations classifier returned an invalid ordinal.");
                    }

                    var state = ParseState(reader.GetString(1));
                    var repairCapability = reader.GetBoolean(3)
                        ? SafeMigrationRepairCapability.Safe
                        : SafeMigrationRepairCapability.None;

                    var code = reader.IsDBNull(4)
                        ? state == SafeMigrationObservedState.Unsupported
                            ? plans[ordinal - operationOffset].UnsupportedCode ?? "classified_unsupported"
                            : $"classified_{StateCode(state)}"
                        : reader.GetString(4);

                    results.Add(new SafeMigrationProviderAnalysis(state, repairCapability, reader.GetBoolean(2), code));
                }
            },
            cancellationToken);
    }

    private static async Task<int> ReadPrerequisiteBatchAsync(
        SafeMigrationCatalogBatch batch,
        SafeMigrationObservedState?[] states,
        int rowsRead,
        CancellationToken cancellationToken
    )
    {
        await batch.ForEachResultSetAsync(
            async (
                reader,
                token
            ) =>
            {
                while (await reader.ReadAsync(token))
                {
                    var resultOrdinal = reader.GetInt32(0);
                    if (resultOrdinal != rowsRead)
                    {
                        throw new InvalidOperationException(
                            "The MySQL SafeMigrations prerequisite classifier returned an invalid ordinal.");
                    }

                    states[resultOrdinal] = reader.IsDBNull(1) ? null : ParseState(reader.GetString(1));
                    rowsRead++;
                }
            },
            cancellationToken);

        return rowsRead;
    }

    private static async Task ReadStateEvaluationGuardBatchAsync(
        SafeMigrationCatalogBatch batch,
        SafeMigrationObservedState?[] states,
        List<int> selectedOrdinals,
        CancellationToken cancellationToken
    )
    {
        var row = 0;
        await batch.ForEachResultSetAsync(
            async (
                reader,
                token
            ) =>
            {
                while (await reader.ReadAsync(token))
                {
                    var resultOrdinal = reader.GetInt32(0);
                    if (row >= selectedOrdinals.Count
                        || resultOrdinal != selectedOrdinals[row])
                    {
                        throw new InvalidOperationException(
                            "The MySQL SafeMigrations state-evaluation guard classifier returned an invalid ordinal.");
                    }

                    states[resultOrdinal] = reader.IsDBNull(1) ? null : ParseState(reader.GetString(1));
                    row++;
                }
            },
            cancellationToken);

        if (row != selectedOrdinals.Count)
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations state-evaluation guard classifier returned " + "an inconsistent row count.");
        }
    }

    private static void ApplyCommandTimeout(
        DbCommand command,
        int? commandTimeout
    )
    {
        if (commandTimeout is not null)
        {
            command.CommandTimeout = commandTimeout.Value;
        }
    }

    public async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        var catalog = SafeMigrationExpectedCatalog.Create(operations);
        var expected = catalog
            .Where(static table => table.Schema is null)
            .ToDictionary(static table => table.Table, StringComparer.Ordinal);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var serverVersion = MySqlServerVersion.AutoDetect(
                connection,
                MySqlServerVersionCompatibilityMode.AllowUnsupported);

            var commandTimeout = context.Database.GetCommandTimeout();
            var findings = new List<SafeMigrationUnexpectedObject>();
            var seen = new HashSet<(SafeMigrationDatabaseObjectKind Kind, string Table, string Name)>();

            await using (var tableCommand = connection.CreateCommand())
            {
                ApplyCommandTimeout(tableCommand, commandTimeout);
                tableCommand.CommandText = BuildUnexpectedTableSql();
                await ReadUnexpectedAsync(tableCommand, expected, findings, seen, cancellationToken);
            }

            foreach (var tableBatch in expected
                         .Keys
                         .Order(StringComparer.Ordinal)
                         .Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues))
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var command = connection.CreateCommand();
                ApplyCommandTimeout(command, commandTimeout);
                var parameters = new MySqlCatalogQueryParameterizer(command);
                var tableScope = string.Join(", ", tableBatch.Select(parameters.AddString));
                command.CommandText = BuildUnexpectedChildObjectSql(tableScope, serverVersion.IsMariaDb);
                await ReadUnexpectedAsync(command, expected, findings, seen, cancellationToken);
            }

            return await RemoveSemanticAliasesAsync(context, operations, findings, cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> RemoveSemanticAliasesAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        List<SafeMigrationUnexpectedObject> findings,
        CancellationToken cancellationToken
    )
    {
        if (findings.Count == 0)
        {
            return findings.AsReadOnly();
        }

        // Reuse the provider's complete catalog comparator instead of
        // maintaining a weaker second definition of semantic equivalence in
        // the inventory path. Candidates are consumed in bounded windows so a
        // large legacy catalog cannot materialize a cross-product in memory.
        var semanticAliases = new HashSet<int>();
        foreach (var candidates in SafeMigrationSemanticCandidateFactory
                     .Create(operations, findings, projectUniqueIndexesAsUniqueConstraints: true)
                     .Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
        {
            var analyses = await AnalyzeAsync(
                context,
                candidates
                    .Select(static candidate => candidate.Operation)
                    .ToArray(),
                cancellationToken);

            for (var index = 0; index < candidates.Length; index++)
            {
                if (analyses[index].ObservedState == SafeMigrationObservedState.Matching)
                {
                    semanticAliases.Add(candidates[index].UnexpectedObjectIndex);
                }
            }
        }

        if (semanticAliases.Count == 0)
        {
            return findings.AsReadOnly();
        }

        return findings
            .Where((
                _,
                index
            ) => !semanticAliases.Contains(index))
            .ToArray();
    }

    private static async Task ReadUnexpectedAsync(
        DbCommand command,
        Dictionary<string, SafeMigrationExpectedTableInventory> expected,
        List<SafeMigrationUnexpectedObject> findings,
        HashSet<(SafeMigrationDatabaseObjectKind Kind, string Table, string Name)> seen,
        CancellationToken cancellationToken
    )
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Unexpected objects are evidence only. They are never folded into
        // the expected catalog and never authorize destructive cleanup.
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = ParseObjectKind(reader.GetString(0));
            var tableName = reader.GetString(1);
            var objectName = reader.GetString(2);
            var providerGeneratedJsonCheck = reader.GetBoolean(3);
            if (!seen.Add((kind, tableName, objectName)))
            {
                continue;
            }

            if (!expected.TryGetValue(tableName, out var table))
            {
                if (kind == SafeMigrationDatabaseObjectKind.Table)
                {
                    findings.Add(Unexpected(kind, table: null, objectName));
                }

                continue;
            }

            if (kind == SafeMigrationDatabaseObjectKind.Table
                || IsExpected(table, kind, objectName, providerGeneratedJsonCheck))
            {
                continue;
            }

            findings.Add(Unexpected(kind, tableName, objectName));
        }
    }

    private static SafeMigrationObservedState ParseState(
        string state
    ) => state switch
    {
        "missing" => SafeMigrationObservedState.Missing,
        "matching" => SafeMigrationObservedState.Matching,
        "different" => SafeMigrationObservedState.Different,
        "unsupported" => SafeMigrationObservedState.Unsupported,
        "data_blocked" => SafeMigrationObservedState.DataBlocked,
        "prerequisite_missing" => SafeMigrationObservedState.PrerequisiteMissing,
        _ => throw new InvalidOperationException("The MySQL SafeMigrations classifier returned an unknown state."),
    };

    private static string StateCode(
        SafeMigrationObservedState state
    ) => state switch
    {
        SafeMigrationObservedState.Missing => "missing",
        SafeMigrationObservedState.Matching => "matching",
        SafeMigrationObservedState.Different => "different",
        SafeMigrationObservedState.Unsupported => "unsupported",
        SafeMigrationObservedState.DataBlocked => "data_blocked",
        SafeMigrationObservedState.PrerequisiteMissing => "prerequisite_missing",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static bool IsExpected(
        SafeMigrationExpectedTableInventory table,
        SafeMigrationDatabaseObjectKind kind,
        string name,
        bool providerGeneratedJsonCheck
    ) => kind switch
    {
        SafeMigrationDatabaseObjectKind.Column => table.Columns.Contains(name),
        SafeMigrationDatabaseObjectKind.CheckConstraint when providerGeneratedJsonCheck
            && table.ColumnStoreTypes.TryGetValue(name, out var storeType)
            && StringComparer.OrdinalIgnoreCase.Equals(storeType?.Trim(), "json") => true,
        SafeMigrationDatabaseObjectKind.Index => table.Indexes.Contains(name),
        SafeMigrationDatabaseObjectKind.PrimaryKey => table.Constraints.Values.Contains(
            SafeMigrationDatabaseObjectKind.PrimaryKey),
        SafeMigrationDatabaseObjectKind.UniqueConstraint when table.UniqueIndexes.Contains(name) => true,
        _ => table.Constraints.TryGetValue(name, out var expectedKind) && expectedKind == kind
    };

    private static SafeMigrationUnexpectedObject Unexpected(
        SafeMigrationDatabaseObjectKind kind,
        string? table,
        string name
    ) => new(kind, schema: null, table, name, $"unexpected_{ObjectCode(kind)}");

    private static SafeMigrationDatabaseObjectKind ParseObjectKind(
        string value
    ) => value switch
    {
        "table" => SafeMigrationDatabaseObjectKind.Table,
        "column" => SafeMigrationDatabaseObjectKind.Column,
        "index" => SafeMigrationDatabaseObjectKind.Index,
        "primary_key" => SafeMigrationDatabaseObjectKind.PrimaryKey,
        "unique_constraint" => SafeMigrationDatabaseObjectKind.UniqueConstraint,
        "check_constraint" => SafeMigrationDatabaseObjectKind.CheckConstraint,
        "foreign_key" => SafeMigrationDatabaseObjectKind.ForeignKey,
        _ => throw new InvalidOperationException(
            "The MySQL unexpected-object inventory returned an unknown object kind."),
    };

    private static string ObjectCode(
        SafeMigrationDatabaseObjectKind kind
    ) => kind switch
    {
        SafeMigrationDatabaseObjectKind.Table => "table",
        SafeMigrationDatabaseObjectKind.Column => "column",
        SafeMigrationDatabaseObjectKind.Index => "index",
        SafeMigrationDatabaseObjectKind.PrimaryKey => "primary_key",
        SafeMigrationDatabaseObjectKind.UniqueConstraint => "unique_constraint",
        SafeMigrationDatabaseObjectKind.CheckConstraint => "check_constraint",
        SafeMigrationDatabaseObjectKind.ForeignKey => "foreign_key",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string BuildUnexpectedTableSql() => """
                                                       SELECT 'table', t.TABLE_NAME, t.TABLE_NAME, FALSE
                                                       FROM INFORMATION_SCHEMA.TABLES t
                                                       WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_TYPE = 'BASE TABLE'
                                                       ORDER BY 1, 2, 3;
                                                       """;

    private static string BuildUnexpectedChildObjectSql(
        string tableScope,
        bool isMariaDb
    ) => $"""
          SELECT 'column', c.TABLE_NAME, c.COLUMN_NAME, FALSE
          FROM INFORMATION_SCHEMA.COLUMNS c
          WHERE c.TABLE_SCHEMA = DATABASE()
            AND c.TABLE_NAME IN ({tableScope})
          UNION ALL
          SELECT CASE tc.CONSTRAINT_TYPE
              WHEN 'PRIMARY KEY' THEN 'primary_key'
              WHEN 'UNIQUE' THEN 'unique_constraint'
              WHEN 'CHECK' THEN 'check_constraint'
              WHEN 'FOREIGN KEY' THEN 'foreign_key'
              END,
              tc.TABLE_NAME,
              tc.CONSTRAINT_NAME,
              {(isMariaDb ? MariaDbImplicitJsonCheckInventoryExpression : "FALSE")}
          FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
          {(isMariaDb ? MariaDbImplicitJsonCheckInventoryJoins : string.Empty)}
          WHERE tc.CONSTRAINT_SCHEMA = DATABASE()
            AND tc.TABLE_NAME IN ({tableScope})
            AND tc.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE', 'CHECK', 'FOREIGN KEY')
          UNION ALL
          SELECT 'index', s.TABLE_NAME, s.INDEX_NAME, FALSE
          FROM INFORMATION_SCHEMA.STATISTICS s
          WHERE s.TABLE_SCHEMA = DATABASE()
            AND s.TABLE_NAME IN ({tableScope})
            AND s.SEQ_IN_INDEX = 1
            AND s.INDEX_NAME <> 'PRIMARY'
            AND NOT EXISTS (
                SELECT 1
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                WHERE tc.CONSTRAINT_SCHEMA = s.TABLE_SCHEMA
                  AND tc.TABLE_NAME = s.TABLE_NAME
                  AND tc.CONSTRAINT_NAME = s.INDEX_NAME)
          ORDER BY 1, 2, 3;
          """;

    private const string MariaDbImplicitJsonCheckInventoryJoins = """
                                                                  LEFT JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
                                                                    ON cc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
                                                                   AND cc.TABLE_NAME = tc.TABLE_NAME
                                                                   AND cc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                                                                  LEFT JOIN INFORMATION_SCHEMA.COLUMNS json_column
                                                                    ON json_column.TABLE_SCHEMA = tc.CONSTRAINT_SCHEMA
                                                                   AND json_column.TABLE_NAME = tc.TABLE_NAME
                                                                   AND json_column.COLUMN_NAME = tc.CONSTRAINT_NAME
                                                                  """;

    private const string MariaDbImplicitJsonCheckInventoryExpression = """
                                                                       CASE WHEN tc.CONSTRAINT_TYPE = 'CHECK'
                                                                         AND json_column.DATA_TYPE = 'longtext'
                                                                         AND LOWER(json_column.COLLATION_NAME) = 'utf8mb4_bin'
                                                                         AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
                                                                               cc.CHECK_CLAUSE, '`', ''), ' ', ''), '(', ''), ')', ''))
                                                                             = CONCAT('json_valid', LOWER(json_column.COLUMN_NAME))
                                                                       THEN TRUE ELSE FALSE END
                                                                       """;

    private sealed class AnalysisScope : IAsyncDisposable
    {
        private IDisposable? _migrationLock;

        public AnalysisScope(
            IDisposable migrationLock
        )
        {
            _migrationLock = migrationLock;
        }

        public ValueTask DisposeAsync()
        {
            _migrationLock?.Dispose();
            _migrationLock = null;
            return ValueTask.CompletedTask;
        }
    }
}
