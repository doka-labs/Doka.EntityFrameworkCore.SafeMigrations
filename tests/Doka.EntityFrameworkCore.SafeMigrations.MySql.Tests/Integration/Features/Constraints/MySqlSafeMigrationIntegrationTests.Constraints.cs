namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the Constraints migration contracts in an independent class collection.</summary>
public sealed class MySqlConstraintsIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlConstraintsIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Matches the native canonical PRIMARY name and preserves primary-key replay safety.</summary>
    [Fact]
    public async Task PrimaryKey_UsesEngineCanonicalNameAndRemainsIdempotent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `snapshots` (`id` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("pk_snapshots", "snapshots", ["id"]);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                WHERE CONSTRAINT_SCHEMA = DATABASE()
                AND TABLE_NAME = 'snapshots' AND CONSTRAINT_NAME = 'PRIMARY'
                AND CONSTRAINT_TYPE = 'PRIMARY KEY';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Preserves idempotent creation and removal across all supported constraint families.</summary>
    [Fact]
    public async Task ConstraintLifecycle_IsIdempotentAcrossEveryConstraintFamily()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            $"""
            CREATE TABLE `parents` (`id` int NOT NULL, PRIMARY KEY (`id`)); CREATE TABLE
            `children` (`id` int NOT NULL,
            `parent_id` int NULL, `code` varchar(30) NULL, `quantity` int NOT NULL);{"\u0020"}
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var add = new MigrationBuilder(context.Database.ProviderName!);
        add.AddPrimaryKeyIfNotExists("pk_children", "children", ["id"]);
        add.AddUniqueConstraintIfNotExists("uq_children_code", "children", ["code"]);
        add.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_children_quantity",
                "children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        add.AddForeignKeyIfNotExists(
            "fk_children_parents",
            "children",
            ["parent_id"],
            "parents",
            ["id"],
            onDelete: ReferentialAction.SetNull);

        // Act
        foreach (var operation in add.Operations)
        {
            try
            {
                await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
                await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
            }
            catch (Exception exception)
            {
                var kind = ((SafeMigrationOperation)operation).Intent.Kind;
                throw new InvalidOperationException($"Constraint lifecycle failed for {kind}.", exception);
            }
        }

        var constraintsAfterAdd = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE()
            AND TABLE_NAME = 'children';
            """,
            cancellationToken: CancellationToken.None);

        var drop = new MigrationBuilder(context.Database.ProviderName!);
        drop.DropForeignKeyIfExists("fk_children_parents", "children");
        drop.DropCheckConstraintIfExists("ck_children_quantity", "children");
        drop.DropUniqueConstraintIfExists("uq_children_code", "children");
        drop.DropPrimaryKeyIfExists("pk_children", "children");
        foreach (var operation in drop.Operations)
        {
            await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
            await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        }

        var constraintsAfterDrop = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE()
            AND TABLE_NAME = 'children';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(4, constraintsAfterAdd);
        Assert.Equal(0, constraintsAfterDrop);
    }

    /// <summary>Rejects existing definition drift for every constraint family before target mutation.</summary>
    [Fact]
    public async Task ExistingConstraintDefinitionDrift_IsRejectedForEveryConstraintFamily()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `constraint_parents` (`id` int NOT NULL, PRIMARY KEY (`id`));
            CREATE TABLE `constraint_drift` (`id` int NOT NULL,
            `alternate_id` int NOT NULL, `code` varchar(20) NULL, `alternate_code` varchar(20)
            NULL,
            `quantity` int NOT NULL, `parent_id` int NULL, `alternate_parent_id` int
            NULL,
            PRIMARY KEY (`alternate_id`), CONSTRAINT `uq_constraint_code` UNIQUE (`alternate_code`),
            CONSTRAINT `ck_constraint_quantity` CHECK (`quantity` > 10), CONSTRAINT
            `fk_constraint_parent` FOREIGN KEY (`alternate_parent_id`) REFERENCES `constraint_parents`
            (`id`));
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("pk_constraint_drift", "constraint_drift", ["id"]);
        builder.AddUniqueConstraintIfNotExists("uq_constraint_code", "constraint_drift", ["code"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_constraint_quantity",
                "constraint_drift",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddForeignKeyIfNotExists(
            "fk_constraint_parent",
            "constraint_drift",
            ["parent_id"],
            "constraint_parents",
            ["id"]);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        List<MySqlException> failures = [];
        foreach (var operation in builder.Operations)
        {
            var exception =
                await AssertOwnedGuardFailureAsync(
                    operationToken => ExecuteOperationsAsync(
                        context,
                        [operation],
                        operationToken),
                    cancellationToken: CancellationToken.None);

            failures.Add(exception);
        }

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        Assert.Equal(builder.Operations.Count, failures.Count);
        Assert.All(failures,
            exception =>
                Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Rejects invalid rows before creating the requested constraint or unique index.</summary>
    [Fact]
    public async Task ConstraintAndUniqueIndexDataBlockers_StopBeforeTargetDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `blocker_parents` (`id` int NOT NULL, PRIMARY KEY (`id`));
            CREATE TABLE `blocker_children` (`id` int NULL,
            `code` varchar(20) NOT NULL, `quantity` int NOT NULL, `parent_id` int NOT
            NULL); INSERT INTO `blocker_parents` VALUES (1); INSERT INTO `blocker_children`
            VALUES (1,
            'duplicate', -1, 999), (1, 'duplicate', 1, 999);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("pk_blocker_children", "blocker_children", ["id"]);
        builder.AddUniqueConstraintIfNotExists("uq_blocker_children_code", "blocker_children", ["code"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_blocker_children_quantity",
                "blocker_children",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddForeignKeyIfNotExists(
            "fk_blocker_children_parent",
            "blocker_children",
            ["parent_id"],
            "blocker_parents",
            ["id"]);

        builder.CreateIndexIfNotExists("ux_blocker_children_code", "blocker_children", ["code"], unique: true);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        List<MySqlException> failures = [];
        foreach (var operation in builder.Operations)
        {
            var exception =
                await AssertOwnedGuardFailureAsync(
                    operationToken => ExecuteOperationsAsync(
                        context,
                        [operation],
                        operationToken),
                    cancellationToken: CancellationToken.None);

            failures.Add(exception);
        }

        var remainingConstraints = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE()
            AND TABLE_NAME = 'blocker_children';
            """,
            cancellationToken: CancellationToken.None);

        var remainingIndexes = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'blocker_children'
            AND INDEX_NAME = 'ux_blocker_children_code';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState));

        Assert.Equal(builder.Operations.Count, failures.Count);
        Assert.All(failures,
            exception =>
                Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0, remainingConstraints);
        Assert.Equal(0, remainingIndexes);
    }
}
