namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task MaximumQuotedMixedCaseUnicodeIdentifiers_ConvergeAcrossOwnedObjects()
    {
        var table = MySqlMaximumIdentifier("Ta`b\\le'_\u00fc_");
        var id = MySqlMaximumIdentifier("I`D\\'_\u00fc_");
        var code = MySqlMaximumIdentifier("Co`de\\'_\u00fc_");
        var parent = MySqlMaximumIdentifier("Pa`rent\\'_\u00fc_");
        var primaryKey = MySqlMaximumIdentifier("PK`\\'_\u00fc_");
        var unique = MySqlMaximumIdentifier("UQ`\\'_\u00fc_");
        var check = MySqlMaximumIdentifier("CK`\\'_\u00fc_");
        var foreignKey = MySqlMaximumIdentifier("FK`\\'_\u00fc_");
        var index = MySqlMaximumIdentifier("IX`\\'_\u00fc_");
        var definition = new ExpectedTableDefinition(
            table,
            [
                new ExpectedColumnDefinition(id, typeof(int), false, "int"),
                new ExpectedColumnDefinition(code, typeof(string), true, "varchar(40)", maxLength: 40),
                new ExpectedColumnDefinition(parent, typeof(int), true, "int"),
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
                    $"{MySqlIdentifier(code)} IS NULL OR {MySqlIdentifier(code)} <> ''"),
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

        try
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }
        catch (Exception exception)
        {
            var diagnostics = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("identifier-instance"));
            var failures = diagnostics
                .Assessments.Where(static assessment => assessment.ObservedState != SafeMigrationObservedState.Matching)
                .Select(static assessment => $"{assessment.Ordinal}:{assessment.OperationKind}:"
                    + $"{assessment.ObjectName}:{assessment.ObservedState}:{assessment.Code}");
            var catalogCheck = await DescribeCheckConstraintAsync(connectionString, check);
            throw new InvalidOperationException(
                $"Identifier convergence failed: {string.Join(";", failures)}. " + $"Catalog check: {catalogCheck}",
                exception);
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
