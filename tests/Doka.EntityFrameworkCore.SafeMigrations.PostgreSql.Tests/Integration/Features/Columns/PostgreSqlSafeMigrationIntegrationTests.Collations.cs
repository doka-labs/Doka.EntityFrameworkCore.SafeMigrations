namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task StructuredCollationIdentity_ConvergesWithoutSchemaOrDotAmbiguity()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE SCHEMA collation_a; CREATE SCHEMA collation_b; "
            + "CREATE COLLATION collation_a.shared FROM \"C\"; "
            + "CREATE COLLATION collation_b.shared FROM \"C\"; "
            + "CREATE COLLATION collation_a.\"name.with.dot\" FROM \"C\"; "
            + "CREATE DOMAIN collation_a.domain_text AS text COLLATE collation_a.shared; "
            + "CREATE TABLE collation_identity (id integer NOT NULL, domain_value collation_a.domain_text NULL);");
        await using var context = CreateContext(connectionString);
        var definitions = new[]
        {
            new ExpectedColumnDefinition(
                "from_a",
                typeof(string),
                true,
                "text",
                collation: new SafeMigrationCollationIdentifier("shared", "collation_a")),
            new ExpectedColumnDefinition(
                "from_b",
                typeof(string),
                true,
                "text",
                collation: new SafeMigrationCollationIdentifier("shared", "collation_b")),
            new ExpectedColumnDefinition(
                "dotted",
                typeof(string),
                true,
                "text",
                collation: new SafeMigrationCollationIdentifier("name.with.dot", "collation_a")),
            new ExpectedColumnDefinition("noncollatable", typeof(int), true, "integer"),
            new ExpectedColumnDefinition("domain_value", typeof(string), true, "collation_a.domain_text"),
        };
        var indexes = new[]
        {
            new ExpectedIndexDefinition(
                "ix_collation_from_a",
                "collation_identity",
                [
                    new ExpectedIndexKeyDefinition(
                        column: "from_a",
                        collation: new SafeMigrationCollationIdentifier("shared", "collation_a"))
                ]),
            new ExpectedIndexDefinition(
                "ix_collation_dotted",
                "collation_identity",
                [
                    new ExpectedIndexKeyDefinition(
                        column: "dotted",
                        collation: new SafeMigrationCollationIdentifier("name.with.dot", "collation_a"))
                ]),
        };
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "collation_table_create",
                [
                    new ExpectedColumnDefinition(
                        "qualified_value",
                        typeof(string),
                        true,
                        "text",
                        collation: new SafeMigrationCollationIdentifier("shared", "collation_a")),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        foreach (var definition in definitions)
        {
            builder.EnsureColumn("collation_identity", definition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        foreach (var index in indexes)
        {
            builder.EnsureIndex(index, SafeMigrationPolicy.ThrowIfDifferent);
        }

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("structured-collations"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(
            report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_attribute a "
                + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
                + "JOIN pg_catalog.pg_collation coll ON coll.oid = a.attcollation "
                + "JOIN pg_catalog.pg_namespace ns ON ns.oid = coll.collnamespace "
                + "WHERE c.relname = 'collation_identity' AND a.attname = 'dotted' "
                + "AND ns.nspname = 'collation_a' AND coll.collname = 'name.with.dot';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_attribute a "
                + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
                + "JOIN pg_catalog.pg_collation coll ON coll.oid = a.attcollation "
                + "JOIN pg_catalog.pg_namespace ns ON ns.oid = coll.collnamespace "
                + "WHERE c.relname = 'collation_table_create' AND a.attname = 'qualified_value' "
                + "AND ns.nspname = 'collation_a' AND coll.collname = 'shared';"));

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.EnsureColumn(
            "collation_identity",
            new ExpectedColumnDefinition(
                "from_a",
                typeof(string),
                true,
                "text",
                collation: new SafeMigrationCollationIdentifier("shared", "collation_b")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var driftReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, drift.Operations, new SafeMigrationRunOptions("structured-collation-drift"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, driftReport.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(driftReport.Assessments)
                .ObservedState);
    }
}
