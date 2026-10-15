namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ColumnsCommentIdentity migration contracts in an independent class collection.</summary>
public sealed partial class MySqlColumnsCommentIdentityIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsCommentIdentityIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Column comment matching preserves case instead of inheriting catalog collation folding.</summary>
    /// <param name="exactComment">Whether the native comment is byte-identical to the target.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColumnCommentIdentityDistinguishesCaseOnlyDrift(bool exactComment)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var actualComment = exactComment ? "Alpha" : "alpha";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `comment_items` (`key` int NOT NULL PRIMARY KEY, "
            + "`required_value` int NULL COMMENT '" + actualComment + "') ENGINE=InnoDB; "
            + "INSERT INTO `comment_items` (`key`, `required_value`) VALUES (1, 7);");

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("comment_items",
                new ExpectedColumnDefinition("required_value", typeof(int), true, "int", comment: "Alpha")),
            SafeMigrationPolicy.RepairIfSafe);

        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var comparison = await ReadCommentIdentityComparisonAsync(connectionString, "comment_items");
        var report = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("column-comment-identity"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var final = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("column-comment-identity-final"), CancellationToken.None);

        var finalComparison = await ReadCommentIdentityComparisonAsync(connectionString, "comment_items");
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `comment_items` WHERE `key` = 1 AND `required_value` = 7;");

        // Assert
        Assert.Equal(exactComment || Fixture.IsMariaDb ? 1 : 0, comparison.CatalogCollationMatches);
        Assert.Equal(exactComment ? 1 : 0, comparison.ExactMatches);
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(exactComment ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(exactComment ? SafeMigrationAction.NoOp : SafeMigrationAction.Repair, assessment.Action);
        if (!exactComment)
        {
            Assert.Contains(assessment.Differences, difference => difference.Facet == "column_comment_digest");
        }

        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.Equal(1, finalComparison.ExactMatches);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>Strict table matching applies the same exact comment contract to each declared column.</summary>
    /// <param name="exactComment">Whether the native column comment has the requested exact case.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictTableColumnCommentIdentityRejectsCaseOnlyDrift(bool exactComment)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var actualComment = exactComment ? "Alpha" : "alpha";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `strict_comment_items` (`key` int NOT NULL PRIMARY KEY, "
            + "`required_value` int NULL COMMENT '" + actualComment + "') ENGINE=InnoDB; "
            + "INSERT INTO `strict_comment_items` (`key`, `required_value`) VALUES (1, 7);");

        var definition = new ExpectedTableDefinition("strict_comment_items",
            [
                new ExpectedColumnDefinition("key", typeof(int), false, "int"),
                new ExpectedColumnDefinition("required_value", typeof(int), true, "int", comment: "Alpha"),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "strict_comment_items", ["key"]));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(definition, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var comparison = await ReadCommentIdentityComparisonAsync(connectionString, "strict_comment_items");
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("strict-table-column-comment-identity"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var finalComparison = await ReadCommentIdentityComparisonAsync(connectionString, "strict_comment_items");
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `strict_comment_items` WHERE `key` = 1 AND `required_value` = 7;");

        // Assert
        Assert.Equal(exactComment || Fixture.IsMariaDb ? 1 : 0, comparison.CatalogCollationMatches);
        Assert.Equal(exactComment ? 1 : 0, comparison.ExactMatches);
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(exactComment ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(exactComment ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent, assessment.Action);
        if (exactComment)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(exception).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(comparison, finalComparison);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>Staged recovery cannot claim ownership when its nullable target comment differs only by case.</summary>
    /// <param name="exactComment">Whether the interrupted nullable stage has the exact target comment.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicRequiredAddCommentIdentityRequiresExactStagedContract(bool exactComment)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var operation = CommentIdentityRequiredAddOperation();
        var actualComment = exactComment ? "Alpha" : "alpha";
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddStagedTableSql(operation,
                AtomicRequiredAddGuardName(operation), "`required_value` IS NOT NULL", columnType: "int",
                columnSuffix: " COMMENT '" + actualComment + "'")
            + " INSERT INTO `atomic_required_items` (`key`, `required_value`) VALUES (1, 7);");

        await using var context = CreateContext(connectionString);
        var before = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Act
        var comparison = await ReadCommentIdentityComparisonAsync(connectionString, "atomic_required_items");
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-comment-identity"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var after = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var finalComparison = await ReadCommentIdentityComparisonAsync(connectionString, "atomic_required_items");
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `atomic_required_items` WHERE `key` = 1 AND `required_value` = 7;");

        // Assert
        Assert.Equal(new AtomicRequiredAddState(1, 1, 1, 1), before);
        Assert.Equal(exactComment || Fixture.IsMariaDb ? 1 : 0, comparison.CatalogCollationMatches);
        Assert.Equal(exactComment ? 1 : 0, comparison.ExactMatches);
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(exactComment ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(exactComment ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(exactComment ? "column_atomic_add_resume" : "column_atomic_add_guard_conflict",
            assessment.AnalysisCode);
        if (exactComment)
        {
            Assert.Null(exception);
            Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 1), after);
        }
        else
        {
            Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(exception).Message,
                StringComparison.Ordinal);
            Assert.Equal(before, after);
        }

        Assert.Equal(comparison, finalComparison);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>
    /// Captures the native required-column contract whose nullable stage retains the authored comment.
    /// </summary>
    /// <returns>The immutable anonymous target with explicit backfill safety metadata.</returns>
    private static SafeMigrationOperation CommentIdentityRequiredAddOperation()
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        builder.AddColumnIfNotExistsFromModel(
            operationFactory: static operations => operations.AddColumn<int>(name: "required_value",
                    table: "atomic_required_items", type: "int", nullable: false, comment: "Alpha")
                .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None));

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>
    /// Separates engine-specific catalog comparison from the required exact metadata contract.
    /// </summary>
    /// <param name="connectionString">The isolated native fixture connection.</param>
    /// <param name="table">The single fixture owner to inspect.</param>
    /// <returns>Separate ordinary-collation and byte-exact match counts for the same native comment.</returns>
    private static async Task<(int CatalogCollationMatches, int ExactMatches)> ReadCommentIdentityComparisonAsync(
        string connectionString,
        string table
    )
    {
        // WHY: MariaDB's catalog comparison masks case drift; the qualified
        // MySQL image already distinguishes it. Both forms inspect the same
        // row so exact fixtures cannot hide an engine-specific false match.
        var predicate = " FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = "
            + AtomicRequiredAddLiteral(table) + " AND COLUMN_NAME = 'required_value' AND ";

        var catalogMatches = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*)" + predicate + "COALESCE(COLUMN_COMMENT, '') = 'Alpha';");

        var exactMatches = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*)" + predicate
            + "CAST(COALESCE(COLUMN_COMMENT, '') AS BINARY) = 'Alpha';");

        return (catalogMatches, exactMatches);
    }
}
