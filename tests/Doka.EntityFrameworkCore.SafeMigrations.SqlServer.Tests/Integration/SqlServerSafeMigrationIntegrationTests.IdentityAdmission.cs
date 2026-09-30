namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Metadata visibility cannot bind even a skipped row arm of an existing identity classifier.</summary>
    [SqlServerLiveFact]
    public async Task ExistingIdentityWithoutSelect_IsStructuredUnsupportedInsteadOfPermissionFailure()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_admission (Id int IDENTITY(1,1) NOT NULL, Value int NOT NULL); "
            + "CREATE USER identity_metadata_reader WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION TO identity_metadata_reader;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'identity_metadata_reader';");
        var operation = IdentityAdmissionColumn("Id", "int", "1, 1");
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses;
        Exception? failure;

        // Act
        try
        {
            analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id=OBJECT_ID(N'dbo.identity_admission');");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("column_row_layout_unproven", analysis.Code);
        Assert.True(analysis.IsInvariantUnsupported);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, columns);
    }

    /// <summary>Creates the first identity on an ordinary empty table and safely replays its exact contract.</summary>
    [SqlServerLiveTheory]
    [InlineData("int", "1, 1")]
    [InlineData("tinyint", "1, 1")]
    [InlineData("smallint", "1, -1")]
    [InlineData("decimal(38,0)", "9223372036854775807, 1")]
    public async Task FirstIdentityOnEmptyTable_AppliesAndReplays(
        string storeType,
        string identity
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.identity_admission (Value int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var operation = IdentityAdmissionColumn("Id", storeType, identity);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var before = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var after = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var count = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'dbo.identity_admission');");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(before).ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(after).ObservedState);
        Assert.True(Assert.Single(after).PostconditionSatisfied);
        Assert.Equal(1, count);
    }

    /// <summary>An occupied physical identity slot rejects another identity before row reads or column DDL.</summary>
    [SqlServerLiveFact]
    public async Task SecondPhysicalIdentity_IsPrerequisiteMissingWithoutMutation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_admission (First int IDENTITY(1,1) NOT NULL, Value int NOT NULL); "
            + "CREATE TABLE dbo.identity_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER identity_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.identity_ddl_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var operation = IdentityAdmissionColumn("Second", "int", "1, 1");

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var identityCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'dbo.identity_admission');");

        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.identity_admission');");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.identity_ddl_events;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("identity_slot_occupied", analysis.Code);
        Assert.Equal(51004, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, identityCount);
        Assert.Equal(2, columns);
        Assert.Equal(0, events);
    }

    /// <summary>Unmodeled replication identity semantics differ from an ordinary authored identity.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityReplicationFacet_IsPartOfExactMatching(
        bool notForReplication
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_admission (Id int IDENTITY(1,1) "
            + (notForReplication ? "NOT FOR REPLICATION " : string.Empty) + "NOT NULL, Value int NOT NULL); "
            + "INSERT dbo.identity_admission(Value) VALUES (7); "
            + "CREATE TABLE dbo.identity_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER identity_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.identity_ddl_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var operation = IdentityAdmissionColumn("Id", "int", "1, 1");

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var physicalFacet = await ScalarIntAsync(connectionString,
            "SELECT CONVERT(int,is_not_for_replication) FROM sys.identity_columns "
            + "WHERE object_id = OBJECT_ID(N'dbo.identity_admission');");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.identity_admission WHERE Value=7;");
        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.identity_ddl_events;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(notForReplication ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            analysis.ObservedState);
        if (notForReplication)
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
            Assert.True(analysis.PostconditionSatisfied);
        }

        Assert.Equal(notForReplication ? 1 : 0, physicalFacet);
        Assert.Equal(1, rows);
        Assert.Equal(0, events);
    }

    /// <summary>Wide physical decimal seeds mismatch without narrowing sql_variant to Int64.</summary>
    [SqlServerLiveFact]
    public async Task WideExistingIdentitySeed_IsAStableMismatchWithoutOverflow()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_admission (Id decimal(38,0) IDENTITY(100000000000000000000,1) NOT NULL, "
            + "Value int NOT NULL); CREATE TABLE dbo.identity_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER identity_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.identity_ddl_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var operation = IdentityAdmissionColumn("Id", "decimal(38,0)", "1, 1");

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id=OBJECT_ID(N'dbo.identity_admission') "
            + "AND CONVERT(decimal(38,0),seed_value)=100000000000000000000;");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.identity_ddl_events;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(analyses).ObservedState);
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, preserved);
        Assert.Equal(0, events);
    }

    /// <summary>Provider projection cannot authorize two identities on a newly authored table.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedNewTableIdentitySlots_BlockTheSecondIdentityBeforeExecution(
        bool inlineFirst
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var first = IdentityAdmissionColumn("First", "int", "1, 1");
        var second = IdentityAdmissionColumn("Second", "int", "1, 1");
        var columns = inlineFirst ? new[] { ((EnsureColumnIntent)first.Intent).Definition }
            : [new ExpectedColumnDefinition("Value", typeof(int), false, "int")];

        var table = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
            "identity_admission", columns), SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        MigrationOperation[] operations = inlineFirst ? [table, second] : [table, first, second];

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("identity-slot-projection"));

        var tables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.identity_admission');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[^1].ObservedState);
        Assert.Equal(0, tables);
    }

    /// <summary>A new ordinary table may accept its first identity and an exact replay.</summary>
    [SqlServerLiveFact]
    public async Task OrderedNewOrdinaryTable_AllowsTheFirstIdentity()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var table = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
            "identity_admission", [new ExpectedColumnDefinition("Value", typeof(int), false, "int")]),
            SafeMigrationTableMode.ConvergenceContainer), SafeMigrationPolicy.ExistenceOnly);

        var identity = IdentityAdmissionColumn("Id", "int", "1, 1");
        MigrationOperation[] operations = [table, identity];
        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("identity-first-projection");

        // Act
        var before = await runner.AnalyzeAsync(context, operations, options);
        await ExecuteOperationsAsync(context, operations);
        var after = await runner.VerifyAsync(context, operations, options);
        await ExecuteOperationsAsync(context, operations);
        var count = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'dbo.identity_admission');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, before.Status);
        Assert.Equal(SafeMigrationAction.Apply, before.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, after.Status);
        Assert.True(after.Assessments[1].PostconditionSatisfied);
        Assert.Equal(1, count);
    }

    private static SafeMigrationOperation IdentityAdmissionColumn(
        string name,
        string storeType,
        string identity
    )
    {
        var source = new AddColumnOperation();
        source["SqlServer:Identity"] = identity;
        var clr = storeType.StartsWith("decimal", StringComparison.Ordinal) ? typeof(decimal)
            : storeType == "tinyint" ? typeof(byte) : storeType == "smallint" ? typeof(short) : typeof(int);

        var column = new ExpectedColumnDefinition(name, clr, false, storeType)
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(source),
        };

        return new SafeMigrationOperation(new EnsureColumnIntent("identity_admission", column),
            SafeMigrationPolicy.ThrowIfDifferent);
    }
}
