namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the SchemaIdentity PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlSchemaIdentityIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlSchemaIdentityIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Current-schema aliases preserve drop/ensure order and final-writer verification.</summary>
    /// <param name="kind">The catalog resource replaced in the ordered stream.</param>
    /// <param name="qualifiedDrop">Whether the drop uses the explicit current schema.</param>
    [Theory]
    [InlineData("table", true)]
    [InlineData("table", false)]
    [InlineData("column", true)]
    [InlineData("column", false)]
    [InlineData("index", true)]
    [InlineData("index", false)]
    [InlineData("primary", true)]
    [InlineData("primary", false)]
    [InlineData("unique", true)]
    [InlineData("unique", false)]
    [InlineData("check", true)]
    [InlineData("check", false)]
    [InlineData("foreign", true)]
    [InlineData("foreign", false)]
    public async Task CurrentSchemaAliasReplacement_UsesProjectedStateAndVerifiesFinalWriter(
        string kind,
        bool qualifiedDrop
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, SchemaIdentityFixtureSql(kind));
        await using var context = CreateContext(connectionString);
        var builder = SchemaIdentityReplacement(context, kind, qualifiedDrop);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("current-schema-replacement"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("current-schema-final-writer"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    /// <summary>The current schema is discovered from the effective path, not assumed to be public.</summary>
    /// <param name="searchPath">The effective path, optionally containing a missing leading schema.</param>
    [Theory]
    [InlineData("identity_module")]
    [InlineData("identity_missing,identity_module,public")]
    public async Task NonPublicCurrentSchemaReplacement_PreservesItsVerifiedAlias(string searchPath)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_module; CREATE TABLE identity_module.identity_target (id integer NULL);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = searchPath };
        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("identity_target", "identity_module");
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("nonpublic-current-schema"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
    }

    /// <summary>Ordinary typed owners share verified schema aliases with following safe operations.</summary>
    /// <param name="qualifiedProvider">Whether the ordinary column add or the safe index carries the qualifier.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryColumnAddAndSafeIndex_ShareVerifiedCurrentSchemaAliases(bool qualifiedProvider)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE public.identity_target (id integer NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumn<int>("added", "identity_target", type: "integer", nullable: true,
            schema: qualifiedProvider ? "public" : null);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_identity_added", "identity_target",
                [new ExpectedIndexKeyDefinition(column: "added")], qualifiedProvider ? null : "public"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("ordinary-safe-current-schema-aliases"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("ordinary-safe-current-schema-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'identity_target' AND indexname = 'ix_identity_added';"));
    }

    /// <summary>A typed ordinary column drop clears the same physical owner seen by an aliased safe ensure.</summary>
    /// <param name="qualifiedProvider">Whether the ordinary drop or safe ensure carries the qualifier.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryColumnDropAndSafeEnsure_ShareVerifiedCurrentSchemaAliases(bool qualifiedProvider)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE public.identity_target (id integer NULL, restored integer NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumn("restored", "identity_target", schema: qualifiedProvider ? "public" : null);
        builder.AddColumnIfNotExists<int>("restored", "identity_target", type: "integer", nullable: true,
            schema: qualifiedProvider ? null : "public");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("ordinary-drop-safe-ensure-schema-aliases"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("ordinary-drop-safe-ensure-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'identity_target' AND column_name = 'restored';"));
    }

    /// <summary>A fallback native owner is rejected before reading or changing the other schema.</summary>
    [Fact]
    public async Task UnqualifiedSearchPathFallback_IsRejectedBeforeCatalogAndExecution()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_module; CREATE TABLE public.identity_target (id integer NULL);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "identity_module,public",
        };

        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "identity_target", type: "integer", nullable: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("search-path-fallback"));

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[0].ObservedState);
        Assert.Equal("schema_search_path_mismatch", report.Assessments[0].AnalysisCode);
        Assert.Equal("P1002", failure.SqlState);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'identity_target' "
            + "AND column_name = 'added';"));
    }

    /// <summary>A metadata-only owner refusal shares classification rather than adding a prerequisite batch.</summary>
    [Fact]
    public async Task CatalogOnlyTableIdentityGuard_RejectsShadowWithoutPrerequisiteRoundTrip()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE public.identity_target (id integer NULL);");
        await using var context = CreateContext(connectionString);
        await using var connection = new CatalogClassificationCountingConnection(
            new NpgsqlConnection(connectionString));

        context.Database.SetDbConnection(connection);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("CREATE TEMPORARY TABLE identity_target (id integer NULL);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var results = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        // Assert
        Assert.Equal(0, connection.PrerequisiteStatementCount);
        Assert.Equal(SafeMigrationObservedState.Unsupported, results[0].ObservedState);
        Assert.Equal("schema_search_path_mismatch", results[0].Code);
        Assert.False(results[0].PostconditionSatisfied);
    }

    /// <summary>Explicit qualification keeps a foreign-schema owner valid even when it is in the search path.</summary>
    [Fact]
    public async Task QualifiedSearchPathFallback_RemainsSupportedAndIsolated()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_module; CREATE TABLE public.identity_target (id integer NULL);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "identity_module,public",
        };

        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "identity_target", type: "integer", nullable: true,
            schema: "public");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("qualified-fallback"));

        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'identity_target' AND column_name = 'added';"));
    }

    /// <summary>A runtime table contract borrows standalone constraints only from its verified current alias.</summary>
    /// <param name="qualifiedTable">Whether the table or the standalone constraint is explicitly qualified.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedCurrentSchemaTableAndStandaloneConstraint_ShareRuntimeContract(bool qualifiedTable)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE public.identity_target (id integer NULL, CONSTRAINT uq_identity_target UNIQUE (id));");

        await using var context = CreateContext(connectionString);
        var builder = SchemaIdentityTableConstraint(context, qualifiedTable, "public");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("current-schema-transition-contract"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("current-schema-transition-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    /// <summary>A single foreign qualifier never authorizes an otherwise unmodeled current-schema constraint.</summary>
    [Fact]
    public async Task ForeignStandaloneConstraint_DoesNotAuthorizeEarlierUnqualifiedTable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_foreign; CREATE TABLE identity_foreign.identity_target (id integer NULL); "
            + "CREATE TABLE public.identity_target (id integer NULL, CONSTRAINT uq_identity_target UNIQUE (id));");

        await using var context = CreateContext(connectionString);
        var builder = SchemaIdentityTableConstraint(context, qualifiedTable: false, "identity_foreign");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("foreign-transition-contract"));

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[0].ObservedState);
        Assert.Equal("P1001", failure.SqlState);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_constraint constraint_object "
            + "JOIN pg_catalog.pg_class relation ON relation.oid = constraint_object.conrelid "
            + "JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace "
            + "WHERE namespace.nspname = 'identity_foreign' AND constraint_object.conname = 'uq_identity_target';"));
    }

    /// <summary>Implicit temporary-schema precedence cannot redirect a guard to a shadow relation.</summary>
    [Fact]
    public async Task TemporaryTableShadow_IsRejectedBeforeChangingEitherOwner()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE public.identity_target (id integer NULL);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("CREATE TEMPORARY TABLE identity_target (id integer NULL);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("identity_target");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("temporary-owner-shadow"));

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal("schema_search_path_mismatch", report.Assessments[0].AnalysisCode);
        Assert.Equal("P1002", failure.SqlState);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' "
            + "AND table_name = 'identity_target';"));
    }

    /// <summary>A qualified operation remains valid when the connection has no effective default schema.</summary>
    [Fact]
    public async Task MissingCurrentSchema_ExplicitOwnerRemainsSupported()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var configured = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "identity_missing" };
        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")], "public"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("qualified-no-default-schema"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var verification = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("qualified-no-default-schema-postflight"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, verification.Status);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' "
            + "AND table_name = 'identity_target';"));
    }

    /// <summary>An unavailable default schema cannot be inferred from another authored qualifier.</summary>
    [Fact]
    public async Task MissingCurrentSchema_UnqualifiedOwnerIsRejected()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var configured = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "identity_missing" };
        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("identity_target");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("unqualified-no-default-schema"));

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal("schema_search_path_mismatch", report.Assessments[0].AnalysisCode);
        Assert.Equal("P1002", failure.SqlState);
    }

    /// <summary>Analysis and generation cannot borrow a future qualified contract across opaque SQL.</summary>
    [Fact]
    public async Task RawSqlBarrier_DoesNotLendLaterConstraintToEarlierTable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE public.identity_target (id integer NULL, CONSTRAINT uq_identity_target UNIQUE (id));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.Sql("SET search_path TO public;");
        builder.EnsureUniqueConstraint(new ExpectedUniqueConstraintDefinition(
                "uq_identity_target", "identity_target", ["id"], "public"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("opaque-schema-contract-boundary"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
    }

    /// <summary>A reused analyzer captures a caller's changed default schema afresh for every invocation.</summary>
    [Fact]
    public async Task CurrentSchemaIdentity_IsNotRetainedBetweenAnalysisInvocations()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_first; CREATE SCHEMA identity_second; "
            + "CREATE TABLE identity_first.identity_target (id integer NULL); "
            + "CREATE TABLE identity_second.identity_target (id integer NULL);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "identity_first" };
        await using var context = CreateContext(configured.ConnectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var first = SchemaIdentityTableReplacement(context, "identity_first");
        var second = SchemaIdentityTableReplacement(context, "identity_second");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var firstReport = await runner.AnalyzeAsync(context, first.Operations,
            new SafeMigrationRunOptions("first-current-schema"));

        await context.Database.ExecuteSqlRawAsync("SET search_path TO identity_second;");
        var secondReport = await runner.AnalyzeAsync(context, second.Operations,
            new SafeMigrationRunOptions("second-current-schema"));

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, firstReport.Assessments[^1].Action);
        Assert.Equal(SafeMigrationAction.Apply, secondReport.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, firstReport.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, secondReport.Status);
    }

    /// <summary>Case-distinct quoted schemas cannot become current-schema aliases.</summary>
    [Fact]
    public async Task QuotedSchemaCase_RemainsDistinctDuringProjection()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA \"Scope\"; CREATE SCHEMA \"scope\"; "
            + "CREATE TABLE \"Scope\".identity_target (id integer NULL); "
            + "CREATE TABLE \"scope\".identity_target (id integer NULL);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "scope" };
        await using var context = CreateContext(configured.ConnectionString);
        var builder = SchemaIdentityTableReplacement(context, "Scope");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("quoted-schema-case"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[^1].Action);
    }

    /// <summary>An earlier unqualified writer cannot be falsely superseded using the final session's schema.</summary>
    [Fact]
    public async Task PostflightHistoricalUnqualifiedOwner_DoesNotHideRetainedOldSchemaTable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_final; CREATE TABLE public.identity_target (id integer NULL); "
            + "CREATE TABLE identity_final.identity_target (id integer NULL);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "identity_final" };
        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("identity_target");
        builder.Sql("SET search_path TO identity_final;");
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")], "identity_final"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("historical-owner-not-proven"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' "
            + "AND table_name = 'identity_target';"));
    }

    /// <summary>An explicit historical owner remains verifiable across a search-path boundary.</summary>
    [Fact]
    public async Task PostflightExplicitHistoricalOwner_RemainsSupportedAcrossSearchPathChange()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_final; CREATE TABLE identity_final.identity_target (id integer NULL);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "identity_final" };
        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("identity_target", "public");
        builder.Sql("SET search_path TO identity_final;");
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")], "identity_final"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("explicit-historical-owner"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.All(report.Assessments.Where(static assessment => assessment.IsSafeOperation),
            assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>Opaque SQL prevents proof of an earlier unqualified final owner without blocking execution.</summary>
    [Fact]
    public async Task PostflightHistoricalUnqualifiedIndex_RequiresAnExplicitOwnerAcrossOpaqueSql()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE public.identity_target (id integer NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition(
                "ix_identity_target", "identity_target", [new ExpectedIndexKeyDefinition(column: "id")]),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.Sql("UPDATE public.identity_target SET id = 7;");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("historical-unqualified-index"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
        Assert.Equal("postcondition_identity_unproven", report.Assessments[0].Code);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE schemaname = 'public' "
            + "AND tablename = 'identity_target' AND indexname = 'ix_identity_target';"));
    }

    /// <summary>Raw SQL can establish the current owner which an initial fallback lookup could not prove.</summary>
    [Fact]
    public async Task RawSqlEstablishesCurrentOwner_InitialMismatchIsDeferredAndRuntimeRechecked()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA identity_module; CREATE TABLE public.identity_target (id integer NULL);");

        var configured = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "identity_module,public",
        };

        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Sql("CREATE TABLE identity_module.identity_target (id integer NULL);");
        builder.AddColumnIfNotExists<int>("added", "identity_target", type: "integer", nullable: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("raw-sql-current-owner"));

        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'identity_module' "
            + "AND table_name = 'identity_target' AND column_name = 'added';"));
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'identity_target' AND column_name = 'added';"));
    }

    /// <summary>Creating a missing leading path schema invalidates the initial unqualified owner proof.</summary>
    [Fact]
    public async Task SchemaCreationActivatesSearchPathEntry_LaterTableEnsureIsDeferred()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE public.identity_target (id integer NULL);");
        var configured = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "identity_new,public",
        };

        await using var context = CreateContext(configured.ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists("identity_new");
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("schema-activation-boundary"));

        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.Equal(2, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'identity_target' "
            + "AND table_schema IN ('identity_new', 'public');"));
    }

    /// <summary>Creates a minimal replacement with a qualified source and unqualified target.</summary>
    private static MigrationBuilder SchemaIdentityTableReplacement(DbContext context, string schema)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("identity_target", schema);
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        return builder;
    }

    /// <summary>Creates the same minimal table/constraint contract with alternate schema spellings.</summary>
    private static MigrationBuilder SchemaIdentityTableConstraint(
        DbContext context,
        bool qualifiedTable,
        string qualifier
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), true, "integer")],
                qualifiedTable ? qualifier : null),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureUniqueConstraint(new ExpectedUniqueConstraintDefinition(
                "uq_identity_target", "identity_target", ["id"], qualifiedTable ? null : qualifier),
            SafeMigrationPolicy.ThrowIfDifferent);

        return builder;
    }

    /// <summary>Builds a minimal live resource without introducing unrelated schema dependencies.</summary>
    private static string SchemaIdentityFixtureSql(string kind)
    {
        var resource = kind switch
        {
            "index" => "CREATE INDEX ix_identity_target ON public.identity_target (code);",
            "primary" => "ALTER TABLE public.identity_target ADD CONSTRAINT pk_identity_target PRIMARY KEY (id);",
            "unique" => "ALTER TABLE public.identity_target ADD CONSTRAINT uq_identity_target UNIQUE (code);",
            "check" => "ALTER TABLE public.identity_target ADD CONSTRAINT ck_identity_target CHECK (id >= 0);",
            "foreign" => "ALTER TABLE public.identity_target ADD CONSTRAINT fk_identity_target "
                + "FOREIGN KEY (parent_id) REFERENCES public.identity_parent (id);",
            _ => string.Empty,
        };

        return "CREATE TABLE public.identity_parent (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE public.identity_target (id integer NOT NULL, parent_id integer NULL, code integer NULL); "
            + resource;
    }

    /// <summary>Creates only the accepted drop and its exact replacement through the other schema spelling.</summary>
    private static MigrationBuilder SchemaIdentityReplacement(
        DbContext context,
        string kind,
        bool qualifiedDrop
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var dropSchema = qualifiedDrop ? "public" : null;
        var ensureSchema = qualifiedDrop ? null : "public";
        switch (kind)
        {
            case "table":
                builder.DropTableIfExists("identity_target", dropSchema);
                builder.EnsureTable(new ExpectedTableDefinition(
                    "identity_target",
                    [
                        new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                        new ExpectedColumnDefinition("parent_id", typeof(int), true, "integer"),
                        new ExpectedColumnDefinition("code", typeof(int), true, "integer"),
                    ],
                    ensureSchema), SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "column":
                builder.DropColumnIfExists("code", "identity_target", dropSchema);
                builder.EnsureColumn("identity_target",
                    new ExpectedColumnDefinition("code", typeof(int), true, "integer"),
                    SafeMigrationPolicy.ThrowIfDifferent, ensureSchema);
                break;
            case "index":
                builder.DropIndexIfExists("ix_identity_target", "identity_target", dropSchema);
                builder.EnsureIndex(new ExpectedIndexDefinition(
                    "ix_identity_target", "identity_target", [new ExpectedIndexKeyDefinition(column: "code")],
                    ensureSchema), SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "primary":
                builder.DropPrimaryKeyIfExists("pk_identity_target", "identity_target", dropSchema);
                builder.EnsurePrimaryKey(new ExpectedPrimaryKeyDefinition(
                    "pk_identity_target", "identity_target", ["id"], ensureSchema),
                    SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "unique":
                builder.DropUniqueConstraintIfExists("uq_identity_target", "identity_target", dropSchema);
                builder.EnsureUniqueConstraint(new ExpectedUniqueConstraintDefinition(
                    "uq_identity_target", "identity_target", ["code"], ensureSchema),
                    SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "check":
                builder.DropCheckConstraintIfExists("ck_identity_target", "identity_target", dropSchema);
                builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_identity_target", "identity_target",
                    SqlColumnAndInt("id", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0), ensureSchema),
                    SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "foreign":
                builder.DropForeignKeyIfExists("fk_identity_target", "identity_target", dropSchema);
                builder.EnsureForeignKey(new ExpectedForeignKeyDefinition(
                    "fk_identity_target", "identity_target", ["parent_id"], "identity_parent", ["id"],
                    ensureSchema, "public"), SafeMigrationPolicy.ThrowIfDifferent);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return builder;
    }

}
