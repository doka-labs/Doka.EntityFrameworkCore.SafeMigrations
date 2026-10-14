namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Rejects a dependency committed while the generated delete waits for its own fence.</summary>
    /// <param name="referentialAction">The native incoming foreign-key delete action.</param>
    /// <param name="isolationLevel">The caller-owned transaction isolation preserved by execution.</param>
    [Theory]
    [InlineData("CASCADE", System.Data.IsolationLevel.ReadCommitted)]
    [InlineData("SET NULL", System.Data.IsolationLevel.ReadCommitted)]
    [InlineData("SET DEFAULT", System.Data.IsolationLevel.ReadCommitted)]
    [InlineData("NO ACTION", System.Data.IsolationLevel.ReadCommitted)]
    [InlineData("RESTRICT", System.Data.IsolationLevel.ReadCommitted)]
    [InlineData("CASCADE", System.Data.IsolationLevel.RepeatableRead)]
    [InlineData("SET NULL", System.Data.IsolationLevel.RepeatableRead)]
    [InlineData("SET DEFAULT", System.Data.IsolationLevel.RepeatableRead)]
    [InlineData("NO ACTION", System.Data.IsolationLevel.RepeatableRead)]
    [InlineData("RESTRICT", System.Data.IsolationLevel.RepeatableRead)]
    [InlineData("CASCADE", System.Data.IsolationLevel.Serializable)]
    [InlineData("SET NULL", System.Data.IsolationLevel.Serializable)]
    [InlineData("SET DEFAULT", System.Data.IsolationLevel.Serializable)]
    [InlineData("NO ACTION", System.Data.IsolationLevel.Serializable)]
    [InlineData("RESTRICT", System.Data.IsolationLevel.Serializable)]
    public async Task ModelManagedDeleteFencePreservesConcurrentDependents(
        string referentialAction,
        System.Data.IsolationLevel isolationLevel
    )
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync(referentialAction);
        var nativeDeleteAction = await ScalarStringAsync(connectionString,
            "SELECT confdeltype::text FROM pg_catalog.pg_constraint "
            + "WHERE conname = 'fk_model_fence_users_role' AND conrelid = 'model_fence_users'::regclass;");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await using var migrationTransaction = await context.Database.BeginTransactionAsync(isolationLevel);
        var migrationConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        var migrationPid = migrationConnection.ProcessID;
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);
        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync();
        await using var writerTransaction = await writer.BeginTransactionAsync();
        await ExecuteModelDeleteFenceSqlAsync(writer, "INSERT INTO model_fence_users VALUES (11, 1);");
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        var writerCommitted = false;
        Exception? failure;

        // Act
        var deletionTask = Record.ExceptionAsync(() => ExecuteOperationsAsync(context, deletion.Operations));
        try
        {
            await WaitForModelDeleteFenceBlockerAsync(observer, migrationPid, writer.ProcessID);
            await writerTransaction.CommitAsync();
            writerCommitted = true;
            failure = await deletionTask;
        }
        finally
        {
            if (!writerCommitted)
            {
                await writerTransaction.RollbackAsync();
            }

            await deletionTask;
            await migrationTransaction.RollbackAsync();
        }

        // Assert
        Assert.Equal(referentialAction switch
        {
            "CASCADE" => "c",
            "SET NULL" => "n",
            "SET DEFAULT" => "d",
            "NO ACTION" => "a",
            "RESTRICT" => "r",
            _ => throw new ArgumentOutOfRangeException(nameof(referentialAction)),
        }, nativeDeleteAction);

        var exception = Assert.IsType<PostgresException>(failure);
        if (isolationLevel == System.Data.IsolationLevel.ReadCommitted)
        {
            Assert.Equal("P1003", exception.SqlState);
            Assert.StartsWith("doka_sm_data_blocked:", exception.MessageText, StringComparison.Ordinal);
        }
        else
        {
            // WHY: A transaction snapshot cannot become fresh after waiting.
            // Native RI crosschecks abort rather than mutate newly committed
            // children. PostgreSQL 14 shares ForeignKeyViolation for RESTRICT
            // and NO ACTION; PostgreSQL 18 distinguishes RestrictViolation.
            // Only these restrictive actions may use those native diagnostics.
            var expectedFailure = exception.SqlState is "40001" or "P1003"
                || (referentialAction == "NO ACTION" && exception.SqlState == "23503")
                || (referentialAction == "RESTRICT" && (exception.SqlState is "23503" or "23001"));

            Assert.True(expectedFailure, exception.SqlState);
            if (exception.SqlState is "23503" or "23001")
            {
                Assert.Equal("fk_model_fence_users_role", exception.ConstraintName);
            }
            else if (exception.SqlState == "P1003")
            {
                Assert.StartsWith("doka_sm_data_blocked:", exception.MessageText, StringComparison.Ordinal);
            }
        }

        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1 AND name = 'source';"));
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_users WHERE id = 11 AND role_id = 1;"));
    }

    /// <summary>
    /// Deletes the exact source once and preserves idempotent replay under every supported isolation.
    /// </summary>
    /// <param name="isolationLevel">The explicit caller-owned transaction isolation.</param>
    [Theory]
    [InlineData(System.Data.IsolationLevel.ReadCommitted)]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task ModelManagedDeleteFenceAllowsExactSourceAndReplay(
        System.Data.IsolationLevel isolationLevel
    )
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync("CASCADE");
        await using var context = CreateContext(connectionString);
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);
        await using var transaction = await context.Database.BeginTransactionAsync(isolationLevel);

        // Act
        await ExecuteOperationsAsync(context, deletion.Operations);
        await ExecuteOperationsAsync(context, deletion.Operations);
        await transaction.CommitAsync();

        // Assert
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1;"));
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 2 AND name = 'default';"));
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_users;"));
    }

    /// <summary>Already committed dependencies remain blocked without applying any referential action.</summary>
    /// <param name="referentialAction">The native incoming foreign-key delete action.</param>
    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    [InlineData("NO ACTION")]
    [InlineData("RESTRICT")]
    public async Task ModelManagedDeleteFencePreservesAlreadyCommittedDependents(
        string referentialAction
    )
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync(referentialAction);
        await ExecuteSqlAsync(connectionString, "INSERT INTO model_fence_users VALUES (11, 1);");
        await using var context = CreateContext(connectionString);
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, deletion.Operations));

        // Assert
        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal("P1003", exception.SqlState);
        Assert.StartsWith("doka_sm_data_blocked:", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1 AND name = 'source';"));
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_users WHERE id = 11 AND role_id = 1;"));
    }

    /// <summary>
    /// Disabled native FK enforcement cannot be used as an application-writer concurrency certificate.
    /// </summary>
    /// <param name="bypass">The independently configured native enforcement bypass.</param>
    [Theory]
    [InlineData("child")]
    [InlineData("parent")]
    [InlineData("replica")]
    public async Task ModelManagedDeleteFenceRejectsDisabledNativeEnforcement(
        string bypass
    )
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync("CASCADE");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        var migrationConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);
        var bypassSql = bypass switch
        {
            "child" => "ALTER TABLE model_fence_users DISABLE TRIGGER ALL;",
            "parent" => "ALTER TABLE model_fence_roles DISABLE TRIGGER ALL;",
            "replica" => "SET session_replication_role = replica;",
            _ => throw new ArgumentOutOfRangeException(nameof(bypass)),
        };

        await ExecuteModelDeleteFenceSqlAsync(migrationConnection, bypassSql);
        Exception? failure;

        // Act
        try
        {
            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, deletion.Operations));
        }
        finally
        {
            if (StringComparer.Ordinal.Equals(bypass, "replica"))
            {
                await ExecuteModelDeleteFenceSqlAsync(migrationConnection, "RESET session_replication_role;");
            }
        }

        // Assert
        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal("P1002", exception.SqlState);
        Assert.StartsWith("doka_sm_unsupported:", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1 AND name = 'source';"));
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_users;"));
    }

    /// <summary>
    /// Rejects PostgreSQL 18 NOT ENFORCED FKs without treating older enforced catalogs as unavailable evidence.
    /// </summary>
    [Fact]
    public async Task ModelManagedDeleteFenceQualifiesNativeForeignKeyEnforcementByCatalogVersion()
    {
        // Arrange
        var supportsNotEnforced = Fixture.ServerVersion.Major >= 18;
        var connectionString = await CreateModelDeleteFenceFixtureAsync(
            "CASCADE", enforced: supportsNotEnforced ? false : null);

        await using var context = CreateContext(connectionString);
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);
        var nativeTriggers = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_trigger native_trigger "
            + "JOIN pg_catalog.pg_constraint native_constraint ON native_constraint.oid = native_trigger.tgconstraint "
            + "WHERE native_constraint.conname = 'fk_model_fence_users_role' AND native_trigger.tgisinternal;");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, deletion.Operations, new SafeMigrationRunOptions("model-delete-native-enforcement"));
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, deletion.Operations));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1 AND name = 'source';");

        // Assert
        var assessment = Assert.Single(report.Assessments);
        if (supportsNotEnforced)
        {
            Assert.Equal(0, nativeTriggers);
            Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
            var exception = Assert.IsType<PostgresException>(failure);

            Assert.Equal("P1002", exception.SqlState);
            Assert.Equal(1, remaining);
        }
        else
        {
            Assert.True(nativeTriggers > 0);
            Assert.Equal(SafeMigrationObservedState.TransitionReady, assessment.ObservedState);
            Assert.Null(failure);
            Assert.Equal(0, remaining);
        }
    }

    /// <summary>
    /// Enforced incoming FKs and the absence of incoming FKs both retain their ordinary delete behavior.
    /// </summary>
    /// <param name="hasConstraint">Whether a physically enforced incoming relationship is present.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelManagedDeleteFenceAllowsEnforcedOrAbsentNativeForeignKeys(
        bool hasConstraint
    )
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync(
            hasConstraint ? "CASCADE" : null,
            enforced: hasConstraint && Fixture.ServerVersion.Major >= 18 ? true : null);

        await using var context = CreateContext(connectionString);
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!, includeForeignKey: hasConstraint);
        var nativeEnforcement = await ScalarStringAsync(connectionString,
            "SELECT COALESCE((SELECT COALESCE(pg_catalog.to_jsonb(native_constraint)->>'conenforced', 'before_18') "
            + "FROM pg_catalog.pg_constraint native_constraint "
            + "WHERE native_constraint.conname = 'fk_model_fence_users_role'), 'absent');");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, deletion.Operations, new SafeMigrationRunOptions("model-delete-enforced-control"));
        await ExecuteOperationsAsync(context, deletion.Operations);

        // Assert
        Assert.Equal(SafeMigrationObservedState.TransitionReady, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(hasConstraint ? Fixture.ServerVersion.Major >= 18 ? "true" : "before_18" : "absent",
            nativeEnforcement);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1;"));
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 2 AND name = 'default';"));
    }

    /// <summary>
    /// Captured relationships without a native enforced counterpart cannot supply a parent-lock certificate.
    /// </summary>
    [Fact]
    public async Task ModelManagedDeleteFenceRejectsCapturedRelationshipWithoutNativeForeignKey()
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync(referentialAction: null);
        await using var context = CreateContext(connectionString);
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!, includeForeignKey: true);
        var nativeConstraints = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_constraint WHERE contype = 'f';");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, deletion.Operations, new SafeMigrationRunOptions("model-delete-missing-native-fk"));
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, deletion.Operations));

        // Assert
        Assert.Equal(0, nativeConstraints);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        var exception = Assert.IsType<PostgresException>(failure);

        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1 AND name = 'source';"));
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_users;"));
    }

    /// <summary>Reclassifies source drift that commits while the generated delete is waiting.</summary>
    /// <param name="isolationLevel">The caller-owned isolation controlling concurrent source visibility.</param>
    [Theory]
    [InlineData(System.Data.IsolationLevel.ReadCommitted)]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task ModelManagedDeleteFenceRechecksConcurrentSourceChanges(
        System.Data.IsolationLevel isolationLevel
    )
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync("CASCADE");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await using var migrationTransaction = await context.Database.BeginTransactionAsync(isolationLevel);
        var migrationConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);
        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync();
        await using var writerTransaction = await writer.BeginTransactionAsync();
        await ExecuteModelDeleteFenceSqlAsync(writer,
            "UPDATE model_fence_roles SET name = 'concurrent' WHERE id = 1;");
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        var writerCommitted = false;
        Exception? failure;

        // Act
        var deletionTask = Record.ExceptionAsync(() => ExecuteOperationsAsync(context, deletion.Operations));
        try
        {
            await WaitForModelDeleteFenceBlockerAsync(
                observer, migrationConnection.ProcessID, writer.ProcessID);
            await writerTransaction.CommitAsync();
            writerCommitted = true;
            failure = await deletionTask;
        }
        finally
        {
            if (!writerCommitted)
            {
                await writerTransaction.RollbackAsync();
            }

            await deletionTask;
            await migrationTransaction.RollbackAsync();
        }

        // Assert
        var exception = Assert.IsType<PostgresException>(failure);
        if (isolationLevel == System.Data.IsolationLevel.ReadCommitted)
        {
            Assert.Equal("P1001", exception.SqlState);
            Assert.StartsWith("doka_sm_different:", exception.MessageText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("40001", exception.SqlState);
        }

        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1 AND name = 'concurrent';"));
    }

    /// <summary>Rechecks incoming topology after waiting for a concurrent native foreign-key addition.</summary>
    [Fact]
    public async Task ModelManagedDeleteFenceRejectsConcurrentUnmodeledForeignKey()
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync(referentialAction: null);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        var migrationConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!, includeForeignKey: false);
        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync();
        await using var writerTransaction = await writer.BeginTransactionAsync();
        await ExecuteModelDeleteFenceSqlAsync(writer,
            "ALTER TABLE model_fence_users ADD CONSTRAINT fk_model_fence_users_role "
            + "FOREIGN KEY (role_id) REFERENCES model_fence_roles (id) ON DELETE CASCADE;"
            + "INSERT INTO model_fence_users VALUES (11, 1);");
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        var writerCommitted = false;
        Exception? failure;

        // Act
        var deletionTask = Record.ExceptionAsync(() => ExecuteOperationsAsync(context, deletion.Operations));
        try
        {
            await WaitForModelDeleteFenceBlockerAsync(
                observer, migrationConnection.ProcessID, writer.ProcessID);
            await writerTransaction.CommitAsync();
            writerCommitted = true;
            failure = await deletionTask;
        }
        finally
        {
            if (!writerCommitted)
            {
                await writerTransaction.RollbackAsync();
            }

            await deletionTask;
        }

        // Assert
        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal("P1002", exception.SqlState);
        Assert.StartsWith("doka_sm_unsupported:", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1 AND name = 'source';"));
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_users WHERE id = 11 AND role_id = 1;"));
    }

    /// <summary>Cancellation while waiting leaves source rows unchanged and releases the owned fence.</summary>
    [Fact]
    public async Task ModelManagedDeleteFenceCancellationPreservesSourceAndAllowsRetry()
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync("CASCADE");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        var migrationConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);
        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync();
        await using var writerTransaction = await writer.BeginTransactionAsync();
        await ExecuteModelDeleteFenceSqlAsync(writer, "INSERT INTO model_fence_users VALUES (11, 1);");
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        Exception? failure;

        // Act
        var deletionTask = Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, deletion.Operations, cancellation.Token));

        try
        {
            await WaitForModelDeleteFenceBlockerAsync(
                observer, migrationConnection.ProcessID, writer.ProcessID);
            cancellation.Cancel();
            failure = await deletionTask;
        }
        finally
        {
            await writerTransaction.RollbackAsync();
            await deletionTask;
        }

        var remainingSource = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1 AND name = 'source';");

        await ExecuteOperationsAsync(context, deletion.Operations);

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, remainingSource);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1;"));
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_users;"));
    }

    /// <summary>Writers arriving after the owned fence cannot become hidden referential-action targets.</summary>
    [Fact]
    public async Task ModelManagedDeleteFenceExcludesNewDependencyWritersUntilCommit()
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync("CASCADE");
        var rendezvous = Random.Shared.NextInt64(1, long.MaxValue).ToString(CultureInfo.InvariantCulture);
        await ExecuteSqlAsync(connectionString,
            "CREATE FUNCTION model_fence_pause_delete() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN PERFORM pg_catalog.pg_advisory_xact_lock(" + rendezvous + "); RETURN OLD; END; $$;"
            + "CREATE TRIGGER model_fence_pause_delete BEFORE DELETE ON model_fence_roles "
            + "FOR EACH ROW EXECUTE FUNCTION model_fence_pause_delete();");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        var migrationConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);
        await using var controller = new NpgsqlConnection(connectionString);
        await controller.OpenAsync();
        await ExecuteModelDeleteFenceSqlAsync(controller,
            "SELECT pg_catalog.pg_advisory_lock(" + rendezvous + ");");
        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync();
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        var released = false;
        Task<Exception?>? insertionTask = null;
        Exception? deletionFailure;
        Exception? insertionFailure;

        // Act
        var deletionTask = Record.ExceptionAsync(() => ExecuteOperationsAsync(context, deletion.Operations));
        try
        {
            await WaitForModelDeleteFenceBlockerAsync(
                observer, migrationConnection.ProcessID, controller.ProcessID);
            insertionTask = Record.ExceptionAsync(() =>
                ExecuteModelDeleteFenceSqlAsync(writer, "INSERT INTO model_fence_users VALUES (11, 1);"));

            await WaitForModelDeleteFenceBlockerAsync(
                observer, writer.ProcessID, migrationConnection.ProcessID);
            await ExecuteModelDeleteFenceSqlAsync(controller,
                "SELECT pg_catalog.pg_advisory_unlock(" + rendezvous + ");");
            released = true;
            deletionFailure = await deletionTask;
            insertionFailure = await insertionTask;
        }
        finally
        {
            if (!released)
            {
                await ExecuteModelDeleteFenceSqlAsync(controller,
                    "SELECT pg_catalog.pg_advisory_unlock(" + rendezvous + ");");
            }

            await deletionTask;
            if (insertionTask is not null)
            {
                await insertionTask;
            }
        }

        // Assert
        Assert.Null(deletionFailure);
        var exception = Assert.IsType<PostgresException>(insertionFailure);
        Assert.Equal("23503", exception.SqlState);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1;"));
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_users;"));
    }

    /// <summary>Deferred incoming checks cannot commit a new child after the fenced delete.</summary>
    /// <param name="referentialAction">The native incoming foreign-key delete action.</param>
    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    [InlineData("NO ACTION")]
    [InlineData("RESTRICT")]
    public async Task ModelManagedDeleteFencePreservesDeferredForeignKeyEnforcement(
        string referentialAction
    )
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync(referentialAction, deferred: true);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await using var migrationTransaction = await context.Database.BeginTransactionAsync();
        var migrationConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);
        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync();
        await using var writerTransaction = await writer.BeginTransactionAsync();
        await ExecuteModelDeleteFenceSqlAsync(writer, "INSERT INTO model_fence_users VALUES (11, 1);");
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        var migrationCommitted = false;
        Task<Exception?>? writerCommitTask = null;
        Exception? failure;

        // Act
        try
        {
            await ExecuteOperationsAsync(context, deletion.Operations);
            writerCommitTask = Record.ExceptionAsync(() => writerTransaction.CommitAsync());
            await WaitForModelDeleteFenceBlockerAsync(
                observer, writer.ProcessID, migrationConnection.ProcessID);
            await migrationTransaction.CommitAsync();
            migrationCommitted = true;
            failure = await writerCommitTask;
        }
        finally
        {
            if (!migrationCommitted)
            {
                await migrationTransaction.RollbackAsync();
            }

            if (writerCommitTask is not null)
            {
                await writerCommitTask;
            }
        }

        // Assert
        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal("23503", exception.SqlState);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1;"));
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_users;"));
    }

    /// <summary>Exercises the generated fence without granting any new UPDATE or child-write privileges.</summary>
    [Fact]
    public async Task ModelManagedDeleteFenceUsesExistingDeleteAndSelectPrivileges()
    {
        // Arrange
        var connectionString = await CreateModelDeleteFenceFixtureAsync("CASCADE");
        var role = "model_delete_reader_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await ExecuteSqlAsync(connectionString,
            "CREATE ROLE " + role + "; GRANT USAGE ON SCHEMA public TO " + role + ";"
            + "GRANT SELECT, DELETE ON model_fence_roles TO " + role + ";"
            + "GRANT SELECT ON model_fence_users TO " + role + ";");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        var migrationConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        var deletion = ModelDeleteFenceOperation(context.Database.ProviderName!);

        // Act
        try
        {
            await ExecuteModelDeleteFenceSqlAsync(migrationConnection, "SET ROLE " + role + ";");
            await ExecuteOperationsAsync(context, deletion.Operations);
        }
        finally
        {
            await ExecuteModelDeleteFenceSqlAsync(migrationConnection, "RESET ROLE;");
            await ExecuteSqlAsync(connectionString, "DROP OWNED BY " + role + "; DROP ROLE " + role + ";");
        }

        // Assert
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 1;"));
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM model_fence_roles WHERE id = 2;"));
    }

    /// <summary>Creates an isolated frozen-source fixture with an optional native incoming foreign key.</summary>
    /// <param name="referentialAction">The fixed native action, or null to leave incoming topology absent.</param>
    /// <param name="deferred">Whether the incoming check is explicitly deferred until the writer commits.</param>
    /// <param name="enforced">The PostgreSQL 18 enforcement clause, or null to use the version-neutral default.</param>
    /// <returns>The fixture-owned database connection string.</returns>
    private async Task<string> CreateModelDeleteFenceFixtureAsync(
        string? referentialAction,
        bool deferred = false,
        bool? enforced = null
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var foreignKey = referentialAction is null ? string.Empty
            : ", CONSTRAINT fk_model_fence_users_role FOREIGN KEY (role_id) "
            + "REFERENCES model_fence_roles (id) ON DELETE " + referentialAction
            + (deferred ? " DEFERRABLE INITIALLY DEFERRED" : string.Empty)
            + (enforced is null ? string.Empty : enforced.Value ? " ENFORCED" : " NOT ENFORCED");

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE model_fence_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NOT NULL);"
            + "CREATE TABLE model_fence_users (id integer NOT NULL PRIMARY KEY, role_id integer NULL DEFAULT 2"
            + foreignKey + "); INSERT INTO model_fence_roles VALUES (1, 'source'), (2, 'default');");

        return connectionString;
    }

    /// <summary>Builds the reviewed source-frozen delete without any mutable external evidence.</summary>
    /// <param name="providerName">The configured provider identity.</param>
    /// <param name="includeForeignKey">Whether the incoming fixture relationship belongs to the frozen source.</param>
    /// <returns>The single-operation migration used by the adversarial tests.</returns>
    private static MigrationBuilder ModelDeleteFenceOperation(
        string providerName,
        bool includeForeignKey = true
    )
    {
        var builder = new MigrationBuilder(providerName);
        _ = builder.DeleteModelManagedDataFromModel(
            "model_fence_roles",
            ["id"],
            ["integer"],
            new object?[,] { { 1 } },
            ["id", "name"],
            ["integer", "character varying(64)"],
            new object?[,] { { 1, "source" } },
            foreignKeys: includeForeignKey
                ? [new ExpectedModelManagedDataForeignKeyDefinition("model_fence_users", ["role_id"], ["id"])]
                : []);

        return builder;
    }

    /// <summary>Executes fixture SQL on the already-open transaction-owning connection.</summary>
    /// <param name="connection">The isolated fixture connection.</param>
    /// <param name="sql">The fixture command text.</param>
    private static async Task ExecuteModelDeleteFenceSqlAsync(
        NpgsqlConnection connection,
        string sql
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Waits for a server-observed blocker instead of relying on scheduler timing or sleeps.</summary>
    /// <param name="observer">The independent fixture observer connection.</param>
    /// <param name="blockedPid">The generated-delete backend expected to wait.</param>
    /// <param name="blockingPid">The uncommitted writer backend that must cause the wait.</param>
    private static async Task WaitForModelDeleteFenceBlockerAsync(
        NpgsqlConnection observer,
        int blockedPid,
        int blockingPid
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var command = observer.CreateCommand();
        command.CommandText = "SELECT @blocker = ANY(pg_catalog.pg_blocking_pids(@blocked));";
        command.Parameters.AddWithValue("blocker", blockingPid);
        command.Parameters.AddWithValue("blocked", blockedPid);

        while (!Convert.ToBoolean(await command.ExecuteScalarAsync(timeout.Token), CultureInfo.InvariantCulture))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }
}
