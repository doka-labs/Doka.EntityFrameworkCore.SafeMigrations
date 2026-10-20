namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises published-provider budgets through native safe analysis, execution and readback.</summary>
public sealed class MySqlProvider106IntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine and per-test database leases.</summary>
    /// <param name="fixture">The initialized MySQL or MariaDB engine.</param>
    public MySqlProvider106IntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Model-derived implicit lengths survive native creation, postflight and idempotent replay.</summary>
    [Fact]
    public async Task CompleteBudgetModelCreatesAndReplaysWithoutLengthOrPrefixDrift()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
        await using var context = MySqlProvider106ContractTests.CreateContext(connectionString, Fixture.ServerVersion);
        var operations = SafeModelOperations(context);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var before = await runner.AnalyzeAsync(context, operations, new SafeMigrationRunOptions("provider-106-model"));
        await ExecuteOperationsAsync(context, operations);
        var after = await runner.VerifyAsync(context, operations, new SafeMigrationRunOptions("provider-106-post"));
        var replay = await runner.AnalyzeAsync(context, operations, new SafeMigrationRunOptions("provider-106-replay"));
        await ExecuteOperationsAsync(context, operations);
        var physical = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND ("
            + "(TABLE_NAME='budget_binary' AND COLUMN_NAME='payload' AND CHARACTER_MAXIMUM_LENGTH=254) OR "
            + "(TABLE_NAME='budget_fixed' AND COLUMN_NAME='payload' AND CHARACTER_MAXIMUM_LENGTH=216) OR "
            + "(TABLE_NAME='budget_explicit' AND COLUMN_NAME='value' AND CHARACTER_MAXIMUM_LENGTH=68) OR "
            + "(TABLE_NAME='budget_guid' AND COLUMN_NAME='value' AND CHARACTER_MAXIMUM_LENGTH=164) OR "
            + "(TABLE_NAME='budget_overlap' AND COLUMN_NAME='shared' AND CHARACTER_MAXIMUM_LENGTH=68) OR "
            + "(TABLE_NAME IN ('budget_principals','budget_dependents') AND COLUMN_NAME='code' "
            + "AND CHARACTER_MAXIMUM_LENGTH=168));");

        var prefixes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME LIKE 'budget\\_%' AND SUB_PART IS NOT NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, before.Status);
        Assert.NotEmpty(before.Assessments);
        Assert.All(before.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, after.Status);
        Assert.All(after.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(7, physical);
        Assert.Equal(0, prefixes);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='budget_dependents' AND REFERENCED_TABLE_NAME='budget_principals';"));
    }

    /// <summary>A historical length mismatch cannot silently rewrite a published table or its rows.</summary>
    [Fact]
    public async Task ExistingLengthDriftRemainsDifferentAndRuntimePreservesItsData()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `budget_explicit` (`id` int NOT NULL, `code` varchar(700) NOT NULL, "
            + "`value` varchar(69) NOT NULL, PRIMARY KEY (`id`)) ENGINE=InnoDB ROW_FORMAT=DYNAMIC "
            + "DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; "
            + "INSERT INTO `budget_explicit` VALUES (1, 'preserved-code', 'preserved-value');");

        await using var context = MySqlProvider106ContractTests.CreateContext(connectionString, Fixture.ServerVersion);
        var original = Assert.Single(MySqlProvider106ContractTests.CreateOperations(context)
            .OfType<CreateTableOperation>(), operation => operation.Name == "budget_explicit");

        var operation = new SafeMigrationOperation(new EnsureTableIntent(
            SafeMigrationExpectedDefinitionFactory.From(original), SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var column = new SafeMigrationOperation(new EnsureColumnIntent("budget_explicit",
            SafeMigrationExpectedDefinitionFactory.From(Assert.Single(original.Columns,
                candidate => candidate.Name == "value"))), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("provider-106-length-drift"));

        // WHY: Detailed column facets belong to column assessments, not strict-table diagnostics.
        // Probe the same captured column independently so its precise mismatch is also established.
        var columnReport = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [column],
            new SafeMigrationRunOptions("provider-106-column-drift"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var length = await ScalarIntAsync(connectionString,
            "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='budget_explicit' AND COLUMN_NAME='value';");

        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `budget_explicit` WHERE `id`=1 AND `code`='preserved-code' "
            + "AND `value`='preserved-value';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.False(assessment.PostconditionSatisfied);
        var columnAssessment = Assert.Single(columnReport.Assessments);
        Assert.Equal(SafeMigrationAction.RejectDifferent, columnAssessment.Action);
        Assert.Contains(columnAssessment.Differences, difference => difference.Facet == "column_store_type"
            && difference.Expected == "varchar(68)" && difference.Actual == "varchar(69)");
        AssertOwnedGuardFailure(failure);
        Assert.Equal("doka_sm_different", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(69, length);
        Assert.Equal(1, preserved);
    }

    /// <summary>Native catalog accounting accepts the exact decimal byte boundary for ordinary keys.</summary>
    /// <param name="precision">The declared decimal precision.</param>
    /// <param name="scale">The declared decimal scale.</param>
    /// <param name="width">The decimal's complete physical width.</param>
    /// <param name="kind">Zero for ordinary index, one for primary key, or two for unique constraint.</param>
    [Theory]
    [InlineData(18, 2, 9, 0)]
    [InlineData(18, 2, 9, 1)]
    [InlineData(18, 2, 9, 2)]
    [InlineData(9, 7, 5, 0)]
    [InlineData(9, 7, 5, 1)]
    [InlineData(9, 7, 5, 2)]
    public async Task DecimalExactBoundaryCreatesAndReplays(int precision, int scale, int width, int kind)
    {
        // Arrange
        var connectionString = await CreateDecimalOwnerAsync(precision, scale, 3072 - width);
        await using var context = CreateContext(connectionString);
        var operations = DecimalKey(context, kind);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var before = await runner.AnalyzeAsync(context, operations, new SafeMigrationRunOptions("decimal-exact"));
        await ExecuteOperationsAsync(context, operations);
        var after = await runner.VerifyAsync(context, operations, new SafeMigrationRunOptions("decimal-post"));
        var replay = await runner.AnalyzeAsync(context, operations, new SafeMigrationRunOptions("decimal-replay"));
        var keyParts = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='decimal_budget' AND INDEX_NAME=" + (kind == 1 ? "'PRIMARY'" : "'decimal_key'")
            + " AND SUB_PART IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, before.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(before.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, after.Status);
        Assert.True(Assert.Single(after.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(2, keyParts);
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `decimal_budget`;"));
    }

    /// <summary>A one-byte overflow is refused by both native preflight and mandatory runtime guards.</summary>
    /// <param name="precision">The declared decimal precision.</param>
    /// <param name="scale">The declared decimal scale.</param>
    /// <param name="width">The decimal's complete physical width.</param>
    /// <param name="kind">Zero for ordinary index, one for primary key, or two for unique constraint.</param>
    [Theory]
    [InlineData(18, 2, 9, 0)]
    [InlineData(18, 2, 9, 1)]
    [InlineData(18, 2, 9, 2)]
    [InlineData(9, 7, 5, 0)]
    [InlineData(9, 7, 5, 1)]
    [InlineData(9, 7, 5, 2)]
    public async Task DecimalOneByteOverflowIsRejectedBeforeDdl(int precision, int scale, int width, int kind)
    {
        // Arrange
        var connectionString = await CreateDecimalOwnerAsync(precision, scale, 3073 - width);
        await using var context = CreateContext(connectionString);
        var operations = DecimalKey(context, kind);
        var reason = kind switch
        {
            0 => "index_prefix_required_for_key_limit",
            1 => "primary_key_exceeds_physical_limit",
            2 => "unique_constraint_exceeds_physical_limit",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("decimal-overflow"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var keyParts = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA=DATABASE() "
            + "AND TABLE_NAME='decimal_budget';");

        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `decimal_budget` WHERE `amount`=1.25 AND `payload`=0x01;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal(reason, assessment.Code);
        AssertOwnedGuardFailure(failure);
        var diagnostic = Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal("doka_sm_unsupported", diagnostic.GuardCode);
        // WHY: Native physical-limit refusals retain their precise preflight code above;
        // the public runtime explanation currently maps this family to its bounded fallback.
        Assert.Equal("provider_contract_unsupported", diagnostic.ReasonCode);
        Assert.Contains("; analysis=" + reason, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(0, keyParts);
        Assert.Equal(1, preserved);
    }

    /// <summary>Captures the finalized provider model in the same immutable safe contracts as scaffolding.</summary>
    private static MigrationOperation[] SafeModelOperations(DbContext context) =>
        MySqlProvider106ContractTests.CreateOperations(context)
            .Where(static operation => operation
                is CreateTableOperation or CreateIndexOperation or AddForeignKeyOperation)
            .Select(static operation => operation is CreateTableOperation table
                ? new SafeMigrationOperation(new EnsureTableIntent(SafeMigrationExpectedDefinitionFactory.From(table),
                    SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent)
                : SafeMigrationOperationCompatibility.Normalize(operation,
                    MySqlSafeMigrationProviderOperationAdapter.Instance).Operation)
            .ToArray();

    /// <summary>Declares a real native decimal fixture, not a mocked catalog or reduced byte limit.</summary>
    private async Task<string> CreateDecimalOwnerAsync(int precision, int scale, int binaryLength)
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var shape = FormattableString.Invariant(
            $"`amount` decimal({precision},{scale}) NOT NULL, `payload` varbinary({binaryLength}) NOT NULL");

        await ExecuteSqlAsync(connectionString, "CREATE TABLE `decimal_budget` (" + shape
            + ") ENGINE=InnoDB ROW_FORMAT=DYNAMIC; INSERT INTO `decimal_budget` VALUES (1.25, 0x01);");

        return connectionString;
    }

    /// <summary>Uses full-value keys so a decimal undercount cannot hide behind a prefix optimization.</summary>
    private static List<MigrationOperation> DecimalKey(DbContext context, int kind)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        switch (kind)
        {
            case 0:
                builder.CreateIndexIfNotExists("decimal_key", "decimal_budget", ["amount", "payload"]);
                break;
            case 1:
                builder.AddPrimaryKeyIfNotExists("decimal_key", "decimal_budget", ["amount", "payload"]);
                break;
            case 2:
                builder.AddUniqueConstraintIfNotExists("decimal_key", "decimal_budget", ["amount", "payload"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return builder.Operations;
    }
}
