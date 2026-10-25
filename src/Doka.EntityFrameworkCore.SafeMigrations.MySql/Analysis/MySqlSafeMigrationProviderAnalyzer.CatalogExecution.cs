namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Rejects foreign identities and missing prerequisites before snapshot or status acquisition.</summary>
    /// <param name="connection">The open analysis connection.</param>
    /// <param name="plans">The immutable plans before any catalog snapshot binding.</param>
    /// <param name="maximumPayloadBytes">The qualified aggregate transport bound.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="operationOffset">The original operation window's stream offset.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The identity and prerequisite rejections that later phases must not query again.</returns>
    private async Task<MySqlShortCircuitState?[]> FindPrerequisiteShortCircuitStatesAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        var states = new MySqlShortCircuitState?[plans.Length];

        await FindDatabaseQualificationStatesAsync(
            connection,
            plans,
            states,
            maximumPayloadBytes,
            commandTimeout,
            operationOffset,
            cancellationToken);

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

        return states;
    }

    private async Task FindDatabaseQualificationStatesAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        MySqlShortCircuitState?[] states,
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
                   && plans[ordinal].CurrentDatabaseQualificationExpression is null)
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
                var parameterizer = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
                var selections = new List<string>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                var statementOrdinals = new List<int>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                var sqlBytes = trailerBytes;
                while (ordinal < plans.Length
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var plan = plans[ordinal];
                    if (plan.CurrentDatabaseQualificationExpression is null)
                    {
                        ordinal++;

                        continue;
                    }

                    var checkpoint = parameterizer.Capture();
                    var qualification = plan.RenderCurrentDatabaseQualificationExpression(parameterizer.Add);
                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                        + $"WHEN NOT COALESCE(({qualification}), FALSE) THEN 'unsupported' ELSE NULL END";

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
                        if (selections.Count == 0
                            && batch.Count == 1)
                        {
                            batch.RemoveLastCommand(command);

                            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                operationOffset + ordinal,
                                prospectiveBatchParameters,
                                prospectiveBatchPayload);
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

            await ReadDatabaseQualificationBatchAsync(batch, states, selectedOrdinals, plans, cancellationToken);
        }
    }

    private async Task FindPrerequisiteStatesAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        MySqlShortCircuitState?[] states,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        var captureIndexIdentity = plans.Any(static plan => plan.CaptureProjectedIndexIdentity);
        var ordinal = 0;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (ordinal < plans.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (ordinal < plans.Length
                   && (states[ordinal] is not null
                       || (StringComparer.Ordinal.Equals(plans[ordinal].PrerequisiteExpression, "TRUE")
                           && plans[ordinal].CapturedSourceSyntaxExpression is null)))
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
                var parameterizer = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
                var selections = new List<string>(
                    Math.Min(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, plans.Length - ordinal));

                var statementOrdinals = new List<int>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                var sqlBytes = trailerBytes;
                while (ordinal < plans.Length
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    // WHY: Only the builder's exact constant is proof that
                    // this catalog-only phase has nothing to evaluate. SQL
                    // equivalence, parameter values, and prior results are not.
                    if (states[ordinal] is not null
                        || (StringComparer.Ordinal.Equals(plans[ordinal].PrerequisiteExpression, "TRUE")
                            && plans[ordinal].CapturedSourceSyntaxExpression is null))
                    {
                        ordinal++;

                        continue;
                    }

                    var checkpoint = parameterizer.Capture();
                    var plan = plans[ordinal];
                    var prerequisite = plan.RenderPrerequisiteExpression(parameterizer.Add);
                    var syntax = plan.CapturedSourceSyntaxExpression is null
                        ? null
                        : plan.RenderCapturedSourceSyntaxExpression(parameterizer.Add);

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                        + (syntax is null ? string.Empty : $"WHEN NOT COALESCE(({syntax}), FALSE) THEN 'unsupported' ")
                        + $"WHEN NOT COALESCE(({prerequisite}), FALSE) THEN 'prerequisite_missing' "
                        + "ELSE NULL END";

                    if (captureIndexIdentity)
                    {
                        selection += plan.CaptureProjectedIndexIdentity
                            ? ", " + plan.RenderProjectedIndexIdentityExpression(parameterizer.Add)
                            : ", NULL";
                    }

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

            await ReadPrerequisiteBatchAsync(
                batch,
                plans,
                states,
                selectedOrdinals,
                captureIndexIdentity,
                cancellationToken);
        }
    }

    /// <summary>Reads lazy-guard states and their catalog-safe codes within the existing bounded dispatch.</summary>
    /// <param name="connection">The already-open analysis connection.</param>
    /// <param name="plans">The plans whose required-add facts were already bound.</param>
    /// <param name="states">The results retaining earlier qualification or prerequisite failures.</param>
    /// <param name="maximumPayloadBytes">The qualified per-dispatch catalog payload limit.</param>
    /// <param name="commandTimeout">The configured command timeout.</param>
    /// <param name="operationOffset">The complete-stream offset used for oversized-operation diagnostics.</param>
    /// <param name="cancellationToken">The asynchronous analysis cancellation token.</param>
    internal async Task FindStateEvaluationGuardStatesAsync(
        DbConnection connection,
        MySqlSafeMigrationRuntimePlan[] plans,
        MySqlShortCircuitState?[] states,
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
                var parameterizer = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
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
                    var stateEvaluationGuard = plan.RenderStateEvaluationGuardExpression(parameterizer.Add);
                    var guardFailure = plan.RenderStateEvaluationGuardFailureExpression(parameterizer.Add);
                    var classificationCode =
                        plan.RenderStateEvaluationGuardClassificationCodeExpression(parameterizer.Add);

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                        + $"WHEN NOT COALESCE(({stateEvaluationGuard}), FALSE) THEN ({guardFailure}) "
                        + "ELSE NULL END, CASE WHEN NOT COALESCE("
                        + $"({stateEvaluationGuard}), FALSE) THEN ({classificationCode}) ELSE NULL END";

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
        MySqlShortCircuitState shortCircuit,
        MySqlSafeMigrationRuntimePlan plan,
        SafeMigrationIntent intent
    ) => new(shortCircuit.State, SafeMigrationRepairCapability.None, false, shortCircuit.Code)
    {
        IndexPhysicalEnvironment = plan.IndexPhysicalEnvironment,
        IsInvariantUnsupported =
            shortCircuit.State == SafeMigrationObservedState.Unsupported
            && (plan.IsStaticallyUnsupported
                || shortCircuit.Code == "captured_expression_identifier_keyword_unverified"),
        RequiresLiveDataProof = IsFreshRequiredColumnAddMissing(intent, plan, shortCircuit.State, shortCircuit.Code)
            || IsBlockedRequiredColumnAddRowProof(intent, plan, shortCircuit.State, shortCircuit.Code),
    };

    /// <summary>Marks only fresh unsafe Missing classifications that independently proved no existing rows.</summary>
    /// <param name="intent">The immutable operation whose guard was evaluated.</param>
    /// <param name="plan">The enriched plan, including materialized required-add status.</param>
    /// <param name="state">The provider-classified current state.</param>
    /// <param name="code">The closed provider code distinguishing fresh addition from owned staging.</param>
    /// <returns>Whether this Missing result carries a captured empty-row proof, never a staging certificate.</returns>
    internal static bool IsFreshRequiredColumnAddMissing(
        SafeMigrationIntent intent,
        MySqlSafeMigrationRuntimePlan plan,
        SafeMigrationObservedState state,
        string? code
    )
    {
        if (state != SafeMigrationObservedState.Missing
            || intent is not EnsureColumnIntent ensure
            || SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(ensure.Definition)
            || StringComparer.Ordinal.Equals(code, "column_atomic_add_resume"))
        {
            return false;
        }

        // WHY: Fresh protocol status and structured SQL NULL's guarded Missing
        // state require an actual LIMIT-1 row check. Prerequisite absence and
        // staged rows cannot supply this empty-table certificate.
        return plan.RequiredColumnAddPlan is { } protocol
            ? StringComparer.Ordinal.Equals(protocol.StatusBinding, "'fresh'")
            : ensure.Definition.DefaultValue is
            {
                Kind: SafeMigrationDefaultValueKind.Sql,
                StructuredExpression: SafeMigrationSqlLiteralExpression { Value: null }
            };
    }

    /// <summary>Marks current blocked add rows as data-dependent without turning them into empty-row proof.</summary>
    /// <param name="intent">The immutable operation whose row guard was evaluated.</param>
    /// <param name="plan">The enriched plan carrying fresh protocol status when applicable.</param>
    /// <param name="state">The provider-classified current state.</param>
    /// <param name="code">The provider code that separates staging from fresh addition.</param>
    /// <returns>Whether subsequent DML must invalidate this adds captured negative row result.</returns>
    internal static bool IsBlockedRequiredColumnAddRowProof(
        SafeMigrationIntent intent,
        MySqlSafeMigrationRuntimePlan plan,
        SafeMigrationObservedState state,
        string? code
    ) => state == SafeMigrationObservedState.DataBlocked
        && IsFreshRequiredColumnAddMissing(intent, plan, SafeMigrationObservedState.Missing, code);

    private static async Task<string?> ReadCurrentDatabaseAsync(
        DbConnection connection,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = "SELECT DATABASE();";

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async Task<int> GetMaximumPayloadBytesAsync(
        DbConnection connection,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        ApplyCommandTimeout(command, commandTimeout);
        // WHY: Both values are connection-qualified immutable server facts.
        // Sharing the required packet probe avoids a new normal-path roundtrip
        // while preventing metadata readers from guessing owner casing.
        command.CommandText = "SELECT @@max_allowed_packet, @@lower_case_table_names;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var profile = await ReadNativeTransportProfileAsync(reader, cancellationToken);
        _tableNamesIgnoreCase = profile.OwnerMode != 0;
        try
        {
            return SafeMigrationCatalogQueryLimits.MySqlMaximumUtf8PayloadBytes(profile.MaximumPacket);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException("MySQL returned an invalid max_allowed_packet value.", exception);
        }
    }

    /// <summary>
    /// Rejects malformed native packet/owner profiles instead of minting speculative table equivalence.
    /// </summary>
    /// <param name="reader">The exact two-field required native transport result.</param>
    /// <param name="cancellationToken">The caller's cancellation token for both protocol reads.</param>
    /// <returns>The exact positive packet ceiling and native table-name mode zero, one or two.</returns>
    internal static async Task<(long MaximumPacket, int OwnerMode)> ReadNativeTransportProfileAsync(
        DbDataReader reader,
        CancellationToken cancellationToken
    )
    {
        if (reader.FieldCount != 2
            || !await reader.ReadAsync(cancellationToken)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1)
            || reader.GetValue(0) is not (int or uint or long or ulong)
            || reader.GetValue(1) is not (sbyte or byte or short or ushort or int or uint or long or ulong))
        {
            throw new InvalidOperationException("MySQL did not return its packet and owner-identity profile.");
        }

        var packet = Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture);
        var ownerMode = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        if (packet is <= 0 or > long.MaxValue
            || ownerMode is < 0 or > 2
            || await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("MySQL returned an invalid packet or owner-identity profile.");
        }

        return ((long)packet, (int)ownerMode);
    }

    private static async Task ReadAnalysisAsync(
        SafeMigrationCatalogBatch batch,
        SafeMigrationProviderAnalysis[] results,
        MySqlSafeMigrationRuntimePlan[] plans,
        SafeMigrationOperation[] operations,
        MySqlDataProbeResult?[] dataProbeResults,
        int operationOffset,
        IReadOnlyList<int> submittedOrdinals,
        CancellationToken cancellationToken
    )
    {
        var consumed = 0;
        var captureDropCompleteness = plans.Any(static plan => plan.DropLeavesNoGeneratedColumnsExpression is not null);
        await batch.ForEachResultSetAsync(
            async (reader, token) =>
            {
                if (captureDropCompleteness && reader.FieldCount != 10)
                {
                    throw new InvalidOperationException("The MySQL DROP classifier returned an invalid shape.");
                }

                while (await reader.ReadAsync(token))
                {
                    var ordinal = reader.GetInt32(0);
                    SafeMigrationCatalogWorkOrder.ValidateResultOrdinal(ordinal, submittedOrdinals, ref consumed);
                    if (results[ordinal] is not null)
                    {
                        throw new InvalidOperationException(
                            "The MySQL SafeMigrations classifier returned an already resolved ordinal.");
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

                    var localOrdinal = ordinal - operationOffset;
                    var plan = plans[localOrdinal];
                    var dropLeavesNoGeneratedColumns = false;
                    if (captureDropCompleteness)
                    {
                        var expectedCompleteness = plan.DropLeavesNoGeneratedColumnsExpression is not null;
                        if (reader.IsDBNull(9) == expectedCompleteness)
                        {
                            throw new InvalidOperationException("The MySQL DROP completeness evidence is invalid.");
                        }

                        if (expectedCompleteness)
                        {
                            dropLeavesNoGeneratedColumns = ReadColumnDependencyBoolean(reader, 9);
                        }
                    }

                    var evidence = plan.ModelManagedRowEvidenceExpression is null
                        ? null
                        : SafeMigrationModelManagedDataEvidence.Parse(
                            reader.GetString(5),
                            plan.ModelManagedRowCount,
                            plan.ModelManagedDependencyCountsExpression is null ? string.Empty : reader.GetString(6),
                            plan.ModelManagedDependencyCount,
                            "MySQL");

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
                        differences =
                            state is not (SafeMigrationObservedState.Different
                                or SafeMigrationObservedState.DataBlocked
                                or SafeMigrationObservedState.Unsupported)
                            || reader.IsDBNull(7)
                                ? []
                                : SafeMigrationFacetDifferenceParser.Parse(reader.GetString(7), "MySQL");
                    }

                    var operationalImpact = state == SafeMigrationObservedState.DataBlocked
                        || (state == SafeMigrationObservedState.Different
                            && repairCapability == SafeMigrationRepairCapability.Safe)
                            ? plan.RepairOperationalImpact
                            : SafeMigrationOperationalImpact.NotApplicable;

                    var requiresNullabilityDataProof = plan.MayRequireNullabilityDataProof
                        && (state == SafeMigrationObservedState.DataBlocked
                            || (state == SafeMigrationObservedState.Different
                                && repairCapability == SafeMigrationRepairCapability.Safe))
                        && differences.Any(static difference =>
                            StringComparer.Ordinal.Equals(difference.Facet, "column_nullability"));

                    var analysis =
                        new SafeMigrationProviderAnalysis(
                            state,
                            repairCapability,
                            reader.GetBoolean(2),
                            code,
                            operationalImpact,
                            differences)
                        {
                            IndexPhysicalEnvironment = plan.IndexPhysicalEnvironment,
                            IsInvariantUnsupported =
                                state == SafeMigrationObservedState.Unsupported && plan.IsStaticallyUnsupported,
                            MatchedObjectName =
                                state == SafeMigrationObservedState.Matching && !reader.IsDBNull(8)
                                    ? reader.GetString(8)
                                    : null,
                            ModelManagedDataEvidence = evidence,
                            DropLeavesNoGeneratedColumns =
                                state == SafeMigrationObservedState.Matching
                                && operations[localOrdinal].Intent is DropColumnIntent
                                && dropLeavesNoGeneratedColumns,
                            RequiredColumnAddResumesOwnedStaging = state == SafeMigrationObservedState.Missing
                                && plan.RequiredColumnAddPlan is not null
                                && StringComparer.Ordinal.Equals(code, "column_atomic_add_resume"),
                            // WHY: Every VARCHAR transition carries a probe template,
                            // but widening is catalog-only. Only a catalog-confirmed
                            // narrowing or nullable-to-required result becomes stale
                            // after projected DML.
                            RequiresLiveDataProof = dataProbeResults[localOrdinal]?.IsRequired == true
                                || requiresNullabilityDataProof
                                || IsFreshRequiredColumnAddMissing(operations[localOrdinal].Intent, plan, state, code)
                                || IsBlockedRequiredColumnAddRowProof(
                                    operations[localOrdinal].Intent,
                                    plan,
                                    state,
                                    code),
                            // WHY: Provider backfill executes UPDATE before MODIFY.
                            // Triggers can change any table, so ordered projection
                            // must invalidate all row proofs after accepted repair.
                            RepairMutatesData = state == SafeMigrationObservedState.Different
                                && repairCapability == SafeMigrationRepairCapability.Safe
                                && operations[localOrdinal] is
                                {
                                    Policy: SafeMigrationPolicy.RepairIfSafe,
                                    Intent: AlterColumnIntent
                                    {
                                        OldDefinition.IsNullable: true, Definition.IsNullable: false,
                                    } alterIntent,
                                }
                                && SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(
                                    alterIntent.Definition.DefaultValue),
                            // WHY: Ensure's full-definition repair never emits
                            // UPDATE. Only its non-narrowing string proof can retain
                            // a captured source-domain bound across ordered DDL.
                            RepairPreservesLiveValueDomain = state == SafeMigrationObservedState.Different
                                && repairCapability == SafeMigrationRepairCapability.Safe
                                && plan.RepairOperationalImpact == SafeMigrationOperationalImpact.TableRewritePossible
                                && dataProbeResults[localOrdinal]?.IsRequired != true
                                && operations[localOrdinal] is
                                {
                                    Policy: SafeMigrationPolicy.RepairIfSafe,
                                    Intent: EnsureColumnIntent { Definition.ClrType: var columnType },
                                }
                                && columnType == typeof(string),
                        };

                    results[ordinal] = analysis;
                }
            },
            cancellationToken);

        SafeMigrationCatalogWorkOrder.ValidateCompletion(consumed, submittedOrdinals);
    }

    private static async Task ReadDatabaseQualificationBatchAsync(
        SafeMigrationCatalogBatch batch,
        MySqlShortCircuitState?[] states,
        List<int> selectedOrdinals,
        MySqlSafeMigrationRuntimePlan[] plans,
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
                            "The MySQL SafeMigrations database-qualification classifier returned an invalid ordinal.");
                    }

                    if (!reader.IsDBNull(1))
                    {
                        states[resultOrdinal] = new MySqlShortCircuitState(
                            ParseState(reader.GetString(1)),
                            plans[resultOrdinal].CurrentDatabaseQualificationFailureCode
                            ?? "database_qualifier_mismatch");
                    }

                    row++;
                }
            },
            cancellationToken);

        if (row != selectedOrdinals.Count)
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations database-qualification classifier returned an inconsistent row count.");
        }
    }

    private static async Task ReadPrerequisiteBatchAsync(
        SafeMigrationCatalogBatch batch,
        MySqlSafeMigrationRuntimePlan[] plans,
        MySqlShortCircuitState?[] states,
        List<int> selectedOrdinals,
        bool captureIndexIdentity,
        CancellationToken cancellationToken
    )
    {
        var row = 0;
        await batch.ForEachResultSetAsync(
            async (reader, token) =>
            {
                if (reader.FieldCount != (captureIndexIdentity ? 3 : 2))
                {
                    throw new InvalidOperationException("The MySQL prerequisite classifier returned an invalid shape.");
                }

                while (await reader.ReadAsync(token))
                {
                    var resultOrdinal = reader.GetInt32(0);
                    if (row >= selectedOrdinals.Count
                        || resultOrdinal != selectedOrdinals[row])
                    {
                        throw new InvalidOperationException(
                            "The MySQL SafeMigrations prerequisite classifier returned an invalid ordinal.");
                    }

                    if (states[resultOrdinal] is null
                        && !reader.IsDBNull(1))
                    {
                        var state = ParseState(reader.GetString(1));
                        states[resultOrdinal] = new MySqlShortCircuitState(
                            state,
                            state == SafeMigrationObservedState.Unsupported
                                ? "captured_expression_identifier_keyword_unverified"
                                : "classified_prerequisite_missing");
                    }

                    if (captureIndexIdentity && !reader.IsDBNull(2))
                    {
                        var identity = ParseState(reader.GetString(2));
                        if (identity is not (SafeMigrationObservedState.Missing
                                or SafeMigrationObservedState.Matching
                                or SafeMigrationObservedState.Different)
                            || !plans[resultOrdinal].CaptureProjectedIndexIdentity)
                        {
                            throw new InvalidOperationException("The MySQL captured index identity is invalid.");
                        }

                        plans[resultOrdinal] = plans[resultOrdinal] with { CapturedIndexIdentity = identity };
                    }

                    row++;
                }
            },
            cancellationToken);

        if (row != selectedOrdinals.Count)
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations prerequisite classifier returned an inconsistent row count.");
        }
    }

    private static async Task ReadStateEvaluationGuardBatchAsync(
        SafeMigrationCatalogBatch batch,
        MySqlShortCircuitState?[] states,
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
                        || resultOrdinal != selectedOrdinals[row]
                        || (uint)resultOrdinal >= (uint)states.Length)
                    {
                        throw new InvalidOperationException(
                            "The MySQL SafeMigrations state-evaluation guard classifier returned an invalid ordinal.");
                    }

                    if (states[resultOrdinal] is null
                        && !reader.IsDBNull(1))
                    {
                        var state = ParseState(reader.GetString(1));

                        states[resultOrdinal] = new MySqlShortCircuitState(
                            state,
                            reader.IsDBNull(2) ? $"classified_{StateCode(state)}" : reader.GetString(2));
                    }

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

    private static bool TargetsCurrentDatabase(
        SafeMigrationIntent intent,
        string? currentDatabase
    ) => MySqlSafeMigrationCatalogSqlBuilder.TargetsDatabase(intent, currentDatabase);

    /// <summary>Retains one catalog-proven early classification and its provider diagnostic.</summary>
    /// <param name="State">The state proven before the main classification query.</param>
    /// <param name="Code">The exact catalog-qualified code, or the classified-state fallback.</param>
    internal readonly record struct MySqlShortCircuitState(
        SafeMigrationObservedState State,
        string Code
    );
}
