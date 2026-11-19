namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ModelManagedDataConcurrency migration contracts in an independent class collection.</summary>
public sealed class MySqlModelManagedDataConcurrencyIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlModelManagedDataConcurrencyIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Independently rechecks late source drift and trigger-modified target postconditions.</summary>
    /// <param name="lateMutation">The concurrent source replacement or target-rewriting trigger.</param>
    [Theory]
    [InlineData("source-row")]
    [InlineData("target-trigger")]
    public async Task ModelManagedUpdateRechecksSourceAndPostconditionAtExecution(
        string lateMutation
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `model_race_roles` (`id` int NOT NULL, `name` varchar(64) NOT
            NULL,
            PRIMARY KEY (`id`)) ENGINE=InnoDB;INSERT INTO `model_race_roles` (`id`,
            `name`) VALUES (1, 'source');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var update = ModelManagedNameUpdate(context.Database.ProviderName!, "model_race_roles");
        var runner = context.GetService<ISafeMigrationRunner>();

        var mutationSql = lateMutation == "source-row"
            ? "UPDATE `model_race_roles` SET `name` = 'concurrent' WHERE `id` = 1;"
            : "CREATE TRIGGER `trg_model_race_roles_rewrite` BEFORE UPDATE "
            + "ON `model_race_roles` FOR EACH ROW SET NEW.`name` = 'triggered';";

        var preservedValue = lateMutation == "source-row"
            ? "concurrent"
            : "source";

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("model-data-source-race"),
            CancellationToken.None);

        await ExecuteSqlAsync(connectionString, mutationSql, cancellationToken: CancellationToken.None);

        Exception? failure;
        await using (var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None))
        {
            failure = await Record.ExceptionAsync(() =>
                ExecuteOperationsAsync(context, update.Operations, CancellationToken.None));

            // The provider intentionally leaves transaction ownership with EF.
            // Roll back explicitly here because this helper invokes the generated
            // migration command outside EF's Migrator transaction boundary.
            await transaction.RollbackAsync(CancellationToken.None);
        }

        var preservedRows = await ScalarIntAsync(
            connectionString,
            $"SELECT COUNT(*) FROM `model_race_roles` WHERE `id` = 1 AND `name` = '{preservedValue}';",
            cancellationToken: CancellationToken.None);

        // Assert
        AssertOwnedGuardFailure(failure);
        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(preflight.Assessments)
                .ObservedState);

        Assert.Equal(1, preservedRows);
    }

    /// <summary>Rechecks a late dependent row before applying any declared foreign-key action.</summary>
    /// <param name="referentialAction">The native foreign-key action challenged by a late dependent row.</param>
    [Theory]
    [InlineData("CASCADE")]
    [InlineData("NO ACTION")]
    [InlineData("RESTRICT")]
    [InlineData("SET NULL")]
    public async Task ModelManagedDeleteRechecksLateDependenciesBeforeReferentialActions(
        string referentialAction
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            $"""
            CREATE TABLE `model_dependency_roles` (`id` int NOT NULL, `name` varchar(64)
            NOT NULL,
            PRIMARY KEY (`id`)) ENGINE=InnoDB;CREATE TABLE `model_dependency_users`
            (`id` int NOT NULL,
            `role_id` int NULL, PRIMARY KEY (`id`), CONSTRAINT `fk_model_dependency_users_role`
            FOREIGN KEY (`role_id`) REFERENCES `model_dependency_roles` (`id`) ON DELETE
            {referentialAction}) ENGINE=InnoDB;INSERT INTO `model_dependency_roles`
            (`id`,
            `name`) VALUES (1, 'source');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var deletion = ModelManagedRoleDeletion(context.Database.ProviderName!);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            deletion.Operations,
            new SafeMigrationRunOptions("model-data-dependency-race"),
            CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `model_dependency_users` (`id`, `role_id`) VALUES (11, 1);",
            cancellationToken: CancellationToken.None);

        _ = await AssertOwnedGuardFailureAsync(
            operationToken =>
                ExecuteOperationsAsync(context, deletion.Operations, operationToken),
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(preflight.Assessments)
                .ObservedState);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `model_dependency_roles` WHERE `id` = 1;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `model_dependency_users` WHERE `id` = 11 AND `role_id` = 1;",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>
    /// Cancels only after the managed write reaches its native statement and preserves the original row.
    /// </summary>
    [Fact]
    public async Task ModelManagedMutationHonorsCancellationDuringExecution()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `model_cancel_roles` (`id` int NOT NULL, `name` varchar(64)
            NOT NULL,
            PRIMARY KEY (`id`)) ENGINE=InnoDB;INSERT INTO `model_cancel_roles` (`id`,
            `name`) VALUES (1, 'source');
            """,
            cancellationToken: CancellationToken.None);

        // WHY: A retained row lock, unlike a timed SLEEP trigger, cannot expire before a
        // loaded observer sees the actual UPDATE. Scope disposal always releases the owner.
        await using var blocker = new MySqlConnection(connectionString);
        await blocker.OpenAsync(CancellationToken.None);
        await using var transaction = await blocker.BeginTransactionAsync(CancellationToken.None);
        await using var blockingCommand = blocker.CreateCommand();
        blockingCommand.Transaction = transaction;
        blockingCommand.CommandText = "SELECT `id` FROM `model_cancel_roles` WHERE `id` = 1 FOR UPDATE;";
        await blockingCommand.ExecuteScalarAsync(CancellationToken.None);

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var update = ModelManagedNameUpdate(context.Database.ProviderName!, "model_cancel_roles");
        using var cancellation = new CancellationTokenSource();

        // Act
        var exception = await MySqlNativeCancellationCheckpoint.CancelWhenDispatchedAsync(
            connectionString,
            ((MySqlConnection)context.Database.GetDbConnection()).ServerThread,
            "UPDATE `model_cancel_roles`",
            cancellation,
            Record.ExceptionAsync(() => ExecuteOperationsAsync(context, update.Operations, cancellation.Token)),
            CancellationToken.None);

        // Assert
        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `model_cancel_roles` WHERE `id` = 1 AND `name` = 'source';",
                cancellationToken: CancellationToken.None));
    }

    private static MigrationBuilder ModelManagedNameUpdate(
        string providerName,
        string table
    )
    {
        var builder = new MigrationBuilder(providerName);
        _ = builder.UpdateModelManagedDataFromModel(
            table,
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            ["name"],
            ["varchar(64)"],
            new object?[,] { { "source" } },
            new object?[,] { { "target" } });

        return builder;
    }

    private static MigrationBuilder ModelManagedRoleDeletion(
        string providerName
    )
    {
        var builder = new MigrationBuilder(providerName);
        _ = builder.DeleteModelManagedDataFromModel(
            "model_dependency_roles",
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            ["id", "name"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, "source" } },
            foreignKeys:
            [
                new ExpectedModelManagedDataForeignKeyDefinition(
                    "model_dependency_users",
                    ["role_id"],
                    ["id"]),
            ]);

        return builder;
    }
}
