namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies native generated columns, scoped compiler proofs, and computed index admission.</summary>
public sealed class SqlServerSafeMigrationGeneratedColumnsIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Creates the isolated native suite using the assembly-owned container.</summary>
    /// <param name="fixture">The native SQL Server fixture.</param>
    public SqlServerSafeMigrationGeneratedColumnsIntegrationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>Constant target names cannot collide with a compiler-owned auxiliary field.</summary>
    /// <param name="name">The target spelling, including a tempdb case-insensitive alias.</param>
    [SqlServerLiveTheory]
    [InlineData("__doka_operand")]
    [InlineData("__DOKA_OPERAND")]
    public async Task ConstantTargetDoesNotCollideWithCompilerAuxiliary(string name)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.generated_nodes(Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes",
            new ExpectedColumnDefinition(name, typeof(int), false, "int",
                computedExpression: SafeMigrationSql.Literal(1)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-auxiliary-name"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>A successful constant compiler cannot invent a missing or previously dropped owner.</summary>
    /// <param name="dropped">Whether an accepted earlier operation would remove the original owner.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstantComputedProofDoesNotInventMissingOwner(bool dropped)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        if (dropped)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.generated_nodes(Id int NOT NULL);");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (dropped)
        {
            builder.DropTableIfExists("generated_nodes");
        }

        builder.EnsureColumn("generated_nodes", new ExpectedColumnDefinition("Flag", typeof(int), false, "int",
            computedExpression: SafeMigrationSql.Literal(1)), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-missing-owner"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[^1].ObservedState);
    }

    /// <summary>Native CI aliases share one operand for fresh and populated generated-column compilation.</summary>
    /// <param name="populated">Whether the source owner already contains rows.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeIdentifierAliasesUseOneComputedOperand(bool populated)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE CURRENT COLLATE Latin1_General_100_CI_AS;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var source = new ExpectedColumnDefinition("left_bound", typeof(long), false, "bigint");
        var flag = new ExpectedColumnDefinition("Flag", typeof(int), false, "int",
            computedColumnSql: "CASE WHEN [left_bound]=[LEFT_BOUND] THEN 1 ELSE 0 END");

        if (populated)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE dbo.generated_nodes(left_bound bigint NOT NULL);"
                + "INSERT dbo.generated_nodes VALUES(4);");
            builder.EnsureColumn("generated_nodes", flag, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureTable(new ExpectedTableDefinition("generated_nodes", [source, flag]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.CreateIndexIfNotExists("IX_generated_alias", "generated_nodes", ["Flag"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-alias"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>Only actual operand identity collisions with tempdb are rejected in a CS database.</summary>
    /// <param name="colliding">Whether two legal source names collapse under the instance's CI tempdb.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseSensitiveSourcesRequireUnambiguousTempdbIdentity(bool colliding)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE CURRENT COLLATE Latin1_General_100_CS_AS;");
        var second = colliding ? "l" : "R";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes([L] bigint NOT NULL,[" + second + "] bigint NOT NULL);"
            + "INSERT dbo.generated_nodes VALUES(4,1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", new ExpectedColumnDefinition("Flag", typeof(int), false, "int",
            computedColumnSql: "CASE WHEN [L]>[" + second + "] THEN 1 ELSE 0 END"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-cs-operand-identity"));

        if (!colliding)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(colliding ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        if (colliding)
        {
            var assessment = Assert.Single(report.Assessments);
            Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
            Assert.Equal("computed_column_operand_identity_unproven", assessment.AnalysisCode);
        }
    }

    /// <summary>Creates and replays an overflow-safe flag, CHECK and index in the authored storage mode.</summary>
    /// <param name="stored">Whether the authored generated flag is persisted.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshGeneratedTableAppliesReplaysAndPreservesExtrema(bool stored)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = GeneratedTable(context, stored);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-fresh"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connectionString,
            "INSERT dbo.generated_nodes (Id,L,R) VALUES (1,1,4),"
            + "(2,9223372036854775807,9223372036854775807),"
            + "(3,-9223372036854775808,-9223372036854775807);");
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-replay"));

        var flags = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.generated_nodes WHERE (Id=1 AND HasChildren=1) "
            + "OR (Id IN(2,3) AND HasChildren=0);");

        var storage = await ScalarIntAsync(connectionString,
            "SELECT CONVERT(int,is_persisted) FROM sys.computed_columns "
            + "WHERE object_id=OBJECT_ID(N'dbo.generated_nodes') AND name=N'HasChildren';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(3, flags);
        Assert.Equal(stored ? 1 : 0, storage);
    }

    /// <summary>A populated nullable operand remains safe because every CASE result is explicitly non-null.</summary>
    /// <param name="included">Whether the generated flag is an included rather than key column.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedGeneratedAdditionSupportsNullOperandsAndCoveringIndexes(bool included)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NULL,R bigint NULL); "
            + "INSERT dbo.generated_nodes VALUES(1,NULL,7),(2,9223372036854775807,9223372036854775807),(3,1,4);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", SqlServerGeneratedColumnSqlTests.Flag(),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("IX_generated_ancestor", "generated_nodes",
            included ? [new ExpectedIndexKeyDefinition("R")] : [new ExpectedIndexKeyDefinition("HasChildren"),
                new ExpectedIndexKeyDefinition("R")], includedColumns: included ? ["HasChildren"] : []),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-populated"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var flags = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.generated_nodes WHERE (Id IN(1,2) AND HasChildren=0) "
            + "OR (Id=3 AND HasChildren=1);");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(3, flags);
    }

    /// <summary>Adopts an ordinary native-created equivalent column without requiring provenance stamps.</summary>
    [SqlServerLiveFact]
    public async Task ExistingUnstampedGeneratedColumnIsRecognizedByNativeDefinition()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL,"
            + "HasChildren AS CONVERT(bit,CASE WHEN R>CASE WHEN L=9223372036854775807 "
            + "THEN L ELSE L+1 END THEN 1 ELSE 0 END));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", SqlServerGeneratedColumnSqlTests.Flag(),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-adoption"));

        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
    }

    /// <summary>Expression, result-type and persistence differences remain real drift without mutation.</summary>
    /// <param name="drift">The independently changed generated facet.</param>
    [SqlServerLiveTheory]
    [InlineData("expression")]
    [InlineData("type")]
    [InlineData("storage")]
    public async Task GeneratedColumnDriftFailsClosed(string drift)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var expression = drift == "expression"
            ? "CONVERT(bit,CASE WHEN R>L THEN 0 ELSE 1 END)"
            : "CONVERT(" + (drift == "type" ? "bigint" : "bit")
                + ",CASE WHEN R>CASE WHEN L=9223372036854775807 THEN L ELSE L+1 END THEN 1 ELSE 0 END)";

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL,"
            + "HasChildren AS " + expression + (drift == "storage" ? " PERSISTED" : string.Empty) + "); "
            + "INSERT dbo.generated_nodes(Id,L,R)VALUES(1,1,4);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", SqlServerGeneratedColumnSqlTests.Flag(),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drift"));

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.generated_nodes;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, rows);
    }

    /// <summary>Private compiler tables neither collide across sessions nor survive completed probes.</summary>
    [SqlServerLiveFact]
    public async Task ParallelCompilerSessionsUseIndependentTemporaryScopes()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL);");
        await using var context = CreateContext(connectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var sql = catalog.BuildProjectedComputedKeyProofSql("generated_nodes", null,
            SqlServerGeneratedColumnSqlTests.Flag(), null, 7);

        await using var first = new SqlConnection(connectionString);
        await using var second = new SqlConnection(connectionString);
        await first.OpenAsync();
        await second.OpenAsync();

        // Act
        var proofs = await Task.WhenAll(ReadCompilerProofAsync(first, sql), ReadCompilerProofAsync(second, sql));
        var firstScope = await CompilerScopeExistsAsync(first, sql);
        var secondScope = await CompilerScopeExistsAsync(second, sql);

        // Assert
        Assert.All(proofs, proof => Assert.InRange(proof, 1, 2));
        Assert.False(firstScope);
        Assert.False(secondScope);
    }

    /// <summary>An occupied temporary name fails closed without changing the caller-owned object.</summary>
    [SqlServerLiveFact]
    public async Task OccupiedCompilerScopePreservesCallerOwnedTable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL);");
        await using var context = CreateContext(connectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var sql = catalog.BuildProjectedComputedKeyProofSql("generated_nodes", null,
            SqlServerGeneratedColumnSqlTests.Flag(), null, 7);

        var name = CompilerScopeName(sql);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var setup = connection.CreateCommand();
        setup.CommandText = "CREATE TABLE [" + name + "](Sentinel int NOT NULL); INSERT [" + name + "]VALUES(42);";
        await setup.ExecuteNonQueryAsync();

        // Act
        var proof = await ReadCompilerProofAsync(connection, sql);
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT Sentinel FROM [" + name + "];";
        var sentinel = Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal(0, proof);
        Assert.Equal(42, sentinel);
    }

    /// <summary>Native inference must agree with the authored type before any user table is created.</summary>
    [SqlServerLiveFact]
    public async Task IncompatibleGeneratedResultTypeIsRejectedBeforeCreation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("generated_nodes",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("Flag", typeof(bool), false, "bit",
                    computedColumnSql: "CAST(1 AS bigint)")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-incompatible-type"));

        var tableCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.generated_nodes');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(0, tableCount);
    }

    /// <summary>Recognizes effective ARITHABORT and rejects incompatible options without session changes.</summary>
    /// <param name="roundAbort">Whether the independently forbidden rounding option is enabled.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComputedIndexAdmissionPreservesCallerSetOptions(bool roundAbort)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL,"
            + "HasChildren AS CONVERT(bit,CASE WHEN R>CASE WHEN L=9223372036854775807 "
            + "THEN L ELSE L+1 END THEN 1 ELSE 0 END));");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await using var settings = context.Database.GetDbConnection().CreateCommand();
        settings.CommandText = "SET ARITHABORT OFF; SET ANSI_WARNINGS ON; "
            + (roundAbort ? "SET NUMERIC_ROUNDABORT ON;" : "SET NUMERIC_ROUNDABORT OFF;");
        await settings.ExecuteNonQueryAsync();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("IX_generated_ancestor", "generated_nodes", ["HasChildren", "R"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-effective-set-options"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        await using var options = context.Database.GetDbConnection().CreateCommand();
        options.CommandText = "SELECT CONVERT(int,SESSIONPROPERTY(N'NUMERIC_ROUNDABORT'))*10 "
            + "+CONVERT(int,SESSIONPROPERTY(N'ARITHABORT'));";
        var after = Convert.ToInt32(await options.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        // Assert
        Assert.Equal(roundAbort ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(roundAbort ? 10 : 0, after);
        if (roundAbort)
        {
            Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    /// <summary>Cancellation after compiler CREATE unwinds temporary state and leaves the session reusable.</summary>
    [SqlServerLiveFact]
    public async Task CancelledNativeCompilerDoesNotRetainTemporaryState()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var definition = new ExpectedColumnDefinition("Flag", typeof(bool), false, "bit",
            computedColumnSql: "CONVERT(bit, CASE WHEN 1=1 THEN 1 ELSE 0 END)");

        var original = catalog.BuildProjectedComputedKeyProofSql("generated_nodes", null, definition, null, 7);
        var resource = "computed-probe-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        // WHY: The synchronization point is inside the real compiler's second
        // dynamic literal, after CREATE/ADD but before catalog acquisition. It
        // uses the existing out-of-band marker because SqlClient can defer INFO
        // callbacks and enter a synchronous TDS drain under its cancellation lock.
        var pause = "DECLARE @ready int; EXEC @ready=sys.sp_getapplock @Resource=N'" + resource
            + "',@LockMode=N'Exclusive',@LockOwner=N'Session',@LockTimeout=0; "
            + "IF @ready<0 THROW 51006,N'computed readiness lock failed',1; WAITFOR DELAY '00:05:00'; ";

        var body = SqlServerGuardedSqlTestContract.DecodeScope(original);
        var blocked = SqlServerGuardedSqlTestContract.EncodeScope(body.Replace("SELECT @definition = cc.definition",
            pause.Replace("'", "''''", StringComparison.Ordinal) + "SELECT @definition = cc.definition",
            StringComparison.Ordinal));
        await using var connection = new SqlConnection(connectionString);
        await using var observer = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await observer.OpenAsync();
        var marker = new SqlServerIdentityInsertPauseInterceptor(resource);
        await using var command = connection.CreateCommand();
        command.CommandText = blocked;
        command.CommandTimeout = SafeMigrationDbContext.CommandTimeoutSeconds;
        using var cancellation = new CancellationTokenSource();
        using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Exception? failure;
        bool retained;
        int repeated;
        Task<int>? execution = null;

        // Act
        try
        {
            execution = command.ExecuteNonQueryAsync(cancellation.Token);
            await marker.WaitUntilReadyAsync(observer, execution, readiness.Token);
            await cancellation.CancelAsync();
            failure = await Record.ExceptionAsync(async () => { await execution; });
            retained = await CompilerScopeExistsAsync(connection, original);
            repeated = await ReadCompilerProofAsync(connection, original);
        }
        finally
        {
            if (execution is { IsCompleted: false, })
            {
                await cancellation.CancelAsync();
                _ = await Record.ExceptionAsync(async () => { await execution; });
            }

            // WHY: The readiness lock is a test-owned session signal, not product
            // state. Release it explicitly before returning a pooled connection.
            await using var release = connection.CreateCommand();
            release.CommandText = "EXEC sys.sp_releaseapplock @Resource=@resource,@LockOwner=N'Session';";
            release.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
            await release.ExecuteNonQueryAsync();
        }

        // Assert
        Assert.True(failure is OperationCanceledException or SqlException);
        Assert.False(retained);
        Assert.InRange(repeated, 1, 2);
    }

    /// <summary>Only real computed dependencies prevent a drop; the computed column itself can be removed.</summary>
    /// <param name="column">The member to drop.</param>
    /// <param name="blocked">Whether the generated expression depends on that member.</param>
    [SqlServerLiveTheory]
    [InlineData("L", true)]
    [InlineData("Payload", false)]
    [InlineData("HasChildren", false)]
    public async Task ComputedDependenciesAreQualifiedPerActualColumn(string column, bool blocked)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL,Payload int NULL,"
            + "HasChildren AS CONVERT(bit,CASE WHEN R>CASE WHEN L=9223372036854775807 "
            + "THEN L ELSE L+1 END THEN 1 ELSE 0 END));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(column, "generated_nodes");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-dependency"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.generated_nodes') "
            + "AND name=N'" + column + "';");

        // Assert
        Assert.Equal(blocked ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(blocked ? 1 : 0, remaining);
        if (blocked)
        {
            Assert.NotNull(failure);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    /// <summary>Native textual dependencies block only source renames actually used by the computed column.</summary>
    /// <param name="column">The source member to rename.</param>
    /// <param name="blocked">Whether the computed expression enforces that source binding.</param>
    [SqlServerLiveTheory]
    [InlineData("L", true)]
    [InlineData("Payload", false)]
    public async Task ExistingComputedDependenciesQualifyOnlyTheirReferencedRename(string column, bool blocked)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(L bigint NOT NULL,R bigint NOT NULL,Payload int NULL,"
            + "HasChildren AS CONVERT(bit,CASE WHEN R>L THEN 1 ELSE 0 END));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameColumnIfExists(column, "generated_nodes", "Renamed");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-source-rename"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.generated_nodes') "
            + "AND name=N'" + column + "';");

        // Assert
        Assert.Equal(blocked ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(blocked ? 1 : 0, remaining);
        if (blocked)
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    /// <summary>Mixed operand projection uses native precision rather than the generated flag's CLR type.</summary>
    /// <param name="imprecise">Whether an authored floating-point source makes the computed key imprecise.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComputedIndexProjectionQualifiesMixedLiveAndNewOperandTypes(bool imprecise)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,R bigint NOT NULL); "
            + "INSERT dbo.generated_nodes VALUES(1,4);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", new ExpectedColumnDefinition("L",
            imprecise ? typeof(double) : typeof(long), true, imprecise ? "float" : "bigint"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn("generated_nodes", SqlServerGeneratedColumnSqlTests.Flag(),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists("IX_generated_ancestor", "generated_nodes", ["HasChildren", "R"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-mixed-operands"));

        if (!imprecise)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.generated_nodes') "
            + "AND name=N'IX_generated_ancestor';");

        // Assert
        Assert.Equal(imprecise ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(imprecise ? 0 : 1, indexes);
        if (imprecise)
        {
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[^1].Action);
        }
    }

    /// <summary>Imprecise expressions preserve native key and INCLUDE admission in each storage mode.</summary>
    /// <param name="stored">Whether the generated result is persisted.</param>
    /// <param name="included">Whether the result is included instead of used as an index key.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImpreciseGeneratedIndexAdmissionDistinguishesKeysAndIncludes(bool stored, bool included)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L float NULL); "
            + "INSERT dbo.generated_nodes VALUES(1,4),(2,NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", new ExpectedColumnDefinition("Flag", typeof(bool), false, "bit",
            computedColumnSql: "CONVERT(bit,CASE WHEN [L]>1 THEN 1 ELSE 0 END)", isStored: stored),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("IX_generated_precision", "generated_nodes",
            [new ExpectedIndexKeyDefinition(included ? "Id" : "Flag")],
            includedColumns: included ? ["Flag"] : []), SafeMigrationPolicy.ThrowIfDifferent);
        var admitted = stored || included;

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-imprecise-index"));

        if (admitted)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.generated_nodes') "
            + "AND name=N'IX_generated_precision';");

        // Assert
        Assert.Equal(admitted ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(admitted ? 1 : 0, indexes);
        if (!admitted)
        {
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[^1].Action);
        }
    }

    /// <summary>Native indexability never certifies uniqueness for duplicate generated values.</summary>
    [SqlServerLiveFact]
    public async Task PersistedComputedUniqueIndexStillRejectsDuplicateResults()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L float NULL); "
            + "INSERT dbo.generated_nodes VALUES(1,4),(2,4);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", new ExpectedColumnDefinition("Flag", typeof(bool), false, "bit",
            computedColumnSql: "CONVERT(bit,CASE WHEN [L]>1 THEN 1 ELSE 0 END)", isStored: true),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists("IX_generated_unique", "generated_nodes", ["Flag"], unique: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-duplicate-results"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.generated_nodes') "
            + "AND name=N'IX_generated_unique';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, indexes);
    }

    /// <summary>Retained dropped-column storage does not block an unmaterialized virtual expression.</summary>
    [SqlServerLiveFact]
    public async Task VirtualGeneratedAdditionRemainsValidAfterOrdinaryColumnDrop()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL,Retired int NULL); "
            + "INSERT dbo.generated_nodes VALUES(1,1,4,NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("Retired", "generated_nodes");
        builder.EnsureColumn("generated_nodes", SqlServerGeneratedColumnSqlTests.Flag(),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists("IX_generated_after_drop", "generated_nodes", ["HasChildren", "R"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-virtual-retained-layout"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var flags = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.generated_nodes WHERE Id=1 AND HasChildren=1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(1, flags);
    }

    /// <summary>A source-identity certificate never erases an accepted same-name index contract.</summary>
    /// <param name="different">Whether the second request changes the accepted index key.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComputedIndexAfterSiblingDropPreservesAcceptedIndexIdentity(bool different)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL,Retired int NULL); "
            + "INSERT dbo.generated_nodes VALUES(1,1,4,NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("Retired", "generated_nodes");
        builder.EnsureColumn("generated_nodes", SqlServerGeneratedColumnSqlTests.Flag(),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists("IX_generated_identity", "generated_nodes", ["HasChildren"]);
        builder.CreateIndexIfNotExists("IX_generated_identity", "generated_nodes",
            [different ? "R" : "HasChildren"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-accepted-index-identity"));

        if (!different)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(different ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(different ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.NoOp,
            report.Assessments[^1].Action);
    }

    /// <summary>Computed source proofs never erase an accepted constraint's backing index identity.</summary>
    /// <param name="primary">Whether the accepted key is PRIMARY KEY rather than UNIQUE.</param>
    /// <param name="dropped">Whether an accepted removal releases the backing index name.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ComputedIndexAfterSiblingDropPreservesConstraintBackingNames(bool primary, bool dropped)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NOT NULL,R bigint NOT NULL,Retired int NULL); "
            + "INSERT dbo.generated_nodes VALUES(1,1,4,NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (primary)
        {
            builder.AddPrimaryKeyIfNotExists("IX_result", "generated_nodes", ["R"]);
        }
        else
        {
            builder.AddUniqueConstraintIfNotExists("IX_result", "generated_nodes", ["R"]);
        }

        if (dropped)
        {
            if (primary)
            {
                builder.DropPrimaryKeyIfExists("IX_result", "generated_nodes");
            }
            else
            {
                builder.DropUniqueConstraintIfExists("IX_result", "generated_nodes");
            }
        }

        builder.DropColumnIfExists("Retired", "generated_nodes");
        builder.EnsureColumn("generated_nodes", SqlServerGeneratedColumnSqlTests.Flag(),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists("IX_result", "generated_nodes", ["HasChildren"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-constraint-backing-index"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var independent = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.generated_nodes') "
            + "AND name=N'IX_result' AND is_primary_key=0 AND is_unique_constraint=0;");

        // Assert
        Assert.Equal(dropped ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(dropped ? 1 : 0, independent);
        if (dropped)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
    }

    /// <summary>A dropped or replaced operand cannot reuse its original native expression certificate.</summary>
    /// <param name="replace">Whether the original name is reused for a different native type.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DroppedComputedSourceCannotReuseOriginalNativeProof(bool replace)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,L bigint NULL,R bigint NOT NULL); "
            + "INSERT dbo.generated_nodes VALUES(1,1,4);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("L", "generated_nodes");
        if (replace)
        {
            builder.EnsureColumn("generated_nodes", new ExpectedColumnDefinition("L", typeof(double), true, "float"),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.EnsureColumn("generated_nodes", SqlServerGeneratedColumnSqlTests.Flag(),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.CreateIndexIfNotExists("IX_generated_after_replacement", "generated_nodes", ["HasChildren"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-invalidated-source"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^1].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^2].Action);
        if (replace)
        {
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
            Assert.Equal("column_layout_unproven", report.Assessments[1].AnalysisCode);
        }
    }

    /// <summary>A source rename is legal before, but not after, an enforced computed dependency exists.</summary>
    /// <param name="dependent">Whether the initial table already declares the dependent expression.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceRenameRespectsComputedDependencyCreationOrder(bool dependent)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var columns = new List<ExpectedColumnDefinition>
        {
            new("Id", typeof(int), false, "int"),
            new("L", typeof(long), false, "bigint"),
            new("R", typeof(long), false, "bigint"),
        };

        if (dependent)
        {
            columns.Add(SqlServerGeneratedColumnSqlTests.Flag());
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("generated_nodes", columns),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameColumnIfExists("L", "generated_nodes", "LeftValue");
        var flag = new ExpectedColumnDefinition("HasChildren", typeof(bool), false,
            "bit", computedColumnSql: "CONVERT(bit, CASE WHEN [R] > CASE WHEN [LeftValue] = 9223372036854775807 "
                + "THEN [LeftValue] ELSE [LeftValue]+1 END THEN 1 ELSE 0 END)");

        builder.EnsureColumn("generated_nodes", flag, SafeMigrationPolicy.ThrowIfDifferent);
        var final = new MigrationBuilder(context.Database.ProviderName!);
        final.EnsureTable(new ExpectedTableDefinition("generated_nodes",
            [columns[0], SafeMigrationPreflightProjection.CopyProjectedColumn(columns[1], "LeftValue"),
                columns[2], flag]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-source-rename"));

        SafeMigrationRunReport? verified = null;
        if (!dependent)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            // WHY: Verify checks the reviewed final schema, not the superseded
            // three-column strict table contract preceding the later ADD.
            verified = await runner.VerifyAsync(context, final.Operations,
                new SafeMigrationRunOptions("generated-source-rename-verify"));
        }

        // Assert
        Assert.Equal(dependent ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        if (dependent)
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
            Assert.Equal("projected_computed_source_dependency", report.Assessments[1].AnalysisCode);
        }
        else
        {
            Assert.NotNull(verified);
            Assert.Equal(SafeMigrationReportStatus.Ready, verified.Status);
        }
    }

    /// <summary>
    /// CHECK dependency roles follow native CI/CS identity and expire after accepted constraint removal.
    /// </summary>
    /// <param name="scenario">The native identity and safe/ordinary declaration or removal combination.</param>
    [SqlServerLiveTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task DeclaredCheckRenameUsesNativeColumnIdentityAndAcceptedLineage(int scenario)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var sensitive = scenario is 1 or 2;
        var blocked = scenario is 0 or 1 or 4;
        var dropped = scenario is 3 or 5 or 6;
        var providerCreated = scenario is 4 or 6;
        await ExecuteSqlAsync(connectionString,
            "ALTER DATABASE CURRENT COLLATE Latin1_General_100_" + (sensitive ? "CS_AS" : "CI_AS") + ";");
        await using var context = CreateContext(connectionString);
        var columns = sensitive
            ? new ExpectedColumnDefinition[]
                { new("L", typeof(int), false, "int"), new("l", typeof(int), false, "int") }
            : [new("Upper", typeof(int), false, "int"), new("Payload", typeof(int), false, "int")];

        var sql = sensitive ? "[L]>0" : "[upper]>0";
        var expression = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var parsed)
            ? parsed : throw new InvalidOperationException("The native CHECK fixture must parse.");

        var check = ExpectedCheckConstraintDefinition.FromCapturedExpression(
            "CK_checked_source", "checked_nodes", expression, sql, schema: null);

        var source = sensitive ? scenario == 2 ? "l" : "L" : "Upper";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (providerCreated)
        {
            builder.CreateTable("checked_nodes",
                static table => new
                {
                    Upper = table.Column<int>(nullable: false),
                    Payload = table.Column<int>(nullable: false),
                }, constraints: table => table.CheckConstraint(check.Name, sql));
        }
        else
        {
            builder.EnsureTable(new ExpectedTableDefinition("checked_nodes", columns, checkConstraints: [check]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        if (dropped)
        {
            if (scenario is 5 or 6)
            {
                builder.DropCheckConstraint(check.Name, "checked_nodes");
            }
            else
            {
                builder.DropCheckConstraintIfExists(check.Name, "checked_nodes");
            }
        }

        builder.RenameColumnIfExists(source, "checked_nodes", "NewValue");
        var final = new MigrationBuilder(context.Database.ProviderName!);
        final.EnsureTable(new ExpectedTableDefinition("checked_nodes",
            columns.Select(column => column.Name == source
                ? SafeMigrationPreflightProjection.CopyProjectedColumn(column, "NewValue") : column),
            checkConstraints: dropped ? [] : [check]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("declared-check-native-rename"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        SafeMigrationRunReport? verified = null;
        if (!blocked)
        {
            verified = await runner.VerifyAsync(context, final.Operations,
                new SafeMigrationRunOptions("declared-check-native-rename-verify"));
        }

        // Assert
        Assert.Equal(blocked ? SafeMigrationReportStatus.Blocked
            : scenario is 5 or 6 ? SafeMigrationReportStatus.ReadyWithProviderOperations
            : SafeMigrationReportStatus.Ready, report.Status);
        if (blocked)
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
            Assert.Equal("projected_check_source_dependency", report.Assessments[^1].AnalysisCode);
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
            Assert.NotNull(verified);
            Assert.Equal(SafeMigrationReportStatus.Ready, verified.Status);
        }
    }

    /// <summary>An unapplied ExistenceOnly wish cannot replace a previously accepted CHECK dependency.</summary>
    /// <param name="different">Whether the reasserted CHECK requests a different member.</param>
    /// <param name="dependent">Whether the rename targets the physically accepted CHECK member.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckRenameUsesAcceptedDefinitionRatherThanExistenceOnlyWish(bool different, bool dependent)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var columns = new ExpectedColumnDefinition[]
            { new("Upper", typeof(int), false, "int"), new("Payload", typeof(int), false, "int") };

        var original = Check("Upper");
        var requested = Check(different ? "Payload" : "Upper");
        var source = dependent ? "Upper" : "Payload";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("checked_nodes", columns, checkConstraints: [original]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureCheckConstraint(requested, SafeMigrationPolicy.ExistenceOnly);
        builder.RenameColumnIfExists(source, "checked_nodes", "NewValue");
        var final = new MigrationBuilder(context.Database.ProviderName!);
        final.EnsureTable(new ExpectedTableDefinition("checked_nodes",
            columns.Select(column => column.Name == source
                ? SafeMigrationPreflightProjection.CopyProjectedColumn(column, "NewValue") : column),
            checkConstraints: [original]), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("declared-check-existence-only-lineage"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        SafeMigrationRunReport? verified = null;
        if (!dependent)
        {
            verified = await runner.VerifyAsync(context, final.Operations,
                new SafeMigrationRunOptions("declared-check-existence-only-verify"));
        }

        // Assert
        Assert.Equal(dependent ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal(different ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            report.Assessments[1].ObservedState);
        if (dependent)
        {
            Assert.Equal("projected_check_source_dependency", report.Assessments[^1].AnalysisCode);
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
            Assert.NotNull(verified);
            Assert.Equal(SafeMigrationReportStatus.Ready, verified.Status);
        }

        static ExpectedCheckConstraintDefinition Check(string column)
        {
            var sql = "[" + column + "]>0";
            var expression = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var parsed)
                ? parsed : throw new InvalidOperationException("The native CHECK fixture must parse.");

            return ExpectedCheckConstraintDefinition.FromCapturedExpression(
                "CK_checked_source", "checked_nodes", expression, sql, schema: null);
        }
    }

    /// <summary>Character expressions retain the compiler-inferred or explicitly requested output collation.</summary>
    /// <param name="explicitCollation">Whether the generated output overrides its source collation.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCharacterCollationMatchesTheNativeExpression(bool explicitCollation)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.generated_nodes(Id int NOT NULL,Code nvarchar(80) "
            + "COLLATE Latin1_General_100_BIN2 NULL); INSERT dbo.generated_nodes VALUES(1,N'A');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("generated_nodes", new ExpectedColumnDefinition("Normalized", typeof(string), true,
            "nvarchar(80)", collation: explicitCollation
                ? new SafeMigrationCollationIdentifier("Latin1_General_100_CI_AS") : null,
            computedColumnSql: "LOWER([Code])"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-collation"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var collation = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.generated_nodes') "
            + "AND name=N'Normalized' AND collation_name=N'"
            + (explicitCollation ? "Latin1_General_100_CI_AS" : "Latin1_General_100_BIN2") + "';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(1, collation);
    }

    private static MigrationBuilder GeneratedTable(DbContext context, bool stored)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("generated_nodes",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("L", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("R", typeof(long), false, "bigint"),
                SqlServerGeneratedColumnSqlTests.Flag(stored)],
            primaryKey: new ExpectedPrimaryKeyDefinition("PK_generated_nodes", "generated_nodes", ["Id"]),
            checkConstraints: [new ExpectedCheckConstraintDefinition("CK_generated_range", "generated_nodes", "R>=L")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("IX_generated_ancestor", "generated_nodes",
            [new ExpectedIndexKeyDefinition("L"), new ExpectedIndexKeyDefinition("HasChildren"),
                new ExpectedIndexKeyDefinition("R")]), SafeMigrationPolicy.ThrowIfDifferent);

        return builder;
    }

    private static async Task<int> ReadCompilerProofAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7, reader.GetInt32(0));

        return reader.GetInt32(1);
    }

    private static string CompilerScopeName(string sql)
        => System.Text.RegularExpressions.Regex.Match(sql, "#doka_sm_computed_[A-F0-9]{24}").Value;

    private static async Task<bool> CompilerScopeExistsAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'tempdb.." + CompilerScopeName(sql)
            + "') IS NULL THEN 0 ELSE 1 END;";

        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 1;
    }
}
