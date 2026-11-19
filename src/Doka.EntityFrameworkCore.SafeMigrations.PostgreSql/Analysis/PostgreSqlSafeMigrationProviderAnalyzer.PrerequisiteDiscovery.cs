namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
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
        var ordinal = 0;
        List<int>? indexInputOrdinals = null;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (ordinal < operations.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);
            Exception? batchFailure = null;

            try
            {
                var selectedOrdinals = new List<int>(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
                    * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch);

                var batchParameterCount = 0;
                var batchPayloadBytes = 0;
                while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                    && ordinal < operations.Count)
                {
                    var command = batch.CreateCommand();
                    using var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
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
                        var prerequisite = builder.BuildPrerequisiteExpression(operation);

                        // WHY: This exact builder constant needs no live catalog
                        // proof before classification, which evaluates metadata
                        // only for these operations and folds in their native owner
                        // guard. Row-reading plans keep a nonconstant prerequisite.
                        if (StringComparer.Ordinal.Equals(prerequisite, "TRUE"))
                        {
                            parameters.Rollback(checkpoint);
                            ordinal++;

                            continue;
                        }

                        var identityGuard = builder.BuildObjectIdentityGuard(operation.Intent);
                        var indexInputState = "NULL";
                        if (operation.Intent is EnsureIndexIntent index
                            && !operation
                                .GetAnnotations()
                                .Any()
                            && builder.GetUnsupportedFeature(operation.Intent) is null)
                        {
                            indexInputState = builder.BuildIndexInputRoutingState(index);
                        }

                        var selection = $"""
                            SELECT {ordinal.ToString(CultureInfo.InvariantCulture)},
                            CASE WHEN NOT COALESCE(({identityGuard}),
                            FALSE) THEN 'schema_search_path_mismatch'
                            WHEN NOT COALESCE(({prerequisite}), FALSE)
                            THEN 'prerequisite_missing' ELSE ({indexInputState}) END
                            """;

                        var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                            + (selections.Count == 0
                                ? 0
                                : separatorBytes);

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
                        selectedOrdinals.Add(ordinal);
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

                if (batch.Count == 0)
                {
                    continue;
                }

                var candidates = await ReadPrerequisiteBatchAsync(batch, states, selectedOrdinals, cancellationToken);
                if (candidates is not null)
                {
                    (indexInputOrdinals ??= []).AddRange(candidates);
                }
            }
            catch (Exception exception)
            {
                batchFailure = exception;

                throw;
            }
            finally
            {
                await SafeMigrationAnalysisCleanup.DisposeAsync(
                    batch,
                    batchFailure,
                    "SafeMigrations.AnalysisBatchDisposeException");
            }
        }

        if (indexInputOrdinals is not null)
        {
            await FindIndexInputStatesAsync(
                connection,
                operations,
                states,
                indexInputOrdinals,
                commandTimeout,
                cancellationToken);
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

            var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);
            Exception? batchFailure = null;

            try
            {
                var selectedOrdinals = new List<StateEvaluationGuardRequest>(
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
                    using var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                    var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                        _typeMappingSource,
                        _sqlGenerationHelper,
                        parameters.AddString,
                        parameters.Add);

                    var selections = new List<string>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                    var statementOrdinals = new List<StateEvaluationGuardRequest>(
                        SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);

                    var sqlBytes = trailerBytes;
                    while (ordinal < operations.Count
                        && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                    {
                        var operation = operations[ordinal]
                            ?? throw new ArgumentException(
                                "The operation batch cannot contain null entries.",
                                nameof(operations));

                        if (_generatedDropCatalogReadable && operation.Intent is DropColumnIntent)
                        {
                            // WHY: The same invocation already proved the required native
                            // dependency catalogs readable in its always-run
                            // visibility scalar. Runtime SQL keeps its fresh guard.
                            ordinal++;

                            continue;
                        }

                        var checkpoint = parameters.Capture();
                        var plan = builder.Build(operation, includeRuntimeIndexInputGuard: false);
                        if (states[ordinal] is null
                            && !_columnConstraintCatalogReadable
                            && plan.ColumnTransitionTargetCatalogGuardExpression is not null)
                        {
                            // WHY: Required-target equality itself needs pg_constraint.
                            // Skip SQL binding only after earlier owner/prerequisite
                            // refusals have retained their original precedence.
                            parameters.Rollback(checkpoint);
                            states[ordinal] = SafeMigrationObservedState.Unsupported;
                            (_stateEvaluationGuardShortCircuitCodes ??= [])[ordinal] = "integer_widening_unproven";
                            ordinal++;

                            continue;
                        }

                        if (states[ordinal] is not null
                            || plan.StateEvaluationGuardFailureExpression is null)
                        {
                            parameters.Rollback(checkpoint);
                            ordinal++;

                            continue;
                        }

                        var guardFailed = $"NOT COALESCE(({plan.StateEvaluationGuardExpression}), FALSE)";
                        var defaultCode = plan.UnsupportedCode ?? "state_evaluation_guard";
                        var failureCode = plan.StateEvaluationGuardFailureCodeExpression
                            ?? parameters.AddString(defaultCode);

                        var selection = $"""
                            SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE WHEN {guardFailed}
                            THEN ({plan.StateEvaluationGuardFailureExpression})
                            ELSE NULL END, CASE WHEN {guardFailed}
                            THEN ({failureCode})
                            ELSE NULL END
                            """;

                        var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                            + (selections.Count == 0
                                ? 0
                                : separatorBytes);

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
                        statementOrdinals.Add(
                            new StateEvaluationGuardRequest(
                                ordinal,
                                defaultCode,
                                plan.StateEvaluationGuardFailureCodeExpression is not null));

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

                var reasons = await ReadStateEvaluationGuardBatchAsync(
                    batch,
                    states,
                    selectedOrdinals,
                    cancellationToken);

                if (reasons is not null)
                {
                    foreach (var (operationOrdinal, reason) in reasons)
                    {
                        (_stateEvaluationGuardShortCircuitCodes ??= [])[operationOrdinal] = reason;
                    }
                }
            }
            catch (Exception exception)
            {
                batchFailure = exception;

                throw;
            }
            finally
            {
                await SafeMigrationAnalysisCleanup.DisposeAsync(
                    batch,
                    batchFailure,
                    "SafeMigrations.AnalysisBatchDisposeException");
            }
        }
    }
}
