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

    /// <summary>Gets the catalog-only prerequisite expression.</summary>
    public string PrerequisiteExpression { get; init; } = "TRUE";

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

    /// <summary>Gets the optional bounded live-data probe required by classification.</summary>
    public PostgreSqlSafeMigrationDataProbe? DataProbe { get; init; }

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
        if (DataProbe is null)
        {
            return expression;
        }

        var dataReplacement = dataBlocked is null
            ? DataProbe.BuildBlockedExpression()
            : dataBlocked.Value
                ? "TRUE"
                : "FALSE";

        var transitionReplacement = transitionEligible is null
            ? DataProbe.TransitionInvariantExpression
                ?? throw new InvalidOperationException("The runtime plan has no transition evidence.")
            : transitionEligible.Value
                ? "TRUE"
                : "FALSE";

        return ReplaceTransitionPlaceholders(expression, dataReplacement, transitionReplacement);
    }

    private string RenderTransitionExpressions(
        string expression,
        string dataBlockedExpression,
        string transitionEligibleExpression
    )
    {
        return DataProbe is null
            ? expression
            : ReplaceTransitionPlaceholders(
                expression,
                dataBlockedExpression,
                transitionEligibleExpression);
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

        if (DataProbe is null)
        {
            builder.Append(expression);

            return;
        }

        AppendTransitionPlaceholders(
            builder,
            expression,
            dataBlockedExpression,
            transitionEligibleExpression);
    }

    private static string ReplaceTransitionPlaceholders(
        string expression,
        string dataReplacement,
        string transitionReplacement
    )
    {
        var dataCount = CountOccurrences(expression, DataProbePlaceholder);
        var transitionCount = CountOccurrences(expression, TransitionInvariantPlaceholder);
        if (dataCount == 0
            && transitionCount == 0)
        {
            return expression;
        }

        var resultLength = CalculateResultLength(
            expression,
            dataReplacement,
            transitionReplacement,
            dataCount,
            transitionCount);

        // WHY: Sequential string.Replace calls materialize the complete state
        // expression twice. A single exact-size pass bounds allocation growth
        // for large repair batches without weakening either runtime predicate.
        return string.Create(
            resultLength,
            (Expression: expression, Data: dataReplacement, Transition: transitionReplacement),
            static (destination, state) => CopyTransitionPlaceholders(
                destination,
                state.Expression,
                state.Data,
                state.Transition));
    }

    private static void AppendTransitionPlaceholders(
        StringBuilder builder,
        string expression,
        string dataReplacement,
        string transitionReplacement
    )
    {
        var source = expression.AsSpan();
        var sourceOffset = 0;
        while (sourceOffset < source.Length)
        {
            var remaining = source[sourceOffset..];
            var nextIndex = FindNextPlaceholder(remaining, out var replaceData);
            if (nextIndex < 0)
            {
                builder.Append(remaining);

                return;
            }

            builder.Append(remaining[..nextIndex]);

            var replacement = replaceData ? dataReplacement : transitionReplacement;
            builder.Append(replacement);
            sourceOffset += nextIndex
                + (replaceData ? DataProbePlaceholder.Length : TransitionInvariantPlaceholder.Length);
        }
    }

    private static int CalculateResultLength(
        string expression,
        string dataReplacement,
        string transitionReplacement,
        int dataCount,
        int transitionCount
    ) => checked(
        expression.Length
        + (dataCount * (dataReplacement.Length - DataProbePlaceholder.Length))
        + (transitionCount * (transitionReplacement.Length - TransitionInvariantPlaceholder.Length)));

    private static void CopyTransitionPlaceholders(
        Span<char> destination,
        string expression,
        string dataReplacement,
        string transitionReplacement
    )
    {
        var source = expression.AsSpan();
        var sourceOffset = 0;
        var destinationOffset = 0;
        while (sourceOffset < source.Length)
        {
            var remaining = source[sourceOffset..];
            var nextIndex = FindNextPlaceholder(remaining, out var replaceData);
            if (nextIndex < 0)
            {
                remaining.CopyTo(destination[destinationOffset..]);

                return;
            }

            remaining[..nextIndex].CopyTo(destination[destinationOffset..]);
            destinationOffset += nextIndex;

            var replacement = replaceData ? dataReplacement : transitionReplacement;
            replacement.AsSpan().CopyTo(destination[destinationOffset..]);
            destinationOffset += replacement.Length;
            sourceOffset += nextIndex
                + (replaceData ? DataProbePlaceholder.Length : TransitionInvariantPlaceholder.Length);
        }
    }

    private static int FindNextPlaceholder(
        ReadOnlySpan<char> source,
        out bool replaceData
    )
    {
        var dataIndex = source.IndexOf(DataProbePlaceholder, StringComparison.Ordinal);
        var transitionIndex = source.IndexOf(
            TransitionInvariantPlaceholder,
            StringComparison.Ordinal);

        replaceData = dataIndex >= 0
            && (transitionIndex < 0 || dataIndex < transitionIndex);

        return replaceData ? dataIndex : transitionIndex;
    }

    private static int CountOccurrences(
        string value,
        string marker
    )
    {
        var count = 0;
        var remaining = value.AsSpan();
        while (true)
        {
            var index = remaining.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            remaining = remaining[(index + marker.Length)..];
        }
    }
}

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
