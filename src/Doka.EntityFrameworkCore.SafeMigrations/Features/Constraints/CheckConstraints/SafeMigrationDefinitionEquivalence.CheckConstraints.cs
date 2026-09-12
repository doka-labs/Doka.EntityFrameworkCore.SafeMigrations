namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    public static bool CheckConstraint(
        ExpectedCheckConstraintDefinition left,
        ExpectedCheckConstraintDefinition right
    ) => StringComparer.Ordinal.Equals(left.Name, right.Name)
        && CheckConstraintSemantics(left, right);

    public static bool CheckConstraintSemantics(
        ExpectedCheckConstraintDefinition left,
        ExpectedCheckConstraintDefinition right
    ) => Identity(left.Table, left.Schema, right.Table, right.Schema)
        && StringComparer.Ordinal.Equals(left.Sql, right.Sql)
        && SafeMigrationSqlExpressionContract.Equivalent(left.Expression, right.Expression);
}
