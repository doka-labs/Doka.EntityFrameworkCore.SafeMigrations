namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>A real historical target retains comment and collation when qualifying an unrelated DROP.</summary>
    [Fact]
    public async Task FacetedGeneratedPendingDropUsesCompleteHistoricalTarget()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await SeedFacetedPendingDropAsync(connectionString, "candidate-marker", "utf8mb4_bin");
        await using var context = new GeneratedFacetPendingDropDbContext(connectionString, Fixture.ServerVersion);
        var runner = context.GetService<ISafeMigrationRunner>();
        var operations = FacetedPendingDropOperations(context);

        // Act
        var preflight = await runner.AnalyzePendingMigrationsAsync(context,
            new SafeMigrationRunOptions("faceted-generated-pending-drop"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: CancellationToken.None);
            await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        });

        var completed = await runner.AnalyzePendingMigrationsAsync(context,
            new SafeMigrationRunOptions("faceted-generated-pending-drop-replay"), CancellationToken.None);

        var verified = await runner.VerifyAsync(context, operations,
            new SafeMigrationRunOptions("faceted-generated-pending-drop-verify"), CancellationToken.None);

        var applied = await context.Database.GetAppliedMigrationsAsync(CancellationToken.None);
        var historyRows = await FacetedPendingHistoryRowsAsync(connectionString);
        var unrelated = await FacetedPendingUnrelatedColumnsAsync(connectionString);
        var retained = await FacetedPendingRetainedRowsAsync(connectionString, includeUnrelated: false);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Null(failure);
        Assert.Equal(SafeMigrationReportStatus.NoOperations, completed.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, verified.Status);
        Assert.True(Assert.Single(verified.Assessments).PostconditionSatisfied);
        Assert.Equal(GeneratedFacetPendingDropDbContext.MigrationId, Assert.Single(applied));
        Assert.Equal(1, historyRows);
        Assert.Equal(0, unrelated);
        Assert.Equal(2, retained);
    }

    /// <summary>A complete historical target cannot hide native generated comment or collation drift.</summary>
    /// <param name="facet">The sole physical facet that differs from the historical target.</param>
    [Theory]
    [InlineData("comment")]
    [InlineData("collation")]
    public async Task FacetedGeneratedPendingDropRejectsNativeFacetDrift(string facet)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var comment = facet == "comment" ? "drift-marker" : "candidate-marker";
        var collation = facet == "collation" ? "utf8mb4_unicode_ci" : "utf8mb4_bin";
        await SeedFacetedPendingDropAsync(connectionString, comment, collation);
        await using var context = new GeneratedFacetPendingDropDbContext(connectionString, Fixture.ServerVersion);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzePendingMigrationsAsync(context,
            new SafeMigrationRunOptions("faceted-generated-pending-drift"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            context.GetService<IMigrator>().MigrateAsync(cancellationToken: CancellationToken.None));

        var historyRows = await FacetedPendingHistoryRowsAsync(connectionString);
        var unrelated = await FacetedPendingUnrelatedColumnsAsync(connectionString);
        var retained = await FacetedPendingRetainedRowsAsync(connectionString, includeUnrelated: true);
        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{GeneratedFacetPendingDropDbContext.TableName}' AND COLUMN_NAME = 'result' "
            + $"AND COLUMN_COMMENT = '{comment}' AND COLLATION_NAME = '{collation}';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal("generated_column_drop_unproven", Assert.Single(preflight.Assessments).AnalysisCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[0].Action);
        Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(0, historyRows);
        Assert.Equal(1, unrelated);
        Assert.Equal(2, retained);
        Assert.Equal(1, unchanged);
    }

    /// <summary>A runtime model with omitted facets cannot become a complete native dependency certificate.</summary>
    [Fact]
    public async Task FacetedGeneratedRuntimeOnlyDropRetainsUnknownFacets()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await SeedFacetedPendingDropAsync(connectionString, "candidate-marker", "utf8mb4_bin");
        await using var context = new GeneratedFacetPendingDropDbContext(connectionString, Fixture.ServerVersion);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", GeneratedFacetPendingDropDbContext.TableName);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("faceted-generated-runtime-unknown"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            foreach (var command in commands)
            {
                await command.ExecuteNonQueryAsync(context.GetService<IRelationalConnection>(),
                    cancellationToken: CancellationToken.None);
            }
        });

        var historyRows = await FacetedPendingHistoryRowsAsync(connectionString);
        var unrelated = await FacetedPendingUnrelatedColumnsAsync(connectionString);
        var retained = await FacetedPendingRetainedRowsAsync(connectionString, includeUnrelated: true);

        // Assert
        Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.RuntimeModel>(context.Model);
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal("generated_column_drop_unproven", Assert.Single(preflight.Assessments).AnalysisCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[0].Action);
        Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(0, historyRows);
        Assert.Equal(1, unrelated);
        Assert.Equal(2, retained);
    }

    /// <summary>Seeds native facets independently of EF's candidate model and migration target.</summary>
    private static Task SeedFacetedPendingDropAsync(string connectionString, string comment, string collation)
        => ExecuteSqlAsync(connectionString,
            $"CREATE TABLE `{GeneratedFacetPendingDropDbContext.TableName}` (id int NOT NULL PRIMARY KEY, "
            + "unrelated int NULL, result varchar(20) CHARACTER SET utf8mb4 COLLATE " + collation
            + " GENERATED ALWAYS AS ('fixed') VIRTUAL COMMENT '" + comment
            + "') ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + $"INSERT INTO `{GeneratedFacetPendingDropDbContext.TableName}` (id, unrelated) "
            + "VALUES (1, 7), (2, NULL);");

    /// <summary>Reads the actual migration fixture without constructing replacement operations.</summary>
    private static IReadOnlyList<MigrationOperation> FacetedPendingDropOperations(DbContext context)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.Migrations.Single(static pair =>
            pair.Key == GeneratedFacetPendingDropDbContext.MigrationId);

        return assembly.CreateMigration(migration.Value, context.Database.ProviderName!).UpOperations;
    }

    /// <summary>Counts applied rows without assuming EF has already bootstrapped its history table.</summary>
    private static async Task<int> FacetedPendingHistoryRowsAsync(string connectionString)
    {
        var exists = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{GeneratedFacetPendingDropDbContext.HistoryTable}';");

        return exists == 0 ? 0 : await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{GeneratedFacetPendingDropDbContext.HistoryTable}`;");
    }

    /// <summary>Checks that native DDL did or did not remove the unrelated member.</summary>
    private static Task<int> FacetedPendingUnrelatedColumnsAsync(string connectionString)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{GeneratedFacetPendingDropDbContext.TableName}' AND COLUMN_NAME = 'unrelated';");

    /// <summary>Checks row preservation, including unrelated values when the DROP is refused.</summary>
    private static Task<int> FacetedPendingRetainedRowsAsync(string connectionString, bool includeUnrelated)
        => ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{GeneratedFacetPendingDropDbContext.TableName}` "
            + "WHERE BINARY result = BINARY 'fixed' "
            + (includeUnrelated ? "AND ((id = 1 AND unrelated = 7) OR (id = 2 AND unrelated IS NULL));"
                : "AND id IN (1, 2);"));
}
