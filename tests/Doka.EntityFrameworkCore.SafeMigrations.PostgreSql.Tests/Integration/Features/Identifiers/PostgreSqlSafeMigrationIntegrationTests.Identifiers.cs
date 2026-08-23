namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ServerQuotedExpressionIdentifiers_ConvergeAcrossEveryStructuredFacet()
    {
        const string table = "expression_identifiers";
        var sum = SqlBinary(
            SqlBinary(SqlColumn("user"), SafeMigrationSqlBinaryOperator.Add, SqlColumn("a$b")),
            SafeMigrationSqlBinaryOperator.Add,
            SqlColumn("ordinary"));
        var definition = new ExpectedTableDefinition(
            table,
            [
                new ExpectedColumnDefinition("user", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("a$b", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("ordinary", typeof(int), false, "integer"),
                new ExpectedColumnDefinition(
                    "default$value",
                    typeof(string),
                    false,
                    "text",
                    defaultValue: SafeMigrationDefaultValue.Sql(
                        SafeMigrationSql.Collate(SafeMigrationSql.Literal("seed", "text"), "C"))),
                new ExpectedColumnDefinition(
                    "select",
                    typeof(int),
                    true,
                    "integer",
                    isStored: true,
                    computedExpression: sum),
            ],
            checkConstraints:
            [
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_expression_identifiers",
                    table,
                    SqlBinary(sum, SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, SafeMigrationSql.Literal(0))),
            ]);
        var index = new ExpectedIndexDefinition(
            "ix_expression_identifiers",
            table,
            [new ExpectedIndexKeyDefinition(structuredExpression: sum)],
            structuredFilter: SqlBinary(
                SqlColumn("a$b"),
                SafeMigrationSqlBinaryOperator.GreaterThan,
                SafeMigrationSql.Literal(0)));

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition);

        for (var ordinal = 0; ordinal < builder.Operations.Count; ordinal++)
        {
            try
            {
                await ExecuteOperationsAsync(context, [builder.Operations[ordinal]]);
            }
            catch (Exception exception)
            {
                var operation = Assert.IsType<SafeMigrationOperation>(builder.Operations[ordinal]);

                throw new InvalidOperationException(
                    $"Server-quoted identifier convergence failed at {ordinal}:{operation.Intent.Kind}.",
                    exception);
            }
        }

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE INDEX ix_expression_identifiers ON expression_identifiers "
            + "(((\"user\" + \"a$b\") + ordinary)) WHERE \"a$b\" > 0;");
        var indexBuilder = new MigrationBuilder(context.Database.ProviderName!);
        indexBuilder.EnsureIndex(index, SafeMigrationPolicy.ThrowIfDifferent);
        var indexReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, indexBuilder.Operations, new SafeMigrationRunOptions("server-quoted-index"));
        var indexCatalog = await ScalarStringAsync(
            connectionString,
            "SELECT pg_catalog.pg_get_indexdef(i.indexrelid, 1, TRUE) || ' | ' || "
            + "pg_catalog.pg_get_expr(i.indpred, i.indrelid) "
            + "FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid "
            + "WHERE c.relname = 'ix_expression_identifiers';");

        Assert.True(
            indexReport.Status == SafeMigrationReportStatus.Ready,
            $"Index catalog form did not converge: {indexCatalog}");

        await ExecuteOperationsAsync(context, indexBuilder.Operations);
        await ExecuteOperationsAsync(context, indexBuilder.Operations);
        var operations = builder
            .Operations
            .Concat(indexBuilder.Operations)
            .ToArray();
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, operations, new SafeMigrationRunOptions("server-quoted-identifiers"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(
            report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
    }

    [Fact]
    public async Task MaximumQuotedMixedCaseUnicodeIdentifiers_ConvergeAcrossOwnedObjects()
    {
        var table = PostgreSqlMaximumIdentifier("Ta\"b\\le'_\u00fc_");
        var id = PostgreSqlMaximumIdentifier("I\"D\\'_\u00fc_");
        var code = PostgreSqlMaximumIdentifier("Co\"de\\'_\u00fc_");
        var parent = PostgreSqlMaximumIdentifier("Pa\"rent\\'_\u00fc_");
        var primaryKey = PostgreSqlMaximumIdentifier("PK\"\\'_\u00fc_");
        var unique = PostgreSqlMaximumIdentifier("UQ\"\\'_\u00fc_");
        var check = PostgreSqlMaximumIdentifier("CK\"\\'_\u00fc_");
        var foreignKey = PostgreSqlMaximumIdentifier("FK\"\\'_\u00fc_");
        var index = PostgreSqlMaximumIdentifier("IX\"\\'_\u00fc_");
        var definition = new ExpectedTableDefinition(
            table,
            [
                new ExpectedColumnDefinition(id, typeof(int), false, "integer"),
                new ExpectedColumnDefinition(code, typeof(string), true, "character varying(40)", maxLength: 40),
                new ExpectedColumnDefinition(parent, typeof(int), true, "integer"),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition(primaryKey, table, [id]),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition(unique, table, [code]),],
            checkConstraints:
            [
                ExpectedCheckConstraintDefinition.FromExpression(
                    check,
                    table,
                    SqlBinary(
                        SafeMigrationSql.IsNull(SqlColumn(code)),
                        SafeMigrationSqlBinaryOperator.Or,
                        SqlBinary(
                            SafeMigrationSql.Cast(SqlColumn(code), "text"),
                            SafeMigrationSqlBinaryOperator.NotEqual,
                            SafeMigrationSql.Literal(string.Empty, "text")))),
            ],
            foreignKeys: [new ExpectedForeignKeyDefinition(foreignKey, table, [parent], table, [id]),]);

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(
            definition,
            [new ExpectedIndexDefinition(index, table, [new ExpectedIndexKeyDefinition(column: parent)]),]);

        for (var ordinal = 0; ordinal < builder.Operations.Count; ordinal++)
        {
            try
            {
                await ExecuteOperationsAsync(context, [builder.Operations[ordinal]]);
            }
            catch (Exception exception)
            {
                var operation = Assert.IsType<SafeMigrationOperation>(builder.Operations[ordinal]);
                var detail = operation.Intent.Kind == SafeMigrationOperationKind.EnsureCheckConstraint
                    ? await ReadCheckExpressionAsync(connectionString, check)
                    : "not_applicable";

                throw new InvalidOperationException(
                    $"Identifier convergence failed at {ordinal}:{operation.Intent.Kind}. "
                    + $"Catalog expression: {detail}",
                    exception);
            }
        }

        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("identifier-instance"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(
            report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
    }
}
