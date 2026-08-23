namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationSqlExpressionInspector
{
    public static bool IsStructurallyComparable(
        SafeMigrationSqlExpression expression
    ) => expression switch
    {
        SafeMigrationSqlIdentifierExpression => true,
        SafeMigrationSqlLiteralExpression => true,
        SafeMigrationSqlUnaryExpression value => IsStructurallyComparable(value.Operand),
        SafeMigrationSqlBinaryExpression value =>
            IsStructurallyComparable(value.Left) && IsStructurallyComparable(value.Right),
        SafeMigrationSqlNullTestExpression value => IsStructurallyComparable(value.Operand),
        SafeMigrationSqlBetweenExpression value =>
            IsStructurallyComparable(value.Operand)
            && IsStructurallyComparable(value.Lower)
            && IsStructurallyComparable(value.Upper),
        SafeMigrationSqlInExpression value =>
            IsStructurallyComparable(value.Operand) && value.Values.All(IsStructurallyComparable),
        SafeMigrationSqlFunctionExpression value => value.Arguments.All(IsStructurallyComparable),
        SafeMigrationSqlCastExpression value => IsStructurallyComparable(value.Operand),
        SafeMigrationSqlCollateExpression value => IsStructurallyComparable(value.Operand),
        SafeMigrationSqlCurrentValueExpression => true,
        SafeMigrationSqlProviderFragmentExpression => false,
        SafeMigrationSqlOpaqueExpression => false,
        _ => throw new UnreachableException(),
    };

    public static SafeMigrationSqlExpression RenameIdentifier(
        SafeMigrationSqlExpression expression,
        string source,
        string target
    ) => expression switch
    {
        SafeMigrationSqlIdentifierExpression value =>
            new SafeMigrationSqlIdentifierExpression(
                value.Parts.Select(part => StringComparer.Ordinal.Equals(part, source) ? target : part)),
        SafeMigrationSqlLiteralExpression => expression,
        SafeMigrationSqlUnaryExpression value =>
            new SafeMigrationSqlUnaryExpression(value.Operator, RenameIdentifier(value.Operand, source, target)),
        SafeMigrationSqlBinaryExpression value =>
            new SafeMigrationSqlBinaryExpression(
                RenameIdentifier(value.Left, source, target),
                value.Operator,
                RenameIdentifier(value.Right, source, target)),
        SafeMigrationSqlNullTestExpression value =>
            new SafeMigrationSqlNullTestExpression(RenameIdentifier(value.Operand, source, target), value.Negated),
        SafeMigrationSqlBetweenExpression value =>
            new SafeMigrationSqlBetweenExpression(
                RenameIdentifier(value.Operand, source, target),
                RenameIdentifier(value.Lower, source, target),
                RenameIdentifier(value.Upper, source, target),
                value.Negated),
        SafeMigrationSqlInExpression value =>
            new SafeMigrationSqlInExpression(
                RenameIdentifier(value.Operand, source, target),
                value.Values.Select(item => RenameIdentifier(item, source, target)),
                value.Negated),
        SafeMigrationSqlFunctionExpression value =>
            new SafeMigrationSqlFunctionExpression(
                value.Name,
                value.Arguments.Select(item => RenameIdentifier(item, source, target))),
        SafeMigrationSqlCastExpression value =>
            new SafeMigrationSqlCastExpression(RenameIdentifier(value.Operand, source, target), value.StoreType),
        SafeMigrationSqlCollateExpression value => new SafeMigrationSqlCollateExpression(
            RenameIdentifier(value.Operand, source, target),
            value.Name,
            value.Schema),
        SafeMigrationSqlCurrentValueExpression => expression,
        SafeMigrationSqlProviderFragmentExpression value => SafeMigrationSql.OpaqueAfterRename(value.Sql),
        SafeMigrationSqlOpaqueExpression value => SafeMigrationSql.OpaqueAfterRename(value.Sql),
        _ => throw new UnreachableException(),
    };
}
