namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the MemberRenameEvidence migration contracts in an independent class collection.</summary>
public sealed partial class MySqlMemberRenameEvidenceIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlMemberRenameEvidenceIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>A brownfield source created earlier cannot overwrite a live owner-local destination.</summary>
    /// <param name="index">Whether an index rather than a column is renamed.</param>
    /// <param name="sourceExists">Whether the source already exists in the immutable live catalog.</param>
    /// <param name="targetExists">Whether the destination already exists in the same physical owner.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task MemberRename_BrownfieldCreationUsesIndependentDestination(
        bool index,
        bool sourceExists,
        bool targetExists
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var source = index ? "source_index" : "source_value";
        var target = index ? "target_index" : "target_value";
        await ExecuteSqlAsync(connectionString, "CREATE TABLE member_rows (id int NOT NULL, decoy int NULL"
            + (!index && sourceExists ? ", source_value int NULL" : string.Empty)
            + (!index && targetExists ? ", target_value int NULL" : string.Empty) + "); "
            + "INSERT INTO member_rows (id) VALUES (7); "
            + (index && sourceExists ? "CREATE INDEX source_index ON member_rows (id); " : string.Empty)
            + (index && targetExists ? "CREATE INDEX target_index ON member_rows (decoy); " : string.Empty));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (index)
        {
            builder.EnsureIndex(new ExpectedIndexDefinition(source, "member_rows",
                [new ExpectedIndexKeyDefinition("id")]), SafeMigrationPolicy.ThrowIfDifferent);
            builder.RenameIndexIfExists(source, "member_rows", target);
        }
        else
        {
            builder.EnsureColumn("member_rows", new ExpectedColumnDefinition(source, typeof(int), true, "int"),
                policy: SafeMigrationPolicy.ThrowIfDifferent);
            builder.RenameColumnIfExists(source, "member_rows", target);
        }

        var rename = (SafeMigrationOperation)builder.Operations[^1];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-brownfield"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var targetCount = await ScalarIntAsync(connectionString, index
            ? "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'member_rows' AND INDEX_NAME = 'target_index';"
            : "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'member_rows' AND COLUMN_NAME = 'target_value';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM member_rows WHERE id = 7;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(targetExists, analysis.RenameTargetExists);
        Assert.Equal(index ? sourceExists : (bool?)null, analysis.RenameSourceSupported);
        Assert.Equal(!sourceExists ? SafeMigrationObservedState.Missing
            : targetExists ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            analysis.ObservedState);
        Assert.Equal(targetExists ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(targetExists ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
        if (targetExists)
        {
            AssertOwnedGuardFailure(failure);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, targetCount);
        Assert.Equal(1, rows);
    }

    /// <summary>Absent-source replay remains NoOp even when the live destination exists.</summary>
    /// <param name="index">Whether an index rather than a column is renamed.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRename_AbsentSourceReplayPreservesDestination(bool index)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id int NOT NULL, target_value int NULL); "
            + "INSERT INTO member_rows VALUES (7, 29); CREATE INDEX target_index ON member_rows (id);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (index)
        {
            builder.RenameIndexIfExists("source_index", "member_rows", "target_index");
        }
        else
        {
            builder.RenameColumnIfExists("source_value", "member_rows", "target_value");
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-replay"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM member_rows WHERE target_value = 29;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, rows);
    }

    /// <summary>An identically named index on another table does not occupy this destination.</summary>
    [Fact]
    public async Task MemberRename_IndexDestinationRemainsOwnerLocal()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id int NOT NULL); CREATE TABLE other_rows (id int NOT NULL); "
            + "CREATE INDEX target_index ON other_rows (id);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("source_index", "member_rows",
            [new ExpectedIndexKeyDefinition("id")]), SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameIndexIfExists("source_index", "member_rows", "target_index");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-owner-local"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var targets = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT TABLE_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND INDEX_NAME = 'target_index';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Equal(2, targets);
    }

    /// <summary>Native case-insensitive member names do not manufacture a free destination.</summary>
    /// <param name="index">Whether the member is an index rather than a column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRename_CaseVariantDestinationIsOccupied(bool index)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id int NOT NULL, decoy int NULL, Target_Value int NULL); "
            + "CREATE INDEX Target_Index ON member_rows (decoy);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (index)
        {
            builder.EnsureIndex(new ExpectedIndexDefinition("source_index", "member_rows",
                [new ExpectedIndexKeyDefinition("id")]), SafeMigrationPolicy.ThrowIfDifferent);
            builder.RenameIndexIfExists("source_index", "member_rows", "target_index");
        }
        else
        {
            builder.EnsureColumn("member_rows", new ExpectedColumnDefinition("source_value", typeof(int), true, "int"),
                policy: SafeMigrationPolicy.ThrowIfDifferent);
            builder.RenameColumnIfExists("source_value", "member_rows", "target_value");
        }

        var rename = (SafeMigrationOperation)builder.Operations[^1];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-native-case"));

        // Assert
        Assert.True(Assert.Single(analyses).RenameTargetExists);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
    }

    /// <summary>A target drop requires an independent source rather than a reserved PRIMARY identity.</summary>
    /// <param name="primary">Whether the source is the table's reserved PRIMARY index.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRename_TargetDropRequiresIndependentlySupportedSource(bool primary)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var source = primary ? "PRIMARY" : "source_index";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id int NOT NULL, decoy int NULL, PRIMARY KEY (id)); "
            + (primary ? string.Empty : "CREATE INDEX source_index ON member_rows (id); ")
            + "CREATE INDEX target_index ON member_rows (decoy);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("target_index", "member_rows");
        builder.RenameIndexIfExists(source, "member_rows", "target_index");
        var rename = (SafeMigrationOperation)builder.Operations[^1];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-source-kind"));

        if (!primary)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var primaryCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'member_rows' AND INDEX_NAME = 'PRIMARY';");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.True(analysis.RenameTargetExists);
        Assert.Equal(!primary, analysis.RenameSourceSupported);
        Assert.Equal(primary ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(primary ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
        Assert.Equal(1, primaryCount);
    }

    /// <summary>A source created earlier cannot rename an ordinary index into the reserved PRIMARY role.</summary>
    /// <param name="sourceExists">Whether the source exists before ordered projection.</param>
    /// <param name="reservedTarget">Whether the authored destination is reserved despite no live primary key.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MemberRename_CreatedSourceRetainsAuthoredDestinationRole(
        bool sourceExists,
        bool reservedTarget
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var target = reservedTarget ? "PRIMARY" : "destination_index";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id int NOT NULL); INSERT INTO member_rows VALUES (7); "
            + (sourceExists ? "CREATE INDEX source_index ON member_rows (id);" : string.Empty));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("source_index", "member_rows",
            [new ExpectedIndexKeyDefinition("id")]), SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameIndexIfExists("source_index", "member_rows", target);
        var rename = (SafeMigrationOperation)builder.Operations[^1];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-reserved-destination"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var sourceCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'member_rows' AND INDEX_NAME = 'source_index';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM member_rows WHERE id = 7;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.False(analysis.RenameTargetExists);
        Assert.Equal(sourceExists, analysis.RenameSourceSupported);
        Assert.Equal(!reservedTarget, analysis.RenameTargetSupported);
        Assert.Equal(reservedTarget ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(reservedTarget ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
        if (reservedTarget)
        {
            Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(failure).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(reservedTarget ? 1 : 0, sourceCount);
        Assert.Equal(1, rows);
    }

    /// <summary>Missing-source replay stays NoOp for a reserved destination and preserves its primary key.</summary>
    [Fact]
    public async Task MemberRename_ReservedDestinationMissingSourceReplayRemainsNoOp()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id int NOT NULL, PRIMARY KEY (id)); INSERT INTO member_rows VALUES (7);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameIndexIfExists("source_index", "member_rows", "PRIMARY");
        var rename = (SafeMigrationOperation)builder.Operations[0];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-reserved-replay"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var primaryCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'member_rows' AND INDEX_NAME = 'PRIMARY';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM member_rows WHERE id = 7;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.True(analysis.RenameTargetExists);
        Assert.False(analysis.RenameTargetSupported);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Null(failure);
        Assert.Equal(1, primaryCount);
        Assert.Equal(1, rows);
    }
}
