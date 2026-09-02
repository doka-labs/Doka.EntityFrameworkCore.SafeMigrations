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
    ) => MySqlCatalogSqlTemplate.Render(StateExpression, ParameterValues, renderValue);

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
    ) => MySqlCatalogSqlTemplate.Render(
        ClassificationCodeExpression
            ?? throw new InvalidOperationException("The runtime plan has no classification-code expression."),
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
    ) => MySqlCatalogSqlTemplate.Render(RepairPrecondition, ParameterValues, renderValue);

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
    ) => MySqlCatalogSqlTemplate.RenderPrepared(StateExpression, renderedValues);

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
    ) => MySqlCatalogSqlTemplate.RenderPrepared(RepairPrecondition, renderedValues);

    /// <summary>Renders the model-managed data mutation with prepared literal values.</summary>
    /// <param name="renderedValues">The rendered literal values in placeholder order.</param>
    /// <returns>The rendered mutation SQL.</returns>
    public string RenderPreparedMutationSql(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(
        MutationSql
            ?? throw new InvalidOperationException("The runtime plan has no model-managed data mutation."),
        renderedValues);
}
