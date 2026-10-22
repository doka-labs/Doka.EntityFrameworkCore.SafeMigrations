namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Native CHECK members use their own comparer without folding literal or numeric source.</summary>
    [GeneratedMariaDbFact]
    public async Task CapturedCheckRenameUsesNativeColumnIdentityAndPreservesOtherSourceRoles()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        const string originalSql = "leftbound >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'";
        const string renamedSql = "score >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'";
        var original = SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
        {
            Name = "ck_member_identity", Table = Table, Sql = originalSql,
        });

        var renamed = SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
        {
            Name = "ck_member_identity", Table = Table, Sql = renamedSql,
        });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition(Table,
                [new ExpectedColumnDefinition("LeftBound", typeof(int), false, "int")], checkConstraints: [original]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameColumnIfExists("LeftBound", Table, "score");
        builder.EnsureCheckConstraint(renamed, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-check-column-identity"), CancellationToken.None);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-check-column-identity-verify"), CancellationToken.None);

        // Assert
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        Assert.True(failure is null, failure?.ToString());
        Assert.True(postflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(postflight));
        Assert.Equal(originalSql, original.CapturedSql);
        Assert.Equal(renamedSql, renamed.CapturedSql);
    }
}
