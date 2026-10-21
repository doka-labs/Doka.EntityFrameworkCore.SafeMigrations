namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies ordered generated-source removal without weakening fresh native DROP guards.</summary>
public sealed class SqlServerSafeMigrationGeneratedDropIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Creates isolated per-test databases on the assembly-owned native container.</summary>
    /// <param name="fixture">The shared native SQL Server fixture.</param>
    public SqlServerSafeMigrationGeneratedDropIntegrationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>Only currently accepted generated dependencies veto a source DROP.</summary>
    /// <param name="scenario">The accepted, removed, missing, rejected, fresh or opaque dependency lifetime.</param>
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
    [InlineData(9)]
    [InlineData(10)]
    public async Task ComputedSourceDropPreservesOrderedDependencyLifetimes(int scenario)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var fresh = scenario == 7;
        var missing = scenario is 4 or 5 or 6;
        var released = scenario is 1 or 6;
        var dependent = scenario is 0 or 5 or 7 or 8;
        var rejected = scenario == 9;
        var opaque = scenario == 10;
        if (!fresh)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.drop_nodes(R bigint NOT NULL,Payload int NULL"
                + (missing ? string.Empty : ",L bigint NOT NULL") + "); INSERT dbo.drop_nodes VALUES"
                + (missing ? "(4,10);" : "(4,10,1);"));
        }

        await using var context = CreateContext(connectionString);
        var flag = SqlServerGeneratedColumnSqlTests.Flag();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (scenario == 6)
        {
            builder.DropColumnIfExists("L", "drop_nodes");
        }

        if (scenario is 5 or 6)
        {
            builder.EnsureColumn("drop_nodes", new ExpectedColumnDefinition("L", typeof(long), true, "bigint"),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        if (scenario == 8)
        {
            var setup = new MigrationBuilder(context.Database.ProviderName!);
            setup.EnsureColumn("drop_nodes", flag, SafeMigrationPolicy.ThrowIfDifferent);
            await ExecuteOperationsAsync(context, setup.Operations);
        }

        if (fresh)
        {
            builder.EnsureTable(new ExpectedTableDefinition("drop_nodes",
                [new("L", typeof(long), false, "bigint"), new("R", typeof(long), false, "bigint"), flag]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else if (scenario != 4)
        {
            builder.EnsureColumn("drop_nodes", rejected
                ? new ExpectedColumnDefinition(flag.Name, typeof(int), false, "int",
                    computedColumnSql: flag.ComputedColumnSql) : flag, SafeMigrationPolicy.ThrowIfDifferent);
        }

        if (released)
        {
            builder.DropColumnIfExists(flag.Name, "drop_nodes");
        }

        if (opaque)
        {
            builder.Sql("SELECT 1;");
        }

        var target = scenario == 2 ? "Payload" : scenario == 3 ? flag.Name : "L";
        builder.DropColumnIfExists(target, "drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-lifetimes"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.drop_nodes') "
            + "AND name=N'" + target + "';");
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.drop_nodes WHERE R=4;");

        // Assert
        Assert.Equal(dependent || rejected ? SafeMigrationReportStatus.Blocked
            : opaque ? SafeMigrationReportStatus.RuntimeValidationRequired : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(fresh ? 0 : 1, preservedRows);
        if (dependent || opaque)
        {
            Assert.Equal(1, remaining);
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
            if (dependent)
            {
                Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
                Assert.Contains("generated_column_drop_dependency", report.Assessments[^1].AnalysisCode,
                    StringComparison.Ordinal);
            }
        }
        else if (rejected)
        {
            Assert.Equal(1, remaining);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[0].Action);
            Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Equal(0, remaining);
            Assert.Null(failure);
            Assert.Equal(scenario == 4 ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply,
                report.Assessments[^1].Action);
        }
    }

    /// <summary>Native CI aliases and CS-distinct members never use CLR case folding for dependency identity.</summary>
    /// <param name="sensitive">Whether the owner has distinct uppercase and lowercase members.</param>
    /// <param name="target">The exact requested source spelling.</param>
    /// <param name="dependent">Whether that native member is referenced.</param>
    [SqlServerLiveTheory]
    [InlineData(false, "l", true)]
    [InlineData(true, "L", true)]
    [InlineData(true, "l", false)]
    public async Task ComputedSourceDropUsesNativeMemberIdentity(bool sensitive, string target, bool dependent)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "ALTER DATABASE CURRENT COLLATE Latin1_General_100_" + (sensitive ? "CS_AS" : "CI_AS") + ";");

        // WHY: SQL Server compiles a complete batch before ALTER DATABASE runs.
        // A separate command must acquire the new identifier collation before
        // compiling legal CS-distinct L/l fields; no product gate is involved.
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.drop_nodes(L bigint NOT NULL,R bigint NOT NULL"
            + (sensitive ? ",l bigint NOT NULL" : string.Empty) + "); INSERT dbo.drop_nodes VALUES"
            + (sensitive ? "(1,4,99);" : "(1,4);"));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("drop_nodes", SqlServerGeneratedColumnSqlTests.Flag(),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists(target, "drop_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-native-member"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.drop_nodes WHERE L=1 AND R=4;");

        // Assert
        Assert.Equal(dependent ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(1, preservedRows);
        if (dependent)
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    /// <summary>Native generated-only discharge never erases an independent index blocker.</summary>
    /// <param name="removeGenerated">Whether the exact native generated dependent is removed first.</param>
    /// <param name="independentIndex">Whether a retained independent source index also blocks removal.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingNativeComputedDependencyRequiresExactCurrentDischarge(
        bool removeGenerated,
        bool independentIndex
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.drop_nodes(L bigint NOT NULL,R bigint NOT NULL,"
            + "HasChildren AS CONVERT(bit,CASE WHEN R>L THEN 1 ELSE 0 END));"
            + "INSERT dbo.drop_nodes(L,R) VALUES(1,4);"
            + (independentIndex ? "CREATE INDEX IX_L ON dbo.drop_nodes(L);" : string.Empty));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (removeGenerated)
        {
            builder.DropColumnIfExists("HasChildren", "drop_nodes");
        }

        if (!removeGenerated || independentIndex)
        {
            builder.EnsureColumn("drop_nodes", new ExpectedColumnDefinition("L", typeof(long), false, "bigint"),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.DropColumnIfExists("L", "drop_nodes");
        var blocked = !removeGenerated || independentIndex;

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-discharge"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.drop_nodes') AND name=N'L';");

        var preservedRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.drop_nodes WHERE R=4;");

        // Assert
        Assert.Equal(blocked ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(blocked ? 1 : 0, remaining);
        Assert.Equal(1, preservedRows);
        if (blocked)
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
            Assert.Equal(51001, Assert.IsType<SqlException>(exception).Number);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
            Assert.Null(exception);
        }
    }

    /// <summary>Named index-removal certificates require exact accepted current absence of every blocker.</summary>
    /// <param name="scenario">The removal order, retained blocker, missing, failed or recreated predecessor.</param>
    [SqlServerLiveTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task NativeNamedIndexDischargePreservesIndependentBlockerLifetimes(int scenario)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var failed = scenario == 6;
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.drop_nodes(L bigint NOT NULL,R bigint NOT NULL,"
            + "HasChildren AS CONVERT(bit,CASE WHEN R>L THEN 1 ELSE 0 END)"
            + (failed ? ",CONSTRAINT PK_drop_nodes PRIMARY KEY(L)" : string.Empty) + ");"
            + "INSERT dbo.drop_nodes(L,R) VALUES(1,4);"
            + (failed ? string.Empty : "CREATE INDEX IX_L ON dbo.drop_nodes(L);")
            + (scenario == 2 ? "CREATE INDEX IX_L_other ON dbo.drop_nodes(L);" : string.Empty)
            + (scenario == 3 ? "CREATE STATISTICS S_L ON dbo.drop_nodes(L);" : string.Empty));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (scenario == 1)
        {
            builder.DropColumnIfExists("HasChildren", "drop_nodes");
        }

        builder.DropIndexIfExists(failed ? "PK_drop_nodes" : scenario == 4 ? "IX_missing" : "IX_L", "drop_nodes");
        if (scenario != 1)
        {
            builder.DropColumnIfExists("HasChildren", "drop_nodes");
        }

        builder.DropColumnIfExists("L", "drop_nodes");
        var safe = scenario is 0 or 1;

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-discharge"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.drop_nodes') AND name=N'L';");
        var preservedRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.drop_nodes WHERE R=4;");

        // Assert
        Assert.Equal(safe ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(safe ? 0 : 1, remaining);
        Assert.Equal(1, preservedRows);
        if (safe)
        {
            Assert.Null(exception);
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
            Assert.Equal("projected_generated_column_drop_safe", report.Assessments[^1].AnalysisCode);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
            Assert.Equal(51001, Assert.IsType<SqlException>(exception).Number);
            if (scenario == 4)
            {
                Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
            }
            else if (failed)
            {
                Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
            }
        }
    }

    /// <summary>A recreated declared dependent rejects the contradictory source-removal plan before DDL.</summary>
    [SqlServerLiveFact]
    public async Task RecreatedIndexRejectsContradictorySourceDropBeforeAnyDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.drop_nodes(L bigint NOT NULL,R bigint NOT NULL,"
            + "HasChildren AS CONVERT(bit,CASE WHEN R>L THEN 1 ELSE 0 END));"
            + "INSERT dbo.drop_nodes(L,R) VALUES(1,4);CREATE INDEX IX_L ON dbo.drop_nodes(L);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_L", "drop_nodes");
        builder.DropColumnIfExists("HasChildren", "drop_nodes");
        builder.CreateIndexIfNotExists("IX_L", "drop_nodes", ["L"]);
        builder.DropColumnIfExists("L", "drop_nodes");

        // Act
        var analysisFailure = await Record.ExceptionAsync(() => context.GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("recreated-index-plan")));

        var scriptFailure = Record.Exception(() => context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.GetService<IDesignTimeModel>().Model));

        var executionFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.drop_nodes WHERE L=1 AND R=4 AND HasChildren=1;");

        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.drop_nodes') AND name=N'IX_L';");

        // Assert
        foreach (var failure in new[] { analysisFailure, scriptFailure, executionFailure })
        {
            var refusal = Assert.IsType<InvalidOperationException>(failure);
            Assert.Contains("Cannot project column drop 'L'", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("index 'IX_L'", refusal.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, preserved);
        Assert.Equal(1, indexes);
    }

    /// <summary>
    /// Owner rebinding remains conservative; an explicitly recreated complete owner has a new lifetime.
    /// </summary>
    /// <param name="recreated">Whether the old owner is replaced instead of renamed with its dependency intact.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGeneratedDropRespectsRenamedAndRecreatedOwnerLifetimes(bool recreated)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.drop_nodes(L bigint NOT NULL,R bigint NOT NULL,"
            + "HasChildren AS CONVERT(bit,CASE WHEN R>L THEN 1 ELSE 0 END));"
            + "INSERT dbo.drop_nodes(L,R) VALUES(1,4);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var owner = recreated ? "drop_nodes" : "renamed_nodes";
        if (recreated)
        {
            builder.DropTableIfExists(owner);
            builder.EnsureTable(new ExpectedTableDefinition(owner,
                [new("L", typeof(long), false, "bigint"), new("R", typeof(long), false, "bigint")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.RenameTableIfExists("drop_nodes", owner);
        }

        builder.DropColumnIfExists("L", owner);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-owner-lifetime"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo." + owner + "') AND name=N'L';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo." + owner + ";");

        // Assert
        Assert.Equal(recreated ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(recreated ? 0 : 1, remaining);
        Assert.Equal(recreated ? 0 : 1, rows);
        if (recreated)
        {
            Assert.Null(exception);
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^1].Action);
            Assert.Equal(51001, Assert.IsType<SqlException>(exception).Number);
        }
    }

    /// <summary>A shared immutable definition cannot reuse another owner's native precision certificate.</summary>
    /// <param name="differentSchema">Whether schema rather than table distinguishes the native owners.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedComputedDefinitionKeepsNativeOwnerPrecisionIndependent(bool differentSchema)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var preciseTable = differentSchema ? "nodes" : "precise_nodes";
        var impreciseTable = differentSchema ? "nodes" : "imprecise_nodes";
        var schema = differentSchema ? "other_schema" : "dbo";
        var preciseName = "dbo." + preciseTable;
        var impreciseName = schema + "." + impreciseTable;
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA other_schema;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE " + preciseName + "(L bigint NOT NULL,R bigint NOT NULL);"
            + "CREATE TABLE " + impreciseName + "(L float NOT NULL,R bigint NOT NULL);"
            + "INSERT " + preciseName + " VALUES(1,4);INSERT " + impreciseName + " VALUES(1,4);");
        await using var context = CreateContext(connectionString);
        var flag = SqlServerGeneratedColumnSqlTests.Flag();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(preciseTable, flag, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(impreciseTable, flag, SafeMigrationPolicy.ThrowIfDifferent, schema);
        builder.CreateIndexIfNotExists("IX_owner_flag", preciseTable, [flag.Name]);
        builder.CreateIndexIfNotExists("IX_owner_flag", impreciseTable, [flag.Name], schema: schema);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-owner-precision"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var precise = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'" + preciseName + "') "
            + "AND name=N'IX_owner_flag';");
        var imprecise = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'" + impreciseName + "') "
            + "AND name=N'IX_owner_flag';");
        var preciseMetadata = await ScalarIntAsync(connectionString,
            "SELECT COLUMNPROPERTY(OBJECT_ID(N'" + preciseName + "'),N'HasChildren',N'IsPrecise');");
        var impreciseMetadata = await ScalarIntAsync(connectionString,
            "SELECT COLUMNPROPERTY(OBJECT_ID(N'" + impreciseName + "'),N'HasChildren',N'IsPrecise');");
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT (SELECT COUNT(*) FROM " + preciseName + " WHERE L=1 AND R=4)"
            + "+(SELECT COUNT(*) FROM " + impreciseName + " WHERE L=1 AND R=4);");

        // Assert
        Assert.Same(((EnsureColumnIntent)((SafeMigrationOperation)builder.Operations[0]).Intent).Definition,
            ((EnsureColumnIntent)((SafeMigrationOperation)builder.Operations[1]).Intent).Definition);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[3].Action);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, precise);
        Assert.Equal(0, imprecise);
        Assert.Equal(1, preciseMetadata);
        Assert.Equal(0, impreciseMetadata);
        Assert.Equal(2, preservedRows);
    }
}
