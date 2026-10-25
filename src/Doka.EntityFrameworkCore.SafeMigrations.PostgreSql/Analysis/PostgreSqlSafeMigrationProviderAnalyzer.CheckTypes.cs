namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private SparseCheckTypeFacts? _sparseCheckTypeFacts;

    /// <summary>Rejects proved native operand incompatibility without reading application rows.</summary>
    private SafeMigrationProviderAnalysis? QualifyProjectedCheckTypes(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (analysis.ObservedState != SafeMigrationObservedState.Missing
            || operation.Intent is not EnsureCheckConstraintIntent check
            || _sparseCheckTypeFacts?.Checks.Contains(check.Definition) != true
            || check.Definition.Expression is not { } expression)
        {
            return null;
        }

        var usesUnchangedLiveMember = false;
        foreach (var column in NativePrerequisiteColumns(check))
        {
            if (!columns.TryGetProjectedColumn(check.Definition.Table, check.Definition.Schema, column, out _)
                && HasLiveColumn(check.Definition.Table, check.Definition.Schema, column))
            {
                usesUnchangedLiveMember = true;

                break;
            }
        }

        if (!usesUnchangedLiveMember)
        {
            // WHY: This eligibility hook is scoped to new sparse promotion.
            // Fully authored projection and independently live matching CHECKs
            // retain their established provider contract unchanged.
            return null;
        }

        expression = SafeMigrationCapturedExpressionNormalization.NormalizeCheck(check.Definition, true).Expression!;
        var resolved = ResolveCheckExpression(expression, check.Definition, columns);
        if (resolved.TypeOid is null && !resolved.Incompatible)
        {
            // WHY: The bridge can promote an initially missing prerequisite
            // only with complete native binding evidence. Opaque SQL, domains
            // and unresolved polymorphic forms retain their original blocker;
            // an independently matching live CHECK needs no such new proof.
            return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationRepairCapability.None, postconditionSatisfied: false,
                "projected_check_binding_unproven");
        }

        if (!resolved.Incompatible && (resolved.TypeOid == 16 || CanImplicitlyCast(resolved.TypeOid!.Value, 16)))
        {
            return null;
        }

        // WHY: Empty rows prove the CHECK has no violating data. They do not
        // make TEXT > integer a valid PostgreSQL operator. This metadata-only
        // certificate never PREPAREs SQL or invokes user functions during analysis.
        return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, "structured_check_operand_type");
    }

    /// <summary>Reads only visible signatures and implicit casts for requested sparse expression names.</summary>
    private async Task<SparseCheckTypeFacts?> CaptureSparseCheckTypeFactsAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> results,
        IReadOnlyList<LivePrerequisiteRequest> memberRequests,
        IReadOnlyDictionary<LiveColumnTypeIdentity, LiveColumnStorage> liveTypes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var hasSparseCheck = false;
        // WHY: Inspect each CHECK together with its own prerequisite observation.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (results[ordinal].ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                && operations[ordinal].Intent is EnsureCheckConstraintIntent { Definition.Expression: not null })
            {
                hasSparseCheck = true;

                break;
            }
        }

        if (!hasSparseCheck)
        {
            return null;
        }

        var members = memberRequests.OfType<LiveColumnRequest>().Select(static item => item.Identity).ToHashSet();
        var checks = new HashSet<ExpectedCheckConstraintDefinition>();
        var requests = new List<CheckCallableIdentity>();
        var requested = new HashSet<CheckCallableIdentity>();
        // WHY: Keep CHECK capability requests tied to their matching analysis slots.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results[ordinal].ObservedState != SafeMigrationObservedState.PrerequisiteMissing
                || operations[ordinal].Intent is not EnsureCheckConstraintIntent check
                || check.Definition.Expression is not { } expression
                || !NativePrerequisiteColumns(check).Any(column => members.Contains(
                    LiveColumnKey(check.Definition.Table, check.Definition.Schema, column))))
            {
                continue;
            }

            checks.Add(check.Definition);
            expression = SafeMigrationCapturedExpressionNormalization.NormalizeCheck(
                check.Definition, true).Expression!;
            foreach (var node in CheckExpressionNodes(expression))
            {
                var identity = node switch
                {
                    SafeMigrationSqlBinaryExpression binary when binary.Operator
                        is not (SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or)
                        => new CheckCallableIdentity(CheckOperator(binary.Operator), 2, Function: false),
                    SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Negate }
                        => new CheckCallableIdentity("-", 1, Function: false),
                    SafeMigrationSqlBetweenExpression => new CheckCallableIdentity(">=", 2, Function: false),
                    SafeMigrationSqlInExpression => new CheckCallableIdentity("=", 2, Function: false),
                    SafeMigrationSqlFunctionExpression function when !IsCommonTypeFunction(function.Name)
                        && !function.Name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase)
                        => new CheckCallableIdentity(function.Name.ToLowerInvariant(),
                            function.Arguments.Count, Function: true),
                    SafeMigrationSqlCastExpression cast when CheckExpressionTypeIdentity(cast) is { } target
                        => new CheckCallableIdentity(target.StoreType, 1, Function: false, ExplicitCast: true),
                    SafeMigrationSqlCollateExpression collate => CheckCollationIdentity(collate),
                    SafeMigrationSqlFunctionExpression function when function.Name.Equals(
                        "NULLIF", StringComparison.OrdinalIgnoreCase)
                        => new CheckCallableIdentity("=", 2, Function: false),
                    _ => (CheckCallableIdentity?)null,
                };

                if (identity is { } callable && requested.Add(callable))
                {
                    requests.Add(callable);
                }

                if (node is SafeMigrationSqlBetweenExpression
                    && requested.Add(new CheckCallableIdentity("<=", 2, Function: false)))
                {
                    requests.Add(new CheckCallableIdentity("<=", 2, Function: false));
                }
            }
        }

        if (checks.Count == 0)
        {
            return null;
        }

        var signatures = new Dictionary<CheckCallableIdentity, List<CheckCallableSignature>>();
        var casts = new HashSet<CheckImplicitCast>();
        foreach (var storage in liveTypes.Values)
        {
            casts.Add(new CheckImplicitCast(storage.TypeOid, storage.TypeOid));
        }

        if (requests.Count > 0)
        {
            await SafeMigrationCatalogProbeBatch.ReadAsync(connection, requests.Count,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes, commandTimeout,
                (command, offset) => BuildCheckCallableStatement(command, requests, offset),
                async (reader, offset, count, token) =>
                {
                    if (reader.FieldCount != 8)
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }

                    var next = offset;
                    var current = -1;
                    var absent = false;
                    var seen = new HashSet<long>();
                    while (await reader.ReadAsync(token))
                    {
                        var ordinal = reader.GetInt32(0);
                        if (ordinal < offset || ordinal >= offset + count
                            || ordinal != current && ordinal != next)
                        {
                            throw InvalidLivePrerequisiteEvidence();
                        }

                        if (ordinal != current)
                        {
                            current = ordinal;
                            next = ordinal + 1;
                            absent = false;
                            seen.Clear();
                            signatures.Add(requests[ordinal], []);
                        }

                        if (absent)
                        {
                            throw InvalidLivePrerequisiteEvidence();
                        }

                        if (reader.IsDBNull(1))
                        {
                            if (seen.Count != 0 || Enumerable.Range(2, 6).Any(index => !reader.IsDBNull(index)))
                            {
                                throw InvalidLivePrerequisiteEvidence();
                            }

                            absent = true;

                            continue;
                        }

                        if (reader.GetValue(1) is not long oid || oid <= 0 || !seen.Add(oid)
                            || reader.GetValue(2) is not long result || result <= 0
                            || reader.GetValue(3) is not string argumentText
                            || reader.GetValue(4) is not string castText
                            || reader.GetValue(5) is not bool polymorphic
                            || reader.GetValue(6) is not bool variadic
                            || reader.GetValue(7) is not long nativeNamespace || nativeNamespace < 0
                            || requests[ordinal].Function && nativeNamespace == 0
                            || variadic && !requests[ordinal].Function)
                        {
                            throw InvalidLivePrerequisiteEvidence();
                        }

                        var arguments = CheckOidList(argumentText);
                        if (arguments.Length != requests[ordinal].Arity)
                        {
                            throw InvalidLivePrerequisiteEvidence();
                        }

                        foreach (var pair in castText.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var separator = pair.IndexOf(':', StringComparison.Ordinal);
                            if (separator <= 0 || !long.TryParse(pair.AsSpan(0, separator),
                                    NumberStyles.None, CultureInfo.InvariantCulture, out var source)
                                || !long.TryParse(pair.AsSpan(separator + 1), NumberStyles.None,
                                    CultureInfo.InvariantCulture, out var target) || source <= 0 || target <= 0)
                            {
                                throw InvalidLivePrerequisiteEvidence();
                            }

                            casts.Add(new CheckImplicitCast(source, target));
                        }

                        signatures[requests[ordinal]].Add(new CheckCallableSignature(result, arguments,
                            polymorphic, variadic, nativeNamespace));
                    }

                    if (next != offset + count)
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }
                }, cancellationToken);
        }

        return new SparseCheckTypeFacts(checks, signatures, casts);
    }

    /// <summary>Uses the same statement, parameter, batch and payload bounds as member capture.</summary>
    private SafeMigrationCatalogProbeStatement BuildCheckCallableStatement(
        SafeMigrationCatalogCommand command,
        IReadOnlyList<CheckCallableIdentity> requests,
        int offset
    )
    {
        var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
        var selections = new List<string>();
        var sqlBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (offset + selections.Count < requests.Count
            && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
        {
            var ordinal = offset + selections.Count;
            var checkpoint = parameters.Capture();
            var request = requests[ordinal];
            var name = parameters.AddString(request.Name);
            var arguments = request.Function
                ? "ARRAY(SELECT CASE WHEN p.provariadic <> 0 AND arg >= p.pronargs - 1 "
                    + "THEN p.provariadic ELSE p.proargtypes[arg] END "
                    + $"FROM generate_series(0, {request.Arity - 1}) arg)"
                : request.Arity == 1 ? "ARRAY[o.oprright]" : "ARRAY[o.oprleft, o.oprright]";

            // WHY: Scalar variadic expansion still requires its first actual
            // variadic input unless a native DEFAULT supplies it. Subtracting
            // another argument would invent a callable zero-input overload.
            var rows = request.Collation
                ? "SELECT c.oid, 16::oid AS result, ARRAY[]::oid[] AS args, "
                    + "FALSE AS variadic, c.collnamespace AS native_namespace FROM pg_catalog.pg_collation c "
                    + "WHERE c.oid = pg_catalog.to_regcollation(" + name + ")"
                : request.ExplicitCast
                ? "SELECT cv.oid, cv.casttarget AS result, ARRAY[cv.castsource] AS args, "
                    + "FALSE AS variadic, 0::oid AS native_namespace "
                    + "FROM pg_catalog.pg_cast cv WHERE cv.casttarget = pg_catalog.to_regtype(" + name + ")"
                : request.Function
                ? "SELECT p.oid, p.prorettype AS result, " + arguments + " AS args, "
                    + "p.provariadic <> 0 AS variadic, p.pronamespace AS native_namespace "
                    + "FROM pg_catalog.pg_proc p "
                    + $"WHERE p.proname = {name} AND pg_catalog.pg_function_is_visible(p.oid) "
                    + $"AND {request.Arity} <= pg_catalog.current_setting('max_function_args')::integer "
                    + "AND p.prokind = 'f' AND NOT p.proretset "
                    + $"AND p.pronargs - p.pronargdefaults <= {request.Arity} "
                    + $"AND (p.provariadic <> 0 OR p.pronargs >= {request.Arity})"
                : "SELECT o.oid, o.oprresult AS result, " + arguments + " AS args, "
                    + "FALSE AS variadic, o.oprnamespace AS native_namespace FROM pg_catalog.pg_operator o "
                    + $"WHERE o.oprname = {name} AND pg_catalog.pg_operator_is_visible(o.oid) "
                    + $"AND o.oprkind = '{(request.Arity == 1 ? "l" : "b")}' AND o.oprresult <> 0";

            var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, c.oid::bigint, "
                + "c.result::bigint, array_to_string(c.args, ','), "
                + "CASE WHEN c.oid IS NULL THEN NULL::text ELSE COALESCE((SELECT string_agg("
                + "castsource::text || ':' || casttarget::text, ',' ORDER BY castsource, casttarget) "
                + "FROM pg_catalog.pg_cast WHERE castcontext = 'i' AND casttarget = ANY(c.args)), '') END, "
                + "CASE WHEN c.oid IS NULL THEN NULL::boolean ELSE EXISTS (SELECT 1 FROM pg_catalog.pg_type t "
                + "WHERE t.oid = ANY(c.args) AND t.typtype = 'p') END, c.variadic, c.native_namespace::bigint "
                + "FROM (SELECT 1) seed LEFT JOIN LATERAL (" + rows + ") c ON TRUE";

            var bytes = Encoding.UTF8.GetByteCount(selection)
                + (selections.Count == 0 ? 0 : Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator));

            var payload = checked(sqlBytes + bytes + parameters.Utf8PayloadBytes);
            if (SafeMigrationCatalogQueryLimits.Exceeded(parameters.Count, payload,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
            {
                var count = parameters.Count;
                parameters.Rollback(checkpoint);
                if (selections.Count == 0)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(ordinal, count, payload);
                }

                break;
            }

            selections.Add(selection);
            sqlBytes += bytes;
        }

        command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
            + SafeMigrationCatalogQueryLimits.Trailer;

        return new SafeMigrationCatalogProbeStatement(selections.Count, parameters.Utf8PayloadBytes, offset);
    }

    /// <summary>Resolves current accepted member types without substituting authored future proposals.</summary>
    private CheckExpressionType ResolveCheckExpression(
        SafeMigrationSqlExpression expression,
        ExpectedCheckConstraintDefinition owner,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var pending = new SafeMigrationWorkStack<CheckExpressionFrame>();
        try
        {
            while (true)
            {
                var frame = new CheckExpressionFrame(expression);
                if (TryBeginCheckExpression(ref frame, out var child))
                {
                    pending.Push(frame);
                    expression = child!;

                    continue;
                }

                var result = ResolveCheckLeafExpression(expression, owner, columns);
                while (true)
                {
                    if (!pending.TryPop(out frame))
                    {
                        return result;
                    }

                    if (TryContinueCheckExpression(ref frame, ref result, out child))
                    {
                        // WHY: A continuation stores only an active ancestor.
                        // Lists retain one cursor rather than one frame per sibling.
                        pending.Push(frame);
                        expression = child!;

                        break;
                    }
                }
            }
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Starts only the child evaluation required by the original expression semantics.</summary>
    private bool TryBeginCheckExpression(ref CheckExpressionFrame frame, out SafeMigrationSqlExpression? child)
    {
        switch (frame.Expression)
        {
            case SafeMigrationSqlCastExpression cast:
                child = cast.Operand;

                return true;
            case SafeMigrationSqlCollateExpression collate:
                child = collate.Operand;

                return true;
            case SafeMigrationSqlNullTestExpression nullTest:
                child = nullTest.Operand;

                return true;
            case SafeMigrationSqlUnaryExpression unary when CheckExpressionTypeIdentity(unary) is null:
                child = unary.Operand;

                return true;
            case SafeMigrationSqlBinaryExpression binary:
                child = binary.Left;

                return true;
            case SafeMigrationSqlBetweenExpression between:
                child = between.Operand;

                return true;
            case SafeMigrationSqlInExpression list:
                child = list.Operand;

                return true;
            case SafeMigrationSqlFunctionExpression function when function.Arguments.Count != 0:
                frame.Results = new CheckExpressionType[function.Arguments.Count];
                child = function.Arguments[0];

                return true;
            case SafeMigrationSqlCaseExpression conditional:
                frame.Results = new CheckExpressionType[conditional.WhenClauses.Count + 1];
                if (conditional.ElseResult is { } otherwise)
                {
                    // WHY: Native CASE common-type selection starts with ELSE,
                    // before any WHEN condition or THEN result is resolved.
                    frame.Step = -1;
                    child = otherwise;

                    return true;
                }

                frame.Results[0] = new CheckExpressionType(705,
                    UnknownLiteral: new SafeMigrationSqlLiteralExpression(null));

                child = conditional.WhenClauses[0].Condition;

                return true;
        }

        child = null;

        return false;
    }

    /// <summary>Resumes one parent without recursively evaluating later or rejected operands.</summary>
    private bool TryContinueCheckExpression(
        ref CheckExpressionFrame frame,
        ref CheckExpressionType result,
        out SafeMigrationSqlExpression? child)
    {
        child = null;
        switch (frame.Expression)
        {
            case SafeMigrationSqlCastExpression cast:
                result = ResolveCheckCast(cast, result);

                return false;
            case SafeMigrationSqlCollateExpression collate:
                result = ResolveCheckCollate(collate, result);

                return false;
            case SafeMigrationSqlNullTestExpression:
                result = BooleanResult(result);

                return false;
            case SafeMigrationSqlUnaryExpression unary:
                result = unary.Operator == SafeMigrationSqlUnaryOperator.Not
                    ? BooleanOperands([result])
                    : ResolveCheckCallable(new CheckCallableIdentity("-", 1, Function: false), [result]);

                return false;
            case SafeMigrationSqlBinaryExpression binary:
                if (frame.Step++ == 0)
                {
                    frame.First = result;
                    child = binary.Right;

                    return true;
                }

                CheckExpressionType[] operands = [frame.First, result];
                result = binary.Operator is SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or
                    ? BooleanOperands(operands)
                    : ResolveCheckCallable(new CheckCallableIdentity(CheckOperator(binary.Operator),
                        2, Function: false), operands);

                return false;
            case SafeMigrationSqlBetweenExpression between:
                if (frame.Step == 0)
                {
                    frame.Step = 1;
                    frame.First = result;
                    child = between.Lower;

                    return true;
                }

                if (frame.Step == 1)
                {
                    frame.Step = 2;
                    frame.Second = ResolveCheckCallable(new CheckCallableIdentity(">=", 2, Function: false),
                        [frame.First, result]);

                    child = between.Upper;

                    return true;
                }

                var upper = ResolveCheckCallable(new CheckCallableIdentity("<=", 2, Function: false),
                    [frame.First, result]);

                result = BooleanOperands([frame.Second, upper]);

                return false;
            case SafeMigrationSqlInExpression list:
                if (frame.Step == 0)
                {
                    frame.First = result;
                }
                else
                {
                    result = ResolveCheckCallable(new CheckCallableIdentity("=", 2, Function: false),
                        [frame.First, result]);

                    if (result.Incompatible || result.TypeOid != 16)
                    {
                        return false;
                    }
                }

                if (frame.Step < list.Values.Count)
                {
                    child = list.Values[frame.Step++];

                    return true;
                }

                result = new CheckExpressionType(16);

                return false;
            case SafeMigrationSqlFunctionExpression function:
                frame.Results![frame.Step++] = result;
                if (frame.Step < function.Arguments.Count)
                {
                    child = function.Arguments[frame.Step];

                    return true;
                }

                result = ResolveCheckFunction(function, frame.Results);

                return false;
            case SafeMigrationSqlCaseExpression conditional:
                if (frame.Step < 0)
                {
                    frame.Results![0] = result;
                    frame.Step = 0;
                }
                else if ((frame.Step & 1) == 0)
                {
                    if (result.Incompatible || result.TypeOid is { } conditionType
                        && conditionType != 16 && !CanCoerceCheckOperand(result, 16))
                    {
                        result = new CheckExpressionType(null, Incompatible: true);

                        return false;
                    }

                    if (result.TypeOid is null)
                    {
                        result = default;

                        return false;
                    }

                    child = conditional.WhenClauses[frame.Step / 2].Result;
                    frame.Step++;

                    return true;
                }
                else
                {
                    frame.Results![frame.Step / 2 + 1] = result;
                    frame.Step++;
                }

                if (frame.Step / 2 < conditional.WhenClauses.Count)
                {
                    child = conditional.WhenClauses[frame.Step / 2].Condition;

                    return true;
                }

                result = ResolveCommonCheckType(frame.Results!);

                return false;
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Resolves leaf contracts without preparing native SQL or invoking application expressions.</summary>
    private CheckExpressionType ResolveCheckLeafExpression(
        SafeMigrationSqlExpression expression,
        ExpectedCheckConstraintDefinition owner,
        ISafeMigrationProjectedColumnSource columns)
    {
        switch (expression)
        {
            case SafeMigrationSqlIdentifierExpression identifier:
                var owned = identifier.Parts.Count == 1
                    || identifier.Parts.Count == 2 && identifier.Parts[0] == owner.Table
                    || identifier.Parts.Count == 3 && identifier.Parts[1] == owner.Table
                        && identifier.Parts[0] == PostgreSqlSafeMigrationSchemaIdentity.Normalize(
                            owner.Schema, _currentSchema);

                return owned && TryGetCurrentForeignKeyStorage(owner.Table,
                    owner.Schema, identifier.Parts[^1], columns, out var storage)
                    ? new CheckExpressionType(storage.TypeOid) : default;
            case SafeMigrationSqlLiteralExpression literal:
                if (literal.StoreType is null && literal.Value is null or string)
                {
                    // WHY: Uncast SQL strings and NULL begin as native unknown,
                    // not TEXT. Assigning TEXT would falsely reject integer = '1'.
                    return new CheckExpressionType(705, UnknownLiteral: literal);
                }

                var literalType = CheckExpressionNativeType(literal);
                if (literal.Value is not null && literal.StoreType is not null
                    && literalType.TypeOid is 20 or 21 or 23)
                {
                    var literalTarget = literalType.TypeOid switch
                    {
                        21 => "smallint", 23 => "integer", 20 => "bigint", _ => throw new UnreachableException(),
                    };

                    return PostgreSqlSafeMigrationSqlExpressionRenderer.TryGetNativeIntegerLiteralValue(
                        new SafeMigrationSqlCastExpression(literal, literalTarget), literalTarget, out _)
                        ? literalType : default;
                }

                if (literal.Value is string text)
                {
                    return literalType.TypeOid is 25 or 1042 or 1043
                        && !text.Contains('\0', StringComparison.Ordinal) ? literalType : default;
                }

                return literalType;
            case SafeMigrationSqlCurrentValueExpression:
                return CheckExpressionNativeType(expression);
            case SafeMigrationSqlUnaryExpression unary:
                // WHY: Signed scanner boundary constants are folded before
                // native unary lookup; they deliberately bypass the child path.
                return CheckExpressionNativeType(unary);
            case SafeMigrationSqlFunctionExpression function:
                return ResolveCheckFunction(function, []);
            default:
                // WHY: Opaque/provider fragments remain under the existing
                // expression eligibility contract. This bridge does not
                // pretend to bind arbitrary SQL or call user code to inspect it.
                return default;
        }
    }

    /// <summary>Preserves CAST's native target and bounded input checks after its operand has completed.</summary>
    private CheckExpressionType ResolveCheckCast(SafeMigrationSqlCastExpression cast, CheckExpressionType operand)
    {
        var target = CheckExpressionNativeType(cast);
        if (operand.Incompatible)
        {
            return operand;
        }

        if (operand.TypeOid is null || target.TypeOid is null)
        {
            return default;
        }

        if (operand.TypeOid != target.TypeOid
            && (!IsBuiltinCheckScalar(operand.TypeOid.Value) && operand.TypeOid != 705
                || !IsBuiltinCheckScalar(target.TypeOid.Value)))
        {
            return default;
        }

        if (cast.Operand is SafeMigrationSqlLiteralExpression { Value: string }
            && target.TypeOid is not (25 or 1042 or 1043))
        {
            var integerTarget = target.TypeOid switch
            {
                21 => "smallint", 23 => "integer", 20 => "bigint", _ => string.Empty,
            };

            if (!PostgreSqlSafeMigrationSqlExpressionRenderer.TryGetNativeIntegerLiteralValue(
                cast, integerTarget, out _))
            {
                // WHY: A target OID alone does not prove a string can pass a
                // native input function; never execute it merely to inspect it.
                return default;
            }
        }

        // WHY: pg_cast omits legal explicit I/O casts. Explicit target approval
        // is not evidence of an implicit conversion and never evaluates a value.
        return !CanExplicitlyCast(operand.TypeOid.Value, target.TypeOid.Value, cast)
            ? new CheckExpressionType(null, Incompatible: true) : target;
    }

    /// <summary>Resolves an approved collation only after retaining the operand's own failure priority.</summary>
    private CheckExpressionType ResolveCheckCollate(
        SafeMigrationSqlCollateExpression collate,
        CheckExpressionType operand)
    {
        if (operand.Incompatible || operand.TypeOid is null)
        {
            return operand;
        }

        if (_sparseCheckTypeFacts is not { } facts
            || !facts.Signatures.TryGetValue(CheckCollationIdentity(collate), out var nativeCollation)
            || nativeCollation.Count == 0)
        {
            return new CheckExpressionType(null, Incompatible: true);
        }

        if (operand.TypeOid == 705)
        {
            return CanCoerceCheckOperand(operand, 25) ? new CheckExpressionType(25) : default;
        }

        return operand.TypeOid is 25 or 1042 or 1043 ? operand
            : IsBuiltinCheckScalar(operand.TypeOid.Value) ? new CheckExpressionType(null, Incompatible: true) : default;
    }

    /// <summary>Distinguishes PostgreSQL's intrinsic common-type forms from catalog function overloads.</summary>
    private CheckExpressionType ResolveCheckFunction(
        SafeMigrationSqlFunctionExpression function,
        CheckExpressionType[] arguments)
        => function.Name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase) ? ResolveCheckNullIf(arguments)
            : IsCommonTypeFunction(function.Name) ? ResolveCommonCheckType(arguments)
            : ResolveCheckCallable(new CheckCallableIdentity(function.Name.ToLowerInvariant(),
                arguments.Length, Function: true), arguments);

    /// <summary>Stores native inference state only while an ancestor awaits a child result.</summary>
    private struct CheckExpressionFrame(SafeMigrationSqlExpression expression)
    {
        internal readonly SafeMigrationSqlExpression Expression = expression;
        internal int Step;
        internal CheckExpressionType First;
        internal CheckExpressionType Second;
        internal CheckExpressionType[]? Results;
    }

    /// <summary>Uses exact overloads first and only native expression-implicit casts for nonexact operands.</summary>
    private CheckExpressionType ResolveCheckCallable(CheckCallableIdentity identity, CheckExpressionType[] operands)
    {
        if (operands.Any(static item => item.Incompatible))
        {
            return new CheckExpressionType(null, Incompatible: true);
        }

        if (operands.Any(static item => item.TypeOid is null)
            || _sparseCheckTypeFacts is null
            || !_sparseCheckTypeFacts.Signatures.TryGetValue(identity, out var signatures))
        {
            return default;
        }

        var hasUnknown = operands.Any(static item => item.TypeOid == 705);
        CheckCallableSignature? exactCandidate = null;
        var exactCount = 0;
        var exactOrdinaryCount = 0;
        long? exactNamespace = null;
        var mixedExactNamespaces = false;
        foreach (var signature in signatures)
        {
            var exact = true;
            // WHY: Compare each operand with the signature argument at the same position.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < operands.Length; index++)
            {
                var source = operands[index].TypeOid;
                // WHY: Native operator resolution first treats an unknown
                // input as the known opposite type. Its literal value still
                // needs the separate bounded input proof; an arbitrary unknown
                // string never becomes a positive conversion certificate.
                exact &= source == signature.Arguments[index]
                    || !identity.Function && operands.Length == 2 && source == 705
                        && signature.Arguments[index] == operands[1 - index].TypeOid
                        && CanCoerceCheckOperand(operands[index], signature.Arguments[index]);
            }

            if (exact)
            {
                exactCount++;
                mixedExactNamespaces |= exactNamespace is not null && exactNamespace != signature.NativeNamespace;
                exactNamespace = signature.NativeNamespace;
                if (!signature.Variadic)
                {
                    exactOrdinaryCount++;
                    exactCandidate = signature;
                }
                else if (exactCandidate is null)
                {
                    exactCandidate = signature;
                }
            }
        }

        if (exactCount > 0)
        {
            // WHY: Native same-schema resolution prefers the exact ordinary
            // signature over an equivalent expanded variadic one. Retain
            // competitors; filtering them out could certify the wrong call.
            return exactCandidate is { Variadic: false, Polymorphic: false }
                && (exactCount == 1 || exactOrdinaryCount == 1 && !mixedExactNamespaces)
                ? new CheckExpressionType(exactCandidate.Result) : default;
        }

        long? result = null;
        var candidateCount = 0;
        var mostExactArguments = -1;
        var selectedVariadic = false;
        foreach (var signature in signatures)
        {
            if (signature.Polymorphic)
            {
                if (signature.Arguments.All(static type => type is 2249 or 2277 or 3500 or 3831 or 4537
                        or 5078 or 5080 or 4538)
                    && operands.All(static item => item.TypeOid is 16 or 20 or 21 or 23 or 25 or 700 or 701
                        or 1042 or 1043 or 1082 or 1083 or 1114 or 1184 or 1266 or 1700 or 2950))
                {
                    // WHY: Native array, enum, record, range and multirange
                    // signatures cannot consume these independently resolved
                    // builtin scalar types. Other polymorphic forms stay unknown.
                    continue;
                }

                // WHY: Polymorphic/domain/array inference is a separate
                // native parser contract. Never mistake an unresolved valid
                // overload for proof that the expression is incompatible.
                return default;
            }

            var compatible = true;
            // WHY: Check coercion against the corresponding native signature argument.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < operands.Length; index++)
            {
                compatible &= CanCoerceCheckOperand(operands[index], signature.Arguments[index]);
            }

            if (compatible)
            {
                var exactArguments = 0;
                // WHY: Count exact matches only between corresponding argument positions.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < operands.Length; index++)
                {
                    exactArguments += operands[index].TypeOid == signature.Arguments[index] ? 1 : 0;
                }

                if (exactArguments > mostExactArguments)
                {
                    mostExactArguments = exactArguments;
                    candidateCount = 1;
                    result = signature.Result;
                    selectedVariadic = signature.Variadic;
                }
                else if (exactArguments == mostExactArguments)
                {
                    candidateCount++;
                }
            }
        }

        // WHY: Native overload ranking is based on its argument signatures,
        // not their shared return type. An unresolved best-match tie remains
        // unproven without invoking the native parser or either function body.
        // WHY: A selected variadic call needs native ARRAY packing to carry
        // the exact stored CHECK expression. Sparse promotion cannot mint
        // Ready before that existing postcondition boundary is certified.
        return candidateCount > 1 || selectedVariadic ? default : result is not null ? new CheckExpressionType(result)
            : hasUnknown ? default : new CheckExpressionType(null, Incompatible: true);
    }

    /// <summary>Retains PostgreSQL common-type forms without falsely treating COALESCE as a pg_proc function.</summary>
    private CheckExpressionType ResolveCommonCheckType(CheckExpressionType[] operands)
    {
        if (operands.Length == 0)
        {
            return new CheckExpressionType(null, Incompatible: true);
        }

        if (operands.Any(static item => item.Incompatible))
        {
            return new CheckExpressionType(null, Incompatible: true);
        }

        if (operands.Any(static item => item.TypeOid is null))
        {
            return default;
        }

        foreach (var candidate in operands)
        {
            if (candidate.TypeOid != 705 && operands.All(item =>
                CanCoerceCheckOperand(item, candidate.TypeOid!.Value)))
            {
                return candidate;
            }
        }

        return operands.All(item => item.TypeOid == 705 && CanCoerceCheckOperand(item, 25))
            ? new CheckExpressionType(25)
            : operands.Any(static item => item.TypeOid == 705) ? default
            : operands.All(static item => item.TypeOid == 705 || IsBuiltinCheckScalar(item.TypeOid!.Value))
                ? new CheckExpressionType(null, Incompatible: true) : default;
    }

    /// <summary>
    /// Preserves NULLIF's first native operand only after its equality binding is independently proven.
    /// </summary>
    private CheckExpressionType ResolveCheckNullIf(CheckExpressionType[] operands)
    {
        if (operands.Length != 2)
        {
            return new CheckExpressionType(null, Incompatible: true);
        }

        var comparison = ResolveCheckCallable(new CheckCallableIdentity("=", 2, Function: false), operands);
        if (comparison.Incompatible || comparison.TypeOid != 16)
        {
            return comparison;
        }

        // WHY: NULLIF can promote its first input when binding equality.
        // Only an exact captured left-input signature proves this result type;
        // unresolved promotion retains the sparse prerequisite blocker.
        return _sparseCheckTypeFacts?.Signatures.TryGetValue(new CheckCallableIdentity("=", 2, Function: false),
                out var signatures) == true
            && signatures.Any(signature => signature.Arguments[0] == operands[0].TypeOid
                && signature.Arguments[1] == operands[1].TypeOid)
            ? operands[0] : default;
    }

    /// <summary>
    /// Requests one exact native collation identity, respecting quoted names and optional namespaces.
    /// </summary>
    private CheckCallableIdentity CheckCollationIdentity(SafeMigrationSqlCollateExpression expression)
        => new(_sqlGenerationHelper.DelimitIdentifier(expression.Name, expression.Schema),
            0, Function: false, Collation: true);

    /// <summary>Recognizes logical operands separately from overloaded comparison operators.</summary>
    private CheckExpressionType BooleanOperands(CheckExpressionType[] operands)
    {
        var incompatible = operands.Any(item => item.Incompatible || item.TypeOid is { } type
            && type != 705 && !CanImplicitlyCast(type, 16));

        return incompatible ? new CheckExpressionType(null, Incompatible: true)
            : operands.Any(static item => item.TypeOid is null or 705) ? default : new CheckExpressionType(16);
    }

    /// <summary>A NULL test's result is Boolean regardless of its valid operand's type.</summary>
    private static CheckExpressionType BooleanResult(CheckExpressionType operand)
        => operand.Incompatible ? new CheckExpressionType(null, Incompatible: true)
            : operand.TypeOid is null ? default : new CheckExpressionType(16);

    /// <summary>Uses only captured native implicit-cast edges, never assignment-only or explicit coercions.</summary>
    private bool CanImplicitlyCast(long source, long target)
        => source == target
            || _sparseCheckTypeFacts?.ImplicitCasts.Contains(new CheckImplicitCast(source, target)) == true;

    /// <summary>
    /// Retains unknown literal value provenance rather than treating every string as a valid native input.
    /// </summary>
    private bool CanCoerceCheckOperand(CheckExpressionType operand, long target)
    {
        if (operand.TypeOid != 705)
        {
            return operand.TypeOid is { } source && CanImplicitlyCast(source, target);
        }

        if (operand.UnknownLiteral is not { } literal)
        {
            return false;
        }

        if (literal.Value is null)
        {
            return IsBuiltinCheckScalar(target);
        }

        if (literal.Value is string text && target is 25 or 1042 or 1043)
        {
            return !text.Contains('\0', StringComparison.Ordinal);
        }

        var integerTarget = target switch
        {
            21 => "smallint", 23 => "integer", 20 => "bigint", _ => string.Empty,
        };

        return PostgreSqlSafeMigrationSqlExpressionRenderer.TryGetNativeIntegerLiteralValue(
            new SafeMigrationSqlCastExpression(literal, integerTarget.Length == 0 ? "text" : integerTarget),
            integerTarget, out _);
    }

    /// <summary>Preserves explicit I/O conversions while rejecting proved absent builtin conversion paths.</summary>
    private bool CanExplicitlyCast(long source, long target, SafeMigrationSqlCastExpression expression)
    {
        if (source == target || source == 705
            || source is 25 or 1042 or 1043 && IsBuiltinCheckScalar(target)
            || target is 25 or 1042 or 1043 && IsBuiltinCheckScalar(source)
            || !IsBuiltinCheckScalar(source) || !IsBuiltinCheckScalar(target))
        {
            return true;
        }

        var identity = CheckExpressionTypeIdentity(expression);

        return identity is { } mapped && _sparseCheckTypeFacts is not null
            && _sparseCheckTypeFacts.Signatures.TryGetValue(
                new CheckCallableIdentity(mapped.StoreType, 1, Function: false, ExplicitCast: true),
                out var signatures) && signatures.Any(signature => signature.Arguments[0] == source);
    }

    /// <summary>Limits negative binding proofs to builtin scalars without domain or polymorphic inference.</summary>
    private static bool IsBuiltinCheckScalar(long type)
        => type is 16 or 20 or 21 or 23 or 25 or 700 or 701 or 1042 or 1043 or 1082 or 1083 or 1114
            or 1184 or 1266 or 1700 or 2950;

    /// <summary>Looks up a literal, CAST target or current value's independently resolved native OID.</summary>
    private CheckExpressionType CheckExpressionNativeType(SafeMigrationSqlExpression expression)
        => CheckExpressionTypeIdentity(expression) is { } identity
            && _livePrerequisiteTypes?.TryGetValue(identity, out var storage) == true
            ? new CheckExpressionType(storage.TypeOid) : default;

    /// <summary>Captures only referenced type identities; native values and user functions are not evaluated.</summary>
    private IEnumerable<LiveColumnTypeIdentity> CheckExpressionTypeIdentities(SafeMigrationSqlExpression expression)
    {
        foreach (var node in CheckExpressionNodes(expression))
        {
            if (CheckExpressionTypeIdentity(node) is { } identity)
            {
                yield return identity;
            }
        }
    }

    /// <summary>Matches native scanner literal typing and canonical provider CAST mappings.</summary>
    private LiveColumnTypeIdentity? CheckExpressionTypeIdentity(SafeMigrationSqlExpression expression)
    {
        var type = expression switch
        {
            SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Negate,
                Operand: SafeMigrationSqlLiteralExpression { Value: long value } literal }
                when !literal.NumericSourceToken.IsEmpty && value == 2147483648L => "integer",
            SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Negate,
                Operand: SafeMigrationSqlLiteralExpression { Value: ulong value } literal }
                when !literal.NumericSourceToken.IsEmpty && value == 9223372036854775808UL => "bigint",
            SafeMigrationSqlCastExpression cast => _typeMappingSource.FindMapping(cast.StoreType)?.StoreTypeNameBase,
            SafeMigrationSqlLiteralExpression { StoreType: { } storeType }
                => _typeMappingSource.FindMapping(storeType)?.StoreTypeNameBase,
            SafeMigrationSqlLiteralExpression literal when !literal.NumericSourceToken.IsEmpty
                && literal.NumericSourceToken.Span.ContainsAny('.', 'e', 'E') => "numeric",
            SafeMigrationSqlLiteralExpression { Value: bool } => "boolean",
            SafeMigrationSqlLiteralExpression { Value: byte or sbyte or short or ushort or int } => "integer",
            SafeMigrationSqlLiteralExpression { Value: long value }
                => value is >= int.MinValue and <= int.MaxValue ? "integer" : "bigint",
            SafeMigrationSqlLiteralExpression { Value: uint value }
                => value <= int.MaxValue ? "integer" : "bigint",
            SafeMigrationSqlLiteralExpression { Value: ulong value }
                => value <= int.MaxValue ? "integer" : value <= long.MaxValue ? "bigint" : "numeric",
            SafeMigrationSqlLiteralExpression { Value: decimal } => "numeric",
            SafeMigrationSqlLiteralExpression { Value: float } => "real",
            SafeMigrationSqlLiteralExpression { Value: double } => "double precision",
            SafeMigrationSqlCurrentValueExpression current => current.Value switch
            {
                SafeMigrationSqlCurrentValue.Date => "date",
                SafeMigrationSqlCurrentValue.Time => "time with time zone",
                SafeMigrationSqlCurrentValue.Timestamp => "timestamp with time zone",
                _ => throw new UnreachableException(),
            },
            _ => null,
        };

        return type is null ? null : new LiveColumnTypeIdentity(type, null, null);
    }

    /// <summary>Walks only explicitly structured operands without parsing opaque SQL.</summary>
    private static IEnumerable<SafeMigrationSqlExpression> CheckExpressionNodes(SafeMigrationSqlExpression root)
    {
        // WHY: This lazy sequence crosses yield boundaries, so a ref-struct
        // pooled walker cannot own its continuation. A single managed cursor
        // stack avoids recursive iterators and stores only genuine branches.
        Stack<(SafeMigrationSqlExpression Expression, int NextChild)>? pending = null;
        var expression = root;
        while (true)
        {
            yield return expression;
            var nextChild = 0;
            while (true)
            {
                if (PostgreSqlSafeMigrationExpressionWalker.TryGetChild(expression, nextChild, out var child))
                {
                    if (PostgreSqlSafeMigrationExpressionWalker.TryGetChild(expression, nextChild + 1, out _))
                    {
                        (pending ??= new()).Push((expression, nextChild + 1));
                    }

                    expression = child;

                    break;
                }

                if (pending is null || !pending.TryPop(out var parent))
                {
                    yield break;
                }

                expression = parent.Expression;
                nextChild = parent.NextChild;
            }
        }
    }

    /// <summary>Maps only the public structured operators to PostgreSQL operator catalog names.</summary>
    private static string CheckOperator(SafeMigrationSqlBinaryOperator value) => value switch
    {
        SafeMigrationSqlBinaryOperator.Equal => "=",
        SafeMigrationSqlBinaryOperator.NotEqual => "<>",
        SafeMigrationSqlBinaryOperator.LessThan => "<",
        SafeMigrationSqlBinaryOperator.LessThanOrEqual => "<=",
        SafeMigrationSqlBinaryOperator.GreaterThan => ">",
        SafeMigrationSqlBinaryOperator.GreaterThanOrEqual => ">=",
        SafeMigrationSqlBinaryOperator.Add => "+",
        SafeMigrationSqlBinaryOperator.Subtract => "-",
        SafeMigrationSqlBinaryOperator.Multiply => "*",
        SafeMigrationSqlBinaryOperator.Divide => "/",
        SafeMigrationSqlBinaryOperator.Modulo => "%",
        _ => throw new UnreachableException(),
    };

    /// <summary>Recognizes native SQL common-type special forms rather than callable catalog entries.</summary>
    private static bool IsCommonTypeFunction(string name)
        => name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GREATEST", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LEAST", StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates the scalar native OID list before exposing signature facts.</summary>
    private static long[] CheckOidList(string value)
    {
        if (value.Length == 0)
        {
            return [];
        }

        var parts = value.Split(',');
        var result = new long[parts.Length];
        // WHY: Parse each native OID directly into its corresponding output slot.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < parts.Length; ordinal++)
        {
            if (!long.TryParse(parts[ordinal], NumberStyles.None, CultureInfo.InvariantCulture,
                out result[ordinal]) || result[ordinal] <= 0)
            {
                throw InvalidLivePrerequisiteEvidence();
            }
        }

        return result;
    }

    /// <summary>A requested visible operator or function family, not a generated SQL expression.</summary>
    private readonly record struct CheckCallableIdentity(
        string Name, int Arity, bool Function, bool ExplicitCast = false, bool Collation = false);
    /// <summary>An independently observed native overload and its input/result type identities.</summary>
    private sealed record CheckCallableSignature(
        long Result, long[] Arguments, bool Polymorphic, bool Variadic, long NativeNamespace);
    /// <summary>A native expression-implicit coercion edge, excluding assignment-only casts.</summary>
    private readonly record struct CheckImplicitCast(long Source, long Target);
    /// <summary>A resolved native type or a positively disproved binding; null means no binding claim.</summary>
    private readonly record struct CheckExpressionType(
        long? TypeOid, bool Incompatible = false, SafeMigrationSqlLiteralExpression? UnknownLiteral = null);
    /// <summary>Invocation-local CHECK bindings published only after complete transport validation.</summary>
    private sealed record SparseCheckTypeFacts(
        HashSet<ExpectedCheckConstraintDefinition> Checks,
        Dictionary<CheckCallableIdentity, List<CheckCallableSignature>> Signatures,
        HashSet<CheckImplicitCast> ImplicitCasts);
}
