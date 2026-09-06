namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Represents one parameterized MySQL or MariaDB runtime catalog plan.</summary>
/// <param name="StateExpression">The expression that classifies current state.</param>
/// <param name="Postcondition">The expression that verifies final state.</param>
/// <param name="RepairCapability">The provider-proven repair capability.</param>
/// <param name="RepairPrecondition">The expression that gates repair execution.</param>
/// <param name="UnsupportedCode">The stable unsupported-feature code, when present.</param>
internal sealed record MySqlSafeMigrationRuntimePlan(
    string StateExpression,
    string Postcondition,
    SafeMigrationRepairCapability RepairCapability,
    string RepairPrecondition,
    string? UnsupportedCode = null
)
{
    internal const string DataProbePlaceholder = "__DOKA_SM_DATA_PROBE__";
    internal const string TransitionInvariantPlaceholder = "__DOKA_SM_TRANSITION_INVARIANT__";

    /// <summary>Gets the captured parameter values in placeholder order.</summary>
    public MySqlCatalogParameterValue[] ParameterValues { get; init; } = [];

    /// <summary>Gets the catalog-only prerequisite expression.</summary>
    public string PrerequisiteExpression { get; init; } = "TRUE";

    /// <summary>Gets the catalog-only guard that must pass before state SQL can be evaluated.</summary>
    public string StateEvaluationGuardExpression { get; init; } = "TRUE";

    /// <summary>
    /// Gets an optional catalog expression that returns a precise internal
    /// classification code for state-dependent outcomes.
    /// </summary>
    public string? ClassificationCodeExpression { get; init; }

    /// <summary>Gets the state expression used when the evaluation guard fails.</summary>
    public string? StateEvaluationGuardFailureExpression { get; init; }

    /// <summary>Gets whether runtime SQL must defer state evaluation behind the guard.</summary>
    public bool RequiresLazyStateEvaluation { get; init; }

    /// <summary>Gets the optional bounded live-data probe required by classification.</summary>
    public MySqlSafeMigrationDataProbe? DataProbe { get; init; }

    /// <summary>Gets whether classification reads live table data.</summary>
    public bool RequiresDataProbe => DataProbe is not null;

    /// <summary>Gets whether a nullable-to-required repair can require live row evidence.</summary>
    public bool MayRequireNullabilityDataProof { get; init; }

    /// <summary>Gets the conservative execution-impact classification for an accepted repair.</summary>
    public SafeMigrationOperationalImpact RepairOperationalImpact { get; init; }
        = SafeMigrationOperationalImpact.Unknown;

    /// <summary>Gets optional bounded catalog-only facet-difference evidence.</summary>
    public string? DiagnosticEvidenceExpression { get; init; }

    /// <summary>Gets optional constant evidence for a privacy-sensitive Different result.</summary>
    public SafeMigrationFacetDifference? DifferentDifference { get; init; }

    /// <summary>
    /// Gets whether the complete operation is unsupported independently of
    /// catalog state and therefore has no executable baseline.
    /// </summary>
    public bool IsStaticallyUnsupported { get; init; }

    /// <summary>Gets the guarded data-mutation SQL template, when the operation mutates model-managed data.</summary>
    public string? MutationSql { get; init; }

    /// <summary>Gets optional compact row-state evidence for ordered model-data projection.</summary>
    public string? ModelManagedRowEvidenceExpression { get; init; }

    /// <summary>Gets optional live dependency counts for ordered model-data projection.</summary>
    public string? ModelManagedDependencyCountsExpression { get; init; }

    /// <summary>Gets the expected number of compact row-state entries.</summary>
    public int ModelManagedRowCount { get; init; }

    /// <summary>Gets the expected number of dependency-count entries.</summary>
    public int ModelManagedDependencyCount { get; init; }

    /// <summary>Renders the prerequisite expression with provider literals.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPrerequisiteExpression(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(PrerequisiteExpression, ParameterValues, renderValue);

    /// <summary>Renders the state expression with provider literals.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderStateExpression(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => RenderTransitionExpressions(StateExpression, renderValue, dataBlocked: null, transitionEligible: null);

    /// <summary>Renders the state expression with an already resolved analysis probe.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <param name="dataBlocked">Whether the grouped analysis probe found blocking data.</param>
    /// <param name="transitionEligible">Whether the catalog proved the lossless transition invariant.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderStateExpression(
        Func<MySqlCatalogParameterValue, string> renderValue,
        bool dataBlocked,
        bool transitionEligible
    ) => RenderTransitionExpressions(StateExpression, renderValue, dataBlocked, transitionEligible);

    /// <summary>Renders the state-evaluation guard with provider literals.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderStateEvaluationGuardExpression(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(StateEvaluationGuardExpression, ParameterValues, renderValue);

    /// <summary>Renders the guard-failure state expression with provider literals.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderStateEvaluationGuardFailureExpression(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(
        StateEvaluationGuardFailureExpression
            ?? throw new InvalidOperationException("The state-evaluation guard has no failure expression."),
        ParameterValues,
        renderValue);

    /// <summary>Renders the state-dependent classification-code expression.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderClassificationCodeExpression(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => RenderWithDataProbe(
        ClassificationCodeExpression
            ?? throw new InvalidOperationException("The runtime plan has no classification-code expression."),
        renderValue,
        dataBlocked: null);

    /// <summary>Renders the classification code with an already resolved analysis probe.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <param name="dataBlocked">Whether the grouped analysis probe found blocking data.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderClassificationCodeExpression(
        Func<MySqlCatalogParameterValue, string> renderValue,
        bool dataBlocked
    ) => RenderWithDataProbe(
        ClassificationCodeExpression
            ?? throw new InvalidOperationException("The runtime plan has no classification-code expression."),
        renderValue,
        dataBlocked);

    /// <summary>Renders bounded catalog-only facet-difference evidence.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderDiagnosticEvidenceExpression(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(
        DiagnosticEvidenceExpression
            ?? throw new InvalidOperationException("The runtime plan has no diagnostic evidence expression."),
        ParameterValues,
        renderValue);

    /// <summary>Renders compact model-managed row evidence.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderModelManagedRowEvidenceExpression(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(
        ModelManagedRowEvidenceExpression
            ?? throw new InvalidOperationException("The runtime plan has no model-managed row evidence."),
        ParameterValues,
        renderValue);

    /// <summary>Renders compact model-managed dependency counts.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderModelManagedDependencyCountsExpression(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(
        ModelManagedDependencyCountsExpression
            ?? throw new InvalidOperationException("The runtime plan has no model-managed dependency evidence."),
        ParameterValues,
        renderValue);

    /// <summary>Renders the postcondition with provider literals.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPostcondition(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(Postcondition, ParameterValues, renderValue);

    /// <summary>Renders the repair precondition with provider literals.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderRepairPrecondition(
        Func<MySqlCatalogParameterValue, string> renderValue
    ) => RenderTransitionExpressions(RepairPrecondition, renderValue, dataBlocked: null, transitionEligible: null);

    /// <summary>Renders the repair precondition with an already resolved analysis probe.</summary>
    /// <param name="renderValue">The provider literal renderer.</param>
    /// <param name="dataBlocked">Whether the grouped analysis probe found blocking data.</param>
    /// <param name="transitionEligible">Whether the catalog proved the lossless transition invariant.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderRepairPrecondition(
        Func<MySqlCatalogParameterValue, string> renderValue,
        bool dataBlocked,
        bool transitionEligible
    ) => RenderTransitionExpressions(RepairPrecondition, renderValue, dataBlocked, transitionEligible);

    /// <summary>Renders the prerequisite expression with prepared literal values.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPreparedPrerequisiteExpression(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(PrerequisiteExpression, renderedValues);

    /// <summary>Renders the state expression with prepared literal values.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPreparedStateExpression(
        IReadOnlyList<string> renderedValues
    ) => RenderPreparedTransitionExpressions(StateExpression, renderedValues);

    /// <summary>Renders the state expression against a previously evaluated data-probe expression.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <param name="dataBlockedExpression">The Boolean SQL expression containing the cached probe result.</param>
    /// <param name="transitionEligibleExpression">The Boolean SQL expression containing transition eligibility.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPreparedStateExpression(
        IReadOnlyList<string> renderedValues,
        string dataBlockedExpression,
        string transitionEligibleExpression
    ) => RenderPreparedTransitionExpressions(
        StateExpression,
        renderedValues,
        dataBlockedExpression,
        transitionEligibleExpression);

    /// <summary>Renders the state-evaluation guard with prepared literal values.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPreparedStateEvaluationGuardExpression(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(StateEvaluationGuardExpression, renderedValues);

    /// <summary>Renders the guard-failure state expression with prepared literal values.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPreparedStateEvaluationGuardFailureExpression(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(
        StateEvaluationGuardFailureExpression
            ?? throw new InvalidOperationException("The state-evaluation guard has no failure expression."),
        renderedValues);

    /// <summary>Renders the postcondition with prepared literal values.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPreparedPostcondition(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(Postcondition, renderedValues);

    /// <summary>Renders the repair precondition with prepared literal values.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPreparedRepairPrecondition(
        IReadOnlyList<string> renderedValues
    ) => RenderPreparedTransitionExpressions(RepairPrecondition, renderedValues);

    /// <summary>Renders the repair precondition against a previously evaluated data-probe expression.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <param name="dataBlockedExpression">The Boolean SQL expression containing the cached probe result.</param>
    /// <param name="transitionEligibleExpression">The Boolean SQL expression containing transition eligibility.</param>
    /// <returns>The rendered expression.</returns>
    public string RenderPreparedRepairPrecondition(
        IReadOnlyList<string> renderedValues,
        string dataBlockedExpression,
        string transitionEligibleExpression
    ) => RenderPreparedTransitionExpressions(
        RepairPrecondition,
        renderedValues,
        dataBlockedExpression,
        transitionEligibleExpression);

    /// <summary>Renders the physical invariant that permits the lossless transition.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered predicate.</returns>
    public string RenderPreparedTransitionInvariantExpression(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(
        DataProbe?.TransitionInvariantExpression
            ?? throw new InvalidOperationException("The runtime plan has no data probe."),
        renderedValues);

    /// <summary>Renders the catalog-only predicate that determines whether a row probe is required.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered predicate.</returns>
    public string RenderPreparedDataProbeRequiredExpression(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(
        DataProbe?.NarrowingExpression
            ?? throw new InvalidOperationException("The runtime plan has no data probe."),
        renderedValues);

    /// <summary>Renders the bounded row probe used only after catalog qualification.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered predicate.</returns>
    public string RenderPreparedDataProbeBlockedExpression(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(
        DataProbe?.BuildBlockedExpression()
            ?? throw new InvalidOperationException("The runtime plan has no data probe."),
        renderedValues);

    /// <summary>Renders the model-managed data mutation with prepared literal values.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered mutation SQL.</returns>
    public string RenderPreparedMutationSql(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(
        MutationSql
            ?? throw new InvalidOperationException("The runtime plan has no model-managed data mutation."),
        renderedValues);

    private string RenderWithDataProbe(
        string expression,
        Func<MySqlCatalogParameterValue, string> renderValue,
        bool? dataBlocked
    )
    {
        var rendered = MySqlCatalogSqlTemplate.Render(expression, ParameterValues, renderValue);
        if (DataProbe is null)
        {
            return rendered;
        }

        var replacement = dataBlocked is null
            ? MySqlCatalogSqlTemplate.Render(DataProbe.BuildBlockedExpression(), ParameterValues, renderValue)
            : dataBlocked.Value
                ? "TRUE"
                : "FALSE";

        return rendered.Replace(DataProbePlaceholder, replacement, StringComparison.Ordinal);
    }

    private string RenderTransitionExpressions(
        string expression,
        Func<MySqlCatalogParameterValue, string> renderValue,
        bool? dataBlocked,
        bool? transitionEligible
    )
    {
        var rendered = MySqlCatalogSqlTemplate.Render(expression, ParameterValues, renderValue);
        if (DataProbe is null)
        {
            return rendered;
        }

        var dataReplacement = dataBlocked is null
            ? MySqlCatalogSqlTemplate.Render(DataProbe.BuildBlockedExpression(), ParameterValues, renderValue)
            : dataBlocked.Value
                ? "TRUE"
                : "FALSE";

        var transitionReplacement = transitionEligible is null
            ? MySqlCatalogSqlTemplate.Render(
                DataProbe.TransitionInvariantExpression
                    ?? throw new InvalidOperationException("The runtime plan has no transition evidence."),
                ParameterValues,
                renderValue)
            : transitionEligible.Value
                ? "TRUE"
                : "FALSE";

        return ReplaceTransitionPlaceholders(rendered, dataReplacement, transitionReplacement);
    }

    private string RenderPreparedTransitionExpressions(
        string expression,
        IReadOnlyList<string> renderedValues,
        string? dataBlockedExpression = null,
        string? transitionEligibleExpression = null
    )
    {
        var rendered = MySqlCatalogSqlTemplate.RenderPrepared(expression, renderedValues);
        if (DataProbe is null)
        {
            return rendered;
        }

        var dataReplacement = dataBlockedExpression
            ?? MySqlCatalogSqlTemplate.RenderPrepared(DataProbe.BuildBlockedExpression(), renderedValues);

        var transitionReplacement = transitionEligibleExpression
            ?? MySqlCatalogSqlTemplate.RenderPrepared(
                DataProbe.TransitionInvariantExpression
                    ?? throw new InvalidOperationException("The runtime plan has no transition evidence."),
                renderedValues);

        return ReplaceTransitionPlaceholders(rendered, dataReplacement, transitionReplacement);
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

        var resultLength = checked(
            expression.Length
            + (dataCount * (dataReplacement.Length - DataProbePlaceholder.Length))
            + (transitionCount * (transitionReplacement.Length - TransitionInvariantPlaceholder.Length)));

        // WHY: Sequential string.Replace calls materialize the complete state
        // expression twice. A single exact-size pass keeps generated SQL byte-
        // equivalent while bounding allocation growth for large repair batches.
        return string.Create(
            resultLength,
            (Expression: expression, Data: dataReplacement, Transition: transitionReplacement),
            static (destination, state) =>
            {
                var source = state.Expression.AsSpan();
                var sourceOffset = 0;
                var destinationOffset = 0;
                while (sourceOffset < source.Length)
                {
                    var remaining = source[sourceOffset..];
                    var dataIndex = remaining.IndexOf(DataProbePlaceholder, StringComparison.Ordinal);
                    var transitionIndex = remaining.IndexOf(
                        TransitionInvariantPlaceholder,
                        StringComparison.Ordinal);

                    var replaceData = dataIndex >= 0
                        && (transitionIndex < 0 || dataIndex < transitionIndex);
                    var nextIndex = replaceData ? dataIndex : transitionIndex;
                    if (nextIndex < 0)
                    {
                        remaining.CopyTo(destination[destinationOffset..]);

                        break;
                    }

                    remaining[..nextIndex].CopyTo(destination[destinationOffset..]);
                    destinationOffset += nextIndex;

                    var replacement = replaceData ? state.Data : state.Transition;
                    replacement.AsSpan().CopyTo(destination[destinationOffset..]);
                    destinationOffset += replacement.Length;
                    sourceOffset += nextIndex
                        + (replaceData ? DataProbePlaceholder.Length : TransitionInvariantPlaceholder.Length);
                }
            });
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

/// <summary>Describes one deduplicatable MySQL/MariaDB narrowing proof.</summary>
/// <param name="Table">The validated relational table name.</param>
/// <param name="Column">The validated relational column name.</param>
/// <param name="TargetLength">The positive target character length.</param>
/// <param name="DelimitedTable">The provider-delimited table identifier.</param>
/// <param name="DelimitedColumn">The provider-delimited column identifier.</param>
/// <param name="TransitionInvariantExpression">
/// The optional physical catalog predicate for a lossless transition.
/// </param>
/// <param name="NarrowingExpression">The catalog predicate that identifies a narrowing transition.</param>
internal sealed record MySqlSafeMigrationDataProbe(
    string Table,
    string Column,
    int TargetLength,
    string DelimitedTable,
    string DelimitedColumn,
    string? TransitionInvariantExpression,
    string NarrowingExpression
)
{
    /// <summary>Builds the bounded row probe used only after catalog qualification.</summary>
    /// <returns>The provider-delimited Boolean SQL expression.</returns>
    public string BuildBlockedExpression() => "EXISTS(SELECT 1 FROM "
        + $"{DelimitedTable} WHERE {DelimitedColumn} IS NOT NULL "
        + $"AND CHAR_LENGTH({DelimitedColumn}) > {TargetLength.ToString(CultureInfo.InvariantCulture)} LIMIT 1)";
}
