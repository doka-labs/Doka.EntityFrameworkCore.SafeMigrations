namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the IndexesDriftMatrix PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlIndexesDriftMatrixIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlIndexesDriftMatrixIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Equivalent Index With Different Name Is An Idempotent No Op.</summary>
    [Fact]
    public async Task EquivalentIndexWithDifferentName_IsAnIdempotentNoOp()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE index_identity (code integer NOT NULL, tenant_id integer NOT NULL);CREATE INDEX
            ix_index_identity_legacy_a ON index_identity (code,
            tenant_id);CREATE INDEX ix_index_identity_legacy_b ON index_identity (code,
            tenant_id);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.EnsureTable(
            new ExpectedTableDefinition(
                "index_identity",
                [
                    new ExpectedColumnDefinition("code", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("tenant_id", typeof(int), isNullable: false, storeType: "integer"),
                ]),
            SafeMigrationTableMode.ConvergenceContainer,
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.CreateIndexIfNotExists(
            "ix_index_identity_expected",
            "index_identity",
            ["code", "tenant_id"]);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("index-identity"),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(2, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
            });

        Assert.Empty(report.UnexpectedObjects);
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relname IN ('ix_index_identity_legacy_a',
                'ix_index_identity_legacy_b', 'ix_index_identity_expected');
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Differently Named Index With Different Shape Remains Applicable.</summary>
    [Fact]
    public async Task DifferentlyNamedIndexWithDifferentShape_RemainsApplicable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE index_nonidentity (code integer NOT NULL, alternate_code integer
            NOT NULL);CREATE INDEX ix_index_nonidentity_legacy ON index_nonidentity
            (code);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_index_nonidentity_expected",
            "index_nonidentity",
            ["alternate_code"]);

        // Act
        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("index-nonidentity"),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("index-nonidentity-post"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(preflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp,
            Assert.Single(postflight.Assessments)
                .Action);
    }

    /// <summary>Invalid Index With Expected Name Is Rejected Before Ddl.</summary>
    [Fact]
    public async Task InvalidIndexWithExpectedName_IsRejectedBeforeDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE invalid_index_drift (id integer NOT NULL, code integer NOT NULL);INSERT INTO
            invalid_index_drift VALUES (1,
            7), (2, 7);
            """,
            cancellationToken: CancellationToken.None);

        var setupFailure = await Record.ExceptionAsync(() =>
            ExecuteSqlAsync(
                connectionString,
                "CREATE UNIQUE INDEX CONCURRENTLY ix_invalid_index_drift ON invalid_index_drift (code);",
                cancellationToken: CancellationToken.None));

        var invalidIndexCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM pg_catalog.pg_index i
            JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid
            WHERE idx.relname = 'ix_invalid_index_drift'
            AND NOT i.indisvalid;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_invalid_index_drift",
            "invalid_index_drift",
            ["code"],
            unique: true);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("invalid-index-drift"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        // Assert
        _ = Assert.IsType<PostgresException>(setupFailure);
        Assert.Equal(1, invalidIndexCount);
        var exception = Assert.IsType<PostgresException>(failure);
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("P1001", exception.SqlState);
        Assert.StartsWith("doka_sm_different:", exception.MessageText, StringComparison.Ordinal);
    }

    /// <summary>Differently Named Invalid Index Does Not Block Valid Index.</summary>
    [Fact]
    public async Task DifferentlyNamedInvalidIndex_DoesNotBlockValidIndex()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE invalid_index_identity (id integer NOT NULL, code integer NOT NULL);INSERT INTO
            invalid_index_identity VALUES (1,
            7), (2, 7);
            """,
            cancellationToken: CancellationToken.None);

        var setupFailure = await Record.ExceptionAsync(() =>
            ExecuteSqlAsync(
                connectionString,
                "CREATE UNIQUE INDEX CONCURRENTLY ix_invalid_index_legacy ON invalid_index_identity (code);",
                cancellationToken: CancellationToken.None));

        await ExecuteSqlAsync(
            connectionString,
            "DELETE FROM invalid_index_identity WHERE id = 2;",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_invalid_index_expected",
            "invalid_index_identity",
            ["code"],
            unique: true);

        // Act
        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("invalid-index-identity"),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("invalid-index-post"),
                CancellationToken.None);

        // Assert
        var preflightAssessment = Assert.Single(preflight.Assessments);
        var postflightAssessment = Assert.Single(postflight.Assessments);

        _ = Assert.IsType<PostgresException>(setupFailure);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflightAssessment.Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, postflightAssessment.Action);
    }

    /// <summary>Partitioned Parent Index Is Matching While Attached Child Operations Are Rejected.</summary>
    /// <param name="operationIndex">The independently rejected attached-index operation.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PartitionedParentIndex_IsMatching_WhileAttachedChildOperationsAreRejected(
        int operationIndex
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE partitioned_index_root (bucket integer NOT NULL, code integer NOT NULL) PARTITION
            BY RANGE (bucket);CREATE TABLE partitioned_index_leaf PARTITION OF partitioned_index_root FOR VALUES
            FROM (0) TO (100);CREATE INDEX ix_partitioned_index_root_code ON partitioned_index_root (code);
            """,
            cancellationToken: CancellationToken.None);

        var childIndex = await ScalarStringAsync(
            connectionString,
            """
            SELECT child.relname FROM pg_catalog.pg_inherits inh
            JOIN pg_catalog.pg_class child ON child.oid = inh.inhrelid
            JOIN pg_catalog.pg_class parent ON parent.oid = inh.inhparent
            WHERE parent.relname = 'ix_partitioned_index_root_code';
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var parent = new MigrationBuilder(context.Database.ProviderName!);
        parent.CreateIndexIfNotExists(
            "ix_partitioned_index_root_code",
            "partitioned_index_root",
            ["code"]);

        var child = new MigrationBuilder(context.Database.ProviderName!);
        child.CreateIndexIfNotExists(childIndex, "partitioned_index_leaf", ["code"]);
        child.DropIndexIfExists(childIndex, "partitioned_index_leaf");
        child.RenameIndexIfExists(childIndex, "partitioned_index_leaf", "ix_partitioned_index_leaf_renamed");

        // Act
        var parentReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                parent.Operations,
                new SafeMigrationRunOptions("partitioned-index-parent"),
                CancellationToken.None);

        var childReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                child.Operations,
                new SafeMigrationRunOptions("partitioned-index-child"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [child.Operations[operationIndex]], CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, parentReport.Status);
        Assert.Equal(SafeMigrationObservedState.Matching,
            Assert.Single(parentReport.Assessments)
                .ObservedState);

        Assert.Equal(SafeMigrationReportStatus.Blocked, childReport.Status);
        Assert.Equal(3, childReport.Assessments.Count);
        Assert.All(
            childReport.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal("P1001", exception.SqlState);
        Assert.StartsWith("doka_sm_different:", exception.MessageText, StringComparison.Ordinal);
    }

    /// <summary>Constraint Owned Index Operations Are Rejected Before Ddl.</summary>
    /// <param name="operationIndex">The independently executed member of the full preflight stream.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ConstraintOwnedIndexOperations_AreRejectedBeforeDdl(
        int operationIndex
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE constraint_owned_index (code integer NULL, "
            + "CONSTRAINT ix_constraint_owned_code UNIQUE (code));",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_constraint_owned_code",
            "constraint_owned_index",
            ["code"],
            unique: true);

        builder.CreateIndexIfNotExists(
            "ix_constraint_owned_expected",
            "constraint_owned_index",
            ["code"],
            unique: true);

        builder.DropIndexIfExists("ix_constraint_owned_code", "constraint_owned_index");
        builder.RenameIndexIfExists(
            "ix_constraint_owned_code",
            "constraint_owned_index",
            "ix_constraint_owned_renamed");

        // Act
        // WHY: Preserve the complete preflight stream; each native rejection owns an isolated test case.
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("constraint-owned-index"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [builder.Operations[operationIndex]], CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(4, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal("P1001", exception.SqlState);
        Assert.StartsWith("doka_sm_different:", exception.MessageText, StringComparison.Ordinal);
    }

    /// <summary>Observable Index Facet Drift Is Rejected One Field At A Time.</summary>
    /// <param name="caseId">The independently discovered single-facet drift fixture.</param>
    [Theory]
    [InlineData("uniqueness")]
    [InlineData("filter")]
    [InlineData("included-columns")]
    [InlineData("method")]
    [InlineData("expression")]
    [InlineData("direction")]
    [InlineData("collation")]
    [InlineData("operator-class")]
    [InlineData("null-semantics")]
    public async Task ObservableIndexFacetDrift_IsRejectedOneFieldAtATime(
        string caseId
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE index_facets (value text NULL, alternate text NULL, payload text NULL, other text NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);

        var canonical = new ExpectedIndexDefinition(
            "ix_index_facets",
            "index_facets",
            [
                new ExpectedIndexKeyDefinition(
                    structuredExpression: SqlFunction("lower", "value"),
                    sortOrder: SafeMigrationIndexSortOrder.Descending),
            ],
            unique: true,
            structuredFilter: SafeMigrationSql.IsNotNull(SqlColumn("value")),
            includedColumns: ["payload"],
            method: "btree",
            nullsDistinct: true);

        var collated = new ExpectedIndexDefinition(
            "ix_index_facets_collated",
            "index_facets",
            [
                new ExpectedIndexKeyDefinition(
                    column: "value",
                    collation: new SafeMigrationCollationIdentifier("C"),
                    operatorClass: "text_pattern_ops"),
            ],
            method: "btree");

        var create = new MigrationBuilder(context.Database.ProviderName!);

        create.EnsureIndex(canonical, SafeMigrationPolicy.ThrowIfDifferent);

        create.EnsureIndex(collated, SafeMigrationPolicy.ThrowIfDifferent);

        var variant = caseId switch
        {
            "uniqueness" => new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                canonical.Keys,
                unique: false,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                structuredFilter: canonical.StructuredFilter),
            "filter" => new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                canonical.Keys,
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                nullsDistinct: true,
                structuredFilter:
                SqlBinary(
                    SqlColumn("value"),
                    SafeMigrationSqlBinaryOperator.NotEqual,
                    SafeMigrationSql.Literal(string.Empty, "text"))),
            "included-columns" => new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                canonical.Keys,
                unique: true,
                includedColumns: ["other"],
                method: canonical.Method,
                nullsDistinct: true,
                structuredFilter: canonical.StructuredFilter),
            "method" => new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [new ExpectedIndexKeyDefinition(structuredExpression: SqlFunction("lower", "value"))],
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: "hash",
                nullsDistinct: true,
                structuredFilter: canonical.StructuredFilter),
            "expression" => new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(
                        structuredExpression: SqlFunction("upper", "value"),
                        sortOrder: SafeMigrationIndexSortOrder.Descending),
                ],
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                nullsDistinct: true,
                structuredFilter: canonical.StructuredFilter),
            "direction" => new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [new ExpectedIndexKeyDefinition(structuredExpression: SqlFunction("lower", "value"))],
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                nullsDistinct: true,
                structuredFilter: canonical.StructuredFilter),
            "collation" => new ExpectedIndexDefinition(
                collated.Name,
                collated.Table,
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        collation: new SafeMigrationCollationIdentifier("POSIX"),
                        operatorClass: "text_pattern_ops"),
                ],
                method: "btree"),
            "operator-class" => new ExpectedIndexDefinition(
                collated.Name,
                collated.Table,
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        collation: new SafeMigrationCollationIdentifier("C"),
                        operatorClass: "text_ops"),
                ],
                method: "btree"),
            "null-semantics" => new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                canonical.Keys,
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                nullsDistinct: false,
                structuredFilter: canonical.StructuredFilter),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown facet fixture."),
        };

        var drift = new MigrationBuilder(context.Database.ProviderName!);

        drift.EnsureIndex(variant, SafeMigrationPolicy.ThrowIfDifferent);

        var expectedState = caseId == "null-semantics"
            ? Fixture.ServerVersion.Major < 15
                ? SafeMigrationObservedState.Unsupported
                : SafeMigrationObservedState.Different
            : SafeMigrationObservedState.Different;

        // Act
        await ExecuteOperationsAsync(context, create.Operations, CancellationToken.None);

        await ExecuteOperationsAsync(context, create.Operations, CancellationToken.None);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                drift.Operations,
                new SafeMigrationRunOptions(caseId == "null-semantics"
                    ? "index-null-semantics"
                    : "index-facet-drift"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);

        Assert.Equal(expectedState, assessment.ObservedState);

        if (caseId != "null-semantics")
        {
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        }
    }

    /// <summary>Relation Namespace Collisions Are Rejected Before Index Ddl.</summary>
    /// <param name="operationIndex">The independently executed member of the full preflight stream.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RelationNamespaceCollisions_AreRejectedBeforeIndexDdl(
        int operationIndex
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE index_namespace_owner (code integer NOT NULL);
            CREATE TABLE ix_index_namespace_target (id integer NOT NULL);
            CREATE TABLE rename_index_owner (code integer NOT NULL);
            CREATE INDEX rename_index_source ON rename_index_owner (code);
            CREATE TABLE rename_index_relation_target (id integer NOT NULL);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_index_namespace_target",
            "index_namespace_owner",
            ["code"]);

        builder.RenameIndexIfExists(
            "rename_index_source",
            "rename_index_owner",
            "rename_index_relation_target");

        // Act
        // WHY: Preserve the complete preflight stream; each native rejection owns an isolated test case.
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("index-relation-collisions"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [builder.Operations[operationIndex]], CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(2, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal("P1001", exception.SqlState);
        Assert.StartsWith("doka_sm_different:", exception.MessageText, StringComparison.Ordinal);

        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relkind IN ('r', 'p')
                AND relname IN ('ix_index_namespace_target', 'rename_index_relation_target');
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class "
                + "WHERE relkind IN ('i', 'I') AND relname = 'rename_index_source';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Prefix Length Is Rejected As Unsupported Before Target Ddl.</summary>
    [Fact]
    public async Task PrefixLength_IsRejectedAsUnsupportedBeforeTargetDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE unsupported_prefix (value text NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_unsupported_prefix",
                "unsupported_prefix",
                [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 12)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unsupported-prefix"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relname = 'ix_unsupported_prefix';",
                cancellationToken: CancellationToken.None));
    }
}
