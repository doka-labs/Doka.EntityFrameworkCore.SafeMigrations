namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Fences generated-member dependencies without changing ordinary index or CHECK removal.</summary>
public sealed partial class PostgreSqlGeneratedDropIntegrationTests : PostgreSqlIntegrationTestBase
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    /// <summary>Uses test-local databases on the shared native provider container.</summary>
    /// <param name="fixture">The assembly-owned native provider fixture.</param>
    /// <param name="output">Receives structural command counts, never hardware-dependent pass thresholds.</param>
    public PostgreSqlGeneratedDropIntegrationTests(
        PostgreSqlContainerFixture fixture, Xunit.Abstractions.ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    /// <summary>Standalone native dependency refusal precedes DDL, including PostgreSQL14 automatic edges.</summary>
    /// <param name="column">The source, generated, unrelated, or already missing target.</param>
    /// <param name="opaque">Whether the existing native generated expression is outside the parser grammar.</param>
    /// <param name="synchronous">Whether generated commands use the synchronous ADO execution path.</param>
    [Theory]
    [InlineData("source", false, false)]
    [InlineData("source", false, true)]
    [InlineData("source", true, false)]
    [InlineData("derived", false, false)]
    [InlineData("unrelated", false, false)]
    [InlineData("unrelated", true, false)]
    [InlineData("missing", false, false)]
    public async Task DropChecksNativeGeneratedDependencyBeforeMutation(
        string column, bool opaque, bool synchronous)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, opaque
            ? "CREATE FUNCTION generated_drop_value(integer) RETURNS integer LANGUAGE SQL IMMUTABLE "
                + "AS $$ SELECT $1 + 1 $$;" : "SELECT 1;");

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_drop (source integer NOT NULL, unrelated integer, "
            + "derived integer GENERATED ALWAYS AS ("
            + (opaque ? "generated_drop_value(source)" : "source + 1") + ") STORED, "
            + "CONSTRAINT ck_generated_drop CHECK(source >= 0)); "
            + "CREATE INDEX ix_generated_drop_source ON generated_drop(source); "
            + "INSERT INTO generated_drop(source, unrelated) VALUES(2,9);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(column, "generated_drop");
        var rejected = column == "source";

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-drop"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteDropAsync(context, builder.Operations, synchronous));
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='generated_drop'::regclass "
            + "AND attnum > 0 AND NOT attisdropped;");

        // Assert
        Assert.Equal(rejected || column == "missing" ? 3 : 2, columns);
        Assert.DoesNotContain(PostgreSqlSafeMigrationCatalogSqlBuilder.EmptyGeneratedDropSetCode,
            Assert.Single(report.Assessments).AnalysisCode, StringComparison.Ordinal);
        if (rejected)
        {
            Assert.Equal("P1001", Assert.IsType<PostgresException>(failure).SqlState);
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal("generated_column_drop_dependency", Assert.Single(report.Assessments).AnalysisCode);
            Assert.Equal("2:9:3", await ScalarStringAsync(connectionString,
                "SELECT source::text || ':' || unrelated::text || ':' || derived::text FROM generated_drop;"));
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
            Assert.Equal(column == "missing" ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply,
                Assert.Single(report.Assessments).Action);
            Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM generated_drop;"));
        }
    }

    /// <summary>Accepted generated definitions block removal of an initially absent source.</summary>
    /// <param name="fresh">Whether the stream creates the complete owner rather than adding one sparse member.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedGeneratedPrefixCannotBorrowInitialMissingForSourceDrop(bool fresh)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (!fresh)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_drop (unrelated integer);");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (fresh)
        {
            builder.CreateTableIfNotExists("generated_drop", table => new
            {
                source = table.Column<int>("integer", nullable: true),
                derived = table.Column<int>("integer", nullable: true,
                    computedColumnSql: "source + 1", stored: true),
            });
        }
        else
        {
            builder.AddColumnIfNotExists<int>("source", "generated_drop", type: "integer", nullable: true);
            builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
                "derived", "generated_drop", type: "integer", nullable: true,
                computedColumnSql: "source", stored: true));
        }

        builder.DropColumnIfExists("source", "generated_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-prefix-drop"), CancellationToken.None);

        // Assert
        Assert.True(report.Assessments.Take(report.Assessments.Count - 1).All(
                static assessment => assessment.Action == SafeMigrationAction.Apply),
            string.Join("; ", report.Assessments.Select(static assessment =>
                assessment.Action + ":" + assessment.AnalysisCode)));
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
        Assert.Equal("generated_column_drop_dependency", report.Assessments[^1].AnalysisCode);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='generated_drop' "
            + "AND column_name IN ('source','derived');"));
    }

    /// <summary>Explicit generated-member removal discharges only its current source dependency.</summary>
    /// <param name="complete">Whether an initial strict matching declaration establishes the complete owner.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedMemberDropThenSourceDropConvergesAndReplays(bool complete)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_drop (source integer, unrelated integer, "
            + "derived integer GENERATED ALWAYS AS (source + 1) STORED); "
            + "INSERT INTO generated_drop(source,unrelated) VALUES(2,9);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (complete)
        {
            builder.CreateTableIfNotExists("generated_drop", table => new
            {
                source = table.Column<int>("integer", nullable: true),
                unrelated = table.Column<int>("integer", nullable: true),
                derived = table.Column<int>("integer", nullable: true,
                    computedColumnSql: "source + 1", stored: true),
            });
        }

        builder.DropColumnIfExists("derived", "generated_drop");
        builder.DropColumnIfExists("source", "generated_drop");
        var replay = new MigrationBuilder(context.Database.ProviderName!);
        replay.DropColumnIfExists("derived", "generated_drop");
        replay.DropColumnIfExists("source", "generated_drop");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-dependent-first-drop"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var repeated = await runner.AnalyzeAsync(context, replay.Operations,
            new SafeMigrationRunOptions("generated-dependent-first-drop-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, replay.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.All(repeated.Assessments,
            static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(9, await ScalarIntAsync(connectionString, "SELECT unrelated FROM generated_drop;"));
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='generated_drop'::regclass "
            + "AND attnum>0 AND NOT attisdropped;"));
    }

    /// <summary>An initial zero-generated snapshot cannot erase a member added before an unrelated DROP.</summary>
    [Fact]
    public async Task NativeEmptySetCertificateCannotOverwriteEarlierGeneratedAdd()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_drop (source integer, unrelated integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "derived", "generated_drop", type: "integer", nullable: true,
            computedColumnSql: "source+1", stored: true));

        builder.DropColumnIfExists("unrelated", "generated_drop");
        builder.DropColumnIfExists("source", "generated_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-empty-set-expired-prefix"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
        Assert.Equal("generated_column_drop_dependency", report.Assessments[^1].AnalysisCode);
        Assert.Equal(2, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='generated_drop'::regclass "
            + "AND attnum>0 AND NOT attisdropped;"));
    }

    /// <summary>Runs the same generated script through either native command API without requiring preflight.</summary>
    private static async Task ExecuteDropAsync(
        DbContext context, IReadOnlyList<MigrationOperation> operations, bool synchronous)
    {
        if (!synchronous)
        {
            await ExecuteOperationsAsync(context, operations);

            return;
        }

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        foreach (var command in commands)
        {
            using var native = connection.CreateCommand();
            native.CommandText = command.CommandText;
            native.ExecuteNonQuery();
        }
    }
}
