namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedCheckConstraintDefinition From(
        AddCheckConstraintOperation operation
    ) => SafeMigrationSqlExpressionParser.TryParse(operation.Sql, out var expression)
        ? ExpectedCheckConstraintDefinition.FromExpression(
            operation.Name,
            operation.Table,
            expression,
            operation.Schema)
        : new ExpectedCheckConstraintDefinition(operation.Name, operation.Table, operation.Sql, operation.Schema);
}
