namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ModelManagedDataConcurrency PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlModelManagedDataConcurrencyIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlModelManagedDataConcurrencyIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Model Managed Update Rechecks Source And Postcondition At Execution.</summary>
    /// <param name="triggerRewritesTarget">
    /// Whether late trigger output, rather than source drift, violates the proof.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelManagedUpdateRechecksSourceAndPostconditionAtExecution(
        bool triggerRewritesTarget
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE model_race_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NOT
            NULL);INSERT INTO model_race_roles (id,
            name) VALUES (1, 'source');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var update = ModelManagedNameUpdate(context.Database.ProviderName!, "model_race_roles");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("model-data-source-race"),
            CancellationToken.None);

        if (triggerRewritesTarget)
        {
            await ExecuteSqlAsync(
                connectionString,
                """
                CREATE FUNCTION model_race_roles_rewrite() RETURNS trigger LANGUAGE plpgsql AS
                $$ BEGIN NEW.name := 'triggered'; RETURN NEW; END $$;
                CREATE TRIGGER trg_model_race_roles_rewrite BEFORE UPDATE ON model_race_roles
                FOR EACH ROW EXECUTE FUNCTION model_race_roles_rewrite();
                """,
                cancellationToken: CancellationToken.None);
        }
        else
        {
            await ExecuteSqlAsync(
                connectionString,
                "UPDATE model_race_roles SET name = 'concurrent' WHERE id = 1;",
                cancellationToken: CancellationToken.None);
        }

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, update.Operations, CancellationToken.None));

        // Assert
        Assert.IsType<PostgresException>(failure);
        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(preflight.Assessments)
                .ObservedState);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                triggerRewritesTarget
                    ? "SELECT COUNT(*) FROM model_race_roles WHERE id = 1 AND name = 'source';"
                    : "SELECT COUNT(*) FROM model_race_roles WHERE id = 1 AND name = 'concurrent';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Model Managed Delete Rechecks Late Dependencies Before Referential Actions.</summary>
    [Theory]
    [InlineData("CASCADE")]
    [InlineData("NO ACTION")]
    [InlineData("RESTRICT")]
    [InlineData("SET DEFAULT")]
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
            CREATE TABLE model_dependency_roles (id integer NOT NULL PRIMARY KEY,
            name varchar(64) NOT NULL);CREATE TABLE model_dependency_users (id integer NOT NULL PRIMARY KEY,
            role_id integer NULL DEFAULT 2, CONSTRAINT fk_model_dependency_users_role FOREIGN KEY (role_id)
            REFERENCES model_dependency_roles (id) ON DELETE {referentialAction});INSERT INTO
            model_dependency_roles (id,
            name) VALUES (1, 'source'), (2, 'default');
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
            "INSERT INTO model_dependency_users (id, role_id) VALUES (11, 1);",
            cancellationToken: CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, deletion.Operations, CancellationToken.None));

        // Assert
        Assert.IsType<PostgresException>(failure);
        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(preflight.Assessments)
                .ObservedState);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_dependency_roles WHERE id = 1;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_dependency_users WHERE id = 11 AND role_id = 1;",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Model Managed Mutation Honors Cancellation During Execution.</summary>
    [Fact]
    public async Task ModelManagedMutationHonorsCancellationDuringExecution()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE model_cancel_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NOT
            NULL);INSERT INTO model_cancel_roles (id,
            name) VALUES (1, 'source');CREATE FUNCTION model_cancel_roles_wait() RETURNS trigger LANGUAGE
            plpgsql AS $$ BEGIN PERFORM pg_advisory_xact_lock(171071); RETURN NEW; END $$;CREATE TRIGGER
            trg_model_cancel_roles_wait BEFORE UPDATE ON model_cancel_roles FOR EACH ROW EXECUTE FUNCTION
            model_cancel_roles_wait();
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var update = ModelManagedNameUpdate(context.Database.ProviderName!, "model_cancel_roles");
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var migrationConnection = (NpgsqlConnection)context.Database.GetDbConnection();
        var observerConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        }.ConnectionString;

        await using var observer = new NpgsqlConnection(observerConnectionString);
        await observer.OpenAsync(CancellationToken.None);
        await using var rendezvous = observer.CreateCommand();
        rendezvous.CommandText = "SELECT pg_catalog.pg_advisory_lock(171071);";
        await rendezvous.ExecuteNonQueryAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Exception? failure;
        Exception? primaryFailure = null;

        // Act
        var execution = ExecuteOperationsAsync(context, update.Operations, cancellation.Token);
        try
        {
            await PostgreSqlNativeAdmission.WaitForAdvisoryLockAsync(
                observer,
                migrationConnection.ProcessID,
                execution,
                CancellationToken.None);

            await cancellation.CancelAsync();
            failure = await Record.ExceptionAsync(() => execution);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            rendezvous.CommandText = "SELECT pg_catalog.pg_advisory_unlock(171071);";
            await PostgreSqlNativeAdmission.CompleteAsync(
                primaryFailure,
                execution,
                observer,
                rendezvous,
                cancellation);
        }

        await using var reuse = migrationConnection.CreateCommand();
        reuse.CommandText = "SELECT 17;";
        var reusedValue = await reuse.ExecuteScalarAsync(CancellationToken.None);

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(PostgresErrorCodes.QueryCanceled,
            Assert.IsType<PostgresException>(canceled.InnerException)
                .SqlState);

        Assert.Equal(17, reusedValue);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_cancel_roles WHERE id = 1 AND name = 'source';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>A pre-canceled mutation is rejected independently of native in-flight cancellation.</summary>
    [Fact]
    public async Task ModelManagedMutationRejectsPreCanceledExecution()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE model_cancel_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NOT NULL);"
            + " INSERT INTO model_cancel_roles VALUES (1, 'source');",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var update = ModelManagedNameUpdate(context.Database.ProviderName!, "model_cancel_roles");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, update.Operations, cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_cancel_roles WHERE id = 1 AND name = 'source';",
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
            ["integer"],
            new object?[,] { { 1 } },
            ["name"],
            ["character varying(64)"],
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
            ["integer"],
            new object?[,] { { 1 } },
            ["id", "name"],
            ["integer", "character varying(64)"],
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
