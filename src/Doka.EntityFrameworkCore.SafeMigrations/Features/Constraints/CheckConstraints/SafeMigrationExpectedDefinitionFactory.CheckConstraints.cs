namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedCheckConstraintDefinition From(
        AddCheckConstraintOperation operation
    ) => CheckConstraint(operation.Name, operation.Table, operation.Sql, operation.Schema);

    /// <summary>Shares the parse-or-opaque boundary across operations, scaffolding, and model metadata.</summary>
    internal static ExpectedCheckConstraintDefinition CheckConstraint(
        string name,
        string table,
        string sql,
        string? schema
    ) => SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression)
        ? ExpectedCheckConstraintDefinition.FromCapturedExpression(
            name,
            table,
            expression,
            sql,
            schema)
        : new ExpectedCheckConstraintDefinition(name, table, sql, schema);
}
