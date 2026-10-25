namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlGeneratedDropIntegrationTests
{
    /// <summary>
    /// The current owner lifetime, not a historical native dependent, determines the ordered source DROP.
    /// </summary>
    [Fact]
    public async Task RecreatedOwnerDoesNotRetainHistoricalGeneratedDependency()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_drop(source integer, derived integer GENERATED ALWAYS AS(source+1) STORED);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("generated_drop");
        builder.CreateTableIfNotExists("generated_drop", table => new
        {
            source = table.Column<int>("integer", nullable: true),
            unrelated = table.Column<int>("integer", nullable: true),
        });

        builder.DropColumnIfExists("source", "generated_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-owner-recreated"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='generated_drop'::regclass "
            + "AND attnum>0 AND NOT attisdropped;"));
    }

    /// <summary>
    /// A new accepted generated lifetime reacquires its dependency after the earlier member was removed.
    /// </summary>
    [Fact]
    public async Task ReaddedGeneratedMemberReacquiresSourceDependency()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_drop(source integer, unrelated integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendGeneratedSourceCopy(builder);
        builder.DropColumnIfExists("derived", "generated_drop");
        AppendGeneratedSourceCopy(builder);
        builder.DropColumnIfExists("source", "generated_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-member-readded"), CancellationToken.None);

        // Assert
        Assert.All(report.Assessments.Take(3),
            static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
        Assert.Equal("generated_column_drop_dependency", report.Assessments[^1].AnalysisCode);
        Assert.Equal(2, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='generated_drop'::regclass "
            + "AND attnum>0 AND NOT attisdropped;"));
    }

    /// <summary>
    /// Equal source names in another physical owner cannot become dependencies of the requested owner.
    /// </summary>
    [Fact]
    public async Task GeneratedDependencyProofRetainsExactOwnerIdentity()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA other_owner; "
            + "CREATE TABLE public.generated_drop(source integer, unrelated integer); "
            + "CREATE TABLE other_owner.generated_drop(source integer, "
            + "derived integer GENERATED ALWAYS AS(source+1) STORED); "
            + "INSERT INTO public.generated_drop VALUES(2,9); "
            + "INSERT INTO other_owner.generated_drop(source) VALUES(2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("source", "generated_drop", schema: "public");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-exact-owner"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(9, await ScalarIntAsync(connectionString, "SELECT unrelated FROM public.generated_drop;"));
        Assert.Equal(3, await ScalarIntAsync(connectionString, "SELECT derived FROM other_owner.generated_drop;"));
    }

    /// <summary>A rejected generated proposal cannot create a dependency in the accepted current prefix.</summary>
    [Fact]
    public async Task RejectedGeneratedPrefixCannotInventDropDependency()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_drop(source integer, unrelated integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "derived", "generated_drop", type: "integer", nullable: true,
            computedColumnSql: "CURRENT_TIMESTAMP", stored: true));

        builder.DropColumnIfExists("source", "generated_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-rejected-prefix"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal("classified_matching", report.Assessments[1].AnalysisCode);
    }

    /// <summary>
    /// A safe index removal preserves generated-set freshness without erasing a real generated member.
    /// </summary>
    /// <param name="generated">Whether the independently created owner has a generated source dependent.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexRemovalDoesNotInventOrEraseGeneratedDependency(bool generated)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_drop(source integer, unrelated integer"
            + (generated ? ", derived integer GENERATED ALWAYS AS(source+1) STORED" : "")
            + "); CREATE INDEX ix_drop_source ON generated_drop(source); "
            + "INSERT INTO generated_drop(source,unrelated) VALUES(2,9);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_drop_source", "generated_drop");
        builder.DropColumnIfExists("source", "generated_drop");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-index-prefix"), CancellationToken.None);

        if (report.Status == SafeMigrationReportStatus.Ready)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(generated ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply,
            report.Assessments[1].Action);
        Assert.Equal(generated ? "generated_column_drop_dependency" : "classified_matching",
            report.Assessments[1].AnalysisCode);
        Assert.Equal(9, await ScalarIntAsync(connectionString, "SELECT unrelated FROM generated_drop;"));
    }

    /// <summary>
    /// Appends a captured builtin source reference without introducing another parsing or ownership path.
    /// </summary>
    private static void AppendGeneratedSourceCopy(MigrationBuilder builder)
        => builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "derived", "generated_drop", type: "integer", nullable: true,
            computedColumnSql: "source", stored: true));
}
