namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the SchemaIdentityRuntimeEffects PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlSchemaIdentityRuntimeEffectsIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlSchemaIdentityRuntimeEffectsIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>DDL-trigger identity changes cannot redirect the postcondition to another physical owner.</summary>
    /// <param name="effect">The trigger's bounded identity effect, or an inert positive control.</param>
    [Theory]
    [InlineData("search-path")]
    [InlineData("temporary-shadow")]
    [InlineData("stable")]
    public async Task RuntimeDdlTriggerIdentityEffect_IsRecheckedBeforePostcondition(string effect)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var triggerBody = effect switch
        {
            "search-path" => "PERFORM pg_catalog.set_config('search_path', 'identity_redirect,public', false);",
            "temporary-shadow" => "EXECUTE 'CREATE TEMPORARY TABLE identity_target "
                + "(id integer NULL, added integer NULL)';",
            "stable" => "PERFORM 1;",
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE public.identity_target (id integer NULL); "
            + "CREATE SCHEMA identity_redirect; "
            + "CREATE TABLE identity_redirect.identity_target (id integer NULL, added integer NULL); "
            + "CREATE FUNCTION public.identity_guard_effect() RETURNS event_trigger LANGUAGE plpgsql "
            + "AS $identity_effect$ BEGIN IF TG_TAG = 'ALTER TABLE' THEN " + triggerBody
            + " END IF; END; $identity_effect$; "
            + "CREATE EVENT TRIGGER identity_after_alter ON ddl_command_end "
            + "EXECUTE FUNCTION public.identity_guard_effect();");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "identity_target", type: "integer", nullable: true);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var currentSchema = await context.Database.SqlQueryRaw<string>(
            "SELECT current_schema()::text AS \"Value\"").SingleAsync();

        var temporaryShadows = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::integer AS \"Value\" FROM pg_catalog.pg_class "
            + "WHERE relnamespace = pg_catalog.pg_my_temp_schema() AND relname = 'identity_target'").SingleAsync();

        var targetColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'identity_target' AND column_name = 'added';");

        // Assert
        Assert.Equal("public", currentSchema);
        Assert.Equal(0, temporaryShadows);
        if (effect == "stable")
        {
            Assert.Null(failure);
            Assert.Equal(1, targetColumns);
        }
        else
        {
            var providerFailure = Assert.IsType<PostgresException>(failure);

            Assert.Equal("P1005", providerFailure.SqlState);
            Assert.Equal("schema_identity_changed_during_execution", providerFailure.Detail);
            Assert.Equal(0, targetColumns);
        }
    }

    /// <summary>Native index-name lookup cannot be redirected by a temporary index on another table.</summary>
    /// <param name="rename">Whether the operation renames the source index instead of dropping it.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryIndexShadow_IsRejectedBeforeChangingEitherIndex(bool rename)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE public.identity_target (id integer NULL); "
            + "CREATE INDEX ix_identity_target ON public.identity_target (id);");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TEMPORARY TABLE identity_auxiliary (id integer NULL); "
            + "CREATE INDEX ix_identity_target ON identity_auxiliary (id);");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (rename)
        {
            builder.RenameIndexIfExists("ix_identity_target", "identity_target", "ix_identity_renamed");
        }
        else
        {
            builder.DropIndexIfExists("ix_identity_target", "identity_target");
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("temporary-index-owner-shadow"));

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOperationsAsync(context, builder.Operations));

        var temporaryIndexes = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::integer AS \"Value\" FROM pg_catalog.pg_class "
            + "WHERE relnamespace = pg_catalog.pg_my_temp_schema() AND relname = 'ix_identity_target' "
            + "AND relkind = 'i'").SingleAsync();

        // Assert
        Assert.Equal("schema_search_path_mismatch", report.Assessments[0].AnalysisCode);
        Assert.Equal("P1002", failure.SqlState);
        Assert.Equal(1, temporaryIndexes);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_class relation "
            + "JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace "
            + "WHERE namespace.nspname = 'public' AND relation.relname = 'ix_identity_target';"));
    }

    /// <summary>Consuming a source does not mistake its newly visible fallback for a changed surviving owner.</summary>
    /// <param name="kind">The table or index name consumed by a drop or rename.</param>
    [Theory]
    [InlineData("drop-table")]
    [InlineData("rename-table")]
    [InlineData("drop-index")]
    [InlineData("rename-index")]
    public async Task ConsumedSourceFallback_RemainsIsolatedDuringExecutionAndVerification(string kind)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_tail; "
            + "CREATE TABLE public.identity_target (id integer NULL); "
            + "CREATE TABLE identity_tail.identity_target (id integer NULL); "
            + "CREATE INDEX ix_identity_target ON public.identity_target (id); "
            + "CREATE INDEX ix_identity_target ON identity_tail.identity_target (id);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "public,identity_tail",
        };

        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        switch (kind)
        {
            case "drop-table":
                builder.DropTableIfExists("identity_target");
                break;
            case "rename-table":
                builder.RenameTableIfExists("identity_target", newName: "identity_renamed");
                break;
            case "drop-index":
                builder.DropIndexIfExists("ix_identity_target", "identity_target");
                break;
            case "rename-index":
                builder.RenameIndexIfExists("ix_identity_target", "identity_target", "ix_identity_renamed");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("consumed-source-fallback"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(2, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_class relation "
            + "JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace "
            + "WHERE namespace.nspname = 'identity_tail' "
            + "AND relation.relname IN ('identity_target', 'ix_identity_target');"));
    }

    /// <summary>A creation target cannot exempt an actual same-name unqualified inline FK principal.</summary>
    [Fact]
    public async Task CreationTargetAndInlinePrincipalSharingAName_StillRequireNativePrincipalProof()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TEMPORARY TABLE identity_target (id integer NOT NULL PRIMARY KEY);");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [
                    new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                    new ExpectedColumnDefinition("parent_id", typeof(int), true, "integer"),
                ],
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_identity_target", "identity_target", ["id"]),
                foreignKeys: [new ExpectedForeignKeyDefinition("fk_identity_parent", "identity_target", ["parent_id"],
                    "identity_target", ["id"])]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("same-name-inline-principal"));

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal("schema_search_path_mismatch", report.Assessments[0].AnalysisCode);
        Assert.Equal("P1002", failure.SqlState);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' "
            + "AND table_name = 'identity_target';"));
    }

    /// <summary>A qualified trigger-bearing DDL makes a later initial path mismatch runtime-dependent.</summary>
    [Fact]
    public async Task QualifiedDdlTriggerChangesPath_LaterInitialMismatchIsDeferredAndReproved()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_final; CREATE TABLE public.identity_source (id integer NULL); "
            + "CREATE TABLE identity_final.identity_target (id integer NULL); "
            + "CREATE FUNCTION public.identity_scope_move() RETURNS event_trigger LANGUAGE plpgsql "
            + "AS $identity_move$ BEGIN IF TG_TAG = 'ALTER TABLE' THEN "
            + "PERFORM pg_catalog.set_config('search_path', 'identity_final,public', false); "
            + "END IF; END; $identity_move$; "
            + "CREATE EVENT TRIGGER identity_scope_after_alter ON ddl_command_end "
            + "EXECUTE FUNCTION public.identity_scope_move();");

        var configured = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "public,identity_final",
        };

        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("first_added", "identity_source", type: "integer", nullable: true,
            schema: "public");
        builder.AddColumnIfNotExists<int>("later_added", "identity_target", type: "integer", nullable: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("trigger-qualified-schema-boundary"));

        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal("projected_schema_identity_state_unknown", report.Assessments[1].AnalysisCode);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'identity_final' "
            + "AND table_name = 'identity_target' AND column_name = 'later_added';"));
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' "
            + "AND table_name = 'identity_target';"));
    }

    /// <summary>A schema boundary cannot defer an intrinsically unsupported annotated contract.</summary>
    [Fact]
    public async Task ScopedIdentityChange_DoesNotDeferInvariantUnsupportedContract()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_final; CREATE TABLE identity_final.identity_target (id integer NULL);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "public,identity_final",
        };

        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Sql("SET search_path TO identity_final,public;");
        builder.AddColumnIfNotExists<int>("added", "identity_target", type: "integer", nullable: true)
            .Annotation("UncertifiedProviderExtension", true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("invariant-unsupported-scoped-owner"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Equal("schema_search_path_mismatch", report.Assessments[1].AnalysisCode);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'identity_final' "
            + "AND table_name = 'identity_target' AND column_name = 'added';"));
    }

    /// <summary>Captured trigger risk cannot certify a retained old-schema owner through the final alias.</summary>
    /// <param name="effect">An actual path change, absent risk, or certified no-op under active risk.</param>
    [Theory]
    [InlineData("redirect")]
    [InlineData("absent")]
    [InlineData("certified-noop")]
    public async Task PostflightQualifiedTriggerBoundary_DoesNotCertifyAnotherSchemaOwner(string effect)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_final; CREATE TABLE public.identity_source (id integer NULL); "
            + "CREATE TABLE public.identity_target (id integer NULL); "
            + "CREATE TABLE identity_final.identity_target (id integer NULL); "
            + "INSERT INTO public.identity_target VALUES (1); "
            + "INSERT INTO identity_final.identity_target VALUES (2);");

        if (effect != "absent")
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE FUNCTION public.identity_final_scope() RETURNS event_trigger LANGUAGE plpgsql "
                + "AS $identity_final_scope$ BEGIN IF TG_TAG = 'ALTER TABLE' THEN "
                + "PERFORM pg_catalog.set_config('search_path', 'identity_final,public', false); "
                + "END IF; END; $identity_final_scope$; "
                + "CREATE EVENT TRIGGER identity_final_scope_after_alter ON ddl_command_end "
                + "EXECUTE FUNCTION public.identity_final_scope();");
        }

        var configured = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "public,identity_final",
        };

        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        if (effect == "certified-noop")
        {
            builder.RenameTable("identity_source", schema: "public", newName: "identity_source");
        }
        else
        {
            builder.AddColumnIfNotExists<int>("added", "identity_source", type: "integer", nullable: true,
                schema: "public");
        }

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("historical-trigger-schema-owner"));

        // Assert
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT id FROM public.identity_target;"));
        Assert.Equal(2, await ScalarIntAsync(connectionString, "SELECT id FROM identity_final.identity_target;"));
        if (effect == "redirect")
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.False(report.Assessments[0].PostconditionSatisfied);
            Assert.Equal("postcondition_identity_unproven", report.Assessments[0].Code);
        }
        else
        {
            Assert.Equal(effect == "certified-noop" ? SafeMigrationReportStatus.ReadyWithProviderOperations
                : SafeMigrationReportStatus.Ready, report.Status);
            Assert.True(report.Assessments[0].PostconditionSatisfied);
        }
    }

}
