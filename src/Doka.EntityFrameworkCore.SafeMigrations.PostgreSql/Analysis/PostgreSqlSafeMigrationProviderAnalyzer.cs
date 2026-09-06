namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed class PostgreSqlSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer
{
    // PostgreSQL advisory locks are already local to the current database. A
    // fixed signed bigint therefore avoids coercing the database's unsigned OID
    // into an integer while retaining one package-owned analysis lock domain.
    internal const string AnalysisAdvisoryLockSql = "SELECT pg_catalog.pg_advisory_xact_lock(1397574913::bigint);";

    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly PostgreSqlSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;

    public PostgreSqlSafeMigrationProviderAnalyzer(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
        _catalogSqlBuilder = new PostgreSqlSafeMigrationCatalogSqlBuilder(typeMappingSource, sqlGenerationHelper);
    }

    public string ProviderId => "npgsql_postgresql";

    public void ValidateContext(
        DbContext context
    ) => ArgumentNullException.ThrowIfNull(context);

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
            return new SafeMigrationProviderEnvironment(ProviderId, "postgresql", connection.ServerVersion);
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

        IDbContextTransaction? transaction = null;
        try
        {
            var currentTransaction = context.Database.CurrentTransaction;
            if (currentTransaction is null)
            {
                transaction = await context.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.RepeatableRead,
                    cancellationToken);
                _ = await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", cancellationToken);
            }
            else
            {
                await ValidateCallerOwnedTransactionAsync(
                    currentTransaction,
                    context.Database.GetCommandTimeout(),
                    cancellationToken);
            }

            await using var command = context
                .Database
                .GetDbConnection()
                .CreateCommand();

            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            ApplyCommandTimeout(command, context.Database.GetCommandTimeout());
            command.CommandText = AnalysisAdvisoryLockSql;
            _ = await command.ExecuteScalarAsync(cancellationToken);

            return new AnalysisScope(transaction);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            throw;
        }
    }

    private static async Task ValidateCallerOwnedTransactionAsync(
        IDbContextTransaction transaction,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var dbTransaction = transaction.GetDbTransaction();
        if (dbTransaction.IsolationLevel is not (System.Data.IsolationLevel.RepeatableRead
            or System.Data.IsolationLevel.Serializable))
        {
            throw new InvalidOperationException(
                "PostgreSQL SafeMigrations analysis requires a caller-owned transaction "
                + "to use RepeatableRead or Serializable isolation.");
        }

        var connection = dbTransaction.Connection
            ?? throw new InvalidOperationException(
                "The caller-owned PostgreSQL analysis transaction has no active connection.");

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = "SHOW transaction_read_only;";
        var readOnly = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        if (!StringComparer.OrdinalIgnoreCase.Equals(readOnly, "on"))
        {
            throw new InvalidOperationException(
                "PostgreSQL SafeMigrations analysis requires a caller-owned transaction to be read-only.");
        }
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
            var commandTimeout = context.Database.GetCommandTimeout();
            var shortCircuitStates = await FindShortCircuitStatesAsync(
                connection,
                operations,
                commandTimeout,
                cancellationToken);

            var dataProbeResults = await ResolveDataProbeResultsAsync(
                connection,
                operations,
                shortCircuitStates,
                commandTimeout,
                cancellationToken);

            var results = new List<SafeMigrationProviderAnalysis>(operations.Count);
            var plans = new PostgreSqlSafeMigrationRuntimePlan?[operations.Count];
            var ordinal = 0;
            var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
            var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
            while (ordinal < operations.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                while (ordinal < operations.Count
                       && shortCircuitStates[ordinal] is { } shortCircuitState)
                {
                    results.Add(ShortCircuitAnalysis(shortCircuitState));
                    ordinal++;
                }

                if (ordinal == operations.Count)
                {
                    break;
                }

                await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);

                var batchParameterCount = 0;
                var batchPayloadBytes = 0;
                while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                       && ordinal < operations.Count
                       && shortCircuitStates[ordinal] is null)
                {
                    var command = batch.CreateCommand();
                    var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                    var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                        _typeMappingSource,
                        _sqlGenerationHelper,
                        parameters.AddString,
                        parameters.Add);

                    var selections = new List<string>(
                        Math.Min(
                            SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                            operations.Count - ordinal));

                    var sqlBytes = trailerBytes;
                    while (ordinal < operations.Count
                           && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                    {
                        if (shortCircuitStates[ordinal] is not null)
                        {
                            break;
                        }

                        var operation = operations[ordinal]
                            ?? throw new ArgumentException(
                                "The operation batch cannot contain null entries.",
                                nameof(operations));

                        var checkpoint = parameters.Capture();
                        var plan = builder.Build(operation);
                        plans[ordinal] = plan;
                        PostgreSqlDataProbeResult? dataProbeResult = plan.DataProbe is null
                            ? null
                            : dataProbeResults[ordinal]
                                ?? throw new InvalidOperationException(
                                    "The PostgreSQL narrowing probe returned no classification result.");

                        var stateExpression = plan.DataProbe is null
                            ? plan.RenderStateExpression()
                            : plan.RenderStateExpression(
                                dataProbeResult!.Value.IsBlocked,
                                dataProbeResult.Value.IsTransitionEligible);

                        var repairPrecondition = plan.DataProbe is null
                            ? plan.RenderRepairPrecondition()
                            : plan.RenderRepairPrecondition(
                                dataProbeResult!.Value.IsBlocked,
                                dataProbeResult.Value.IsTransitionEligible);

                        var classificationCode = plan.DataProbe is null
                            ? plan.RenderClassificationCodeExpression() ?? "NULL"
                            : plan.RenderClassificationCodeExpression(dataProbeResult!.Value.IsBlocked) ?? "NULL";

                        var rowEvidence = plan.ModelManagedRowEvidenceExpression ?? "NULL";
                        var dependencyCounts = plan.ModelManagedDependencyCountsExpression ?? "NULL";
                        var diagnosticEvidence = plan.DiagnosticEvidenceExpression ?? "NULL";
                        var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                            + $"({stateExpression})::text, "
                            + $"COALESCE(({plan.Postcondition}), FALSE), "
                            + $"COALESCE(({repairPrecondition}), FALSE), "
                            + $"({classificationCode}), "
                            + $"({rowEvidence}), "
                            + $"({dependencyCounts}), "
                            + $"({diagnosticEvidence})";

                        var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                            + (selections.Count == 0 ? 0 : separatorBytes);

                        var statementPayload = sqlBytes + selectionBytes + parameters.Utf8PayloadBytes;
                        var prospectiveBatchParameters = batchParameterCount + parameters.Count;
                        var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                        if (SafeMigrationCatalogQueryLimits.Exceeded(
                                prospectiveBatchParameters,
                                prospectiveBatchPayload,
                                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                        {
                            parameters.Rollback(checkpoint);
                            if (selections.Count == 0)
                            {
                                if (batch.Count == 1)
                                {
                                    batch.RemoveLastCommand(command);

                                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                        ordinal,
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
                    batchParameterCount += parameters.Count;
                    batchPayloadBytes += sqlBytes + parameters.Utf8PayloadBytes;
                }

                await ReadAnalysisAsync(batch, results, plans, dataProbeResults, cancellationToken);
            }

            if (results.Count != operations.Count)
            {
                throw new InvalidOperationException(
                    "The PostgreSQL SafeMigrations classifier returned an inconsistent row count.");
            }

            await PopulateColumnDiagnosticsAsync(
                connection,
                operations,
                results,
                commandTimeout,
                cancellationToken);

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

    private async Task<PostgreSqlDataProbeResult?[]> ResolveDataProbeResultsAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationObservedState?[] shortCircuitStates,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var results = new PostgreSqlDataProbeResult?[operations.Count];
        var identities = new PostgreSqlDataProbeIdentity?[operations.Count];
        var candidates = new Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeCandidate>();
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (shortCircuitStates[ordinal] is not null)
            {
                continue;
            }

            var operation = operations[ordinal]
                ?? throw new ArgumentException(
                    "The operation batch cannot contain null entries.",
                    nameof(operations));

            var plan = _catalogSqlBuilder.Build(operation);
            var probe = plan.DataProbe;
            if (probe is null)
            {
                continue;
            }

            var identity = new PostgreSqlDataProbeIdentity(
                probe.Schema,
                probe.Table,
                probe.Column,
                probe.TargetLength);

            identities[ordinal] = identity;
            candidates.TryAdd(identity, new PostgreSqlDataProbeCandidate(identity, operation, probe));
        }

        var cache = new Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeResult>(candidates.Count);
        if (candidates.Count > 0)
        {
            var values = candidates.Values.ToArray();
            var transitionPlans = values
                .Select(candidate => _catalogSqlBuilder.Build(
                    candidate.Operation,
                    includeAnalysisEvidence: false,
                    includeTransitionEvidence: true))
                .ToArray();

            var resolved = await FindRequiredDataProbesAsync(
                connection,
                values,
                transitionPlans,
                commandTimeout,
                cancellationToken);

            foreach (var entry in resolved)
            {
                cache.Add(entry.Key, entry.Value);
            }

            await FindBlockingDataAsync(
                connection,
                values.Where(candidate => cache[candidate.Identity].IsRequired),
                cache,
                commandTimeout,
                cancellationToken);
        }

        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (shortCircuitStates[ordinal] is not null)
            {
                continue;
            }

            if (identities[ordinal] is not { } identity)
            {
                continue;
            }

            results[ordinal] = cache.TryGetValue(identity, out var result)
                ? result
                : throw new InvalidOperationException(
                    "The PostgreSQL narrowing probe did not resolve every candidate.");
        }

        return results;
    }

    private async Task PopulateColumnDiagnosticsAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        List<SafeMigrationProviderAnalysis> results,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var candidates = new List<int>();
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var analysis = results[ordinal];
            if (operations[ordinal].Intent is EnsureColumnIntent
                && analysis.Differences.Count == 0
                && analysis.ObservedState is SafeMigrationObservedState.Different
                    or SafeMigrationObservedState.DataBlocked)
            {
                candidates.Add(ordinal);
            }
        }

        for (var offset = 0;
             offset < candidates.Count;
             offset += SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(
                SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                candidates.Count - offset);

            await using var command = connection.CreateCommand();
            ApplyCommandTimeout(command, commandTimeout);
            var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
            var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                _typeMappingSource,
                _sqlGenerationHelper,
                parameters.AddString,
                parameters.Add);

            var selections = new List<string>(count);
            var plans = new PostgreSqlSafeMigrationRuntimePlan[count];
            for (var index = 0; index < count; index++)
            {
                var ordinal = candidates[offset + index];
                var plan = builder.Build(
                    operations[ordinal],
                    includeAnalysisEvidence: true,
                    includeTransitionEvidence: false);

                plans[index] = plan;
                selections.Add(
                    $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                    + $"({plan.DiagnosticEvidenceExpression ?? "NULL"})");
            }

            command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                + SafeMigrationCatalogQueryLimits.Trailer;

            var payloadBytes = Encoding.UTF8.GetByteCount(command.CommandText) + parameters.Utf8PayloadBytes;
            if (SafeMigrationCatalogQueryLimits.Exceeded(
                    parameters.Count,
                    payloadBytes,
                    SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
            {
                throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                    candidates[offset],
                    parameters.Count,
                    payloadBytes);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var row = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (row >= count
                    || reader.GetInt32(0) != candidates[offset + row])
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL diagnostic query returned an invalid ordinal.");
                }

                var differences = reader.IsDBNull(1)
                    ? []
                    : SafeMigrationFacetDifferenceParser.Parse(reader.GetString(1), "PostgreSQL");

                ReplaceAnalysisWithDifferences(
                    results,
                    candidates[offset + row],
                    plans[row],
                    differences);

                row++;
            }

            if (row != count)
            {
                throw new InvalidOperationException(
                    "The PostgreSQL diagnostic query returned an inconsistent row count.");
            }
        }
    }

    private static void ReplaceAnalysisWithDifferences(
        List<SafeMigrationProviderAnalysis> results,
        int ordinal,
        PostgreSqlSafeMigrationRuntimePlan plan,
        IReadOnlyList<SafeMigrationFacetDifference> differences
    )
    {
        var current = results[ordinal];
        results[ordinal] = new SafeMigrationProviderAnalysis(
            current.ObservedState,
            current.RepairCapability,
            current.PostconditionSatisfied,
            current.Code,
            current.OperationalImpact,
            differences)
        {
            ModelManagedDataEvidence = current.ModelManagedDataEvidence,
            RequiresLiveDataProof = current.RequiresLiveDataProof
                || (plan.MayRequireNullabilityDataProof
                    && differences.Any(static difference =>
                        StringComparer.Ordinal.Equals(difference.Facet, "column_nullability"))),
        };
    }

    private static async Task<Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeResult>>
        FindRequiredDataProbesAsync(
        DbConnection connection,
        IReadOnlyList<PostgreSqlDataProbeCandidate> candidates,
        PostgreSqlSafeMigrationRuntimePlan[] transitionPlans,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var results = new Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeResult>(candidates.Count);
        for (var offset = 0;
             offset < candidates.Count;
             offset += SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = Math.Min(
                SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                candidates.Count - offset);

            var selections = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                var probe = transitionPlans[offset + index].DataProbe
                    ?? throw new InvalidOperationException(
                        "The PostgreSQL transition build returned no data-probe plan.");

                selections.Add(
                    $"SELECT {index.ToString(CultureInfo.InvariantCulture)}, "
                    + $"COALESCE(({probe.TransitionInvariantExpression}), FALSE), "
                    + $"COALESCE(({probe.NarrowingExpression}), FALSE)");
            }

            await using var command = connection.CreateCommand();
            ApplyCommandTimeout(command, commandTimeout);
            command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                + SafeMigrationCatalogQueryLimits.Trailer;

            var payloadBytes = Encoding.UTF8.GetByteCount(command.CommandText);
            if (SafeMigrationCatalogQueryLimits.Exceeded(
                    parameters: 0,
                    payloadBytes,
                    SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
            {
                throw SafeMigrationCatalogQueryLimits.OversizedOperation(offset, parameters: 0, payloadBytes);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var row = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(0) != row)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL narrowing prerequisite query returned an invalid ordinal.");
                }

                var transitionEligible = reader.GetBoolean(1);
                var narrowing = reader.GetBoolean(2);

                results.Add(
                    candidates[offset + row].Identity,
                    new PostgreSqlDataProbeResult(
                        transitionEligible,
                        IsRequired: transitionEligible && narrowing,
                        IsBlocked: false));

                row++;
            }

            if (row != count)
            {
                throw new InvalidOperationException(
                    "The PostgreSQL narrowing prerequisite query returned an inconsistent row count.");
            }
        }

        return results;
    }

    private async Task FindBlockingDataAsync(
        DbConnection connection,
        IEnumerable<PostgreSqlDataProbeCandidate> requiredCandidates,
        Dictionary<PostgreSqlDataProbeIdentity, PostgreSqlDataProbeResult> cache,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        foreach (var tableGroup in requiredCandidates.GroupBy(
                     static candidate => new PostgreSqlTableIdentity(
                         candidate.Identity.Schema,
                         candidate.Identity.Table)))
        {
            var candidates = tableGroup.ToArray();
            var table = tableGroup.Key.Schema is null
                ? _sqlGenerationHelper.DelimitIdentifier(tableGroup.Key.Table)
                : _sqlGenerationHelper.DelimitIdentifier(tableGroup.Key.Table, tableGroup.Key.Schema);

            for (var offset = 0;
                 offset < candidates.Length;
                 offset += SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var count = Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                    candidates.Length - offset);

                string commandText;
                if (count == 1)
                {
                    var probe = candidates[offset].Probe;
                    var column = _sqlGenerationHelper.DelimitIdentifier(probe.Column);
                    commandText = "SELECT EXISTS(SELECT 1 FROM "
                        + $"{table} WHERE {column} IS NOT NULL AND char_length({column}) "
                        + $"> {probe.TargetLength.ToString(CultureInfo.InvariantCulture)} LIMIT 1);";
                }
                else
                {
                    var selections = new List<string>(count);
                    for (var index = 0; index < count; index++)
                    {
                        var probe = candidates[offset + index].Probe;
                        var column = _sqlGenerationHelper.DelimitIdentifier(probe.Column);
                        selections.Add(
                            "COALESCE(bool_or("
                            + $"{column} IS NOT NULL AND char_length({column}) "
                            + $"> {probe.TargetLength.ToString(CultureInfo.InvariantCulture)}), FALSE)");
                    }

                    commandText = $"SELECT {string.Join(", ", selections)} FROM {table};";
                }

                var payloadBytes = Encoding.UTF8.GetByteCount(commandText);
                if (SafeMigrationCatalogQueryLimits.Exceeded(
                        parameters: 0,
                        payloadBytes,
                        SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                        offset,
                        parameters: 0,
                        payloadBytes);
                }

                await using var command = connection.CreateCommand();
                ApplyCommandTimeout(command, commandTimeout);
                command.CommandText = commandText;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("The PostgreSQL narrowing data query returned no result.");
                }

                for (var index = 0; index < count; index++)
                {
                    var identity = candidates[offset + index].Identity;
                    cache[identity] = cache[identity] with { IsBlocked = reader.GetBoolean(index) };
                }

                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL narrowing data query returned more than one result row.");
                }
            }
        }
    }

    private async Task<SafeMigrationObservedState?[]> FindShortCircuitStatesAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var states = new SafeMigrationObservedState?[operations.Count];

        // PostgreSQL resolves every relation referenced by one SQL statement
        // before CASE can select a branch. Keep catalog-only prerequisites in
        // their own statement so a data probe is never planned for a missing
        // table.
        await FindPrerequisiteStatesAsync(connection, operations, states, commandTimeout, cancellationToken);
        await FindStateEvaluationGuardStatesAsync(connection, operations, states, commandTimeout, cancellationToken);

        return states;
    }

    private async Task FindPrerequisiteStatesAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationObservedState?[] states,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var rowsRead = 0;
        var ordinal = 0;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (ordinal < operations.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);

            var batchParameterCount = 0;
            var batchPayloadBytes = 0;
            while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                   && ordinal < operations.Count)
            {
                var command = batch.CreateCommand();
                var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                    _typeMappingSource,
                    _sqlGenerationHelper,
                    parameters.AddString,
                    parameters.Add);

                var selections = new List<string>(
                    Math.Min(
                        SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                        operations.Count - ordinal));

                var sqlBytes = trailerBytes;
                while (ordinal < operations.Count
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var operation = operations[ordinal]
                        ?? throw new ArgumentException(
                            "The operation batch cannot contain null entries.",
                            nameof(operations));

                    var checkpoint = parameters.Capture();
                    var plan = builder.Build(operation);

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                        + $"WHEN NOT COALESCE(({plan.PrerequisiteExpression}), FALSE) "
                        + "THEN 'prerequisite_missing' "
                        + "ELSE NULL END";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var statementPayload = sqlBytes + selectionBytes + parameters.Utf8PayloadBytes;
                    var prospectiveBatchParameters = batchParameterCount + parameters.Count;
                    var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                    if (SafeMigrationCatalogQueryLimits.Exceeded(
                            prospectiveBatchParameters,
                            prospectiveBatchPayload,
                            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                    {
                        parameters.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            if (batch.Count == 1)
                            {
                                batch.RemoveLastCommand(command);

                                throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                    ordinal,
                                    prospectiveBatchParameters,
                                    prospectiveBatchPayload);
                            }
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
                batchParameterCount += parameters.Count;
                batchPayloadBytes += sqlBytes + parameters.Utf8PayloadBytes;
            }

            rowsRead = await ReadPrerequisiteBatchAsync(batch, states, rowsRead, cancellationToken);
        }

        if (rowsRead != operations.Count)
        {
            throw new InvalidOperationException(
                "The PostgreSQL SafeMigrations prerequisite classifier returned an inconsistent row count.");
        }
    }

    private async Task FindStateEvaluationGuardStatesAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationObservedState?[] states,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var ordinal = 0;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (ordinal < operations.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);

            var selectedOrdinals = new List<int>(
                Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
                    * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch,
                    operations.Count - ordinal));

            var batchParameterCount = 0;
            var batchPayloadBytes = 0;
            while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                   && ordinal < operations.Count)
            {
                var command = batch.CreateCommand();
                var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                    _typeMappingSource,
                    _sqlGenerationHelper,
                    parameters.AddString,
                    parameters.Add);

                var selections = new List<string>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                var statementOrdinals = new List<int>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                var sqlBytes = trailerBytes;
                while (ordinal < operations.Count
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var operation = operations[ordinal]
                        ?? throw new ArgumentException(
                            "The operation batch cannot contain null entries.",
                            nameof(operations));

                    var checkpoint = parameters.Capture();
                    var plan = builder.Build(operation);
                    if (states[ordinal] is not null
                        || plan.StateEvaluationGuardFailureExpression is null)
                    {
                        parameters.Rollback(checkpoint);
                        ordinal++;

                        continue;
                    }

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                        + $"WHEN NOT COALESCE(({plan.StateEvaluationGuardExpression}), FALSE) THEN "
                        + $"({plan.StateEvaluationGuardFailureExpression}) ELSE NULL END";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var statementPayload = sqlBytes + selectionBytes + parameters.Utf8PayloadBytes;
                    var prospectiveBatchParameters = batchParameterCount + parameters.Count;
                    var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                    if (SafeMigrationCatalogQueryLimits.Exceeded(
                            prospectiveBatchParameters,
                            prospectiveBatchPayload,
                            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                    {
                        parameters.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            if (batch.Count == 1)
                            {
                                batch.RemoveLastCommand(command);

                                throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                    ordinal,
                                    prospectiveBatchParameters,
                                    prospectiveBatchPayload);
                            }

                            break;
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
                batchParameterCount += parameters.Count;
                batchPayloadBytes += sqlBytes + parameters.Utf8PayloadBytes;
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

    private static async Task ReadAnalysisAsync(
        SafeMigrationCatalogBatch batch,
        List<SafeMigrationProviderAnalysis> results,
        PostgreSqlSafeMigrationRuntimePlan?[] plans,
        PostgreSqlDataProbeResult?[] dataProbeResults,
        CancellationToken cancellationToken
    )
    {
        await batch.ForEachResultSetAsync(
            async (reader, token) =>
            {
                while (await reader.ReadAsync(token))
                {
                    var ordinal = reader.GetInt32(0);
                    if (ordinal != results.Count)
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations classifier returned an invalid ordinal.");
                    }

                    var state = ParseState(reader.GetString(1));
                    var repairCapability = reader.GetBoolean(3)
                        ? SafeMigrationRepairCapability.Safe
                        : SafeMigrationRepairCapability.None;

                    var plan = plans[ordinal]
                        ?? throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations classifier has no runtime plan for its result ordinal.");

                    var code = reader.IsDBNull(4)
                        ? state == SafeMigrationObservedState.Unsupported
                            ? plan.UnsupportedCode ?? "classified_unsupported"
                            : $"classified_{StateCode(state)}"
                        : reader.GetString(4);

                    var evidence = plan.ModelManagedRowEvidenceExpression is null
                        ? null
                        : SafeMigrationModelManagedDataEvidence.Parse(
                                reader.GetString(5),
                                plan.ModelManagedRowCount,
                                plan.ModelManagedDependencyCountsExpression is null
                                    ? string.Empty
                                    : reader.GetString(6),
                                plan.ModelManagedDependencyCount,
                                "PostgreSQL");

                    IReadOnlyList<SafeMigrationFacetDifference> differences;
                    if (state == SafeMigrationObservedState.Different
                        && plan.DifferentDifference is not null)
                    {
                        // WHY: Model-managed values stay outside SQL diagnostics;
                        // the provider reports only the bounded mismatch category.
                        differences = [plan.DifferentDifference];
                    }
                    else
                    {
                        differences = state is not (SafeMigrationObservedState.Different
                            or SafeMigrationObservedState.DataBlocked
                            or SafeMigrationObservedState.Unsupported)
                            || reader.IsDBNull(7)
                            ? []
                            : SafeMigrationFacetDifferenceParser.Parse(reader.GetString(7), "PostgreSQL");
                    }

                    var operationalImpact = state == SafeMigrationObservedState.DataBlocked
                        || (state == SafeMigrationObservedState.Different
                            && repairCapability == SafeMigrationRepairCapability.Safe)
                            ? plan.RepairOperationalImpact
                            : SafeMigrationOperationalImpact.NotApplicable;

                    var requiresNullabilityDataProof = plan.MayRequireNullabilityDataProof
                        && differences.Any(static difference =>
                            StringComparer.Ordinal.Equals(difference.Facet, "column_nullability"));

                    var analysis = new SafeMigrationProviderAnalysis(
                        state,
                        repairCapability,
                        reader.GetBoolean(2),
                        code,
                        operationalImpact,
                        differences)
                    {
                        ModelManagedDataEvidence = evidence,
                        // WHY: Every VARCHAR transition carries a probe template,
                        // but widening is catalog-only. Only a catalog-confirmed
                        // narrowing or nullable-to-required result becomes stale
                        // after projected DML.
                        RequiresLiveDataProof = dataProbeResults[ordinal]?.IsRequired == true
                            || requiresNullabilityDataProof,
                    };

                    results.Add(analysis);
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
            async (reader, token) =>
            {
                while (await reader.ReadAsync(token))
                {
                    var resultOrdinal = reader.GetInt32(0);
                    if (resultOrdinal != rowsRead)
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations prerequisite classifier returned an invalid ordinal.");
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
            async (reader, token) =>
            {
                while (await reader.ReadAsync(token))
                {
                    var resultOrdinal = reader.GetInt32(0);
                    if (row >= selectedOrdinals.Count
                        || resultOrdinal != selectedOrdinals[row])
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations state-evaluation guard classifier "
                            + "returned an invalid ordinal.");
                    }

                    states[resultOrdinal] = reader.IsDBNull(1) ? null : ParseState(reader.GetString(1));
                    row++;
                }
            },
            cancellationToken);

        if (row != selectedOrdinals.Count)
        {
            throw new InvalidOperationException(
                "The PostgreSQL SafeMigrations state-evaluation guard classifier returned "
                + "an inconsistent row count.");
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

        var expected = SafeMigrationExpectedCatalog.Create(operations);
        if (expected.Count == 0)
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
            var commandTimeout = context.Database.GetCommandTimeout();
            var findings = new List<SafeMigrationUnexpectedObject>();
            var lookup = new ExpectedTableLookup(expected);
            var seen = new HashSet<(SafeMigrationDatabaseObjectKind Kind, string Schema, string Table, string Name)>();

            var schemaScopes = BuildSchemaScopeBatches(expected);
            foreach (var schemaBatch in schemaScopes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var command = connection.CreateCommand();
                ApplyCommandTimeout(command, commandTimeout);
                var parameters = new PostgreSqlCatalogQueryParameters(command);
                var schemaScope = BuildSchemaScope(schemaBatch, parameters);
                command.CommandText = BuildUnexpectedTableSql(schemaScope);
                await ReadUnexpectedAsync(command, lookup, findings, seen, cancellationToken);
            }

            foreach (var tableBatch in expected.Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues))
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var command = connection.CreateCommand();
                ApplyCommandTimeout(command, commandTimeout);
                var parameters = new PostgreSqlCatalogQueryParameters(command);
                var childScope = BuildExpectedTableScope(tableBatch, parameters, "n.nspname", "c.relname");
                var indexScope = BuildExpectedTableScope(tableBatch, parameters, "n.nspname", "tbl.relname");
                command.CommandText = BuildUnexpectedChildObjectSql(childScope, indexScope);
                await ReadUnexpectedAsync(command, lookup, findings, seen, cancellationToken);
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
        var currentSchema = await GetCurrentSchemaAsync(
            context.Database.GetDbConnection(),
            context.Database.GetCommandTimeout(),
            cancellationToken);

        var semanticAliases = new HashSet<int>();
        foreach (var candidates in SafeMigrationSemanticCandidateFactory
                     .Create(operations, findings, currentSchema)
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
            .Where((_, index) => !semanticAliases.Contains(index))
            .ToArray();
    }

    private static async Task<string> GetCurrentSchemaAsync(
        DbConnection connection,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = "SELECT current_schema();";

        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("PostgreSQL did not return the current schema.");
    }

    private static async Task ReadUnexpectedAsync(
        DbCommand command,
        ExpectedTableLookup lookup,
        List<SafeMigrationUnexpectedObject> findings,
        HashSet<(SafeMigrationDatabaseObjectKind Kind, string Schema, string Table, string Name)> seen,
        CancellationToken cancellationToken
    )
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Unexpected objects are evidence only. They are never folded into
        // the expected catalog and never authorize destructive cleanup.
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = ParseObjectKind(reader.GetString(0));
            var schema = reader.GetString(1);
            var tableName = reader.GetString(2);
            var objectName = reader.GetString(3);
            if (!seen.Add((kind, schema, tableName, objectName)))
            {
                continue;
            }

            var currentSchema = reader.GetString(4);
            var table = lookup.Find(schema, tableName, currentSchema);
            if (table is null)
            {
                if (kind == SafeMigrationDatabaseObjectKind.Table)
                {
                    findings.Add(Unexpected(kind, schema, table: null, objectName));
                }

                continue;
            }

            if (kind == SafeMigrationDatabaseObjectKind.Table
                || IsExpected(table, kind, objectName))
            {
                continue;
            }

            findings.Add(Unexpected(kind, schema, tableName, objectName));
        }
    }

    private static SafeMigrationExpectedTableInventory[][] BuildSchemaScopeBatches(
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected
    )
    {
        var representatives = new List<SafeMigrationExpectedTableInventory>();
        if (expected.FirstOrDefault(static table => table.Schema is null) is { } providerDefault)
        {
            representatives.Add(providerDefault);
        }

        representatives.AddRange(
            expected
                .Where(static table => table.Schema is not null)
                .DistinctBy(static table => table.Schema, StringComparer.Ordinal));

        return representatives
            .Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues)
            .ToArray();
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
        "transition_ready" => SafeMigrationObservedState.TransitionReady,
        _ => throw new InvalidOperationException("The PostgreSQL SafeMigrations classifier returned an unknown state."),
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
        SafeMigrationObservedState.TransitionReady => "transition_ready",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string BuildSchemaScope(
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected,
        PostgreSqlCatalogQueryParameters parameters
    )
    {
        var conditions = new List<string>();
        if (expected.Any(static table => table.Schema is null))
        {
            conditions.Add("n.nspname = current_schema()");
        }

        conditions.AddRange(
            expected
                .Select(static table => table.Schema)
                .Where(static schema => schema is not null)
                .Distinct(StringComparer.Ordinal)
                .Select(schema => $"n.nspname = {parameters.AddString(schema!)}"));

        return $"({string.Join(" OR ", conditions)})";
    }

    private static string BuildExpectedTableScope(
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected,
        PostgreSqlCatalogQueryParameters parameters,
        string schemaExpression,
        string tableExpression
    )
    {
        var conditions = expected
            .Select(table => "("
                + (table.Schema is null
                    ? $"{schemaExpression} = current_schema()"
                    : $"{schemaExpression} = {parameters.AddString(table.Schema)}")
                + $" AND {tableExpression} = {parameters.AddString(table.Table)})")
            .ToArray();

        return $"({string.Join(" OR ", conditions)})";
    }

    private static bool IsExpected(
        SafeMigrationExpectedTableInventory table,
        SafeMigrationDatabaseObjectKind kind,
        string name
    ) => kind switch
    {
        SafeMigrationDatabaseObjectKind.Column => table.Columns.Contains(name),
        SafeMigrationDatabaseObjectKind.Index => table.Indexes.Contains(name),
        _ => table.Constraints.TryGetValue(name, out var expectedKind) && expectedKind == kind
    };

    private static SafeMigrationUnexpectedObject Unexpected(
        SafeMigrationDatabaseObjectKind kind,
        string schema,
        string? table,
        string name
    ) => new(kind, schema, table, name, $"unexpected_{ObjectCode(kind)}");

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
            "The PostgreSQL unexpected-object inventory returned an unknown object kind."),
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

    private static string BuildUnexpectedTableSql(
        string schemaScope
    ) => $"""
          SELECT 'table', n.nspname, c.relname, c.relname, current_schema()
          FROM pg_catalog.pg_class c
          JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE {schemaScope} AND c.relkind IN ('r', 'p')
          ORDER BY 1, 2, 3, 4;
          """;

    private static string BuildUnexpectedChildObjectSql(
        string childScope,
        string indexScope
    ) => $"""
          SELECT 'column', n.nspname, c.relname, a.attname, current_schema()
          FROM pg_catalog.pg_attribute a
          JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
          JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE {childScope} AND c.relkind IN ('r', 'p')
            AND a.attnum > 0 AND NOT a.attisdropped
          UNION ALL
          SELECT CASE co.contype
              WHEN 'p' THEN 'primary_key'
              WHEN 'u' THEN 'unique_constraint'
              WHEN 'c' THEN 'check_constraint'
              WHEN 'f' THEN 'foreign_key'
              END,
              n.nspname,
              c.relname,
              co.conname,
              current_schema()
          FROM pg_catalog.pg_constraint co
          JOIN pg_catalog.pg_class c ON c.oid = co.conrelid
          JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE {childScope} AND co.contype IN ('p', 'u', 'c', 'f')
          UNION ALL
          SELECT 'index', n.nspname, tbl.relname, idx.relname, current_schema()
          FROM pg_catalog.pg_index i
          JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid
          JOIN pg_catalog.pg_class tbl ON tbl.oid = i.indrelid
          JOIN pg_catalog.pg_namespace n ON n.oid = tbl.relnamespace
          WHERE {indexScope}
            AND NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_constraint co WHERE co.conindid = idx.oid)
          ORDER BY 1, 2, 3, 4;
          """;

    private readonly record struct PostgreSqlDataProbeIdentity(
        string? Schema,
        string Table,
        string Column,
        int TargetLength
    );

    private readonly record struct PostgreSqlDataProbeResult(
        bool IsTransitionEligible,
        bool IsRequired,
        bool IsBlocked
    );

    private readonly record struct PostgreSqlTableIdentity(
        string? Schema,
        string Table
    );

    private sealed record PostgreSqlDataProbeCandidate(
        PostgreSqlDataProbeIdentity Identity,
        SafeMigrationOperation Operation,
        PostgreSqlSafeMigrationDataProbe Probe
    );

    private sealed class ExpectedTableLookup
    {
        private readonly Dictionary<(string Schema, string Table), SafeMigrationExpectedTableInventory> _explicit;
        private readonly Dictionary<string, SafeMigrationExpectedTableInventory> _providerDefault;

        public ExpectedTableLookup(
            IReadOnlyList<SafeMigrationExpectedTableInventory> expected
        )
        {
            _explicit = expected
                .Where(static table => table.Schema is not null)
                .ToDictionary(static table => (table.Schema!, table.Table));

            _providerDefault = expected
                .Where(static table => table.Schema is null)
                .ToDictionary(static table => table.Table, StringComparer.Ordinal);
        }

        public SafeMigrationExpectedTableInventory? Find(
            string schema,
            string table,
            string currentSchema
        )
        {
            if (_explicit.TryGetValue((schema, table), out var exact))
            {
                return exact;
            }

            return StringComparer.Ordinal.Equals(schema, currentSchema)
                && _providerDefault.TryGetValue(table, out var providerDefault)
                    ? providerDefault
                    : null;
        }
    }

    private sealed class AnalysisScope : IAsyncDisposable
    {
        private IDbContextTransaction? _transaction;

        public AnalysisScope(
            IDbContextTransaction? transaction
        )
        {
            _transaction = transaction;
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction is not null)
            {
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }
    }
}
