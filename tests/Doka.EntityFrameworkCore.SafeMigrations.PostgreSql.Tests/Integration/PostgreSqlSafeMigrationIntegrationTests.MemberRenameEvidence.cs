namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>A source created on a brownfield table cannot overwrite an occupied destination.</summary>
    /// <param name="index">Whether an index rather than a column is renamed.</param>
    /// <param name="sourceExists">Whether the source exists in the immutable catalog.</param>
    /// <param name="targetExists">Whether the destination exists in its native namespace.</param>
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
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var source = index ? "source_index" : "source_value";
        var target = index ? "target_index" : "target_value";
        await ExecuteSqlAsync(connectionString, "CREATE TABLE member_rows (id integer NOT NULL, decoy integer NULL"
            + (!index && sourceExists ? ", source_value integer NULL" : string.Empty)
            + (!index && targetExists ? ", target_value integer NULL" : string.Empty) + "); "
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
            builder.EnsureColumn("member_rows", new ExpectedColumnDefinition(source, typeof(int), true, "integer"),
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
            ? "SELECT COUNT(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = 'public' AND c.relname = 'target_index';"
            : "SELECT COUNT(*) FROM pg_catalog.pg_attribute a "
                + "WHERE a.attrelid = 'public.member_rows'::regclass AND a.attname = 'target_value' "
                + "AND a.attnum > 0 AND NOT a.attisdropped;");

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
            Assert.IsType<PostgresException>(failure);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, targetCount);
        Assert.Equal(1, rows);
    }

    /// <summary>Index destinations include other owners and non-index relations in the same schema.</summary>
    /// <param name="tableOccupant">Whether a table rather than an index occupies the schema-global name.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRename_IndexDestinationUsesSchemaGlobalNamespace(bool tableOccupant)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE member_rows (id integer NOT NULL); "
            + "CREATE TABLE other_rows (id integer NOT NULL); "
            + (tableOccupant ? "CREATE TABLE target_index (id integer NOT NULL);"
                : "CREATE INDEX target_index ON other_rows (id);"));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("source_index", "member_rows",
            [new ExpectedIndexKeyDefinition("id")]), SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameIndexIfExists("source_index", "member_rows", "target_index");
        var rename = (SafeMigrationOperation)builder.Operations[^1];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-schema-global"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var ownerExists = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_class WHERE oid = 'public.other_rows'::regclass;");

        // Assert
        Assert.True(Assert.Single(analyses).RenameTargetExists);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
        Assert.Equal("P1001", Assert.IsType<PostgresException>(failure).SqlState);
        Assert.Equal(1, ownerExists);
    }

    /// <summary>Absent-source replay preserves the existing destination without demanding mutation.</summary>
    /// <param name="index">Whether an index rather than a column is renamed.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRename_AbsentSourceReplayPreservesDestination(bool index)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id integer NOT NULL, target_value integer NULL); "
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

    /// <summary>Quoted member casing retains distinct PostgreSQL destinations rather than CLR case folding.</summary>
    /// <param name="index">Whether the member is an index rather than a column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRename_DifferentIdentifierCaseDoesNotOccupyDestination(bool index)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id integer NOT NULL, decoy integer NULL, \"Target_Value\" integer NULL); "
            + "CREATE INDEX \"Target_Index\" ON member_rows (decoy);");

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
            builder.EnsureColumn("member_rows", new ExpectedColumnDefinition("source_value", typeof(int), true,
                "integer"), policy: SafeMigrationPolicy.ThrowIfDifferent);
            builder.RenameColumnIfExists("source_value", "member_rows", "target_value");
        }

        var rename = (SafeMigrationOperation)builder.Operations[^1];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-native-case"));

        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.False(Assert.Single(analyses).RenameTargetExists);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
    }

    /// <summary>A target drop discharges only collision, never unsupported source ownership or kind.</summary>
    /// <param name="kind">The independent, foreign-owner or constraint-owned source fixture.</param>
    [Theory]
    [InlineData("independent")]
    [InlineData("foreign-owner")]
    [InlineData("constraint")]
    public async Task MemberRename_TargetDropRequiresIndependentlySupportedSource(string kind)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var supported = kind == "independent";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id integer NOT NULL, decoy integer NULL"
            + (kind == "constraint" ? ", CONSTRAINT source_index PRIMARY KEY (id)" : string.Empty)
            + "); CREATE TABLE other_rows (id integer NOT NULL); "
            + "INSERT INTO member_rows (id) VALUES (7); "
            + (kind == "constraint" ? string.Empty : "CREATE INDEX source_index ON "
                + (kind == "foreign-owner" ? "other_rows" : "member_rows") + " (id); ")
            + "CREATE INDEX target_index ON member_rows (decoy);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("target_index", "member_rows");
        builder.RenameIndexIfExists("source_index", "member_rows", "target_index");
        var rename = (SafeMigrationOperation)builder.Operations[^1];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-rename-source-kind"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var sourceCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + "WHERE n.nspname = 'public' AND c.relname = 'source_index';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM member_rows WHERE id = 7;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.True(analysis.RenameTargetExists);
        Assert.Equal(supported, analysis.RenameSourceSupported);
        Assert.Equal(supported ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(supported ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent,
            report.Assessments[^1].Action);
        if (supported)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("P1001", Assert.IsType<PostgresException>(failure).SqlState);
        }

        Assert.Equal(supported ? 0 : 1, sourceCount);
        Assert.Equal(1, rows);
    }
}
