namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the LifecycleEdgeCases PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlLifecycleEdgeCasesIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlLifecycleEdgeCasesIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Missing Parent Table Is Reported As Prerequisite Failure Across Child Families.</summary>
    [Fact]
    public async Task MissingParentTable_IsReportedAsPrerequisiteFailureAcrossChildFamilies()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "missing_parent",
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_missing_parent_value",
                "missing_parent",
                [new ExpectedIndexKeyDefinition(column: "value")]),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_missing_parent_value",
                "missing_parent",
                SqlColumnAndInt("value", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("missing-parent"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [builder.Operations[0]], CancellationToken.None));

        // Assert
        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(3, report.Assessments.Count);
        Assert.Equal([0, 1, 2], report.Assessments.Select(static assessment => assessment.Ordinal));
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, assessment.Action);
                Assert.Equal("prerequisite_missing", assessment.Code);
            });

        Assert.Equal("P1004", exception.SqlState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'missing_parent';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Legacy Convergence Creates Missing Table With Required Columns And Remains Idempotent.</summary>
    [Fact]
    public async Task LegacyConvergence_CreatesMissingTableWithRequiredColumnsAndRemainsIdempotent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "missing_legacy_table",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition(
                    "required_value",
                    typeof(string),
                    false,
                    "character varying(40)",
                    maxLength: 40),
            ]);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("missing-legacy-table-preflight"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("missing-legacy-table-postflight"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.All(
            preflight.Assessments.Skip(1),
            static assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
            });

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'missing_legacy_table'
                AND is_nullable = 'NO';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Missing Referenced Columns Are Classified Before Any Data Probe.</summary>
    [Fact]
    public async Task MissingReferencedColumns_AreClassifiedBeforeAnyDataProbe()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE column_prerequisite_child (id integer NOT NULL); CREATE TABLE
            column_prerequisite_parent (id integer NOT NULL);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ux_column_prerequisite_code",
                "column_prerequisite_child",
                [new ExpectedIndexKeyDefinition(column: "missing_code")],
                unique: true),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddPrimaryKeyIfNotExists(
            "pk_column_prerequisite_child",
            "column_prerequisite_child",
            ["missing_primary"]);

        builder.AddUniqueConstraintIfNotExists(
            "uq_column_prerequisite_child",
            "column_prerequisite_child",
            ["missing_unique"]);

        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_column_prerequisite_child",
                "column_prerequisite_child",
                SqlColumnAndInt("missing_check", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddForeignKeyIfNotExists(
            "fk_column_prerequisite_child",
            "column_prerequisite_child",
            ["missing_foreign"],
            "column_prerequisite_parent",
            ["missing_principal"]);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("missing-column-prerequisites"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(5, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            static assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, assessment.Action);
            });
    }

    /// <summary>Legacy Convergence Adds Nullable Column Before Unique Index And Remains Idempotent.</summary>
    [Fact]
    public async Task LegacyConvergence_AddsNullableColumnBeforeUniqueIndexAndRemainsIdempotent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE legacy_unique_users (id integer NOT NULL); "
            + "INSERT INTO legacy_unique_users (id) VALUES (1), (2);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "legacy_unique_users",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("email", typeof(string), true, "text"),
            ]);

        var index = new ExpectedIndexDefinition(
            "ux_legacy_unique_users_email",
            "legacy_unique_users",
            [new ExpectedIndexKeyDefinition(column: "email")],
            unique: true);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition, [index]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("legacy-unique-users-preflight"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("legacy-unique-users-postflight"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[^1].ObservedState);
        Assert.Equal("projected_missing", preflight.Assessments[^1].Code);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE schemaname = current_schema()
                AND tablename = 'legacy_unique_users'
                AND indexname = 'ux_legacy_unique_users_email';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Legacy Convergence Does Not Project Unique Safety Through Non Null Default.</summary>
    [Fact]
    public async Task LegacyConvergence_DoesNotProjectUniqueSafetyThroughNonNullDefault()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE legacy_default_users (id integer NOT NULL); "
            + "INSERT INTO legacy_default_users (id) VALUES (1), (2);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "legacy_default_users",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition(
                    "tenant_id",
                    typeof(int),
                    true,
                    "integer",
                    defaultValue: SafeMigrationDefaultValue.Literal(0)),
            ]);

        var index = new ExpectedIndexDefinition(
            "ux_legacy_default_users_tenant",
            "legacy_default_users",
            [new ExpectedIndexKeyDefinition(column: "tenant_id")],
            unique: true);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition, [index]);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("legacy-default-users"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[^1].ObservedState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'legacy_default_users'
                AND column_name = 'tenant_id';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Analyzer Processes More Than One Bounded Classification Chunk In Global Order.</summary>
    [Fact]
    public async Task Analyzer_ProcessesMoreThanOneBoundedClassificationChunkInGlobalOrder()
    {
        // Arrange
        const int operationCount = 513;
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE chunked_analysis (id integer NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < operationCount; ordinal++)
        {
            builder.EnsureColumn(
                "chunked_analysis",
                new ExpectedColumnDefinition($"value_{ordinal}", typeof(int), false, "integer"),
                SafeMigrationPolicy.RepairIfSafe);
        }

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("chunked-analysis"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(operationCount, report.Assessments.Count);
        Assert.Equal(Enumerable.Range(0, operationCount), report.Assessments.Select(static value => value.Ordinal));
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
            });
    }

    /// <summary>
    /// Analyzer Rejects Single Operation Above The Utf8 Payload Limit Before Classification Execution.
    /// </summary>
    [Fact]
    public async Task Analyzer_RejectsSingleOperationAboveTheUtf8PayloadLimitBeforeClassificationExecution()
    {
        // Arrange
        const int maximumUtf8PayloadBytes = 4 * 1024 * 1024;
        var oversizedComment = new string('\u00e4', maximumUtf8PayloadBytes / 2 + 1);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE oversized_analysis (id integer NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "oversized_analysis",
            new ExpectedColumnDefinition("payload", typeof(string), true, "text", comment: oversizedComment),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var failure = await Record.ExceptionAsync(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("oversized-analysis"),
                CancellationToken.None));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("operation 0 exceeds a bounded query limit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("utf8_payload_bytes=", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'oversized_analysis'
                AND column_name = 'payload';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Analyzer Rejects Single Operation Above The Parameter Limit Before Classification Execution.</summary>
    [Fact]
    public async Task Analyzer_RejectsSingleOperationAboveTheParameterLimitBeforeClassificationExecution()
    {
        // Arrange
        const int columnCount = 5_400;
        var columns = Enumerable
            .Range(0, columnCount)
            .Select(ordinal => new ExpectedColumnDefinition(
                $"value_{ordinal}",
                typeof(string),
                true,
                "text",
                comment: $"comment_{ordinal}",
                defaultValue: SafeMigrationDefaultValue.Literal($"default_{ordinal}")))
            .ToArray();

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition("oversized_parameters", columns),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var failure = await Record.ExceptionAsync(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("oversized-parameters"),
                CancellationToken.None));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("operation 0 exceeds a bounded query limit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("parameters=", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = current_schema()
                AND table_name = 'oversized_parameters';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Analyzer Accepts Qualified Caller Owned Read Only Transaction.</summary>
    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Analyzer_AcceptsQualifiedCallerOwnedReadOnlyTransaction(
        IsolationLevel isolationLevel
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(
            isolationLevel,
            CancellationToken.None);

        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", CancellationToken.None);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                [],
                new SafeMigrationRunOptions("qualified-caller-transaction"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.NoOperations, report.Status);
        Assert.Same(transaction, context.Database.CurrentTransaction);
        Assert.Equal(
            "on",
            await ScalarOnCurrentTransactionAsync(
                context,
                "SHOW transaction_read_only;",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Analyzer Rejects Caller Owned Read Committed Transaction.</summary>
    [Fact]
    public async Task Analyzer_RejectsCallerOwnedReadCommittedTransaction()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            CancellationToken.None);

        // Act
        var failure = await Record.ExceptionAsync(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                [],
                new SafeMigrationRunOptions("read-committed-caller-transaction"),
                CancellationToken.None));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("RepeatableRead or Serializable", exception.Message, StringComparison.Ordinal);
        Assert.Same(transaction, context.Database.CurrentTransaction);
    }

    /// <summary>Analyzer Rejects Caller Owned Read Write Transaction.</summary>
    [Fact]
    public async Task Analyzer_RejectsCallerOwnedReadWriteTransaction()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            CancellationToken.None);

        // Act
        var failure = await Record.ExceptionAsync(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                [],
                new SafeMigrationRunOptions("read-write-caller-transaction"),
                CancellationToken.None));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("to be read-only", exception.Message, StringComparison.Ordinal);
        Assert.Same(transaction, context.Database.CurrentTransaction);
    }

    /// <summary>Analyzer Uses One Database Local Signed Bigint Advisory Key.</summary>
    [Fact]
    public void Analyzer_UsesOneDatabaseLocalSignedBigintAdvisoryKey()
    {
        // Arrange
        const string expected = "SELECT pg_catalog.pg_advisory_xact_lock(1397574913::bigint);";

        // Act
        const string actual = PostgreSqlSafeMigrationProviderAnalyzer.AnalysisAdvisoryLockSql;

        // Assert
        Assert.Equal(expected, actual);

        Assert.DoesNotContain("oid", actual);
    }

    /// <summary>Opaque Sql Expression Is Unsupported With Stable Reason Before Target Ddl.</summary>
    [Fact]
    public async Task OpaqueSqlExpression_IsUnsupportedWithStableReasonBeforeTargetDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE opaque_expression (value integer NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        // WHY: The public SQL helper now parses supported CHECK grammar.
        // This negative case intentionally exercises an explicitly opaque definition.
        builder.EnsureCheckConstraint(
            new ExpectedCheckConstraintDefinition(
                "ck_opaque_expression",
                "opaque_expression",
                "value >= 0"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("opaque-expression"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("opaque_sql_expression", assessment.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint WHERE conname = 'ck_opaque_expression';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Unsupported Structured Cast Type Is Rejected Before Target Ddl.</summary>
    [Fact]
    public async Task UnsupportedStructuredCastType_IsRejectedBeforeTargetDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE unsupported_cast_type (value integer NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_unsupported_cast_type",
                "unsupported_cast_type",
                SafeMigrationSql.Binary(
                    SafeMigrationSql.Cast(
                        SafeMigrationSql.Identifier("value"),
                        "integer); DROP TABLE unsupported_cast_type; --"),
                    SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                    SafeMigrationSql.Literal(0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unsupported-cast-type"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("structured_cast_type", assessment.Code);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relname = 'unsupported_cast_type';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint WHERE conname = 'ck_unsupported_cast_type';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Matching Derived Context Uses The Canonical Snapshot And Migration History.</summary>
    [Fact]
    public async Task MatchingDerivedContext_UsesTheCanonicalSnapshotAndMigrationHistory()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = new MatchingDerivedContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var emptyReport = await runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("matching-derived"),
            CancellationToken.None);

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var pendingReport = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("matching-derived"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.NoOperations, emptyReport.Status);

        Assert.Equal(SafeMigrationReportStatus.NoOperations, pendingReport.Status);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM \"__ApplicationDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Postflight Blocks When The Expected Target State Was Not Reached.</summary>
    [Fact]
    public async Task Postflight_BlocksWhenTheExpectedTargetStateWasNotReached()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "postflight_missing",
            table => new { Id = table.Column<int>(type: "integer", nullable: false) });

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .VerifyAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("postflight-negative"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(assessment.PostconditionSatisfied);
        Assert.Equal("postcondition_failed", assessment.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = current_schema()
                AND table_name = 'postflight_missing';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Runner Honors Cancellation Before Catalog Access.</summary>
    [Fact]
    public async Task Runner_HonorsCancellationBeforeCatalogAccess()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var failure = await Record.ExceptionAsync(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("cancelled"), cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = current_schema()
                AND table_name = '__ApplicationDbContextMigrationsHistory';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Runner Cancellation During Catalog Access Closes Its Owned Connection.</summary>
    [Fact]
    public async Task Runner_CancellationDuringCatalogAccessClosesItsOwnedConnection()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var analyzer = new BlockingProviderAnalyzer("npgsql_postgresql");
        var runner = new SafeMigrationRunner(analyzer);
        using var cancellation = new CancellationTokenSource();

        // Act
        var run = runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("cancelled-during-catalog"),
            cancellation.Token);

        Exception? failure;
        Exception? primaryFailure = null;
        try
        {
            await Task.WhenAny(analyzer.Started, run);
            if (!analyzer.Started.IsCompleted)
            {
                await run;

                throw new InvalidOperationException("The runner completed without entering its catalog analyzer.");
            }

            await analyzer.Started;
            await cancellation.CancelAsync();
            failure = await Record.ExceptionAsync(() => run);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            // WHY: The started runner must finish before its context is disposed on every exit.
            await OwnedTestOperationCleanup.CompleteAsync(primaryFailure, run, null, cancellation);
        }

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(
            ConnectionState.Closed,
            context.Database.GetDbConnection()
                .State);
    }

    /// <summary>Pending Preflight Rejects Unknown And Backward Targets And Reports Completed History.</summary>
    /// <param name="caseId">The independent unknown, completed or backward target contract.</param>
    [Theory]
    [InlineData("unknown")]
    [InlineData("completed")]
    [InlineData("backward")]
    public async Task PendingPreflight_RejectsUnknownAndBackwardTargetsAndReportsCompletedHistory(
        string caseId
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        if (caseId != "unknown")
        {
            await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        }

        if (caseId == "backward")
        {
            await ExecuteSqlAsync(
                connectionString,
                """
                INSERT INTO "__ApplicationDbContextMigrationsHistory" ("MigrationId",
                "ProductVersion") VALUES ('209901010000_Future', '10.0.0');
                """,
                cancellationToken: CancellationToken.None);
        }

        var options = caseId switch
        {
            "unknown" => new SafeMigrationRunOptions("unknown-target", "209901010000_Unknown"),
            "completed" => new SafeMigrationRunOptions("completed"),
            "backward" => new SafeMigrationRunOptions("backward-target", CoreConvergenceMigration.MigrationIdentifier),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId)),
        };

        SafeMigrationRunReport? report = null;

        // Act
        var failure = await Record.ExceptionAsync(async () =>
            report = await runner.AnalyzePendingMigrationsAsync(context, options, CancellationToken.None));

        // Assert
        switch (caseId)
        {
            case "unknown":
                Assert.IsType<ArgumentException>(failure);
                break;
            case "backward":
                Assert.IsType<InvalidOperationException>(failure);
                break;
            default:
                Assert.Null(failure);
                Assert.NotNull(report);
                Assert.Equal(SafeMigrationReportStatus.NoOperations, report.Status);
                Assert.Empty(report.Assessments);
                break;
        }
    }

    /// <summary>Convergence Baseline Down Fails Before Producing Destructive Operations.</summary>
    [Fact]
    public void ConvergenceBaseline_DownFailsBeforeProducingDestructiveOperations()
    {
        // Arrange
        var migration = new CoreConvergenceMigration();

        // Act
        var failure = Record.Exception(() => { _ = migration.DownOperations; });

        // Assert
        var exception = Assert.IsType<NotSupportedException>(failure);

        Assert.Contains("forward-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rename Target Collisions Are Rejected Without Mutating Either Object.</summary>
    /// <param name="operationOrdinal">The independent table, column or index rename.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RenameTargetCollisions_AreRejectedWithoutMutatingEitherObject(
        int operationOrdinal
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE rename_source (source_column integer NULL, target_column integer
            NULL); CREATE TABLE rename_target (id integer NOT NULL); CREATE INDEX rename_source_index
            ON rename_source (source_column); CREATE INDEX rename_target_index ON rename_source
            (target_column);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_target");
        builder.RenameColumnIfExists("source_column", "rename_source", "target_column");
        builder.RenameIndexIfExists("rename_source_index", "rename_source", "rename_target_index");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("rename-collisions"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [builder.Operations[operationOrdinal]], CancellationToken.None));

        // Assert
        Assert.Equal(3, report.Assessments.Count);
        Assert.Equal([0, 1, 2], report.Assessments.Select(static assessment => assessment.Ordinal));
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        Assert.Equal("P1001",
            Assert.IsType<PostgresException>(failure)
                .SqlState);

        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = current_schema()
                AND table_name IN ('rename_source', 'rename_target');
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'rename_source'
                AND column_name IN ('source_column', 'target_column');
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relkind = 'i'
                AND relname IN ('rename_source_index', 'rename_target_index');
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Unexpected Object Inventory Covers Every Constraint Family Without Mutation.</summary>
    [Fact]
    public async Task UnexpectedObjectInventory_CoversEveryConstraintFamilyWithoutMutation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE inventory_parent (id integer NOT NULL PRIMARY KEY); CREATE TABLE
            inventory_constraints (id integer NOT NULL,
            parent_id integer NULL, code text NULL, quantity integer NOT NULL, CONSTRAINT
            pk_inventory_constraints PRIMARY KEY (id),
            CONSTRAINT uq_inventory_code UNIQUE (code), CONSTRAINT ck_inventory_quantity CHECK (quantity >= 0),
            CONSTRAINT fk_inventory_parent FOREIGN KEY (parent_id) REFERENCES inventory_parent (id));
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "inventory_constraints",
            table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                ParentId = table.Column<int>(name: "parent_id", type: "integer", nullable: true),
                Code = table.Column<string>(type: "text", nullable: true),
                Quantity = table.Column<int>(type: "integer", nullable: false),
            },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unexpected-constraints"),
                CancellationToken.None);

        // Assert
        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.PrimaryKey, Name: "pk_inventory_constraints",
            });

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.UniqueConstraint, Name: "uq_inventory_code",
            });

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.CheckConstraint, Name: "ck_inventory_quantity",
            });

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is { ObjectKind: SafeMigrationDatabaseObjectKind.ForeignKey, Name: "fk_inventory_parent" });

        Assert.Equal(
            4,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_constraint co
                JOIN pg_catalog.pg_class c ON c.oid = co.conrelid
                WHERE c.relname = 'inventory_constraints'
                AND co.conname IN ('pk_inventory_constraints', 'uq_inventory_code', 'ck_inventory_quantity',
                'fk_inventory_parent');
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Four Concurrent Migrators Produce One History Row And One Canonical Schema.</summary>
    [Fact]
    public async Task FourConcurrentMigrators_ProduceOneHistoryRowAndOneCanonicalSchema()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var contexts = Enumerable
            .Range(0, 4)
            .Select(_ => CreateContext(connectionString))
            .ToArray();

        Exception? primaryFailure = null;

        // Act
        var migrations = Task.WhenAll(
            contexts.Select(context => context.Database.MigrateAsync(cancellationToken: CancellationToken.None)));

        try
        {
            await migrations;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            await OwnedTestOperationCleanup.CompleteAsync(
                primaryFailure,
                migrations,
                null,
                null,
                () => Task.WhenAll(contexts.Select(async context => await context.DisposeAsync())));
        }

        // Assert
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM \"__ApplicationDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'pipeline_state'
                AND column_name = 'payload';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Reads a scalar using the fixture's current transaction.</summary>
    /// <param name="context">The configured fixture context.</param>
    /// <param name="sql">The source-controlled fixture SQL.</param>
    /// <param name="cancellationToken">Cancels the owned operation before and during asynchronous work.</param>
    /// <returns>The asynchronous fixture operation or observation.</returns>
    private static async Task<string> ScalarOnCurrentTransactionAsync(
        DbContext context,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = context
            .Database
            .GetDbConnection()
            .CreateCommand();

        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;

        var result = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToString(result, CultureInfo.InvariantCulture) ?? "<null>";
    }
}
