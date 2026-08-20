namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
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
            uniqueConstraints:
            [
                new ExpectedUniqueConstraintDefinition(unique, table, [code]),
            ],
            checkConstraints:
            [
                new ExpectedCheckConstraintDefinition(
                    check,
                    table,
                    $"({PostgreSqlIdentifier(code)} IS NULL) OR "
                    + $"(({PostgreSqlIdentifier(code)})::text <> ''::text)"),
            ],
            foreignKeys:
            [
                new ExpectedForeignKeyDefinition(foreignKey, table, [parent], table, [id]),
            ]);

        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(
            definition,
            [
                new ExpectedIndexDefinition(index, table, [new ExpectedIndexKeyDefinition(column: parent)]),
            ]);

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
