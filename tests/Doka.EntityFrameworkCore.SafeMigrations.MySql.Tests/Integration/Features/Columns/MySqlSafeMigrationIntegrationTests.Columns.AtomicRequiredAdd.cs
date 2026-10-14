namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Missing required columns use two atomic ALTERs without inventing row values or defaults.</summary>
    /// <param name="useGuidStorage">
    /// Whether the required target uses the annotated GUID rather than integer shape.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicRequiredAddMissingColumnConvergesAndRemainsIdempotent(bool useGuidStorage)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql());
        var operation = AtomicRequiredAddOperation(useGuidStorage);
        var guardName = AtomicRequiredAddGuardName(operation);
        var observer = new AtomicRequiredAddStageProbe(guardName);
        await using var context = CreateRuntimeContext(connectionString, observer);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-rerun"), CancellationToken.None);

        rerun.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(rerun.Assessments).Action);
        Assert.True(observer.AtomicBodyObserved);
        Assert.Contains("ADD", observer.StageOneSql, StringComparison.Ordinal);
        Assert.Contains("CHECK", observer.StageOneSql, StringComparison.Ordinal);
        Assert.Contains("IS NOT NULL", observer.StageOneSql, StringComparison.Ordinal);
        Assert.Contains("MODIFY", observer.StageTwoSql, StringComparison.Ordinal);
        Assert.Contains("NOT NULL", observer.StageTwoSql, StringComparison.Ordinal);
        Assert.Contains(guardName, observer.StageOneSql, StringComparison.Ordinal);
        Assert.Contains(guardName, observer.StageTwoSql, StringComparison.Ordinal);
        Assert.Contains(Fixture.IsMariaDb ? "DROP CONSTRAINT" : "DROP CHECK", observer.StageTwoSql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DEFAULT", observer.StageOneSql, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFAULT", observer.StageTwoSql, StringComparison.Ordinal);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 0), state);
        Assert.Equal(SafeMigrationDefaultValueKind.None,
            Assert.IsType<EnsureColumnIntent>(operation.Intent).Definition.DefaultValue.Kind);
    }

    /// <summary>
    /// A committed guarded nullable stage is recoverable under the original strict difference policy.
    /// </summary>
    /// <param name="useGuidStorage">Whether the target uses GUID rather than integer storage.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicRequiredAddInterruptedStageResumesWithoutRepairApproval(bool useGuidStorage)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql());
        var operation = AtomicRequiredAddOperation(useGuidStorage);
        var observer = new AtomicRequiredAddStageProbe(AtomicRequiredAddGuardName(operation),
            "SELECT * FROM `atomic_required_add_missing_probe`;");

        await using var context = CreateRuntimeContext(connectionString, observer);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var interrupted = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var staged = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var resume = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-resume"), CancellationToken.None);

        resume.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var final = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-resume-final"), CancellationToken.None);

        // Assert
        Assert.Equal(1146, Assert.IsType<MySqlException>(interrupted).Number);
        Assert.True(observer.Injected);
        Assert.Equal(new AtomicRequiredAddState(1, 1, 1, 0), staged);
        Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, operation.Policy);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(resume.Assessments).Action);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 0), final);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    /// <summary>Non-strict sessions cannot start a protocol whose final ALTER must never coerce NULL rows.</summary>
    [Fact]
    public async Task AtomicRequiredAddWithoutStrictModeFailsBeforeTargetDdl()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql());
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = '';");
        var operation = AtomicRequiredAddOperation();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-no-strict-mode"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(new AtomicRequiredAddState(0, 0, 0, 0), state);
    }

    /// <summary>Non-InnoDB tables cannot enter the atomic guarded required-column protocol.</summary>
    [Fact]
    public async Task AtomicRequiredAddNonInnodbTableFailsBeforeTargetDdl()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `atomic_required_items` (`key` int NOT NULL PRIMARY KEY) ENGINE=MyISAM;");
        await using var context = CreateContext(connectionString);
        var operation = AtomicRequiredAddOperation();

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-non-innodb"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(new AtomicRequiredAddState(0, 0, 0, 0), state);
    }

    /// <summary>Disabled MariaDB checks or a MySQL unenforced CHECK cannot certify the staging barrier.</summary>
    [Fact]
    public async Task AtomicRequiredAddRequiresActiveCheckEnforcement()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var operation = AtomicRequiredAddOperation();
        var guardName = AtomicRequiredAddGuardName(operation);
        await ExecuteSqlAsync(connectionString, Fixture.IsMariaDb
            ? AtomicRequiredAddOriginalTableSql()
            : AtomicRequiredAddStagedTableSql(operation, guardName, "`required_value` IS NOT NULL",
                checkSuffix: " NOT ENFORCED"));

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        if (Fixture.IsMariaDb)
        {
            await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION check_constraint_checks = OFF;");
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-unenforced"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Assert
        Assert.Equal(Fixture.IsMariaDb ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.RejectDifferent,
            Assert.Single(report.Assessments).Action);
        Assert.Contains(Fixture.IsMariaDb ? "doka_sm_unsupported" : "doka_sm_different",
            AssertOwnedGuardFailure(exception).Message, StringComparison.Ordinal);
        Assert.Equal(Fixture.IsMariaDb
            ? new AtomicRequiredAddState(0, 0, 0, 0)
            : new AtomicRequiredAddState(1, 1, 1, 0), state);
    }

    /// <summary>An owned-looking name does not authorize a malformed intermediate column or unrelated CHECK.</summary>
    /// <param name="failure">The independent ownership or intermediate-shape defect.</param>
    [Theory]
    [InlineData("wrong-expression")]
    [InlineData("wrong-type")]
    [InlineData("unexpected-default")]
    [InlineData("lookalike-name")]
    [InlineData("upper-case-name")]
    [InlineData("unowned-name")]
    [InlineData("missing-guard")]
    [InlineData("name-collision")]
    public async Task AtomicRequiredAddRejectsMalformedOrUnownedIntermediateState(string failure)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var operation = AtomicRequiredAddOperation();
        var guardName = AtomicRequiredAddGuardName(operation);
        var actualGuardName = failure switch
        {
            "lookalike-name" => guardName[..^1] + (guardName[^1] == 'a' ? "b" : "a"),
            "upper-case-name" => guardName.ToUpperInvariant(),
            "unowned-name" => "unrelated_required_value_check",
            _ => guardName,
        };

        var createSql = failure == "name-collision"
            ? "CREATE TABLE `atomic_required_items` (`key` int NOT NULL PRIMARY KEY, CONSTRAINT "
                + AtomicRequiredAddDelimited(actualGuardName) + " CHECK (`key` > 0)) ENGINE=InnoDB;"
            : AtomicRequiredAddStagedTableSql(operation,
                failure == "missing-guard" ? null : actualGuardName,
                failure == "wrong-expression" ? "`required_value` IS NULL" : "`required_value` IS NOT NULL",
                columnType: failure == "wrong-type" ? "varchar(36)" : "char(36)",
                columnSuffix: failure == "unexpected-default" ? " DEFAULT 'caller-owned'" : "");

        await ExecuteSqlAsync(connectionString, createSql);
        await using var context = CreateContext(connectionString);
        var before = await ReadAtomicRequiredAddStateAsync(connectionString, operation, actualGuardName);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-unowned-stage"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var after = await ReadAtomicRequiredAddStateAsync(connectionString, operation, actualGuardName);

        // Assert
        Assert.Equal(failure == "name-collision"
            ? SafeMigrationAction.RejectUnsupported
            : SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Contains(failure == "name-collision" ? "doka_sm_unsupported" : "doka_sm_different",
            AssertOwnedGuardFailure(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(before, after);
        Assert.Equal(failure == "name-collision" ? 0 : 1, after.Columns);
        Assert.Equal(failure == "missing-guard" ? 0 : 1, after.Guards);
    }

    /// <summary>
    /// An impossible required target plus reserved guard is a conflict, not permission for CHECK cleanup.
    /// </summary>
    [Fact]
    public async Task AtomicRequiredAddMatchingTargetWithReservedGuardRejectsWithoutCleanup()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var operation = AtomicRequiredAddOperation();
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddStagedTableSql(operation,
            AtomicRequiredAddGuardName(operation), "`required_value` IS NOT NULL", nullable: false));

        await using var context = CreateContext(connectionString);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-existing-final"), CancellationToken.None);

        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 1, 0), state);
    }

    /// <summary>
    /// Qualified quoted identities remain safe under SQL modes that invalidate backslash-string escaping.
    /// </summary>
    /// <param name="ansiQuotes">Whether ANSI_QUOTES accompanies NO_BACKSLASH_ESCAPES.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicRequiredAddQuotedQualifiedIdentifiersPreserveExactIdentity(bool ansiQuotes)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        const string table = "atomic_'slash\\`owner";
        const string column = "value_'slash\\`";
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddOriginalTableSql(table));
        // WHY: MySqlConnector's string-parameter escaping must agree with the
        // session mode; retaining default driver escaping would test a broken
        // connection contract instead of SafeMigrations identifier handling.
        var configuredConnection = new MySqlConnectionStringBuilder(connectionString) { NoBackslashEscapes = true };
        await using var context = CreateContext(configuredConnection.ConnectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_TRANS_TABLES,"
            + "NO_BACKSLASH_ESCAPES" + (ansiQuotes ? ",ANSI_QUOTES" : "") + "';");
        var operation = AtomicRequiredAddOperation(table: table, column: column,
            schema: new MySqlConnectionStringBuilder(connectionString).Database);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-required-add-quoted-identity"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var actualOwnerHex = await ScalarStringAsync(connectionString,
            "SELECT HEX(TABLE_NAME) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = " + AtomicRequiredAddLiteral(table) + ";");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 0), state);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(table)), actualOwnerHex);
    }

    /// <summary>Unexpected inventory hides only an exact intermediate guard owned by one pending target.</summary>
    /// <param name="ownership">The exact, wrong-expression, lookalike, or ambiguous pending ownership.</param>
    [Theory]
    [InlineData("owned")]
    [InlineData("wrong-expression")]
    [InlineData("lookalike-name")]
    [InlineData("ambiguous-target")]
    public async Task AtomicRequiredAddRecoveryInventoryAdmitsOnlyExactPendingOwnership(string ownership)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var operation = AtomicRequiredAddOperation();
        var guardName = AtomicRequiredAddGuardName(operation);
        var actualGuardName = ownership == "lookalike-name"
            ? guardName[..^1] + (guardName[^1] == 'a' ? "b" : "a")
            : guardName;

        await ExecuteSqlAsync(connectionString, AtomicRequiredAddStagedTableSql(operation, actualGuardName,
            ownership == "wrong-expression" ? "`required_value` IS NULL" : "`required_value` IS NOT NULL"));

        await using var context = CreateContext(connectionString);
        var intent = Assert.IsType<EnsureColumnIntent>(operation.Intent);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(intent.Table,
                [new ExpectedColumnDefinition("key", typeof(int), false, "int"), intent.Definition],
                primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", intent.Table, ["key"])),
            SafeMigrationTableMode.ConvergenceContainer, SafeMigrationPolicy.ExistenceOnly);
        builder.Operations.Add(operation);
        if (ownership == "ambiguous-target")
        {
            builder.Operations.Add(AtomicRequiredAddOperation(guid: false));
        }

        // Act
        var inventory = await context.GetService<ISafeMigrationProviderAnalyzer>().FindUnexpectedObjectsAsync(
            context, builder.Operations, CancellationToken.None);

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation, actualGuardName);

        // Assert
        if (ownership == "owned")
        {
            Assert.DoesNotContain(inventory, item => item.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint
                && item.Table == intent.Table && item.Name == actualGuardName);
        }
        else
        {
            Assert.Contains(inventory, item => item.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint
                && item.Table == intent.Table && item.Name == actualGuardName);
        }

        Assert.Equal(new AtomicRequiredAddState(1, 1, 1, 0), state);
    }

    /// <summary>Resuming a staged column with actual duplicate values cannot invent an empty-table key proof.</summary>
    [Fact]
    public async Task AtomicRequiredAddResumeDoesNotAuthorizePrimaryKeyOverExistingDuplicates()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var operation = AtomicRequiredAddOperation();
        await ExecuteSqlAsync(connectionString, AtomicRequiredAddStagedTableSql(operation,
            AtomicRequiredAddGuardName(operation), "`required_value` IS NOT NULL")
            + " INSERT INTO `atomic_required_items` (`key`, `required_value`) VALUES "
            + "(1, '00000000-0000-0000-0000-000000000001'), (2, '00000000-0000-0000-0000-000000000001');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(operation);
        builder.DropPrimaryKeyIfExists("pk_atomic_required_items_key", "atomic_required_items");
        builder.AddPrimaryKeyIfNotExists("pk_atomic_required_items_value", "atomic_required_items", ["required_value"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("atomic-required-add-duplicate-key"), CancellationToken.None);

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var originalKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'atomic_required_items' AND CONSTRAINT_NAME = 'PRIMARY' AND COLUMN_NAME = 'key';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal("column_atomic_add_resume", report.Assessments[0].AnalysisCode);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.True(report.Assessments[2].Action is SafeMigrationAction.RejectDataBlocked
            or SafeMigrationAction.RejectPrerequisiteMissing);
        Assert.Equal(new AtomicRequiredAddState(1, 1, 1, 2), state);
        Assert.Equal(1, originalKeys);
    }

    /// <summary>
    /// Captures an eligible required addition with no default and the provider's exact boolean safety marker.
    /// </summary>
    /// <param name="guid">Whether the target uses GUID rather than integer storage.</param>
    /// <param name="table">The anonymous target table.</param>
    /// <param name="column">The anonymous target column.</param>
    /// <param name="schema">The optional current-database qualifier.</param>
    /// <returns>The immutable strict-policy column operation.</returns>
    private static SafeMigrationOperation AtomicRequiredAddOperation(
        bool guid = true,
        string table = "atomic_required_items",
        string column = "required_value",
        string? schema = null
    )
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        if (guid)
        {
            builder.AddColumnIfNotExistsFromModel(
                operationFactory: operations => operations.AddColumn<Guid>(
                        name: column, table: table, schema: schema, type: "char(36)",
                        fixedLength: true, maxLength: 36, nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ClientGuid));
        }
        else
        {
            builder.AddColumnIfNotExistsFromModel(
                operationFactory: operations => operations.AddColumn<int>(
                        name: column, table: table, schema: schema, type: "int", nullable: false)
                    .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None));
        }

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>
    /// Reads the shared owner-and-target-bound guard name instead of duplicating its fingerprint algorithm.
    /// </summary>
    /// <param name="operation">The exact pending required-column operation.</param>
    /// <returns>The stable owned CHECK name.</returns>
    private static string AtomicRequiredAddGuardName(SafeMigrationOperation operation)
        => MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            Assert.IsType<EnsureColumnIntent>(operation.Intent));

    /// <summary>Creates an empty InnoDB owner without any required target or fabricated row values.</summary>
    /// <param name="table">The anonymous owner name, including quoted-identifier controls.</param>
    /// <returns>The original fixture DDL.</returns>
    private static string AtomicRequiredAddOriginalTableSql(string table = "atomic_required_items")
        => "CREATE TABLE " + AtomicRequiredAddDelimited(table)
            + " (`key` int NOT NULL PRIMARY KEY) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

    /// <summary>Creates a deliberately controlled durable intermediate state for ownership probes.</summary>
    /// <param name="operation">The pending target whose physical owner is reused.</param>
    /// <param name="guardName">The actual CHECK name, or null for an unguarded intermediate column.</param>
    /// <param name="expression">The caller-controlled CHECK expression.</param>
    /// <param name="columnType">The actual column type for malformed-shape controls.</param>
    /// <param name="columnSuffix">An optional unrelated default or other column suffix.</param>
    /// <param name="checkSuffix">An optional provider CHECK enforcement suffix.</param>
    /// <param name="nullable">Whether the actual column is still an intermediate nullable target.</param>
    /// <returns>The isolated test-only DDL; it is never used to implement production recovery.</returns>
    private static string AtomicRequiredAddStagedTableSql(
        SafeMigrationOperation operation,
        string? guardName,
        string expression,
        string columnType = "char(36)",
        string columnSuffix = "",
        string checkSuffix = "",
        bool nullable = true
    )
    {
        var intent = Assert.IsType<EnsureColumnIntent>(operation.Intent);

        return "CREATE TABLE " + AtomicRequiredAddDelimited(intent.Table)
            + " (`key` int NOT NULL PRIMARY KEY, " + AtomicRequiredAddDelimited(intent.Definition.Name)
            + " " + columnType + (nullable ? " NULL" : " NOT NULL") + columnSuffix
            + (guardName is null ? "" : ", CONSTRAINT " + AtomicRequiredAddDelimited(guardName)
                + " CHECK (" + expression + ")" + checkSuffix)
            + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
    }

    /// <summary>Reads durable column, guard and row-count state through fresh observer connections.</summary>
    /// <param name="connectionString">The isolated fixture connection string.</param>
    /// <param name="operation">The pending target whose owner is inspected.</param>
    /// <param name="actualGuardName">An optional deliberately unowned CHECK name to inspect.</param>
    /// <returns>The immutable state needed to prove that rejection never loses the barrier or data.</returns>
    private static async Task<AtomicRequiredAddState> ReadAtomicRequiredAddStateAsync(
        string connectionString,
        SafeMigrationOperation operation,
        string? actualGuardName = null
    )
    {
        var intent = Assert.IsType<EnsureColumnIntent>(operation.Intent);
        var owner = "TABLE_SCHEMA = DATABASE() AND TABLE_NAME = " + AtomicRequiredAddLiteral(intent.Table);
        var column = owner + " AND COLUMN_NAME = " + AtomicRequiredAddLiteral(intent.Definition.Name);
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE " + column + ";");
        var nullableColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE " + column + " AND IS_NULLABLE = 'YES';");
        var guards = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = " + AtomicRequiredAddLiteral(intent.Table) + " AND CONSTRAINT_TYPE = 'CHECK' "
            + "AND CONSTRAINT_NAME = "
            + AtomicRequiredAddLiteral(actualGuardName ?? AtomicRequiredAddGuardName(operation))
            + ";");
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM " + AtomicRequiredAddDelimited(intent.Table) + ";");

        return new AtomicRequiredAddState(columns, nullableColumns, guards, rows);
    }

    /// <summary>Sets only this test's already open migration session, never global server configuration.</summary>
    /// <param name="context">The context retaining the session under test.</param>
    /// <param name="sql">The bounded test-owned session statement.</param>
    private static async Task AtomicRequiredAddSessionSqlAsync(DbContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Escapes identifiers using delimiter doubling, independently of literal-string SQL modes.</summary>
    /// <param name="identifier">The anonymous test-owned identifier.</param>
    /// <returns>The exact backtick-delimited identifier.</returns>
    private static string AtomicRequiredAddDelimited(string identifier)
        => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <summary>Uses UTF-8 hex literals so catalog probes do not assume backslash escape semantics.</summary>
    /// <param name="value">The anonymous identifier used as a catalog value.</param>
    /// <returns>The SQL-mode-independent literal expression.</returns>
    private static string AtomicRequiredAddLiteral(string value)
        => "CONVERT(0x" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + " USING utf8mb4)";

    /// <summary>Retains only durable aggregate state, not connection details or row payloads.</summary>
    /// <param name="Columns">The actual named target-column count.</param>
    /// <param name="NullableColumns">The actual nullable named target-column count.</param>
    /// <param name="Guards">The exact named CHECK count.</param>
    /// <param name="Rows">The owner row count.</param>
    private readonly record struct AtomicRequiredAddState(int Columns, int NullableColumns, int Guards, int Rows);

    /// <summary>
    /// Observes or interrupts the real atomic body exactly after its first complete prepared triplet.
    /// </summary>
    /// <remarks>
    /// WHY: Both atomic ALTERs share one body dispatch. An ordinary before-command
    /// failure would miss durable staging; injection inside that actual command
    /// proves the first ALTER committed before the second was allowed to start.
    /// </remarks>
    private sealed class AtomicRequiredAddStageProbe : DbCommandInterceptor
    {
        private const string Deallocate = "DEALLOCATE PREPARE doka_sm_statement;";
        private readonly string _guardName;
        private readonly string? _failureSql;

        /// <summary>Initializes an observer scoped to one exact owner-bound staging guard.</summary>
        /// <param name="guardName">The expected exact staging CHECK name.</param>
        /// <param name="failureSql">An optional one-shot statement inserted after the first completed ALTER.</param>
        public AtomicRequiredAddStageProbe(string guardName, string? failureSql = null)
        {
            _guardName = guardName;
            _failureSql = failureSql;
        }

        /// <summary>Gets whether the generated two-ALTER atomic body was actually observed.</summary>
        public bool AtomicBodyObserved { get; private set; }

        /// <summary>Gets whether the one-shot failure reached the real body rather than its earlier setup.</summary>
        public bool Injected { get; private set; }

        /// <summary>Gets the anonymous first atomic ALTER for structural assertions.</summary>
        public string StageOneSql { get; private set; } = "";

        /// <summary>Gets the anonymous final atomic ALTER for structural assertions.</summary>
        public string StageTwoSql { get; private set; } = "";

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            Observe(command);

            return ValueTask.FromResult(result);
        }

        /// <summary>
        /// Finds both real DDL payloads before selecting their intervening prepared-control boundary.
        /// </summary>
        /// <param name="command">The actual dispatched provider command.</param>
        private void Observe(DbCommand command)
        {
            if (!command.CommandText.Contains("SET @doka_sm_post_ok", StringComparison.Ordinal))
            {
                return;
            }

            var stages = AtomicRequiredAddDecodedPayloads(command.CommandText)
                .Where(sql => sql.StartsWith("ALTER TABLE ", StringComparison.Ordinal)
                    && sql.Contains(_guardName, StringComparison.Ordinal)).ToArray();

            if (stages.Length != 2
                || !stages[0].Contains("CHECK", StringComparison.Ordinal)
                || !stages[1].Contains("MODIFY", StringComparison.Ordinal))
            {
                return;
            }

            AtomicBodyObserved = true;
            StageOneSql = stages[0];
            StageTwoSql = stages[1];
            if (Injected || _failureSql is null)
            {
                return;
            }

            var firstStageEnd = command.CommandText.IndexOf(Deallocate, StringComparison.Ordinal);
            if (firstStageEnd < 0)
            {
                throw new InvalidOperationException("The atomic body omitted its first completed prepared triplet.");
            }

            command.CommandText = command.CommandText.Insert(firstStageEnd + Deallocate.Length, _failureSql);
            Injected = true;
        }
    }

    /// <summary>Decodes anonymous guarded DDL payloads without changing production rendering or planning.</summary>
    /// <param name="sql">The actual body SQL dispatched by the real provider pipeline.</param>
    /// <returns>The prepared statement payloads in their transport order.</returns>
    private static List<string> AtomicRequiredAddDecodedPayloads(string sql)
    {
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";
        var result = new List<string>();
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var start = offset + prefix.Length;
            var end = sql.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("An atomic prepared payload is unterminated.");
            }

            result.Add(Encoding.UTF8.GetString(Convert.FromHexString(sql.AsSpan(start, end - start))));
            offset = end + suffix.Length;
        }

        return result;
    }
}
