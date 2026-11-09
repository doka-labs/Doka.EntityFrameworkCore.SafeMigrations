namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>A real historical target retains comment and collation when qualifying an unrelated DROP.</summary>
    [Fact]
    public async Task FacetedGeneratedPendingDropUsesCompleteHistoricalTarget()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await SeedFacetedPendingDropAsync(
            connectionString,
            "candidate-marker",
            "utf8mb4_bin",
            cancellationToken: CancellationToken.None);

        await using var context = new GeneratedFacetPendingDropDbContext(connectionString, Fixture.ServerVersion);
        var runner = context.GetService<ISafeMigrationRunner>();
        var operations = FacetedPendingDropOperations(context);

        // Act
        var preflight = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("faceted-generated-pending-drop"),
            CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await context
                .GetService<IMigrator>()
                .MigrateAsync(cancellationToken: CancellationToken.None);

            await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        });

        var completed = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("faceted-generated-pending-drop-replay"),
            CancellationToken.None);

        var verified = await runner.VerifyAsync(
            context,
            operations,
            new SafeMigrationRunOptions("faceted-generated-pending-drop-verify"),
            CancellationToken.None);

        var applied = await context.Database.GetAppliedMigrationsAsync(CancellationToken.None);
        var historyRows = await FacetedPendingHistoryRowsAsync(
            connectionString,
            cancellationToken: CancellationToken.None);

        var unrelated = await FacetedPendingUnrelatedColumnsAsync(
            connectionString,
            cancellationToken: CancellationToken.None);

        var retained = await FacetedPendingRetainedRowsAsync(
            connectionString,
            includeUnrelated: false,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply,
            Assert.Single(preflight.Assessments)
                .Action);

        Assert.Null(failure);
        Assert.Equal(SafeMigrationReportStatus.NoOperations, completed.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, verified.Status);
        Assert.True(Assert.Single(verified.Assessments)
            .PostconditionSatisfied);

        Assert.Equal(GeneratedFacetPendingDropDbContext.MigrationId, Assert.Single(applied));
        Assert.Equal(1, historyRows);
        Assert.Equal(0, unrelated);
        Assert.Equal(2, retained);
    }

    /// <summary>Unrelated facet drift does not invalidate a separate native source-independence certificate.</summary>
    /// <param name="facet">The sole physical facet that differs from the historical target.</param>
    [Theory]
    [InlineData("comment")]
    [InlineData("collation")]
    public async Task FacetedGeneratedPendingDropPreservesNativeFacetDrift(
        string facet
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var comment = facet == "comment"
            ? "drift-marker"
            : "candidate-marker";

        var collation = facet == "collation"
            ? "utf8mb4_unicode_ci"
            : "utf8mb4_bin";

        await SeedFacetedPendingDropAsync(
            connectionString,
            comment,
            collation,
            cancellationToken: CancellationToken.None);

        await using var context = new GeneratedFacetPendingDropDbContext(connectionString, Fixture.ServerVersion);

        // Act
        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzePendingMigrationsAsync(
                context,
                new SafeMigrationRunOptions("faceted-generated-pending-drift"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            context
                .GetService<IMigrator>()
                .MigrateAsync(cancellationToken: CancellationToken.None));

        var historyRows = await FacetedPendingHistoryRowsAsync(
            connectionString,
            cancellationToken: CancellationToken.None);

        var unrelated = await FacetedPendingUnrelatedColumnsAsync(
            connectionString,
            cancellationToken: CancellationToken.None);

        var retained = await FacetedPendingRetainedRowsAsync(
            connectionString,
            includeUnrelated: !Fixture.IsMariaDb,
            cancellationToken: CancellationToken.None);

        var unchanged = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{GeneratedFacetPendingDropDbContext.TableName}' AND COLUMN_NAME = 'result' "
            + $"AND COLUMN_COMMENT = '{comment}' AND COLLATION_NAME = '{collation}';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            Fixture.IsMariaDb
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked,
            preflight.Status);

        Assert.Equal(
            Fixture.IsMariaDb
                ? SafeMigrationAction.Apply
                : SafeMigrationAction.RejectUnsupported,
            Assert.Single(preflight.Assessments)
                .Action);

        if (Fixture.IsMariaDb)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("generated_column_drop_unproven", preflight.Assessments[0].AnalysisCode);
            Assert.Equal("doka_sm_unsupported",
                Assert.IsType<SafeMigrationExecutionException>(failure)
                    .GuardCode);
        }

        Assert.Equal(
            Fixture.IsMariaDb
                ? 1
                : 0,
            historyRows);

        Assert.Equal(
            Fixture.IsMariaDb
                ? 0
                : 1,
            unrelated);

        Assert.Equal(2, retained);
        Assert.Equal(1, unchanged);
    }

    /// <summary>Unknown runtime facets stay unknown while MariaDB separately proves source independence.</summary>
    [Fact]
    public async Task FacetedGeneratedRuntimeOnlyDropRetainsUnknownFacets()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await SeedFacetedPendingDropAsync(
            connectionString,
            "candidate-marker",
            "utf8mb4_bin",
            cancellationToken: CancellationToken.None);

        await using var context = new GeneratedFacetPendingDropDbContext(connectionString, Fixture.ServerVersion);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", GeneratedFacetPendingDropDbContext.TableName);
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        // Act
        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("faceted-generated-runtime-unknown"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            foreach (var command in commands)
            {
                await command.ExecuteNonQueryAsync(
                    context.GetService<IRelationalConnection>(),
                    cancellationToken: CancellationToken.None);
            }
        });

        var historyRows = await FacetedPendingHistoryRowsAsync(
            connectionString,
            cancellationToken: CancellationToken.None);

        var unrelated = await FacetedPendingUnrelatedColumnsAsync(
            connectionString,
            cancellationToken: CancellationToken.None);

        var retained = await FacetedPendingRetainedRowsAsync(
            connectionString,
            includeUnrelated: !Fixture.IsMariaDb,
            cancellationToken: CancellationToken.None);

        var unchanged = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{GeneratedFacetPendingDropDbContext.TableName}' AND COLUMN_NAME = 'result' "
            + "AND COLUMN_COMMENT = 'candidate-marker' AND COLLATION_NAME = 'utf8mb4_bin';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.IsAssignableFrom<RuntimeModel>(context.Model);
        Assert.Equal(
            Fixture.IsMariaDb
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked,
            preflight.Status);

        Assert.Equal(
            Fixture.IsMariaDb
                ? SafeMigrationAction.Apply
                : SafeMigrationAction.RejectUnsupported,
            Assert.Single(preflight.Assessments)
                .Action);

        if (Fixture.IsMariaDb)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("generated_column_drop_unproven", preflight.Assessments[0].AnalysisCode);
            Assert.Equal("doka_sm_unsupported",
                Assert.IsType<SafeMigrationExecutionException>(failure)
                    .GuardCode);
        }

        Assert.Equal(0, historyRows);
        Assert.Equal(
            Fixture.IsMariaDb
                ? 0
                : 1,
            unrelated);

        Assert.Equal(2, retained);
        Assert.Equal(1, unchanged);
    }

    /// <summary>Seeds native facets independently of EF's candidate model and migration target.</summary>
    /// <param name="connectionString">The test database receiving the native generated-column fixture.</param>
    /// <param name="comment">The trusted fixture comment written on the generated column.</param>
    /// <param name="collation">The native collation token selected by the test for the generated column.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static Task SeedFacetedPendingDropAsync(
        string connectionString,
        string comment,
        string collation,
        CancellationToken cancellationToken
    ) => ExecuteSqlAsync(
        connectionString,
        $"CREATE TABLE `{GeneratedFacetPendingDropDbContext.TableName}` (id int NOT NULL PRIMARY KEY, "
        + "unrelated int NULL, result varchar(20) CHARACTER SET utf8mb4 COLLATE "
        + collation
        + " GENERATED ALWAYS AS ('fixed') VIRTUAL COMMENT '"
        + comment
        + "') ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
        + $"INSERT INTO `{GeneratedFacetPendingDropDbContext.TableName}` (id, unrelated) "
        + "VALUES (1, 7), (2, NULL);",
        cancellationToken: cancellationToken);

    /// <summary>Reads the actual migration fixture without constructing replacement operations.</summary>
    private static IReadOnlyList<MigrationOperation> FacetedPendingDropOperations(
        DbContext context
    )
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.Migrations.Single(static pair =>
            pair.Key == GeneratedFacetPendingDropDbContext.MigrationId);

        return assembly.CreateMigration(migration.Value, context.Database.ProviderName!)
            .UpOperations;
    }

    /// <summary>Counts applied rows without assuming EF has already bootstrapped its history table.</summary>
    /// <param name="connectionString">The test database whose migration history is counted.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task<int> FacetedPendingHistoryRowsAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var exists = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + $"AND TABLE_NAME = '{GeneratedFacetPendingDropDbContext.HistoryTable}';",
            cancellationToken: cancellationToken);

        return exists == 0
            ? 0
            : await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `{GeneratedFacetPendingDropDbContext.HistoryTable}`;",
                cancellationToken: cancellationToken);
    }

    /// <summary>Checks that native DDL did or did not remove the unrelated member.</summary>
    /// <param name="connectionString">The test database whose unrelated column is located in the catalog.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static Task<int> FacetedPendingUnrelatedColumnsAsync(
        string connectionString,
        CancellationToken cancellationToken
    ) => ScalarIntAsync(
        connectionString,
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
        + $"AND TABLE_NAME = '{GeneratedFacetPendingDropDbContext.TableName}' AND COLUMN_NAME = 'unrelated';",
        cancellationToken: cancellationToken);

    /// <summary>Checks row preservation, including unrelated values when the DROP is refused.</summary>
    /// <param name="connectionString">The test database whose original rows are checked.</param>
    /// <param name="includeUnrelated">Whether retained rows must also preserve the unrelated column's values.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static Task<int> FacetedPendingRetainedRowsAsync(
        string connectionString,
        bool includeUnrelated,
        CancellationToken cancellationToken
    ) => ScalarIntAsync(
        connectionString,
        $"SELECT COUNT(*) FROM `{GeneratedFacetPendingDropDbContext.TableName}` "
        + "WHERE BINARY result = BINARY 'fixed' "
        + (includeUnrelated
            ? "AND ((id = 1 AND unrelated = 7) OR (id = 2 AND unrelated IS NULL));"
            : "AND id IN (1, 2);"),
        cancellationToken: cancellationToken);
}
