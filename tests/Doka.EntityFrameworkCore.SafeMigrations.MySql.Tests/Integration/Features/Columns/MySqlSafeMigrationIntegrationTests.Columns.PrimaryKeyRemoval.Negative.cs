namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>A nullable alteration cannot bypass a retained physical PRIMARY containing its column.</summary>
    [Fact]
    public async Task PrimaryKeyRemovalStandaloneAlterRejectsRetainedPhysicalPrimaryKey()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalFixture(supportingIndexExists: true)
            + " ALTER TABLE `key_items` DROP FOREIGN KEY `fk_key_items_user`;");
        await using var context = CreateContext(connectionString);
        var operation = PrimaryKeyRemovalOperations()[3];
        var originalColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("retained-primary-nullability"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));
        var retained = await PrimaryKeyRemovalSentinelCountAsync(connectionString);
        var primaryMembership = await PrimaryKeyRemovalUserPrimaryMembershipCountAsync(connectionString);
        var remainingColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, retained);
        Assert.Equal(1, primaryMembership);
        Assert.Equal(originalColumn, remainingColumn);
    }

    /// <summary>Removing a key does not authorize a nullable transition from a different native-valid source.</summary>
    /// <param name="sourceDrift">The native-valid comment, default or character storage drift.</param>
    [Theory]
    [InlineData("comment")]
    [InlineData("default")]
    [InlineData("store-type")]
    public async Task PrimaryKeyRemovalRejectsExactSourceDrift(string sourceDrift)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalFixture(supportingIndexExists: true)
            + " ALTER TABLE `key_items` DROP FOREIGN KEY `fk_key_items_user`; "
            + PrimaryKeyRemovalMutationSql(sourceDrift));
        await using var context = CreateContext(connectionString);
        var operations = PrimaryKeyRemovalOperations();
        var originalColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("primary-removal-source-drift"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, operations, CancellationToken.None));
        var retained = await PrimaryKeyRemovalSentinelCountAsync(connectionString);
        var primaryMembership = await PrimaryKeyRemovalUserPrimaryMembershipCountAsync(connectionString);
        var remainingColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.True(report.Assessments[^1].Action is SafeMigrationAction.RejectDifferent
            or SafeMigrationAction.RejectPrerequisiteMissing);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, retained);
        Assert.Equal(0, primaryMembership);
        Assert.Equal(originalColumn, remainingColumn);
    }

    /// <summary>A recreated PRIMARY containing the target column cancels any earlier removal authorization.</summary>
    [Fact]
    public async Task PrimaryKeyRemovalRecreatedPrimaryRejectsNullableAlter()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalFixture(supportingIndexExists: true));
        await using var context = CreateContext(connectionString);
        var operations = PrimaryKeyRemovalOperations();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("pk_key_items_recreated", "key_items", ["question_id", "user_id"]);
        operations.Insert(3, Assert.Single(builder.Operations));
        var originalColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("primary-removal-recreated"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, operations, CancellationToken.None));
        var retained = await PrimaryKeyRemovalSentinelCountAsync(connectionString);
        var primaryMembership = await PrimaryKeyRemovalUserPrimaryMembershipCountAsync(connectionString);
        var remainingColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.True(report.Assessments[^1].Action is SafeMigrationAction.RejectDifferent
            or SafeMigrationAction.RejectPrerequisiteMissing);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, retained);
        Assert.Equal(1, primaryMembership);
        Assert.Equal(originalColumn, remainingColumn);
    }

    /// <summary>A post-prefix key recreation or source drift must fail the fresh alter-only runtime guard.</summary>
    /// <param name="mutation">The recreated PRIMARY or exact source metadata changed after the prefix.</param>
    [Theory]
    [InlineData("primary")]
    [InlineData("comment")]
    [InlineData("default")]
    [InlineData("store-type")]
    public async Task PrimaryKeyRemovalRejectsMutationBetweenPrefixAndAlter(string mutation)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalFixture(supportingIndexExists: true));
        await using var context = CreateContext(connectionString);
        var operations = PrimaryKeyRemovalOperations();

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("primary-removal-runtime-race"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations.Take(3).ToArray(), CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalMutationSql(mutation));
        var racedColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operations[3]], CancellationToken.None));
        var retained = await PrimaryKeyRemovalSentinelCountAsync(connectionString);
        var primaryMembership = await PrimaryKeyRemovalUserPrimaryMembershipCountAsync(connectionString);
        var remainingColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, preflight.Assessments[3].Action);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, retained);
        Assert.Equal(mutation == "primary" ? 1 : 0, primaryMembership);
        Assert.Equal(racedColumn, remainingColumn);
    }

    /// <summary>A different owner's PRIMARY removal cannot authorize a retained key on the target owner.</summary>
    [Fact]
    public async Task PrimaryKeyRemovalOnUnrelatedOwnerDoesNotAuthorizeNullableAlter()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalFixture(supportingIndexExists: true)
            + " ALTER TABLE `key_items` DROP FOREIGN KEY `fk_key_items_user`; "
            + "CREATE TABLE `other_key_items` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("pk_other_key_items", "other_key_items");
        builder.Operations.Add(PrimaryKeyRemovalOperations()[3]);
        var originalColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("unrelated-primary-nullability"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));
        var retained = await PrimaryKeyRemovalSentinelCountAsync(connectionString);
        var primaryMembership = await PrimaryKeyRemovalUserPrimaryMembershipCountAsync(connectionString);
        var remainingColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);
        var unrelatedPrimary = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'other_key_items' AND CONSTRAINT_NAME = 'PRIMARY';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, retained);
        Assert.Equal(1, primaryMembership);
        Assert.Equal(0, unrelatedPrimary);
        Assert.Equal(originalColumn, remainingColumn);
    }

    /// <summary>Opaque key recreation defers preflight, but cannot bypass the fresh retained-PRIMARY guard.</summary>
    [Fact]
    public async Task PrimaryKeyRemovalOpaqueRecreationDefersAndRejectsAtRuntime()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, PrimaryKeyRemovalFixture(supportingIndexExists: true));
        await using var context = CreateContext(connectionString);
        var operations = PrimaryKeyRemovalOperations();
        operations.Insert(3, new SqlOperation { Sql = PrimaryKeyRemovalMutationSql("primary") });
        var originalColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("opaque-primary-recreation"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, operations, CancellationToken.None));
        var retained = await PrimaryKeyRemovalSentinelCountAsync(connectionString);
        var primaryMembership = await PrimaryKeyRemovalUserPrimaryMembershipCountAsync(connectionString);
        var remainingColumn = await PrimaryKeyRemovalColumnStateAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.Contains("doka_sm_different", Assert.IsType<MySqlException>(failure).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, retained);
        Assert.Equal(1, primaryMembership);
        Assert.Equal(originalColumn, remainingColumn);
    }

    /// <summary>Creates native-valid drift or restores the same composite physical PRIMARY.</summary>
    /// <param name="mutation">The exact schema mutation to inject before the guarded alteration.</param>
    /// <returns>The isolated native DDL for the requested adversarial mutation.</returns>
    private static string PrimaryKeyRemovalMutationSql(string mutation)
        => mutation switch
        {
            "primary" => "ALTER TABLE `key_items` ADD PRIMARY KEY (`question_id`, `user_id`);",
            "comment" => "ALTER TABLE `key_items` MODIFY COLUMN `user_id` char(36) NOT NULL "
                + "COMMENT 'source-drift';",
            "default" => "ALTER TABLE `key_items` MODIFY COLUMN `user_id` char(36) NOT NULL "
                + "DEFAULT '00000000-0000-0000-0000-000000000003';",
            "store-type" => "ALTER TABLE `key_items` MODIFY COLUMN `user_id` varchar(36) NOT NULL;",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown key-removal mutation."),
        };

    /// <summary>
    /// Reads storage, nullability, default and comment without assuming engine-specific default rendering.
    /// </summary>
    /// <param name="connectionString">The isolated test database connection.</param>
    /// <returns>The exact column metadata fingerprint on the current engine.</returns>
    private static Task<string> PrimaryKeyRemovalColumnStateAsync(string connectionString)
        => ScalarStringAsync(connectionString,
            "SELECT CONCAT(COLUMN_TYPE, '|', IS_NULLABLE, '|', COALESCE(COLUMN_DEFAULT, '<none>'), '|', "
            + "COLUMN_COMMENT) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'key_items' AND COLUMN_NAME = 'user_id';");

    /// <summary>Counts the complete deterministic sentinel rather than only total rows.</summary>
    /// <param name="connectionString">The isolated test database connection.</param>
    /// <returns>The number of unchanged sentinel rows.</returns>
    private static Task<int> PrimaryKeyRemovalSentinelCountAsync(string connectionString)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `key_items` WHERE `question_id` = "
            + "'00000000-0000-0000-0000-000000000001' AND `user_id` = "
            + "'00000000-0000-0000-0000-000000000002' AND `payload` = 'retained';");

    /// <summary>Reads physical PRIMARY membership independently of the logical operation name.</summary>
    /// <param name="connectionString">The isolated test database connection.</param>
    /// <returns>The number of physical PRIMARY entries containing the altered column.</returns>
    private static Task<int> PrimaryKeyRemovalUserPrimaryMembershipCountAsync(string connectionString)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE CONSTRAINT_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'key_items' AND CONSTRAINT_NAME = 'PRIMARY' AND COLUMN_NAME = 'user_id';");
}
