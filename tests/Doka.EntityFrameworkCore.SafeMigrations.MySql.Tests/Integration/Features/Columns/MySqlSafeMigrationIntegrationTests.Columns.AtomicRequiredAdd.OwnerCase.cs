namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ColumnsAtomicRequiredAddOwnerCase migration contracts in an independent class collection.</summary>
public sealed partial class MySqlColumnsAtomicRequiredAddOwnerCaseIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsAtomicRequiredAddOwnerCaseIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// Required SQL-default eligibility binds mixed-case authored owners through native mode-one identity.
    /// </summary>
    [Fact]
    public async Task AtomicRequiredAddModeOneSqlDefaultOwnerAppliesAndMatchingRemainsNoOp()
    {
        // Arrange
        var ownerFixture = new MySqlEngineContainerFixture(lowerCaseTableNames: 1);
        try
        {
            await ownerFixture.InitializeAsync();
            var connectionString = await ownerFixture.CreateDatabaseAsync(CancellationToken.None);
            await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql("MixedItems"));
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync(CancellationToken.None);
            await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES';");
            var operation = new SafeMigrationOperation(new EnsureColumnIntent("MixedItems",
                    new ExpectedColumnDefinition("required_value", typeof(int), false, "int",
                        defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(7)))),
                SafeMigrationPolicy.ThrowIfDifferent);

            var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);
            var runner = context.GetService<ISafeMigrationRunner>();

            // Act
            var preflight = await runner.AnalyzeAsync(context, [operation],
                new SafeMigrationRunOptions("required-sql-default-mode-one-owner"), CancellationToken.None);

            preflight.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
            await ExecuteSqlAsync(connectionString,
                "INSERT INTO `mixeditems` (`key`, `required_value`) VALUES (1, 11);");
            await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = '';");
            var rerun = await runner.AnalyzeAsync(context, [operation],
                new SafeMigrationRunOptions("required-sql-default-mode-one-matching"), CancellationToken.None);

            rerun.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
            var postflight = await runner.VerifyAsync(context, [operation],
                new SafeMigrationRunOptions("required-sql-default-mode-one-final"), CancellationToken.None);

            var ownerMode = await ScalarIntAsync(connectionString, "SELECT @@lower_case_table_names;");
            var physicalOwner = await ScalarStringAsync(connectionString,
                "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = 'MixedItems';");

            var requiredColumns = await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
                + "AND BINARY TABLE_NAME = BINARY 'mixeditems' AND COLUMN_NAME = 'required_value' "
                + "AND IS_NULLABLE = 'NO';");

            var defaultRows = await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
                + "AND BINARY TABLE_NAME = BINARY 'mixeditems' AND COLUMN_NAME = 'required_value' "
                + "AND COLUMN_DEFAULT = '7';");

            var preserved = await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM `mixeditems` WHERE `key` = 1 AND `required_value` = 11;");

            // Assert
            Assert.Equal(1, ownerMode);
            Assert.Equal("mixeditems", physicalOwner);
            Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
            Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
            Assert.Equal(SafeMigrationReportStatus.Ready, rerun.Status);
            Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(rerun.Assessments).Action);
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
            Assert.Equal(1, requiredColumns);
            Assert.Equal(1, defaultRows);
            Assert.Equal(1, preserved);
            Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
        }
        finally
        {
            await ownerFixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Native folded owners retain their raw authored guard identity through interrupted add and recovery.
    /// </summary>
    [Fact]
    public async Task AtomicRequiredAddModeOneMixedOwnerRecoversAndPreservesExplicitRows()
    {
        // Arrange
        var ownerFixture = new MySqlEngineContainerFixture(lowerCaseTableNames: 1);
        try
        {
            await ownerFixture.InitializeAsync();
            var connectionString = await ownerFixture.CreateDatabaseAsync(CancellationToken.None);
            await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql("MixedItems"));
            var operation = AtomicRequiredAddOperation(table: "MixedItems");
            var guardName = AtomicRequiredAddGuardName(operation);
            var probe = new AtomicRequiredAddStageProbe(guardName,
                "SELECT * FROM `atomic_required_add_missing_probe`;");

            await using var context = CreateRuntimeContext(connectionString, probe);
            var runner = context.GetService<ISafeMigrationRunner>();
            const string explicitValue = "00000000-0000-0000-0000-000000000031";

            // Act
            var interrupted = await Record.ExceptionAsync(() =>
                ExecuteOperationsAsync(context, [operation], CancellationToken.None));

            await ExecuteSqlAsync(connectionString,
                "INSERT INTO `mixeditems` (`key`, `required_value`) VALUES (1, "
                + AtomicRequiredAddLiteral(explicitValue) + ");");
            var staged = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
            var ownerMode = await ScalarIntAsync(connectionString, "SELECT @@lower_case_table_names;");
            var physicalOwner = await ScalarStringAsync(connectionString,
                "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = 'MixedItems';");

            var inventory = await context.GetService<ISafeMigrationProviderAnalyzer>().FindUnexpectedObjectsAsync(
                context, AtomicRequiredAddOwnerInventoryOperations([operation]), CancellationToken.None);

            var resume = await runner.AnalyzeAsync(context, [operation],
                new SafeMigrationRunOptions("atomic-add-mode-one-resume"), CancellationToken.None);

            resume.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
            var postflight = await runner.VerifyAsync(context, [operation],
                new SafeMigrationRunOptions("atomic-add-mode-one-final"), CancellationToken.None);

            var final = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
            var preserved = await ScalarStringAsync(connectionString,
                "SELECT `required_value` FROM `mixeditems` WHERE `key` = 1;");

            // Assert
            Assert.Equal(1, ownerMode);
            Assert.Equal("mixeditems", physicalOwner);
            Assert.Equal(1146, Assert.IsType<MySqlException>(interrupted).Number);
            Assert.True(probe.Injected);
            Assert.Equal(new AtomicRequiredAddState(1, 1, 1, 1), staged);
            Assert.DoesNotContain(inventory, item => item.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint
                && item.Name == guardName);
            Assert.Equal(SafeMigrationReportStatus.Ready, resume.Status);
            Assert.Equal(SafeMigrationAction.Apply, Assert.Single(resume.Assessments).Action);
            Assert.Equal("column_atomic_add_resume", Assert.Single(resume.Assessments).AnalysisCode);
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
            Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 1), final);
            Assert.Equal(explicitValue, preserved);
        }
        finally
        {
            await ownerFixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Two authored owner cases for one physical target reject inventory before hiding its recovery CHECK.
    /// </summary>
    [Fact]
    public async Task AtomicRequiredAddModeOneAliasedPendingTargetRejectsAmbiguousInventory()
    {
        // Arrange
        var ownerFixture = new MySqlEngineContainerFixture(lowerCaseTableNames: 1);
        try
        {
            await ownerFixture.InitializeAsync();
            var connectionString = await ownerFixture.CreateDatabaseAsync(CancellationToken.None);
            await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql("MixedItems"));
            var original = AtomicRequiredAddOperation(table: "MixedItems");
            var alias = AtomicRequiredAddOperation(table: "mixeditems");
            var originalGuard = AtomicRequiredAddGuardName(original);
            var aliasGuard = AtomicRequiredAddGuardName(alias);
            var probe = new AtomicRequiredAddStageProbe(originalGuard,
                "SELECT * FROM `atomic_required_add_missing_probe`;");

            await using var context = CreateRuntimeContext(connectionString, probe);

            // Act
            var interrupted = await Record.ExceptionAsync(() =>
                ExecuteOperationsAsync(context, [original], CancellationToken.None));

            var unambiguous = await context.GetService<ISafeMigrationProviderAnalyzer>().FindUnexpectedObjectsAsync(
                context, AtomicRequiredAddOwnerInventoryOperations([original]), CancellationToken.None);

            // WHY: Full table inventory rejects duplicate physical expected
            // definitions before recovery filtering. This fail-closed boundary
            // must remain distinct from a successful inventory containing a CHECK.
            var inventoryFailure = await Record.ExceptionAsync(() =>
                context.GetService<ISafeMigrationProviderAnalyzer>().FindUnexpectedObjectsAsync(
                    context, AtomicRequiredAddOwnerInventoryOperations([original, alias]), CancellationToken.None));

            var aliasReport = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [alias],
                new SafeMigrationRunOptions("atomic-add-mode-one-other-authored-owner"), CancellationToken.None);

            var aliasFailure = await Record.ExceptionAsync(() =>
                ExecuteOperationsAsync(context, [alias], CancellationToken.None));

            var state = await ReadAtomicRequiredAddStateAsync(connectionString, original);
            var ownerMode = await ScalarIntAsync(connectionString, "SELECT @@lower_case_table_names;");

            // Assert
            Assert.Equal(1, ownerMode);
            Assert.NotEqual(originalGuard, aliasGuard);
            Assert.Equal(1146, Assert.IsType<MySqlException>(interrupted).Number);
            Assert.True(probe.Injected);
            Assert.DoesNotContain(unambiguous,
                item => item.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint
                    && item.Name == originalGuard);
            Assert.Contains("multiple expected table definitions for one MySQL database object",
                Assert.IsType<InvalidOperationException>(inventoryFailure).Message, StringComparison.Ordinal);
            Assert.Equal(SafeMigrationReportStatus.Blocked, aliasReport.Status);
            Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(aliasReport.Assessments).Action);
            Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(aliasFailure).Message,
                StringComparison.Ordinal);
            Assert.Equal(new AtomicRequiredAddState(1, 1, 1, 0), state);
        }
        finally
        {
            await ownerFixture.DisposeAsync();
        }
    }

    /// <summary>Mode-zero case-distinct tables cannot use one owner's staged CHECK to recover another owner.</summary>
    [Fact]
    public async Task AtomicRequiredAddModeZeroDistinctOwnersCannotBorrowRecoveryGuard()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var original = AtomicRequiredAddOperation(table: "MixedItems");
        var other = AtomicRequiredAddOperation(table: "mixeditems");
        var originalGuard = AtomicRequiredAddGuardName(original);
        var otherGuard = AtomicRequiredAddGuardName(other);
        await ExecuteSqlAsync(connectionString,
            AtomicRequiredAddStagedTableSql(original, originalGuard, "`required_value` IS NOT NULL")
            + AtomicRequiredAddStagedTableSql(other, guardName: null, expression: "`required_value` IS NOT NULL"));

        await using var context = CreateContext(connectionString);

        // Act
        var ownerMode = await ScalarIntAsync(connectionString, "SELECT @@lower_case_table_names;");
        var physicalOwners = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + "AND BINARY TABLE_NAME IN (BINARY 'MixedItems', BINARY 'mixeditems');");

        var inventory = await context.GetService<ISafeMigrationProviderAnalyzer>().FindUnexpectedObjectsAsync(
            context, AtomicRequiredAddOwnerInventoryOperations([original, other]), CancellationToken.None);

        var otherReport = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [other],
            new SafeMigrationRunOptions("atomic-add-mode-zero-distinct-owner"), CancellationToken.None);

        var otherFailure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [other], CancellationToken.None));

        var originalState = await ReadAtomicRequiredAddExactOwnerStateAsync(connectionString, original);
        var otherState = await ReadAtomicRequiredAddExactOwnerStateAsync(connectionString, other);

        // Assert
        Assert.Equal(0, ownerMode);
        Assert.Equal(2, physicalOwners);
        Assert.NotEqual(originalGuard, otherGuard);
        Assert.DoesNotContain(inventory,
            item => item.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint && item.Name == originalGuard);
        Assert.Equal(SafeMigrationReportStatus.Blocked, otherReport.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(otherReport.Assessments).Action);
        Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(otherFailure).Message,
            StringComparison.Ordinal);
        Assert.Equal(new AtomicRequiredAddState(1, 1, 1, 0), originalState);
        Assert.Equal(new AtomicRequiredAddState(1, 1, 0, 0), otherState);
    }

    /// <summary>Snapshot classification cannot borrow a case-distinct owner's already required target column.</summary>
    /// <param name="requestedLowerCase">
    /// Whether the absent requested column belongs to the lowercase physical owner.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicRequiredAddModeZeroSnapshotCannotBorrowMatchingShadow(bool requestedLowerCase)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var requestedTable = requestedLowerCase ? "mixeditems" : "MixedItems";
        var shadowTable = requestedLowerCase ? "MixedItems" : "mixeditems";
        var requested = AtomicRequiredAddOperation(useGuidStorage: false, table: requestedTable);
        var shadow = AtomicRequiredAddOperation(useGuidStorage: false, table: shadowTable);
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql(requestedTable)
            + AtomicRequiredAddStagedTableSql(shadow, guardName: null, expression: "`required_value` IS NOT NULL",
                columnType: "int", nullable: false)
            + " INSERT INTO " + AtomicRequiredAddDelimited(shadowTable)
            + " (`key`, `required_value`) VALUES (1, 7); "
            + "CREATE TABLE `benign_required_add_owner` (`value` int NULL) ENGINE=InnoDB;");

        var benign = new SafeMigrationOperation(new EnsureColumnIntent("benign_required_add_owner",
            new ExpectedColumnDefinition("value", typeof(int), true, "int")), SafeMigrationPolicy.ThrowIfDifferent);

        // WHY: Real matching operations reach the MariaDB snapshot threshold;
        // a missing owner would short-circuit before the faulty terminal match.
        var operations = Enumerable.Repeat<MigrationOperation>(benign,
                SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
            .Append(requested).ToArray();

        await using var observer = new RequiredAddCatalogTransportObserver(connectionString);
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseMySql(observer, Fixture.ServerVersion)
            .UseMySqlSafeMigrations<SafeMigrationDbContext>()
            .Options;

        await using var analysisContext = new SafeMigrationDbContext(options);
        await using var runtimeContext = CreateContext(connectionString);
        var original = await ReadAtomicRequiredAddExactOwnerStateAsync(connectionString, requested);

        // Act
        var preflight = await analysisContext.GetService<ISafeMigrationRunner>().AnalyzeAsync(analysisContext,
            operations, new SafeMigrationRunOptions("atomic-add-case-distinct-snapshot"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(runtimeContext, [requested], CancellationToken.None);
        var postflight = await runtimeContext.GetService<ISafeMigrationRunner>().VerifyAsync(runtimeContext,
            [requested], new SafeMigrationRunOptions("atomic-add-case-distinct-final"), CancellationToken.None);

        var ownerMode = await ScalarIntAsync(connectionString, "SELECT @@lower_case_table_names;");
        var targetFinal = await ReadAtomicRequiredAddExactOwnerStateAsync(connectionString, requested);
        var shadowFinal = await ReadAtomicRequiredAddExactOwnerStateAsync(connectionString, shadow);
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM " + AtomicRequiredAddDelimited(shadowTable)
            + " WHERE `key` = 1 AND `required_value` = 7;");

        // Assert
        Assert.Equal(0, ownerMode);
        Assert.Equal(new AtomicRequiredAddState(0, 0, 0, 0), original);
        Assert.True(observer.RequiredAddFacts > 0, "The regression must execute required-add catalog certification.");
        Assert.True(observer.NativeBatches > 0, "The observer must retain the actual native batch transport.");
        Assert.Equal(Fixture.IsMariaDb, observer.Snapshots > 0);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments.Take(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement),
            assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[^1].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 0), targetFinal);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 1), shadowFinal);
        Assert.Equal(1, preserved);
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{

    /// <summary>Declares only test-owned expected objects while preserving each authored owner and target.</summary>
    /// <param name="pending">The complete pending required-column stream used for ownership reconciliation.</param>
    /// <returns>The expected owner definitions followed by the untouched pending column operations.</returns>
    protected static List<MigrationOperation> AtomicRequiredAddOwnerInventoryOperations(
        IReadOnlyList<SafeMigrationOperation> pending
    )
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in pending)
        {
            var intent = Assert.IsType<EnsureColumnIntent>(operation.Intent);
            if (owners.Add(intent.Table))
            {
                // WHY: Raw authored owners stay distinct here. Only the live
                // provider may resolve whether the server folds their identity.
                builder.EnsureTable(new ExpectedTableDefinition(intent.Table,
                        [new ExpectedColumnDefinition("key", typeof(int), false, "int"), intent.Definition],
                        primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", intent.Table, ["key"])),
                    SafeMigrationTableMode.ConvergenceContainer, SafeMigrationPolicy.ExistenceOnly);
            }
        }

        foreach (var operation in pending)
        {
            builder.Operations.Add(operation);
        }

        return builder.Operations;
    }
}

public sealed partial class MySqlColumnsAtomicRequiredAddOwnerCaseIntegrationTests : MySqlSafeMigrationIntegrationTests
{

    /// <summary>Observes case-distinct mode-zero owners without catalog-collation aliases.</summary>
    /// <param name="connectionString">The isolated mode-zero database.</param>
    /// <param name="operation">The original authored physical owner and guard contract.</param>
    /// <returns>The exact owner state used to prove that another table was not modified.</returns>
    private static async Task<AtomicRequiredAddState> ReadAtomicRequiredAddExactOwnerStateAsync(
        string connectionString,
        SafeMigrationOperation operation
    )
    {
        var intent = Assert.IsType<EnsureColumnIntent>(operation.Intent);
        // WHY: INFORMATION_SCHEMA name columns have their own case-insensitive
        // collation even when mode zero keeps physical table owners distinct.
        var owner = "TABLE_SCHEMA = DATABASE() AND BINARY TABLE_NAME = BINARY "
            + AtomicRequiredAddLiteral(intent.Table);

        var column = owner + " AND BINARY COLUMN_NAME = BINARY "
            + AtomicRequiredAddLiteral(intent.Definition.Name);

        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE " + column + ";");

        var nullableColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE " + column + " AND IS_NULLABLE = 'YES';");

        var guards = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND BINARY TABLE_NAME = BINARY " + AtomicRequiredAddLiteral(intent.Table)
            + " AND CONSTRAINT_TYPE = 'CHECK' AND BINARY CONSTRAINT_NAME = BINARY "
            + AtomicRequiredAddLiteral(AtomicRequiredAddGuardName(operation)) + ";");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM " + AtomicRequiredAddDelimited(intent.Table) + ";");

        return new AtomicRequiredAddState(columns, nullableColumns, guards, rows);
    }
}
