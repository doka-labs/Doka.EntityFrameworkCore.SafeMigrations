namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises rename source absence with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationRenameSourceAbsenceIntegrationTests : SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>A missing table source satisfies an idempotent rename even without a destination.</summary>
    [Fact]
    public async Task TableRenameWithBothNamesMissing_SatisfiesSourceAbsencePostflight()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("absent_source", "absent_target");
        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(
            connection,
            "PRAGMA schema_version;",
            cancellationToken: CancellationToken.None);

        // Act
        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-table-rename-absent-source"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-table-rename-absent-postflight"),
            cancellationToken: CancellationToken.None);

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var finalSchemaVersion = await ScalarIntAsync(
            connection,
            "PRAGMA schema_version;",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.True(assessment.PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments)
            .PostconditionSatisfied);

        Assert.DoesNotContain("absent_source", snapshot.Tables.Keys);
        Assert.DoesNotContain("absent_target", snapshot.Tables.Keys);
        Assert.Equal(schemaVersion, finalSchemaVersion);
    }

    /// <summary>
    /// Missing member sources satisfy rename postflight without manufacturing their destinations or owners.
    /// </summary>
    /// <param name="operationKind">The member rename operation whose source is absent.</param>
    /// <param name="ownerExists">Whether the owner exists while both member names are absent.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.RenameColumn, false)]
    [InlineData(SafeMigrationOperationKind.RenameColumn, true)]
    [InlineData(SafeMigrationOperationKind.RenameIndex, false)]
    [InlineData(SafeMigrationOperationKind.RenameIndex, true)]
    public async Task MemberRenameWithBothNamesMissing_SatisfiesSourceAbsencePostflight(
        SafeMigrationOperationKind operationKind,
        bool ownerExists
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        if (ownerExists)
        {
            await ExecuteSqlAsync(
                connection,
                "CREATE TABLE absent_member_owner (Id INTEGER NOT NULL); "
                + "INSERT INTO absent_member_owner (Id) VALUES (1);",
                cancellationToken: CancellationToken.None);
        }

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (operationKind == SafeMigrationOperationKind.RenameColumn)
        {
            builder.RenameColumnIfExists("absent_source", "absent_member_owner", "absent_target");
        }
        else
        {
            builder.RenameIndexIfExists("absent_source", "absent_member_owner", "absent_target");
        }

        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(
            connection,
            "PRAGMA schema_version;",
            cancellationToken: CancellationToken.None);

        // Act
        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-member-rename-absent-source"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-member-rename-absent-postflight"),
            cancellationToken: CancellationToken.None);

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var finalSchemaVersion = await ScalarIntAsync(
            connection,
            "PRAGMA schema_version;",
            cancellationToken: CancellationToken.None);

        var remainingRows = ownerExists
            ? await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM absent_member_owner WHERE Id = 1;",
                cancellationToken: CancellationToken.None)
            : 0;

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.True(assessment.PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments)
            .PostconditionSatisfied);

        Assert.Equal(ownerExists, snapshot.Tables.ContainsKey("absent_member_owner"));
        Assert.Equal(
            ownerExists
                ? 1
                : 0,
            remainingRows);

        Assert.Equal(schemaVersion, finalSchemaVersion);
    }
}
