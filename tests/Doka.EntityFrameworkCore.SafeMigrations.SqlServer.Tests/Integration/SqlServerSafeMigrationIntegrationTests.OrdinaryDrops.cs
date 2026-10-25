namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies native ordinary-column DROP blockers and exact accepted prerequisite-removal lifetimes.</summary>
public sealed class SqlServerSafeMigrationOrdinaryDropIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Creates isolated native databases without sharing mutable schema between test classes.</summary>
    /// <param name="fixture">The assembly-owned native SQL Server fixture.</param>
    public SqlServerSafeMigrationOrdinaryDropIntegrationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>A matching source confirmation cannot erase any independent native DROP blocker.</summary>
    /// <param name="kind">The native index, CHECK, key, FK, statistics, view or owned-default blocker.</param>
    [SqlServerLiveTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task MatchingSourceConfirmationPreservesEveryNativeDependency(int kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = await CreateBlockedSourceAsync(context, connectionString, kind);
        var direct = new MigrationBuilder(context.Database.ProviderName!);
        direct.DropColumnIfExists("L", "ordinary_drop_nodes");
        var confirmed = new MigrationBuilder(context.Database.ProviderName!);
        confirmed.EnsureColumn("ordinary_drop_nodes", definition, SafeMigrationPolicy.ThrowIfDifferent);
        confirmed.DropColumnIfExists("L", "ordinary_drop_nodes");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var before = await runner.AnalyzeAsync(context, direct.Operations,
            new SafeMigrationRunOptions("ordinary-drop-direct"));

        var after = await runner.AnalyzeAsync(context, confirmed.Operations,
            new SafeMigrationRunOptions("ordinary-drop-confirmed"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, confirmed.Operations));
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND L=1 AND R=4;");

        var blockers = await ScalarIntAsync(connectionString, BlockerCountSql(kind));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, before.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, before.Assessments[0].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, after.Status);
        Assert.Equal(SafeMigrationAction.NoOp, after.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, after.Assessments[1].Action);
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, rows);
        Assert.Equal(1, blockers);
    }

    /// <summary>An object-level dependency row cannot make a fully bound R-only view depend on unrelated L.</summary>
    [SqlServerLiveFact]
    public async Task UnrelatedSchemaBoundViewDoesNotBlockSourceDrop()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ordinary_drop_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL);"
            + "INSERT dbo.ordinary_drop_nodes VALUES(1,1,4);");
        await ExecuteSqlAsync(connectionString, "CREATE VIEW dbo.ordinary_R_view WITH SCHEMABINDING "
            + "AS SELECT R FROM dbo.ordinary_drop_nodes;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", new ExpectedColumnDefinition("L", typeof(long), false, "bigint"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-unrelated-view"));

        var objectReferences = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.sql_expression_dependencies "
            + "WHERE referencing_id=OBJECT_ID(N'dbo.ordinary_R_view') "
            + "AND referenced_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') AND referenced_minor_id=0;");

        var columnReferences = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.sql_expression_dependencies d JOIN sys.columns c "
            + "ON c.object_id=d.referenced_id AND c.column_id=d.referenced_minor_id "
            + "WHERE d.referencing_id=OBJECT_ID(N'dbo.ordinary_R_view') "
            + "AND c.object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') AND c.name=N'R';");

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await SourceCountAsync(connectionString);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.ordinary_R_view WHERE R=4;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(1, objectReferences);
        Assert.Equal(1, columnReferences);
        Assert.Null(failure);
        Assert.Equal(0, remaining);
        Assert.Equal(1, rows);
    }

    /// <summary>Optimizer-owned statistics follow native DROP instead of acting as caller-owned blockers.</summary>
    [SqlServerLiveFact]
    public async Task AutomaticallyCreatedSourceStatisticsDoNotBlockNativeDrop()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.StatsNodes(L bigint NOT NULL,R bigint NOT NULL);INSERT dbo.StatsNodes VALUES(1,4);");
        // WHY: The real optimizer creates the statistic. A _WA-like name or a user-created AUTO_DROP statistic
        // would not exercise the auto_created ownership contract used by the production dependency predicate.
        var violations = await ScalarIntAsync(connectionString,
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.StatsNodes WHERE NOT(L>0)) THEN 1 ELSE 0 END;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("StatsNodes", new ExpectedColumnDefinition("L", typeof(long), false, "bigint"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("L", "StatsNodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-automatic-statistics"));

        var statisticsId = await ScalarIntAsync(connectionString,
            "SELECT s.stats_id FROM sys.stats s JOIN sys.stats_columns sc "
            + "ON sc.object_id=s.object_id AND sc.stats_id=s.stats_id JOIN sys.columns c "
            + "ON c.object_id=sc.object_id AND c.column_id=sc.column_id "
            + "WHERE s.object_id=OBJECT_ID(N'dbo.StatsNodes') AND s.auto_created=1 AND s.user_created=0 "
            + "AND c.name=N'L';");

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.StatsNodes') AND name=N'L';");

        var retainedStatistics = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.stats WHERE object_id=OBJECT_ID(N'dbo.StatsNodes') AND stats_id="
            + statisticsId.ToString(CultureInfo.InvariantCulture) + ";");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.StatsNodes WHERE R=4;");

        // Assert
        Assert.Equal(0, violations);
        Assert.True(statisticsId > 0);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Null(failure);
        Assert.Equal(0, remaining);
        Assert.Equal(0, retainedStatistics);
        Assert.Equal(1, rows);
    }

    /// <summary>Only exact accepted removals discharge native index, CHECK, key and FK prerequisites.</summary>
    /// <param name="kind">The native prerequisite that is explicitly removed.</param>
    [SqlServerLiveTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task ExactTypedDependencyRemovalPermitsOrdinarySourceDrop(int kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = await CreateBlockedSourceAsync(context, connectionString, kind == 9 ? 5 : kind);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", definition, SafeMigrationPolicy.ThrowIfDifferent);
        AppendExactRemoval(builder, kind);
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-explicit-removal"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await SourceCountAsync(connectionString);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND R=4;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Null(failure);
        Assert.Equal(0, remaining);
        Assert.Equal(1, rows);
    }

    /// <summary>Independent native dependency families may be removed in either supported typed order.</summary>
    /// <param name="reverse">Whether CHECK removal precedes rather than follows native index removal.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedNativeDependencyRemovalPreservesBothValidOrders(bool reverse)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = await CreateBlockedSourceAsync(context, connectionString, 0);
        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE dbo.ordinary_drop_nodes ADD CONSTRAINT CK_ordinary_L CHECK(L>0);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", definition, SafeMigrationPolicy.ThrowIfDifferent);
        AppendExactRemoval(builder, reverse ? 1 : 0);
        AppendExactRemoval(builder, reverse ? 0 : 1);
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-mixed-order"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await SourceCountAsync(connectionString);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND R=4;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Null(failure);
        Assert.Equal(0, remaining);
        Assert.Equal(1, rows);
    }

    /// <summary>A newly applied CHECK survives source confirmation until its exact typed removal is accepted.</summary>
    /// <param name="captured">Whether the CHECK arrives through captured SQL rather than the public AST.</param>
    /// <param name="removed">Whether the CHECK is explicitly removed before the source DROP.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NewlyAppliedCheckRetainsOnlyItsAcceptedCurrentDependency(bool captured, bool removed)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        // WHY: The old failure must be attributable to CHECK lineage, not accidentally masked by native statistics.
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS OFF;"
            + "CREATE TABLE dbo.ordinary_drop_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL);"
            + "INSERT dbo.ordinary_drop_nodes VALUES(1,1,4);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", new ExpectedColumnDefinition("L", typeof(long), false, "bigint"),
            SafeMigrationPolicy.ThrowIfDifferent);
        if (captured)
        {
            builder.AddCheckConstraintIfNotExists("CK_ordinary_L", "ordinary_drop_nodes", "L>0");
        }
        else
        {
            builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression(
                "CK_ordinary_L", "ordinary_drop_nodes", SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("L"), SafeMigrationSqlBinaryOperator.GreaterThan,
                    SafeMigrationSql.Literal(0L))), SafeMigrationPolicy.ThrowIfDifferent);
        }

        if (removed)
        {
            builder.DropCheckConstraintIfExists("CK_ordinary_L", "ordinary_drop_nodes");
        }

        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-new-check"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await SourceCountAsync(connectionString);
        var checks = await ScalarIntAsync(connectionString, BlockerCountSql(1));
        var statistics = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.stats WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes');");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND R=4;");

        // Assert
        Assert.Equal(removed ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(removed ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent,
            report.Assessments[^1].Action);
        Assert.Equal(removed ? 0 : 1, remaining);
        Assert.Equal(removed ? 0 : 1, checks);
        Assert.Equal(0, statistics);
        Assert.Equal(1, rows);
        if (removed)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
    }

    /// <summary>Absent, wrong-owner, rejected or incomplete removals cannot discharge native blockers.</summary>
    /// <param name="scenario">The missing, wrong-owner, retained-index, statistics or rejected key-index case.</param>
    [SqlServerLiveTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task NonDischargingRemovalPreservesNativeRefusal(int scenario)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = await CreateBlockedSourceAsync(context, connectionString, scenario == 4 ? 2 : 0);
        if (scenario == 1)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE dbo.other_nodes(L bigint NOT NULL);CREATE INDEX IX_ordinary_L ON dbo.other_nodes(L);");
        }

        if (scenario is 2 or 3)
        {
            await ExecuteSqlAsync(connectionString, scenario == 2
                ? "CREATE INDEX IX_retained_L ON dbo.ordinary_drop_nodes(L);"
                : RetainedStatisticsSql("ST_retained_L"));
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", definition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropIndexIfExists(scenario == 0 ? "IX_missing" : scenario == 4 ? "PK_ordinary_L" : "IX_ordinary_L",
            scenario == 1 ? "other_nodes" : "ordinary_drop_nodes");
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-nondischarging-removal"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND L=1 AND R=4;");

        var retained = await ScalarIntAsync(connectionString, scenario switch
        {
            2 => "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') "
                + "AND name=N'IX_retained_L';",
            3 => "SELECT COUNT(*) FROM sys.stats WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') "
                + "AND name=N'ST_retained_L' AND user_created=1 AND auto_created=0;",
            4 => BlockerCountSql(2),
            _ => BlockerCountSql(0),
        });

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, rows);
        Assert.Equal(1, retained);
        if (scenario == 0)
        {
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        }
        else if (scenario == 4)
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        }
    }

    /// <summary>Recreating a declared index rejects the contradictory plan before native DDL.</summary>
    [SqlServerLiveFact]
    public async Task RecreatedDeclaredIndexRejectsBeforeAnyDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = await CreateBlockedSourceAsync(context, connectionString, 0);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", definition, SafeMigrationPolicy.ThrowIfDifferent);
        AppendExactRemoval(builder, 0);
        builder.CreateIndexIfNotExists("IX_ordinary_L", "ordinary_drop_nodes", ["L"]);
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var analysisFailure = await Record.ExceptionAsync(() => context.GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("ordinary-drop-recreated")));

        var scriptFailure = Record.Exception(() => context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.GetService<IDesignTimeModel>().Model));

        var executionFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND L=1 AND R=4;");

        // Assert
        foreach (var failure in new[] { analysisFailure, scriptFailure, executionFailure })
        {
            var refusal = Assert.IsType<InvalidOperationException>(failure);

            Assert.Contains("Cannot project column drop 'L'", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("index 'IX_ordinary_L'", refusal.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, rows);
        Assert.Equal(1, await ScalarIntAsync(connectionString, BlockerCountSql(0)));
    }

    /// <summary>A recreated CHECK establishes a new dependency instead of inheriting its old tombstone.</summary>
    [SqlServerLiveFact]
    public async Task RecreatedCheckReestablishesItsNativeSourceDependency()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS OFF;");
        await using var context = CreateContext(connectionString);
        var definition = await CreateBlockedSourceAsync(context, connectionString, 1);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", definition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropCheckConstraintIfExists("CK_ordinary_L", "ordinary_drop_nodes");
        builder.AddCheckConstraintIfNotExists("CK_ordinary_L", "ordinary_drop_nodes", "L>0");
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-recreated-check"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND L=1 AND R=4;");

        var checks = await ScalarIntAsync(connectionString, BlockerCountSql(1));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[3].Action);
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, rows);
        Assert.Equal(1, checks);
    }

    /// <summary>An unapplied ExistenceOnly wish neither invents nor removes native CHECK dependencies.</summary>
    /// <param name="nativeDependent">Whether the physical CHECK actually references the source being dropped.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistenceOnlyCheckWishDoesNotReplaceNativeDependencyIdentity(bool nativeDependent)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS OFF;"
            + "CREATE TABLE dbo.ordinary_drop_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL);"
            + "INSERT dbo.ordinary_drop_nodes VALUES(1,1,4);"
            + "ALTER TABLE dbo.ordinary_drop_nodes ADD CONSTRAINT CK_ordinary_source CHECK("
            + (nativeDependent ? "L" : "R") + ">0);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", new ExpectedColumnDefinition("L", typeof(long), false, "bigint"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddCheckConstraintIfNotExists("CK_ordinary_source", "ordinary_drop_nodes",
            (nativeDependent ? "R" : "L") + ">0", policy: SafeMigrationPolicy.ExistenceOnly);
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-check-existence-wish"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await SourceCountAsync(connectionString);
        var references = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.sql_expression_dependencies d JOIN sys.check_constraints cc "
            + "ON cc.object_id=d.referencing_id JOIN sys.columns c ON c.object_id=d.referenced_id "
            + "AND c.column_id=d.referenced_minor_id WHERE cc.parent_object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') "
            + "AND cc.name=N'CK_ordinary_source' AND c.name=N'" + (nativeDependent ? "L" : "R") + "';");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND R=4;");

        // Assert
        Assert.Equal(nativeDependent ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal(nativeDependent ? 1 : 0, remaining);
        Assert.Equal(1, references);
        Assert.Equal(1, rows);
        if (nativeDependent)
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    /// <summary>Opaque transitions cannot turn a retained ordinary dependency into a proven safe DROP.</summary>
    [SqlServerLiveFact]
    public async Task OpaqueBoundaryRequiresFreshRuntimeDependencyValidation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = await CreateBlockedSourceAsync(context, connectionString, 0);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", definition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.Sql("SELECT 1;");
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-opaque"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND L=1 AND R=4;");

        // Assert
        Assert.NotEqual(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, rows);
    }

    /// <summary>An accepted DROP discharges its blocker without approving unsupported source readdition.</summary>
    [SqlServerLiveFact]
    public async Task SourceReadditionPreservesIndependentRetainedLayoutRejectionAfterAcceptedDrop()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = await CreateBlockedSourceAsync(context, connectionString, 0);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("ordinary_drop_nodes", definition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropIndexIfExists("IX_ordinary_L", "ordinary_drop_nodes");
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");
        builder.EnsureColumn("ordinary_drop_nodes", new ExpectedColumnDefinition("L", typeof(long), true, "bigint"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("L", "ordinary_drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-source-replacement"));

        // WHY: This direct helper executes commands individually, not inside an EF migration transaction.
        // The earlier authorized removals persist when the independently unsupported ADD is rejected.
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await SourceCountAsync(connectionString);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_nodes WHERE Id=1 AND R=4;");

        var indexes = await ScalarIntAsync(connectionString, BlockerCountSql(0));
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes');");

        var highestColumnId = await ScalarIntAsync(connectionString,
            "SELECT MAX(column_id) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Collection(report.Assessments,
            static assessment => Verify(assessment, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp,
                "classified_matching"),
            static assessment => Verify(assessment, SafeMigrationObservedState.Matching, SafeMigrationAction.Apply,
                "classified_matching"),
            static assessment => Verify(assessment, SafeMigrationObservedState.Matching, SafeMigrationAction.Apply,
                "projected_column_drop_safe"),
            static assessment => Verify(assessment, SafeMigrationObservedState.Unsupported,
                SafeMigrationAction.RejectUnsupported, "column_layout_unproven"),
            static assessment => Verify(assessment, SafeMigrationObservedState.Missing, SafeMigrationAction.NoOp,
                "projected_missing"));
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, remaining);
        Assert.Equal(0, indexes);
        Assert.Equal(2, columns);
        Assert.Equal(3, highestColumnId);
        Assert.Equal(1, rows);

        static void Verify(SafeMigrationAssessment assessment, SafeMigrationObservedState state,
            SafeMigrationAction action, string code)
        {
            Assert.Equal(state, assessment.ObservedState);
            Assert.Equal(action, assessment.Action);
            Assert.Equal(code, assessment.AnalysisCode);
        }
    }

    /// <summary>A rename retains native blockers; explicit owner replacement starts a distinct lifetime.</summary>
    /// <param name="recreated">Whether the owner is explicitly removed and rebuilt without dependencies.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerTransitionsPreserveNativeDependencyBoundaries(bool recreated)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        _ = await CreateBlockedSourceAsync(context, connectionString, 0);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var owner = recreated ? "ordinary_drop_nodes" : "ordinary_renamed_nodes";
        if (recreated)
        {
            builder.DropTableIfExists(owner);
            builder.EnsureTable(new ExpectedTableDefinition(owner,
                [new("L", typeof(long), true, "bigint"), new("R", typeof(long), true, "bigint")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.RenameTableIfExists("ordinary_drop_nodes", owner);
        }

        builder.DropColumnIfExists("L", owner);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-drop-owner-transition"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo." + owner + "') AND name=N'L';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo." + owner + ";");

        // Assert
        Assert.Equal(recreated, report.Status == SafeMigrationReportStatus.Ready);
        Assert.Equal(recreated ? 0 : 1, remaining);
        Assert.Equal(recreated ? 0 : 1, rows);
        if (recreated)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
    }

    private static async Task<ExpectedColumnDefinition> CreateBlockedSourceAsync(
        DbContext context,
        string connectionString,
        int kind
    )
    {
        var definition = new ExpectedColumnDefinition("L", typeof(long), false, "bigint",
            defaultValue: kind == 8 ? SafeMigrationDefaultValue.Literal(1L) : SafeMigrationDefaultValue.None);

        if (kind == 8)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE dbo.ordinary_drop_nodes(Id int NOT NULL,R bigint NOT NULL);"
                + "INSERT dbo.ordinary_drop_nodes VALUES(1,4);");
            var setup = new MigrationBuilder(context.Database.ProviderName!);
            setup.EnsureColumn("ordinary_drop_nodes", definition, SafeMigrationPolicy.ThrowIfDifferent);
            await ExecuteOperationsAsync(context, setup.Operations);
        }
        else
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE dbo.ordinary_drop_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL);"
                + "INSERT dbo.ordinary_drop_nodes VALUES(1,1,4);");
            if (kind == 7)
            {
                // WHY: CREATE VIEW must be its own batch; the schema-bound reference is the intended native blocker.
                await ExecuteSqlAsync(connectionString, "CREATE VIEW dbo.ordinary_L_view WITH SCHEMABINDING "
                    + "AS SELECT L FROM dbo.ordinary_drop_nodes;");
            }
            else
            {
                await ExecuteSqlAsync(connectionString, kind switch
                {
                    0 => "CREATE INDEX IX_ordinary_L ON dbo.ordinary_drop_nodes(L);",
                    1 => "ALTER TABLE dbo.ordinary_drop_nodes ADD CONSTRAINT CK_ordinary_L CHECK(L>0);",
                    2 => "ALTER TABLE dbo.ordinary_drop_nodes ADD CONSTRAINT PK_ordinary_L PRIMARY KEY(L);",
                    3 => "ALTER TABLE dbo.ordinary_drop_nodes ADD CONSTRAINT UQ_ordinary_L UNIQUE(L);",
                    4 => "CREATE TABLE dbo.ordinary_parent(Id bigint NOT NULL PRIMARY KEY);"
                        + "INSERT dbo.ordinary_parent VALUES(1);ALTER TABLE dbo.ordinary_drop_nodes "
                        + "ADD CONSTRAINT FK_ordinary_L FOREIGN KEY(L) REFERENCES dbo.ordinary_parent(Id);",
                    5 => "ALTER TABLE dbo.ordinary_drop_nodes ADD CONSTRAINT UQ_ordinary_L UNIQUE(L);"
                        + "CREATE TABLE dbo.ordinary_child(L bigint NOT NULL CONSTRAINT FK_child_L "
                        + "REFERENCES dbo.ordinary_drop_nodes(L));INSERT dbo.ordinary_child VALUES(1);",
                    6 => RetainedStatisticsSql("ST_ordinary_L"),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                });
            }
        }

        return definition;
    }

    private static void AppendExactRemoval(MigrationBuilder builder, int kind)
    {
        switch (kind)
        {
            case 0:
                builder.DropIndexIfExists("IX_ordinary_L", "ordinary_drop_nodes");
                break;
            case 1:
                builder.DropCheckConstraintIfExists("CK_ordinary_L", "ordinary_drop_nodes");
                break;
            case 2:
                builder.DropPrimaryKeyIfExists("PK_ordinary_L", "ordinary_drop_nodes");
                break;
            case 3:
                builder.DropUniqueConstraintIfExists("UQ_ordinary_L", "ordinary_drop_nodes");
                break;
            case 4:
                builder.DropForeignKeyIfExists("FK_ordinary_L", "ordinary_drop_nodes");
                break;
            case 5:
                builder.DropForeignKeyIfExists("FK_child_L", "ordinary_child");
                builder.DropUniqueConstraintIfExists("UQ_ordinary_L", "ordinary_drop_nodes");
                break;
            case 9:
                builder.DropTableIfExists("ordinary_child");
                builder.DropUniqueConstraintIfExists("UQ_ordinary_L", "ordinary_drop_nodes");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static string BlockerCountSql(int kind)
        => kind switch
        {
            0 => "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') "
                + "AND name=N'IX_ordinary_L';",
            1 => "SELECT COUNT(*) FROM sys.check_constraints "
                + "WHERE parent_object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') AND name=N'CK_ordinary_L';",
            2 or 3 => "SELECT COUNT(*) FROM sys.key_constraints "
                + "WHERE parent_object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') AND name=N'"
                + (kind == 2 ? "PK_ordinary_L" : "UQ_ordinary_L") + "';",
            4 or 5 => "SELECT COUNT(*) FROM sys.foreign_keys WHERE name=N'"
                + (kind == 4 ? "FK_ordinary_L" : "FK_child_L") + "';",
            6 => "SELECT COUNT(*) FROM sys.stats WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') "
                + "AND name=N'ST_ordinary_L';",
            7 => "SELECT COUNT(*) FROM sys.views WHERE object_id=OBJECT_ID(N'dbo.ordinary_L_view') "
                + "AND OBJECTPROPERTY(object_id,'IsSchemaBound')=1;",
            8 => "SELECT COUNT(*) FROM sys.default_constraints "
                + "WHERE parent_object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') "
                + "AND parent_column_id=COLUMNPROPERTY(parent_object_id,N'L','ColumnId');",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static Task<int> SourceCountAsync(string connectionString)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_nodes') AND name=N'L';");

    private static string RetainedStatisticsSql(string name)
        // WHY: SQL Server 2022+ defaults to auto-droppable user statistics. The negative fixture must really block
        // native DROP; dynamic SQL keeps the new option invisible to the SQL Server 2019 parser.
        => "IF CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))>=16 "
            + "EXEC(N'CREATE STATISTICS " + name + " ON dbo.ordinary_drop_nodes(L) WITH AUTO_DROP=OFF;');"
            + "ELSE CREATE STATISTICS " + name + " ON dbo.ordinary_drop_nodes(L);";
}
