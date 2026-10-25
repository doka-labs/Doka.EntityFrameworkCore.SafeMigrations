/// <summary>Checks the new expression API through each actual isolated provider package.</summary>
internal static class GeneratedExpressionConsumerContract
{
    /// <summary>Verifies public CASE construction and generated metadata capture without a database.</summary>
    internal static void Verify()
    {
        var expression = SafeMigrationSql.Case(
            [new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true), SafeMigrationSql.Literal(1))],
            SafeMigrationSql.Literal(0));

        var builder = new MigrationBuilder("PackageConsumer");
        builder.AddColumnIfNotExistsFromModel(
            static operations => operations.AddColumn<long>(
                name: "computed_value", table: "expression_probe", nullable: true,
                computedColumnSql: "CASE WHEN source_value = 0 THEN 1 ELSE 2 END"));

        builder.AddCheckConstraintIfNotExists(
            name: "ck_expression_probe", table: "expression_probe", sql: "source_value >= 0");

        // WHY: ProjectReference-only tests cannot detect missing public types,
        // metadata bridges or compile assets in an isolated NuGet consumer.
        if (expression is not SafeMigrationSqlCaseExpression { WhenClauses.Count: 1, ElseResult: not null }
            || builder.Operations is not
            [
                SafeMigrationOperation
                {
                    Intent: EnsureColumnIntent { Definition.ComputedExpression: SafeMigrationSqlCaseExpression },
                },
                SafeMigrationOperation { Intent: EnsureCheckConstraintIntent { Definition.Expression: not null } },
            ])
        {
            throw new System.InvalidOperationException("The packaged generated-expression contract is incomplete.");
        }
    }
}
