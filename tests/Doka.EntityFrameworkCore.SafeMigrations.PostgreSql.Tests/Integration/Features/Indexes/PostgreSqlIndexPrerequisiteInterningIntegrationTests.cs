namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>
/// Checks shared metadata-only certificates against native planner demand and real schema identities.
/// </summary>
public sealed class PostgreSqlIndexPrerequisiteInterningIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the pinned, independently owned PostgreSQL qualification fixture.</summary>
    /// <param name="fixture">The provider's synthetic test fixture.</param>
    public PostgreSqlIndexPrerequisiteInterningIntegrationTests(PostgreSqlContainerFixture fixture) : base(fixture) { }

    /// <summary>Matching indexes never demand type proofs or mistake a generated CTE for their actual owner.</summary>
    /// <param name="distinct">Whether each operation has a different name but the same physical native inputs.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingNativeInputsRemainLazyAndDoNotShadowDataOwners(bool distinct)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE \"_doka_sm_index_input_0\" (id integer, parent_id integer);"
            + " CREATE INDEX ix_prerequisite ON \"_doka_sm_index_input_0\" (id,parent_id);");

        await using var context = CreateContext(connectionString);
        var operations = PostgreSqlIndexPrerequisiteInterningTests.Operations(context, 32, distinct);
        await using var capture = new CatalogStatementTestConnection(false,
            PostgreSqlIndexPrerequisiteInterningTests.Results);

        // Act
        _ = await PostgreSqlIndexPrerequisiteInterningTests.ReadPrerequisitesAsync(context, capture, operations);
        var submission = capture.Submissions[0];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + submission.Sql, connection);
        foreach (var parameter in submission.Parameters)
        {
            command.Parameters.AddWithValue(parameter.ParameterName, parameter.Value ?? DBNull.Value);
        }

        using var plan = System.Text.Json.JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("lazy-native-inputs"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.DoesNotContain(Plans(plan.RootElement[0].GetProperty("Plan")), static node =>
            node.TryGetProperty("Subplan Name", out var name)
            && name.GetString()!.StartsWith("CTE _doka_sm_index_input_", StringComparison.Ordinal));
        Assert.DoesNotContain("SELECT 1 FROM \"_doka_sm_index_input_0\"", submission.Sql, StringComparison.Ordinal);
    }

    /// <summary>A later zero-row expression owner cannot be shadowed by an earlier generated proof alias.</summary>
    /// <param name="explicitSchema">Whether the native owner is independently schema-qualified.</param>
    /// <param name="cast">Whether its native expression is CAST rather than LOWER.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ComputedOwnerNamesRemainNativeRelations(bool explicitSchema, bool cast)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        const string owner = "_doka_sm_index_input_0";
        await ExecuteSqlAsync(connectionString, "CREATE TABLE \"" + owner
            + "\" (id integer, label text, payload json); CREATE INDEX ix_computed ON \"" + owner
            + "\" (" + (cast ? "(payload::text)" : "lower(label)") + ");");

        await using var context = CreateContext(connectionString);
        var migration = new MigrationBuilder(context.Database.ProviderName!);
        migration.CreateIndexIfNotExists("ix_physical", owner, ["id"]);
        migration.EnsureIndex(new ExpectedIndexDefinition("ix_computed", owner,
            [new ExpectedIndexKeyDefinition(structuredExpression: cast
                ? SafeMigrationSql.Cast(SafeMigrationSql.Identifier("payload"), "text")
                : SafeMigrationSql.Function("lower", SafeMigrationSql.Identifier("label")))],
            schema: explicitSchema ? "public" : null), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, migration.Operations,
            new SafeMigrationRunOptions("computed-owner-proof-alias"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
    }

    /// <summary>
    /// An independently different unique index is classified before unsupported grouping SQL can bind.
    /// </summary>
    /// <param name="jsonb">Whether the requested native grouping input has its ordinary btree contract.</param>
    /// <param name="replace">Whether an accepted earlier DROP makes the later CREATE newly executable.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DifferentUniqueIdentityRetainsSafeDuplicateProofBinding(bool jsonb, bool replace)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE input_members (id integer, payload "
            + (jsonb ? "jsonb" : "json") + "); CREATE UNIQUE INDEX ix_target ON input_members(id); "
            + "INSERT INTO input_members VALUES (1,'{}'),(2,'{}');");

        await using var context = CreateContext(connectionString);
        var migration = new MigrationBuilder(context.Database.ProviderName!);
        if (replace)
        {
            migration.DropIndexIfExists("ix_target", "input_members");
        }

        migration.CreateIndexIfNotExists("ix_target", "input_members", ["payload"], unique: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, migration.Operations,
            new SafeMigrationRunOptions("different-unique-input-binding"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(replace ? jsonb ? SafeMigrationAction.RejectDataBlocked : SafeMigrationAction.RejectUnsupported
            : SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
    }

    /// <summary>
    /// Compatible missing unique inputs retain actual row checks and native apply/verify/replay behavior.
    /// </summary>
    /// <param name="computed">Whether the key retains the separate existing computed-expression contract.</param>
    /// <param name="duplicate">Whether actual rows contain duplicate key values.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task QualifiedUniqueInputsRetainRowProofAndPostconditions(bool computed, bool duplicate)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE input_members (id integer, payload jsonb, label text);"
            + " INSERT INTO input_members VALUES (1,'{}','FIRST'),(2,'"
            + (duplicate ? "{}" : "{\"value\":2}") + "','" + (duplicate ? "first" : "second") + "');");

        await using var context = CreateContext(connectionString);
        var migration = new MigrationBuilder(context.Database.ProviderName!);
        migration.EnsureIndex(new ExpectedIndexDefinition("ix_target", "input_members",
            [computed ? new ExpectedIndexKeyDefinition(structuredExpression:
                SafeMigrationSql.Function("lower", SafeMigrationSql.Identifier("label")))
                : new ExpectedIndexKeyDefinition("payload")], unique: true), SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();
        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;

        // Act
        var report = await runner.AnalyzeAsync(context, migration.Operations,
            new SafeMigrationRunOptions("qualified-unique-row-proof"), CancellationToken.None);
        if (!duplicate)
        {
            await ExecuteOperationsAsync(context, migration.Operations);
            postflight = await runner.VerifyAsync(context, migration.Operations,
                new SafeMigrationRunOptions("qualified-unique-postflight"), CancellationToken.None);
            replay = await runner.AnalyzeAsync(context, migration.Operations,
                new SafeMigrationRunOptions("qualified-unique-replay"), CancellationToken.None);
        }

        // Assert
        Assert.Equal(duplicate ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(duplicate ? SafeMigrationAction.RejectDataBlocked : SafeMigrationAction.Apply,
            report.Assessments[0].Action);
        if (!duplicate)
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight!.Status);
            Assert.True(postflight.Assessments[0].PostconditionSatisfied);
            Assert.Equal(SafeMigrationAction.NoOp, replay!.Assessments[0].Action);
        }
    }

    /// <summary>Explicit operator classes retain native schema visibility and physical OID identity.</summary>
    /// <param name="hidden">Whether the native index uses the same-spelling non-visible class.</param>
    /// <param name="qualified">Whether the requested class explicitly names that hidden schema.</param>
    /// <param name="alias">Whether the native index is only a semantic alias rather than the exact symbol.</param>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public async Task ExplicitOperatorClassMatchesNativeVisibleIdentity(bool hidden, bool qualified, bool alias)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA hidden; CREATE OPERATOR CLASS hidden.int4_ops "
            + "FOR TYPE integer USING btree AS OPERATOR 1 <(integer,integer), OPERATOR 2 <=(integer,integer), "
            + "OPERATOR 3 =(integer,integer), OPERATOR 4 >=(integer,integer), OPERATOR 5 >(integer,integer), "
            + "FUNCTION 1 btint4cmp(integer,integer); CREATE TABLE input_members(id integer); CREATE INDEX "
            + (alias ? "ix_alias" : "ix_target") + " ON input_members(id "
            + (hidden ? "hidden.int4_ops" : "pg_catalog.int4_ops") + ");");

        await using var context = CreateContext(connectionString);
        var migration = new MigrationBuilder(context.Database.ProviderName!);
        migration.EnsureIndex(new ExpectedIndexDefinition("ix_target", "input_members",
            [new ExpectedIndexKeyDefinition("id", operatorClass: qualified ? "hidden.int4_ops" : "int4_ops")]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, migration.Operations,
            new SafeMigrationRunOptions("native-visible-operator-class"), CancellationToken.None);

        // Assert
        Assert.Equal(hidden && !qualified ? alias ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent
            : SafeMigrationAction.NoOp, report.Assessments[0].Action);
    }

    private static IEnumerable<System.Text.Json.JsonElement> Plans(System.Text.Json.JsonElement plan)
    {
        yield return plan;
        if (!plan.TryGetProperty("Plans", out var children))
        {
            yield break;
        }

        foreach (var child in children.EnumerateArray())
        {
            foreach (var nested in Plans(child))
            {
                yield return nested;
            }
        }
    }
}
