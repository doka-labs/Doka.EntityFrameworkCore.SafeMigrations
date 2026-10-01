namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Admits the exact 8,060-byte fixed row boundary and rejects additions beyond it before DDL.</summary>
    /// <param name="existingBytes">The physical fixed column width.</param>
    /// <param name="addedBytes">The requested additional fixed width.</param>
    /// <param name="supported">Whether unavoidable row storage remains within the engine limit.</param>
    [SqlServerLiveTheory]
    [InlineData(8000, 53, true)]
    [InlineData(8000, 54, false)]
    [InlineData(5000, 5000, false)]
    public async Task ExistingFixedLayout_EnforcesExactAdditionBoundary(
        int existingBytes,
        int addedBytes,
        bool supported
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_probe (Existing char("
            + existingBytes.ToString(CultureInfo.InvariantCulture) + ") NULL);");
        await CreateColumnLayoutAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var operation = LayoutColumn("Added", "char(" + addedBytes.ToString(CultureInfo.InvariantCulture) + ")");

        // Act
        var tableId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.layout_probe',N'U');");
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var currentTableId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.layout_probe',N'U');");
        var added = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND name=N'Added';");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");

        // Assert
        var analysis = Assert.Single(analyses);

        Assert.Equal(supported ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Unsupported,
            analysis.ObservedState);
        Assert.Equal(tableId, currentTableId);
        Assert.Equal(supported ? 1 : 0, added);
        Assert.Equal(supported ? 1 : 0, events);
        if (supported)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("column_fixed_row_limit", analysis.Code);
            Assert.False(analysis.IsInvariantUnsupported);
            Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        }
    }

    /// <summary>The normal 1,024-column limit is checked independently of packed-bit byte capacity.</summary>
    /// <param name="count">The existing physical column count.</param>
    /// <param name="supported">Whether one further normal column is allowed.</param>
    [SqlServerLiveTheory]
    [InlineData(1023, true)]
    [InlineData(1024, false)]
    public async Task ExistingColumnCount_RejectsThe1025thColumnWithoutMutation(
        int count,
        bool supported
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        var columns = string.Join(",", Enumerable.Range(0, count)
            .Select(static ordinal => "C" + ordinal.ToString(CultureInfo.InvariantCulture) + " bit NULL"));

        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_probe (" + columns + ");");
        await CreateColumnLayoutAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var operation = LayoutColumn("Added", "bit", typeof(bool));

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var currentColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");

        // Assert
        var analysis = Assert.Single(analyses);

        Assert.Equal(supported ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Unsupported,
            analysis.ObservedState);
        Assert.Equal(count + (supported ? 1 : 0), currentColumns);
        Assert.Equal(supported ? 1 : 0, events);
        if (supported)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("column_limit", analysis.Code);
            Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        }
    }

    /// <summary>Variable payloads retain SQL Server's legitimate row-overflow path and idempotent replay.</summary>
    [SqlServerLiveFact]
    public async Task VariablePayloadAddition_AllowsLargeStoredRowAndReplay()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_probe (Existing char(6000) NOT NULL);");
        await using var context = CreateContext(connectionString);
        var operation = LayoutColumn("Added", "varchar(8000)");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("sqlserver-layout-overflow"));

        await ExecuteOperationsAsync(context, [operation]);
        await ExecuteSqlAsync(connectionString,
            "INSERT dbo.layout_probe(Existing,Added) VALUES(REPLICATE('a',6000),REPLICATE('b',5000));");
        await ExecuteOperationsAsync(context, [operation]);
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("sqlserver-layout-overflow-postflight"));

        var bytes = await ScalarIntAsync(connectionString,
            "SELECT DATALENGTH(Existing)+DATALENGTH(Added) FROM dbo.layout_probe;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(11000, bytes);
    }

    /// <summary>
    /// Missing physical history is not mistaken for reclaimed storage, while existing columns still match.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DroppedPhysicalColumn_RejectsNewAllocationButAllowsMatchingExistingColumn()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.layout_probe (Id int NOT NULL, Removed char(5000) NULL); "
            + "INSERT dbo.layout_probe(Id) VALUES(7); ALTER TABLE dbo.layout_probe DROP COLUMN Removed;");
        await CreateColumnLayoutAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var existing = new SafeMigrationOperation(new EnsureColumnIntent("layout_probe",
            new ExpectedColumnDefinition("Id", typeof(int), false, "int")), SafeMigrationPolicy.ThrowIfDifferent);

        var addition = LayoutColumn("Added", "char(4000)");

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, [existing, addition]);

        await ExecuteOperationsAsync(context, [existing]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [addition]));
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_probe WHERE Id=7;");
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analyses[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analyses[1].ObservedState);
        Assert.Equal("column_layout_unproven", analyses[1].Code);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, rows);
        Assert.Equal(1, columns);
        Assert.Equal(0, events);
    }

    /// <summary>
    /// Accepted preceding additions consume capacity even though every live analysis initially sees free space.
    /// </summary>
    /// <param name="freshTable">Whether the initial layout is created earlier in the same stream.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedColumnAdditions_CannotReuseImmutableVacantCapacity(
        bool freshTable
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        if (!freshTable)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_probe (Existing char(4000) NULL);");
        }

        await CreateColumnLayoutAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var operations = new List<MigrationOperation>();
        if (freshTable)
        {
            operations.Add(new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
                "layout_probe", [new ExpectedColumnDefinition("Existing", typeof(string), true, "char(4000)")]),
                SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent));
        }

        operations.Add(LayoutColumn("First", "char(3000)"));
        operations.Add(LayoutColumn("Second", "char(2000)"));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sqlserver-ordered-layout"));

        var failure = Record.Exception(report.ThrowIfBlocked);
        var added = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND name IN(N'First',N'Second');");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^2].Action);
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[^1].ObservedState);
        Assert.Equal("column_fixed_row_limit", report.Assessments[^1].AnalysisCode);
        Assert.IsType<SafeMigrationPreflightException>(failure);
        Assert.Equal(0, added);
        Assert.Equal(0, events);
    }

    /// <summary>A rejected predecessor never consumes the later admissible operation's capacity.</summary>
    [SqlServerLiveFact]
    public async Task RejectedColumnAddition_DoesNotAdvanceLayoutProof()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_probe (Existing char(4000) NULL);");
        await using var context = CreateContext(connectionString);
        MigrationOperation[] operations = [LayoutColumn("Rejected", "char(5000)"), LayoutColumn("Valid", "char(3000)")];

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sqlserver-rejected-layout"));

        var added = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND name IN(N'Rejected',N'Valid');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(0, added);
    }

    /// <summary>A projected drop does not subtract fixed allocation before the following physical addition.</summary>
    [SqlServerLiveFact]
    public async Task OrderedColumnDrop_DoesNotReclaimFixedWidth()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.layout_probe (Id int NOT NULL, Removed char(5000) NULL);");
        await CreateColumnLayoutAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("Removed", "layout_probe");
        builder.Operations.Add(LayoutColumn("Added", "char(5000)"));

        // Act
        var live = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-retained-layout"));

        var failure = Record.Exception(report.ThrowIfBlocked);
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, live[1].ObservedState);
        Assert.Equal("column_fixed_row_limit", live[1].Code);
        Assert.False(live[1].IsInvariantUnsupported);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Equal("column_layout_unproven", report.Assessments[1].AnalysisCode);
        Assert.IsType<SafeMigrationPreflightException>(failure);
        Assert.Equal(2, columns);
        Assert.Equal(0, events);
    }

    /// <summary>
    /// A small projected addition obeys the same post-drop physical-lineage gate as runtime execution.
    /// </summary>
    [SqlServerLiveFact]
    public async Task OrderedSmallColumnDrop_InvalidatesNewAllocationButPreservesExistingMatching()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.layout_probe (Id int NOT NULL,Removed int NULL); "
            + "INSERT dbo.layout_probe VALUES(7,9);");
        await CreateColumnLayoutAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("Removed", "layout_probe");
        builder.Operations.Add(new SafeMigrationOperation(new EnsureColumnIntent("layout_probe",
            new ExpectedColumnDefinition("Id", typeof(int), false, "int")), SafeMigrationPolicy.ThrowIfDifferent));
        builder.Operations.Add(LayoutColumn("Added", "int", typeof(int)));
        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();

        // Act
        var live = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-small-dropped-layout"));

        var failure = Record.Exception(report.ThrowIfBlocked);
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.layout_probe WHERE Id=7 AND Removed=9;");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, live[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, live[1].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, live[2].ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[2].Action);
        Assert.Equal("column_layout_unproven", report.Assessments[2].AnalysisCode);
        Assert.IsType<SafeMigrationPreflightException>(failure);
        Assert.Equal(2, columns);
        Assert.Equal(1, rows);
        Assert.Equal(0, events);
    }

    /// <summary>
    /// A proved full table recreation establishes a fresh allocation lineage instead of retained old width.
    /// </summary>
    [SqlServerLiveFact]
    public async Task OrderedTableRecreation_ResetsFixedAllocationLineage()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_probe (Old char(5000) NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("layout_probe");
        builder.Operations.Add(new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
            "layout_probe", [new ExpectedColumnDefinition("Id", typeof(int), true, "int")]),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent));
        builder.Operations.Add(LayoutColumn("Added", "char(6000)"));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-recreated-layout"));

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND name IN(N'Id',N'Added');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(2, columns);
    }

    /// <summary>
    /// A schema-valid addition cannot materialize a populated near-ceiling row without a capacity proof.
    /// </summary>
    /// <param name="kind">The fixed, variable-default, or non-overflowable clustered-key boundary.</param>
    [SqlServerLiveTheory]
    [InlineData("fixed")]
    [InlineData("variable-default")]
    [InlineData("clustered")]
    public async Task PopulatedNearCapacityRows_AreDataBlockedBeforeColumnDdl(
        string kind
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        var setup = kind switch
        {
            "fixed" => "CREATE TABLE dbo.layout_probe (A char(8000) NOT NULL,B varchar(1) NOT NULL); "
                + "INSERT dbo.layout_probe VALUES('a','x');",
            "variable-default" => "CREATE TABLE dbo.layout_probe (A char(8000) NOT NULL,B char(53) NOT NULL); "
                + "INSERT dbo.layout_probe VALUES('a','b');",
            "clustered" => "CREATE TABLE dbo.layout_probe (A char(7500) NOT NULL,"
                + "B varchar(900) NOT NULL PRIMARY KEY CLUSTERED); "
                + "INSERT dbo.layout_probe VALUES('a',REPLICATE('b',500));",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await ExecuteSqlAsync(connectionString, setup);
        await CreateColumnLayoutAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var definition = kind == "fixed"
            ? new ExpectedColumnDefinition("Added", typeof(string), true, "char(53)")
            : new ExpectedColumnDefinition("Added", typeof(string), false,
                kind == "clustered" ? "char(100)" : "varchar(1)",
                defaultValue: SafeMigrationDefaultValue.Literal("x"));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("layout_probe", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("sqlserver-populated-layout"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_probe;");
        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, Assert.Single(report.Assessments).Action);
        Assert.Equal("column_row_layout_unproven", report.Assessments[0].AnalysisCode);
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(2, columns);
        Assert.Equal(1, rows);
        Assert.Equal(0, events);
    }

    /// <summary>
    /// Near-ceiling fixed storage still permits a trailing nullable variable column without backfill.
    /// </summary>
    [SqlServerLiveFact]
    public async Task NullableVariableAddition_DoesNotRequireSpaceForAnUnmaterializedValue()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.layout_probe (A char(8000) NOT NULL,B char(53) NOT NULL); "
            + "INSERT dbo.layout_probe VALUES('a','b');");
        await using var context = CreateContext(connectionString);
        var operation = LayoutColumn("Added", "varchar(1)");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("sqlserver-null-variable-layout"));

        await ExecuteOperationsAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_probe WHERE Added IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, rows);
    }

    /// <summary>The empty-table witness keeps otherwise row-sensitive schema additions supported.</summary>
    [SqlServerLiveFact]
    public async Task EmptyNearCapacityTable_AllowsDefaultedVariableAddition()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.layout_probe (A char(8000) NOT NULL,B char(53) NOT NULL);");
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("layout_probe",
            new ExpectedColumnDefinition("Added", typeof(string), false, "varchar(1)",
                defaultValue: SafeMigrationDefaultValue.Literal("x"))), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("sqlserver-empty-layout"));

        await ExecuteOperationsAsync(context, [operation]);
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(3, columns);
    }

    /// <summary>A new clustered variable key changes the subsequent column's non-overflowable row contract.</summary>
    /// <param name="populated">Whether the existing table contains a row that needs materialization.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedPrimaryKey_UpdatesVariableRowCapacityProof(
        bool populated
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.layout_probe (A char(7500) NOT NULL,B varchar(900) NOT NULL);"
            + (populated ? "INSERT dbo.layout_probe VALUES('a',REPLICATE('b',500));" : string.Empty));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("PK_layout_probe", "layout_probe", ["B"]);
        builder.Operations.Add(new SafeMigrationOperation(new EnsureColumnIntent("layout_probe",
            new ExpectedColumnDefinition("Added", typeof(string), false, "char(100)",
                defaultValue: SafeMigrationDefaultValue.Literal("x"))), SafeMigrationPolicy.ThrowIfDifferent));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-clustered-layout"));

        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var keys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        // Assert
        Assert.Equal(populated ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(populated ? SafeMigrationAction.RejectDataBlocked : SafeMigrationAction.Apply,
            report.Assessments[1].Action);
        Assert.Equal(2, columns);
        Assert.Equal(0, keys);
    }

    /// <summary>
    /// Opaque allocation is deferred and the runtime layout guard rejects before the later column DDL.
    /// </summary>
    [SqlServerLiveFact]
    public async Task OpaqueMutation_DoesNotAuthorizeFromStaleLayoutCapacity()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_probe (Existing char(4000) NULL);");
        await using var context = CreateContext(connectionString);
        MigrationOperation[] operations =
        [
            new SqlOperation { Sql = "ALTER TABLE dbo.layout_probe ADD HiddenAllocation char(3000) NULL;" },
            LayoutColumn("Added", "char(2000)"),
        ];

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("sqlserver-opaque-layout"));

        var preflightColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U');");

        var preflightFailure = Record.Exception(report.ThrowIfBlocked);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var added = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND name=N'Added';");

        var hidden = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND name=N'HiddenAllocation';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.Equal("projected_structure_state_unknown", report.Assessments[^1].AnalysisCode);
        Assert.Equal(1, preflightColumns);
        Assert.Null(preflightFailure);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, added);
        Assert.Equal(1, hidden);
    }

    /// <summary>
    /// Metadata visibility without SELECT cannot turn a near-ceiling row proof into a raw permission error.
    /// </summary>
    [SqlServerLiveFact]
    public async Task RowCapacityProof_WithoutSelectPermissionFailsClosedBeforeDataBinding()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.layout_probe (A char(8000) NOT NULL,B varchar(1) NOT NULL); "
            + "INSERT dbo.layout_probe VALUES('a','x'); "
            + "CREATE USER layout_no_select WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION TO layout_no_select; "
            + "GRANT ALTER ON OBJECT::dbo.layout_probe TO layout_no_select; "
            + "DENY SELECT ON OBJECT::dbo.layout_probe TO layout_no_select;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'layout_no_select';");
        var operation = LayoutColumn("Added", "char(53)");
        IReadOnlyList<SafeMigrationProviderAnalysis>? analyses = null;
        Exception? failure;

        // Act
        try
        {
            failure = await Record.ExceptionAsync(async () =>
            {
                analyses = await context.GetService<ISafeMigrationProviderAnalyzer>()
                    .AnalyzeAsync(context, [operation]);
            });
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var added = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_probe',N'U') "
            + "AND name=N'Added';");

        // Assert
        Assert.Null(failure);
        var analysis = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<SafeMigrationProviderAnalysis>>(analyses));

        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("column_row_layout_unproven", analysis.Code);
        Assert.Equal(0, added);
    }

    private static SafeMigrationOperation LayoutColumn(
        string name,
        string storeType,
        Type? clrType = null
    ) => new(new EnsureColumnIntent("layout_probe", new ExpectedColumnDefinition(
        name, clrType ?? typeof(string), true, storeType)), SafeMigrationPolicy.ThrowIfDifferent);

    private static async Task CreateColumnLayoutAuditAsync(
        string connectionString
    )
    {
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString, "CREATE TRIGGER layout_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.layout_ddl_events VALUES(1);");
    }
}
