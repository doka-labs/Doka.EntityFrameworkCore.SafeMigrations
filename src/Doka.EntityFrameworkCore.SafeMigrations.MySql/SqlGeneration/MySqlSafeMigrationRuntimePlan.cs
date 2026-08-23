namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed record MySqlSafeMigrationRuntimePlan(
    string StateExpression,
    string Postcondition,
    SafeMigrationRepairCapability RepairCapability,
    string RepairPrecondition,
    string? UnsupportedCode = null
)
{
    public string[] ParameterValues { get; init; } = [];

    public string PrerequisiteExpression { get; init; } = "TRUE";

    public bool RequiresLazyStateEvaluation { get; init; }

    public string RenderPrerequisiteExpression(
        Func<string, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(PrerequisiteExpression, ParameterValues, renderValue);

    public string RenderStateExpression(
        Func<string, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(StateExpression, ParameterValues, renderValue);

    public string RenderPostcondition(
        Func<string, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(Postcondition, ParameterValues, renderValue);

    public string RenderRepairPrecondition(
        Func<string, string> renderValue
    ) => MySqlCatalogSqlTemplate.Render(RepairPrecondition, ParameterValues, renderValue);

    public string RenderPreparedPrerequisiteExpression(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(PrerequisiteExpression, renderedValues);

    public string RenderPreparedStateExpression(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(StateExpression, renderedValues);

    public string RenderPreparedPostcondition(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(Postcondition, renderedValues);

    public string RenderPreparedRepairPrecondition(
        IReadOnlyList<string> renderedValues
    ) => MySqlCatalogSqlTemplate.RenderPrepared(RepairPrecondition, renderedValues);
}
