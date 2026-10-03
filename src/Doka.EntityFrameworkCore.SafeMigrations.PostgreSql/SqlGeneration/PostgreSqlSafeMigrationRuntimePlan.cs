namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Represents one PostgreSQL runtime catalog plan.</summary>
/// <param name="StateExpression">The expression that classifies current state.</param>
/// <param name="Postcondition">The expression that verifies final state.</param>
/// <param name="RepairCapability">The provider-proven repair capability.</param>
/// <param name="RepairPrecondition">The expression that gates repair execution.</param>
/// <param name="UnsupportedCode">The stable unsupported-feature code, when present.</param>
internal sealed record PostgreSqlSafeMigrationRuntimePlan(
    string StateExpression,
    string Postcondition,
    SafeMigrationRepairCapability RepairCapability,
    string RepairPrecondition,
    string? UnsupportedCode = null
)
{
    internal const string DataProbePlaceholder = "__DOKA_SM_DATA_PROBE__";
    internal const string TransitionInvariantPlaceholder = "__DOKA_SM_TRANSITION_INVARIANT__";
    internal const string NullabilityDataProbePlaceholder = "__DOKA_SM_NULLABILITY_DATA_PROBE__";
    internal const string NullabilityRepairInvariantPlaceholder = "__DOKA_SM_NULLABILITY_REPAIR_INVARIANT__";

    /// <summary>Gets the catalog-only prerequisite expression.</summary>
    public string PrerequisiteExpression { get; init; } = "TRUE";

    /// <summary>
    /// Gets the immediate postcondition for an applied operation when it
    /// differs from the terminal migration contract.
    /// </summary>
    public string? ExecutionPostcondition { get; init; }

    /// <summary>Gets the catalog-only guard that must pass before state SQL can be evaluated.</summary>
    public string StateEvaluationGuardExpression { get; init; } = "TRUE";

    /// <summary>Gets the state expression used when the evaluation guard fails.</summary>
    public string? StateEvaluationGuardFailureExpression { get; init; }

    /// <summary>Gets an optional stable classification-code expression.</summary>
    public string? ClassificationCodeExpression { get; init; }

    /// <summary>
    /// Gets whether the complete operation is unsupported independently of
    /// catalog state and therefore has no executable baseline.
    /// </summary>
    public bool IsStaticallyUnsupported { get; init; }

    /// <summary>Gets the conservative execution-impact classification for an accepted repair.</summary>
    public SafeMigrationOperationalImpact RepairOperationalImpact { get; init; }
        = SafeMigrationOperationalImpact.Unknown;

    /// <summary>Gets optional bounded catalog-only facet-difference evidence.</summary>
    public string? DiagnosticEvidenceExpression { get; init; }

    /// <summary>Gets optional constant evidence for a privacy-sensitive Different result.</summary>
    public SafeMigrationFacetDifference? DifferentDifference { get; init; }

    /// <summary>Gets the uniquely resolved physical object name for a matching ensure operation.</summary>
    public string? MatchedObjectNameExpression { get; init; }

    /// <summary>Gets the optional bounded live-data probe required by classification.</summary>
    public PostgreSqlSafeMigrationDataProbe? DataProbe { get; init; }

    /// <summary>Gets the optional fresh, operation-local NULL proof shared by classification and repair.</summary>
    public PostgreSqlSafeMigrationNullabilityDataProbe? NullabilityDataProbe { get; init; }

    /// <summary>Gets whether a nullable-to-required repair can require live row evidence.</summary>
    public bool MayRequireNullabilityDataProof { get; init; }

    /// <summary>Gets optional compact row-state evidence for ordered model-data projection.</summary>
    public string? ModelManagedRowEvidenceExpression { get; init; }

    /// <summary>Gets optional live dependency counts for ordered model-data projection.</summary>
    public string? ModelManagedDependencyCountsExpression { get; init; }

    /// <summary>Gets the expected number of compact row-state entries.</summary>
    public int ModelManagedRowCount { get; init; }

    /// <summary>Gets the expected number of dependency-count entries.</summary>
    public int ModelManagedDependencyCount { get; init; }

    /// <summary>Renders the state expression for runtime execution.</summary>
    /// <returns>The rendered expression.</returns>
    public string RenderStateExpression() =>
        RenderTransitionExpressions(StateExpression, dataBlocked: null, transitionEligible: null);

    /// <summary>Renders the state expression with an already resolved analysis probe.</summary>
    /// <param name="dataBlocked">Whether the grouped analysis probe found blocking data.</param>
    /// <param name="transitionEligible">Whether the catalog proved the lossless transition invariant.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderStateExpression(
        bool dataBlocked,
        bool transitionEligible
    ) => RenderTransitionExpressions(StateExpression, dataBlocked, transitionEligible);

    /// <summary>Renders the state expression against a previously evaluated data-probe expression.</summary>
    /// <param name="dataBlockedExpression">The Boolean SQL expression containing the cached probe result.</param>
    /// <param name="transitionEligibleExpression">The Boolean SQL expression containing transition eligibility.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderStateExpression(
        string dataBlockedExpression,
        string transitionEligibleExpression
    ) => RenderTransitionExpressions(
        StateExpression,
        dataBlockedExpression,
        transitionEligibleExpression);

    /// <summary>Appends the state expression without materializing an intermediate string.</summary>
    /// <param name="builder">The destination SQL builder.</param>
    /// <param name="dataBlockedExpression">The Boolean SQL expression containing the cached probe result.</param>
    /// <param name="transitionEligibleExpression">The Boolean SQL expression containing transition eligibility.</param>
    public void AppendStateExpression(
        StringBuilder builder,
        string dataBlockedExpression,
        string transitionEligibleExpression
    ) => AppendTransitionExpressions(
        builder,
        StateExpression,
        dataBlockedExpression,
        transitionEligibleExpression);

    /// <summary>Renders the repair precondition for runtime execution.</summary>
    /// <returns>The rendered expression.</returns>
    public string RenderRepairPrecondition() =>
        RenderTransitionExpressions(RepairPrecondition, dataBlocked: null, transitionEligible: null);

    /// <summary>Renders the repair precondition with an already resolved analysis probe.</summary>
    /// <param name="dataBlocked">Whether the grouped analysis probe found blocking data.</param>
    /// <param name="transitionEligible">Whether the catalog proved the lossless transition invariant.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderRepairPrecondition(
        bool dataBlocked,
        bool transitionEligible
    ) => RenderTransitionExpressions(RepairPrecondition, dataBlocked, transitionEligible);

    /// <summary>Renders the repair precondition against a previously evaluated data-probe expression.</summary>
    /// <param name="dataBlockedExpression">The Boolean SQL expression containing the cached probe result.</param>
    /// <param name="transitionEligibleExpression">The Boolean SQL expression containing transition eligibility.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderRepairPrecondition(
        string dataBlockedExpression,
        string transitionEligibleExpression
    ) => RenderTransitionExpressions(
        RepairPrecondition,
        dataBlockedExpression,
        transitionEligibleExpression);

    /// <summary>Appends the repair precondition without materializing an intermediate string.</summary>
    /// <param name="builder">The destination SQL builder.</param>
    /// <param name="dataBlockedExpression">The Boolean SQL expression containing the cached probe result.</param>
    /// <param name="transitionEligibleExpression">The Boolean SQL expression containing transition eligibility.</param>
    public void AppendRepairPrecondition(
        StringBuilder builder,
        string dataBlockedExpression,
        string transitionEligibleExpression
    ) => AppendTransitionExpressions(
        builder,
        RepairPrecondition,
        dataBlockedExpression,
        transitionEligibleExpression);

    /// <summary>Renders fresh NULL-proof eligibility against the current operation's transition evidence.</summary>
    /// <returns>The physical repair invariant without a row-reading NULL query.</returns>
    public string RenderNullabilityRepairInvariantExpression() => ReplaceTransitionPlaceholders(
        NullabilityDataProbe?.RepairInvariantExpression
            ?? throw new InvalidOperationException("The runtime plan has no nullability data probe."),
        "doka_data_blocked",
        "doka_transition_eligible",
        "FALSE",
        "FALSE");

    /// <summary>Appends fresh physical repair eligibility without an intermediate predicate string.</summary>
    /// <param name="builder">The destination SQL builder.</param>
    public void AppendNullabilityRepairInvariantExpression(
        StringBuilder builder
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        AppendTransitionPlaceholders(
            builder,
            NullabilityDataProbe?.RepairInvariantExpression
                ?? throw new InvalidOperationException("The runtime plan has no nullability data probe."),
            "doka_data_blocked",
            "doka_transition_eligible",
            "FALSE",
            "FALSE");
    }

    /// <summary>Renders the fresh catalog eligibility that makes a NULL proof stale after projected DML.</summary>
    /// <param name="transitionEligible">The grouped physical-transition result, when already evaluated.</param>
    /// <returns>A catalog-only Boolean expression indicating that NULL row evidence is required.</returns>
    public string RenderNullabilityDataProbeRequiredExpression(
        bool? transitionEligible = null
    )
    {
        if (NullabilityDataProbe is null)
        {
            return "FALSE";
        }

        var transitionReplacement = DataProbe is null ? "FALSE" : transitionEligible is null
            ? DataProbe.TransitionInvariantExpression
                ?? throw new InvalidOperationException("The runtime plan has no transition evidence.")
            : transitionEligible.Value ? "TRUE" : "FALSE";

        var invariant = ReplaceTransitionPlaceholders(
            NullabilityDataProbe.RepairInvariantExpression, "FALSE", transitionReplacement, "FALSE", "FALSE");

        return $"({invariant}) AND NOT ({NullabilityDataProbe.NotNullContractExpression})";
    }

    /// <summary>Renders the optional classification-code expression for runtime execution.</summary>
    /// <returns>The rendered expression, or null when no specialized classification exists.</returns>
    public string? RenderClassificationCodeExpression() => ClassificationCodeExpression is null
        ? null
        : RenderWithDataProbe(ClassificationCodeExpression, dataBlocked: null);

    /// <summary>Renders the optional classification code with an already resolved analysis probe.</summary>
    /// <param name="dataBlocked">Whether the grouped analysis probe found blocking data.</param>
    /// <returns>The rendered expression, or null when no specialized classification exists.</returns>
    public string? RenderClassificationCodeExpression(
        bool dataBlocked
    ) => ClassificationCodeExpression is null
        ? null
        : RenderWithDataProbe(ClassificationCodeExpression, dataBlocked);

    private string RenderWithDataProbe(
        string expression,
        bool? dataBlocked
    )
    {
        if (DataProbe is null)
        {
            return expression;
        }

        var replacement = dataBlocked is null
            ? DataProbe.BuildBlockedExpression()
            : dataBlocked.Value
                ? "TRUE"
                : "FALSE";

        return expression.Replace(DataProbePlaceholder, replacement, StringComparison.Ordinal);
    }

    private string RenderWithDataProbe(
        string expression,
        string dataBlockedExpression
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataBlockedExpression);

        return DataProbe is null
            ? expression
            : expression.Replace(DataProbePlaceholder, dataBlockedExpression, StringComparison.Ordinal);
    }

    private string RenderTransitionExpressions(
        string expression,
        bool? dataBlocked,
        bool? transitionEligible
    )
    {
        if (DataProbe is null && NullabilityDataProbe is null)
        {
            return expression;
        }

        var dataReplacement = DataProbe is null ? "FALSE" : dataBlocked is null
            ? DataProbe.BuildBlockedExpression()
            : dataBlocked.Value
                ? "TRUE"
                : "FALSE";

        var transitionReplacement = DataProbe is null ? "FALSE" : transitionEligible is null
            ? DataProbe.TransitionInvariantExpression
                ?? throw new InvalidOperationException("The runtime plan has no transition evidence.")
            : transitionEligible.Value
                ? "TRUE"
                : "FALSE";

        var repairInvariantReplacement = NullabilityDataProbe is null
            ? "FALSE"
            : ReplaceTransitionPlaceholders(
                NullabilityDataProbe.RepairInvariantExpression,
                dataReplacement,
                transitionReplacement,
                "FALSE",
                "FALSE");

        var nullabilityReplacement = NullabilityDataProbe is null
            ? "FALSE"
            : "CASE WHEN ("
                + repairInvariantReplacement
                + $") AND NOT ({NullabilityDataProbe.NotNullContractExpression}) THEN ("
                + NullabilityDataProbe.BlockedExpression
                + ") ELSE FALSE END";

        return ReplaceTransitionPlaceholders(
            expression, dataReplacement, transitionReplacement, nullabilityReplacement, repairInvariantReplacement);
    }

    private string RenderTransitionExpressions(
        string expression,
        string dataBlockedExpression,
        string transitionEligibleExpression
    )
    {
        return DataProbe is null && NullabilityDataProbe is null
            ? expression
            : ReplaceTransitionPlaceholders(
                expression,
                dataBlockedExpression,
                transitionEligibleExpression,
                "doka_nullability_blocked",
                "doka_nullability_repair_eligible");
    }

    private void AppendTransitionExpressions(
        StringBuilder builder,
        string expression,
        string dataBlockedExpression,
        string transitionEligibleExpression
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataBlockedExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionEligibleExpression);

        if (DataProbe is null && NullabilityDataProbe is null)
        {
            builder.Append(expression);

            return;
        }

        AppendTransitionPlaceholders(
            builder,
            expression,
            dataBlockedExpression,
            transitionEligibleExpression,
            "doka_nullability_blocked",
            "doka_nullability_repair_eligible");
    }

    private static string ReplaceTransitionPlaceholders(
        string expression,
        string dataReplacement,
        string transitionReplacement,
        string nullabilityReplacement,
        string repairInvariantReplacement
    )
    {
        var resultLength = expression.Length;
        var sourceOffset = 0;
        var hasMarkers = false;
        while (FindNextPlaceholder(expression.AsSpan(), sourceOffset) is var marker && marker.Index >= 0)
        {
            hasMarkers = true;
            var replacement = ProofReplacement(
                marker.Kind,
                dataReplacement,
                transitionReplacement,
                nullabilityReplacement,
                repairInvariantReplacement);

            resultLength = checked(resultLength + replacement.Length - marker.Length);
            sourceOffset = marker.Index + marker.Length;
        }

        if (!hasMarkers)
        {
            return expression;
        }

        // WHY: Sequential string.Replace calls materialize the complete state
        // expression twice. A single exact-size pass bounds allocation growth
        // for large repair batches without weakening either runtime predicate.
        return string.Create(
            resultLength,
            (Expression: expression, Data: dataReplacement, Transition: transitionReplacement,
                Nullability: nullabilityReplacement, RepairInvariant: repairInvariantReplacement),
            static (destination, state) => CopyTransitionPlaceholders(
                destination,
                state.Expression,
                state.Data,
                state.Transition,
                state.Nullability,
                state.RepairInvariant));
    }

    private static void AppendTransitionPlaceholders(
        StringBuilder builder,
        string expression,
        string dataReplacement,
        string transitionReplacement,
        string nullabilityReplacement,
        string repairInvariantReplacement
    )
    {
        var source = expression.AsSpan();
        var sourceOffset = 0;
        while (sourceOffset < source.Length)
        {
            var marker = FindNextPlaceholder(source, sourceOffset);
            if (marker.Index < 0)
            {
                builder.Append(source[sourceOffset..]);

                return;
            }

            builder.Append(source.Slice(sourceOffset, marker.Index - sourceOffset));

            var replacement = ProofReplacement(
                marker.Kind,
                dataReplacement,
                transitionReplacement,
                nullabilityReplacement,
                repairInvariantReplacement);

            builder.Append(replacement);
            sourceOffset = marker.Index + marker.Length;
        }
    }

    private static string ProofReplacement(
        int kind,
        string dataReplacement,
        string transitionReplacement,
        string nullabilityReplacement,
        string repairInvariantReplacement
    ) => kind switch
    {
        0 => dataReplacement,
        1 => transitionReplacement,
        2 => nullabilityReplacement,
        _ => repairInvariantReplacement,
    };

    private static void CopyTransitionPlaceholders(
        Span<char> destination,
        string expression,
        string dataReplacement,
        string transitionReplacement,
        string nullabilityReplacement,
        string repairInvariantReplacement
    )
    {
        var source = expression.AsSpan();
        var sourceOffset = 0;
        var destinationOffset = 0;
        while (sourceOffset < source.Length)
        {
            var marker = FindNextPlaceholder(source, sourceOffset);
            if (marker.Index < 0)
            {
                source[sourceOffset..].CopyTo(destination[destinationOffset..]);

                return;
            }

            var before = source.Slice(sourceOffset, marker.Index - sourceOffset);
            before.CopyTo(destination[destinationOffset..]);
            destinationOffset += before.Length;

            var replacement = ProofReplacement(
                marker.Kind,
                dataReplacement,
                transitionReplacement,
                nullabilityReplacement,
                repairInvariantReplacement);

            replacement.AsSpan().CopyTo(destination[destinationOffset..]);
            destinationOffset += replacement.Length;
            sourceOffset = marker.Index + marker.Length;
        }
    }

    /// <summary>Locates structural markers without interpreting proof-token text in quoted names or values.</summary>
    private static (int Index, int Kind, int Length) FindNextPlaceholder(
        ReadOnlySpan<char> source,
        int start
    )
    {
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] is '\'' or '"')
            {
                // WHY: Provider-rendered identifiers and literals double their
                // delimiter. A caller can legitimately use a proof-token name.
                var quote = source[index];
                var closed = false;
                while (++index < source.Length)
                {
                    if (source[index] != quote)
                    {
                        continue;
                    }

                    if (index + 1 < source.Length && source[index + 1] == quote)
                    {
                        index++;

                        continue;
                    }

                    closed = true;

                    break;
                }

                if (!closed)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL proof template contains an unterminated quoted token.");
                }

                continue;
            }

            if (source[index] != '_')
            {
                continue;
            }

            var remaining = source[index..];
            if (remaining.StartsWith(DataProbePlaceholder, StringComparison.Ordinal))
            {
                return (index, 0, DataProbePlaceholder.Length);
            }

            if (remaining.StartsWith(TransitionInvariantPlaceholder, StringComparison.Ordinal))
            {
                return (index, 1, TransitionInvariantPlaceholder.Length);
            }

            if (remaining.StartsWith(NullabilityDataProbePlaceholder, StringComparison.Ordinal))
            {
                return (index, 2, NullabilityDataProbePlaceholder.Length);
            }

            if (remaining.StartsWith(NullabilityRepairInvariantPlaceholder, StringComparison.Ordinal))
            {
                return (index, 3, NullabilityRepairInvariantPlaceholder.Length);
            }
        }

        return (-1, 0, 0);
    }
}

/// <summary>Describes a fresh NULL row proof shared by one operation's state and repair decision.</summary>
/// <param name="NotNullContractExpression">The validated and enforced catalog proof covering the row relation.</param>
/// <param name="RepairInvariantExpression">The physical repair-eligibility predicate.</param>
/// <param name="BlockedExpression">The provider-delimited bounded NULL row query.</param>
/// <param name="QualifiedTable">The provider-delimited table identity used for the runtime lock.</param>
internal sealed record PostgreSqlSafeMigrationNullabilityDataProbe(
    string NotNullContractExpression,
    string RepairInvariantExpression,
    string BlockedExpression,
    string QualifiedTable
);

/// <summary>Describes one deduplicatable PostgreSQL narrowing proof.</summary>
/// <param name="Table">The validated relational table name.</param>
/// <param name="Schema">The validated relational schema name.</param>
/// <param name="Column">The validated relational column name.</param>
/// <param name="TargetLength">The positive target character length.</param>
/// <param name="QualifiedTable">The provider-delimited table identity used for the runtime lock.</param>
/// <param name="DelimitedColumn">The provider-delimited column identifier used by the bounded row probe.</param>
/// <param name="TransitionInvariantExpression">
/// The optional physical catalog predicate for a lossless transition.
/// </param>
/// <param name="NarrowingExpression">The catalog predicate that identifies a narrowing transition.</param>
internal sealed record PostgreSqlSafeMigrationDataProbe(
    string Table,
    string? Schema,
    string Column,
    int TargetLength,
    string QualifiedTable,
    string DelimitedColumn,
    string? TransitionInvariantExpression,
    string NarrowingExpression
)
{
    /// <summary>Builds the bounded row probe used only after catalog qualification.</summary>
    /// <returns>The provider-delimited Boolean SQL expression.</returns>
    public string BuildBlockedExpression() => "EXISTS(SELECT 1 FROM "
        + $"{QualifiedTable} WHERE {DelimitedColumn} IS NOT NULL "
        + $"AND char_length({DelimitedColumn}) > {TargetLength.ToString(CultureInfo.InvariantCulture)} LIMIT 1)";
}
