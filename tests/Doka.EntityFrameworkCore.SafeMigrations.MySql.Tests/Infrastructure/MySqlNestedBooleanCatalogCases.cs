namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Retains independently captured MySQL 8.4.13/9.7.4 clauses and their authored Boolean trees.</summary>
internal static class MySqlNestedBooleanCatalogCases
{
    internal const string ActiveSource = "((`ActiveGeneration` IS NULL AND `ActiveShape` IS NULL "
        + "AND `ActiveProof` IS NULL) OR (`ActiveGeneration` IS NOT NULL AND `ActiveShape` IS NOT NULL "
        + "AND `ActiveProof` IS NOT NULL AND `ActiveGeneration` > 0\nAND `ActiveProof` = 1))";

    internal const string TargetSource = "((`TargetGeneration` IS NULL AND `TargetShape` IS NULL) "
        + "OR (`TargetGeneration` IS NOT NULL AND `TargetShape` IS NOT NULL AND `TargetGeneration` > 0))";

    internal const string ActiveTuple = "(((`ActiveGeneration` is null) and (`ActiveShape` is null) "
        + "and (`ActiveProof` is null)) or ((`ActiveGeneration` is not null) and (`ActiveShape` is not null) "
        + "and (`ActiveProof` is not null) and (`ActiveGeneration` > 0) and (`ActiveProof` = 1)))";

    internal const string TargetTuple = "(((`TargetGeneration` is null) and (`TargetShape` is null)) "
        + "or ((`TargetGeneration` is not null) and (`TargetShape` is not null) and (`TargetGeneration` > 0)))";

    /// <summary>Builds the original null-tuple contract without parsing its captured native spelling.</summary>
    /// <param name="target">Whether to use the two-member target tuple instead of the active tuple.</param>
    /// <param name="rightAssociated">Whether source chains associate to the right.</param>
    /// <param name="proof">The independently specified active proof literal.</param>
    internal static SafeMigrationSqlExpression Create(
        bool target,
        bool rightAssociated,
        int proof = 1
    )
    {
        var generation = SafeMigrationSql.Identifier(target
            ? "TargetGeneration"
            : "ActiveGeneration");

        var shape = SafeMigrationSql.Identifier(target
            ? "TargetShape"
            : "ActiveShape");

        var activeProof = SafeMigrationSql.Identifier("ActiveProof");
        SafeMigrationSqlExpression[] missing = target
            ? [SafeMigrationSql.IsNull(generation), SafeMigrationSql.IsNull(shape)]
            :
            [
                SafeMigrationSql.IsNull(generation), SafeMigrationSql.IsNull(shape),
                SafeMigrationSql.IsNull(activeProof),
            ];

        var positiveGeneration = SafeMigrationSql.Binary(generation,
            SafeMigrationSqlBinaryOperator.GreaterThan,
            SafeMigrationSql.Literal(0));

        SafeMigrationSqlExpression[] present = target
            ? [SafeMigrationSql.IsNotNull(generation), SafeMigrationSql.IsNotNull(shape), positiveGeneration]
            :
            [
                SafeMigrationSql.IsNotNull(generation), SafeMigrationSql.IsNotNull(shape),
                SafeMigrationSql.IsNotNull(activeProof), positiveGeneration,
                SafeMigrationSql.Binary(activeProof,
                    SafeMigrationSqlBinaryOperator.Equal,
                    SafeMigrationSql.Literal(proof)),
            ];

        return SafeMigrationSql.Binary(Chain(missing, rightAssociated),
            SafeMigrationSqlBinaryOperator.Or,
            Chain(present, rightAssociated));
    }

    /// <summary>Changes only source association, retaining operand order and every predicate.</summary>
    private static SafeMigrationSqlExpression Chain(
        ReadOnlySpan<SafeMigrationSqlExpression> expressions,
        bool rightAssociated
    )
    {
        var result = rightAssociated
            ? expressions[^1]
            : expressions[0];

        // WHY: Opposite traversal directions build both AST associations without changing operand order.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 1; index < expressions.Length; index++)
        {
            result = rightAssociated
                ? SafeMigrationSql.Binary(expressions[^(index + 1)], SafeMigrationSqlBinaryOperator.And, result)
                : SafeMigrationSql.Binary(result, SafeMigrationSqlBinaryOperator.And, expressions[index]);
        }

        return result;
    }
}
