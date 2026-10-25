namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies complete table postconditions after known computed-source renames.</summary>
public sealed partial class SafeMigrationPostflightComputedRenameTests
{
    /// <summary>A final metadata batch verifies the transformed table, not an unconditional superseded flag.</summary>
    [Fact]
    public async Task KnownRenameRevalidatesCompleteTransformedTableContract()
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final);
        var operations = RenameOperations(initial, final);
        operations[0].AddAnnotation("Test:CapturedMetadata", "preserved");

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.All(report.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal([3, 1], analyzer.Batches.Select(static batch => batch.Length));
        var rewritten = Assert.IsType<EnsureTableIntent>(Assert.Single(analyzer.Batches[1]).Intent).Definition;
        Assert.True(SafeMigrationDefinitionEquivalence.Table(final, rewritten));
        Assert.Equal("preserved", analyzer.Batches[1][0]["Test:CapturedMetadata"]);
        Assert.Equal("classified_matching", report.Assessments[0].AnalysisCode);
        Assert.NotEqual("postcondition_superseded", report.Assessments[0].Code);
        Assert.Same(initial,
            Assert.IsType<EnsureTableIntent>(((SafeMigrationOperation)operations[0]).Intent).Definition);
    }

    /// <summary>Unrelated facets remain observable after an intentional referenced-member rename.</summary>
    /// <param name="drift">The unrelated final-catalog discrepancy.</param>
    [Theory]
    [InlineData("table_comment")]
    [InlineData("column_comment")]
    [InlineData("default")]
    [InlineData("unique_key")]
    public async Task ComputedRenameDoesNotHideUnrelatedFinalTableDrift(string drift)
    {
        // Arrange
        var initial = Table("source");
        var expected = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(Table("renamed", drift));
        var operations = RenameOperations(initial, expected);

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
        Assert.Equal([3, 1], analyzer.Batches.Select(static batch => batch.Length));
        Assert.Equal("postcondition_failed", report.Assessments[0].Code);
    }

    /// <summary>The ordinary no-rename path performs exactly the original single classification batch.</summary>
    [Fact]
    public async Task NoRenameUsesOneClassificationBatch()
    {
        // Arrange
        var final = Table("source");
        var analyzer = new FinalCatalogAnalyzer(final);
        MigrationOperation[] operations = [Safe(new EnsureTableIntent(final, SafeMigrationTableMode.StrictDefinition))];

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.True(Assert.Single(report.Assessments).PostconditionSatisfied);
        Assert.Single(analyzer.Batches);
        Assert.Same(operations[0], Assert.Single(analyzer.Batches[0]));
    }

    /// <summary>A same-named member on a different owner cannot rewrite this complete table contract.</summary>
    [Fact]
    public async Task UnrelatedOwnerRenameDoesNotReclassifyTable()
    {
        // Arrange
        var final = Table("source");
        var analyzer = new FinalCatalogAnalyzer(final);
        MigrationOperation[] operations =
        [
            Safe(new EnsureTableIntent(final, SafeMigrationTableMode.StrictDefinition)),
            Safe(new RenameColumnIntent("source", "other_nodes", "renamed")),
        ];

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.All(report.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Single(analyzer.Batches);
    }

    /// <summary>Opaque work and source removal cannot become certificates for a later renamed table shape.</summary>
    /// <param name="boundary">The operation that invalidates known source lineage.</param>
    [Theory]
    [InlineData("sql")]
    [InlineData("provider")]
    [InlineData("drop_source")]
    public async Task UnprovenBoundaryDoesNotRewriteEarlierTable(string boundary)
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final);
        var operations = RenameOperations(initial, final).ToList();
        operations.Insert(1, boundary switch
        {
            "sql" => new SqlOperation { Sql = "SELECT 1;", },
            "provider" => new AlterTableOperation { Name = "source_nodes", },
            _ => Safe(new DropColumnIntent("source", "source_nodes")),
        });

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
        Assert.Single(analyzer.Batches);
    }

    /// <summary>A failed rename cannot authorize transformed metadata or hide its own failed postcondition.</summary>
    [Fact]
    public async Task FailedRenameDoesNotRewriteEarlierTable()
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(initial) { RenameSatisfied = false, };
        var operations = RenameOperations(initial, final);

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[1].PostconditionSatisfied);
        Assert.Single(analyzer.Batches);
    }

    /// <summary>Unproven historical owner identity cannot be repaired by querying a different final owner.</summary>
    [Fact]
    public async Task UnknownHistoricalIdentityDoesNotRewriteEarlierTable()
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final) { HistoricalIdentityProven = false, };
        var operations = RenameOperations(initial, final);

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Single(analyzer.Batches);
        Assert.Equal("postcondition_identity_unproven", report.Assessments[0].Code);
    }

    /// <summary>Unknown computed syntax cannot acquire a source rewrite merely from its table declaration.</summary>
    [Fact]
    public async Task OpaqueComputedSourceDoesNotAcquirePostflightProjection()
    {
        // Arrange
        var initial = Table("source", opaqueComputed: true);
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final);
        var operations = RenameOperations(initial, final);

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
        Assert.Single(analyzer.Batches);
    }

    /// <summary>Full-stream positions and safe-subset positions retain distinct target-model alignment.</summary>
    [Fact]
    public async Task RenamedTableProbeRetainsItsOriginalTargetModelAndReportOrdinal()
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final);
        MigrationOperation[] operations =
            [new SqlOperation { Sql = "SELECT 1;", }, .. RenameOperations(initial, final)];

        var tableModel = new ModelBuilder().FinalizeModel();
        List<IModel?> models = [null, tableModel, new ModelBuilder().FinalizeModel(), null];

        // Act
        var report = await VerifyAsync(operations, analyzer, models);

        // Assert
        Assert.True(report.Assessments[1].PostconditionSatisfied);
        Assert.Equal(1, report.Assessments[1].Ordinal);
        Assert.Equal(4, analyzer.ModelBatches[0]!.Length);
        Assert.Same(tableModel, Assert.Single(analyzer.ModelBatches[1]!));
        Assert.Equal([3, 1], analyzer.Batches.Select(static batch => batch.Length));
    }

    /// <summary>A malformed second result count cannot be paired with unrelated original operation evidence.</summary>
    [Fact]
    public async Task RenamedTableReclassificationRejectsMisalignedResultCount()
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final) { MalformedSecondResult = true, };
        var operations = RenameOperations(initial, final);

        // Act
        var exception = await Record.ExceptionAsync(() => VerifyAsync(operations, analyzer));

        // Assert
        Assert.Contains("table analyzer returned an inconsistent result count",
            Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);
        Assert.Equal(2, analyzer.Batches.Count);
    }

    /// <summary>Subset reclassification cannot replace the full stream's immutable qualified-owner evidence.</summary>
    [Fact]
    public async Task RenamedTableProbeRetainsFullStreamPhysicalIdentitySnapshot()
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final) { ResetIdentityAfterSubset = true, };
        MigrationOperation[] operations =
        [
            .. RenameOperations(initial, final),
            Safe(new EnsureColumnIntent("other_nodes", new ExpectedColumnDefinition("extra", typeof(int), true))),
            Safe(new DropColumnIntent("extra", "other_nodes", "custom")),
        ];

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.All(report.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal("postcondition_superseded", report.Assessments[3].Code);
        Assert.Null(analyzer.CurrentSchema);
        Assert.Equal([5, 1], analyzer.Batches.Select(static batch => batch.Length));
    }

    /// <summary>Unknown later ownership invalidates a pending rewritten table probe.</summary>
    [Fact]
    public async Task IdentityUncertaintyAfterRenameInvalidatesPendingTableProbe()
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final) { FirstUnprovenOrdinal = 2, };
        var operations = RenameOperations(initial, final);

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Single(analyzer.Batches);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
    }

    /// <summary>A distinct later owner mutation cannot leave a substituted table contract in authority.</summary>
    [Fact]
    public async Task ChangedOwnerMutationAfterRenameInvalidatesPendingTableProbe()
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed", "column_comment");
        var analyzer = new FinalCatalogAnalyzer(final);
        MigrationOperation[] operations =
        [
            Safe(new EnsureTableIntent(initial, SafeMigrationTableMode.StrictDefinition)),
            Safe(new RenameColumnIntent("source", "source_nodes", "renamed")),
            Safe(new AlterColumnIntent("source_nodes", final.Columns[1], initial.Columns[1])),
        ];

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Single(analyzer.Batches);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
    }

    /// <summary>Independent index work and exact column reassertions retain complete member-rename lineage.</summary>
    /// <param name="intermediate">The shape-neutral operation between two member renames.</param>
    /// <param name="drift">Whether the final table has an unrelated default discrepancy.</param>
    [Theory]
    [InlineData("ensure_index", false)]
    [InlineData("drop_index", false)]
    [InlineData("rename_index", false)]
    [InlineData("ensure_column", false)]
    [InlineData("ensure_index", true)]
    [InlineData("ensure_column", true)]
    public async Task ShapeNeutralOperationsPreserveSuccessiveRenameLineage(string intermediate, bool drift)
    {
        // Arrange
        var initial = Table("source");
        var middle = Table("renamed");
        var expected = Table("final");
        var analyzer = new FinalCatalogAnalyzer(Table("final", drift ? "default" : null));
        SafeMigrationIntent neutral = intermediate switch
        {
            "ensure_index" => new EnsureIndexIntent(new ExpectedIndexDefinition(
                "ix_source", "source_nodes", [new ExpectedIndexKeyDefinition(column: "renamed")])),
            "drop_index" => new DropIndexIntent("ix_source", "source_nodes"),
            "rename_index" => new RenameIndexIntent("ix_source", "source_nodes", "ix_renamed"),
            _ => new EnsureColumnIntent("source_nodes", middle.Columns[2]),
        };

        MigrationOperation[] operations =
        [
            Safe(new EnsureTableIntent(initial, SafeMigrationTableMode.StrictDefinition)),
            Safe(new RenameColumnIntent("source", "source_nodes", "renamed")),
            Safe(neutral),
            Safe(new RenameColumnIntent("renamed", "source_nodes", "final")),
            Safe(new EnsureColumnIntent("source_nodes", expected.Columns[2])),
        ];

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(!drift, report.Assessments[0].PostconditionSatisfied);
        Assert.Equal(drift ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal([5, 1], analyzer.Batches.Select(static batch => batch.Length));
        var probe = Assert.IsType<EnsureTableIntent>(Assert.Single(analyzer.Batches[1]).Intent).Definition;
        Assert.True(SafeMigrationDefinitionEquivalence.Table(expected, probe));
    }

    /// <summary>Opaque work after a proven rename discards its pending transformed complete-table probe.</summary>
    /// <param name="providerOwned">Whether the boundary is a standard provider operation rather than SQL.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpaqueBoundaryAfterRenameInvalidatesPendingTableProbe(bool providerOwned)
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final);
        MigrationOperation[] operations =
        [
            .. RenameOperations(initial, final),
            providerOwned
                ? new AlterTableOperation { Name = "source_nodes", }
                : new SqlOperation { Sql = "SELECT 1;", },
        ];

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
        Assert.Single(analyzer.Batches);
    }

    /// <summary>Unknown nested expression nodes cannot certify a complete transformed native table contract.</summary>
    [Fact]
    public async Task NestedOpaqueComputedSourceDoesNotAcquirePostflightProjection()
    {
        // Arrange
        var initial = Table("source", nestedOpaqueComputed: true);
        var final = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(final);
        var operations = RenameOperations(initial, final);

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
        Assert.Single(analyzer.Batches);
    }

    /// <summary>An exact renamed CHECK reassertion retains complete-table comparison and unrelated facets.</summary>
    /// <param name="unrelatedDrift">Whether an unrelated final default differs from the reviewed table.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactRenamedCheckReassertionRetainsFullTablePostcondition(bool unrelatedDrift)
    {
        // Arrange
        var initial = Table("source");
        var expected = Table("renamed");
        var analyzer = new FinalCatalogAnalyzer(Table("renamed", unrelatedDrift ? "default" : null));
        MigrationOperation[] operations =
        [
            .. RenameOperations(initial, expected),
            Safe(new EnsureCheckConstraintIntent(expected.CheckConstraints[0])),
        ];

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(!unrelatedDrift, report.Assessments[0].PostconditionSatisfied);
        Assert.Equal(unrelatedDrift ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal([4, 1], analyzer.Batches.Select(static batch => batch.Length));
        var probe = Assert.IsType<EnsureTableIntent>(Assert.Single(analyzer.Batches[1]).Intent).Definition;
        Assert.True(SafeMigrationDefinitionEquivalence.Table(expected, probe));
    }

    private static MigrationOperation[] RenameOperations(
        ExpectedTableDefinition initial,
        ExpectedTableDefinition final
    ) =>
    [
        Safe(new EnsureTableIntent(initial, SafeMigrationTableMode.StrictDefinition)),
        Safe(new RenameColumnIntent("source", "source_nodes", "renamed")),
        Safe(new EnsureColumnIntent("source_nodes", final.Columns[2])),
    ];

    private static ExpectedTableDefinition Table(
        string sourceName,
        string? drift = null,
        bool opaqueComputed = false,
        bool nestedOpaqueComputed = false
    )
    {
        var sql = $"CASE WHEN \"{sourceName}\" > 0 THEN \"{sourceName}\" ELSE 0 END";
        var computed = nestedOpaqueComputed
            ? new ExpectedColumnDefinition("derived", typeof(int), false, "int", computedExpression:
                SafeMigrationSql.Binary(SafeMigrationSql.Opaque("unknown()"), SafeMigrationSqlBinaryOperator.Add,
                    SafeMigrationSql.Literal(1)))
            : opaqueComputed
            ? new ExpectedColumnDefinition("derived", typeof(int), false, "int", computedColumnSql: sql)
            : SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
            {
                Name = "derived", Table = "source_nodes", ClrType = typeof(int), ColumnType = "int",
                IsNullable = false, ComputedColumnSql = sql,
            });

        return new ExpectedTableDefinition("source_nodes",
            [
                new ExpectedColumnDefinition(sourceName, typeof(int), false, "int"),
                new ExpectedColumnDefinition("payload", typeof(int), false, "int",
                    comment: drift == "column_comment" ? "drift" : "retained",
                    defaultValue: SafeMigrationDefaultValue.Literal(drift == "default" ? 2 : 1)),
                computed,
            ],
            comment: drift == "table_comment" ? "drift" : "retained table",
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_nodes", "source_nodes", [sourceName]),
            uniqueConstraints: drift == "unique_key" ? [] :
                [new ExpectedUniqueConstraintDefinition("uq_payload", "source_nodes", ["payload"])],
            checkConstraints:
                [SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_source", "source_nodes",
                    $"\"{sourceName}\" >= 0", null)]);
    }

    private static SafeMigrationOperation Safe(SafeMigrationIntent intent) =>
        new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    private static async Task<SafeMigrationRunReport> VerifyAsync(
        IReadOnlyList<MigrationOperation> operations,
        FinalCatalogAnalyzer analyzer,
        List<IModel?>? targetModels = null
    )
    {
        using var context = new DbContext(new DbContextOptionsBuilder().Options);
        var runner = new SafeMigrationRunner(analyzer);
        var method = typeof(SafeMigrationRunner).GetMethod("AnalyzeOperationsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // WHY: Exercise the production report/projection loop with deterministic
        // complete catalog definitions, not a fabricated report or a live DB.
        var task = (Task<SafeMigrationRunReport>)method.Invoke(runner,
        [
            context, operations, SafeMigrationReportMode.Postflight, new SafeMigrationRunOptions("isolated"),
            $"safe-relational-model:v1:sqlite:sha256:{new string('a', 64)}", new string('b', 64),
            DateTimeOffset.UnixEpoch, new SafeMigrationProviderEnvironment("sqlite", "sqlite", "test"),
            targetModels, null, CancellationToken.None,
        ])!;

        return await task;
    }

    private sealed class FinalCatalogAnalyzer(ExpectedTableDefinition finalTable)
        : ISafeMigrationProviderAnalyzer, ISafeMigrationProviderObjectIdentityNormalizer,
            ISafeMigrationProviderTargetModelAnalyzer
    {
        public List<SafeMigrationOperation[]> Batches { get; } = [];
        public List<IModel?[]?> ModelBatches { get; } = [];
        public IReadOnlyList<ModelManagedDataIntent>? FinalManagedContracts { get; init; }
        public bool RenameSatisfied { get; init; } = true;
        public bool HistoricalIdentityProven { get; init; } = true;
        public bool MalformedSecondResult { get; init; }
        public bool ResetIdentityAfterSubset { get; init; }
        public int? FirstUnprovenOrdinal { get; init; }
        public string? CurrentSchema { get; private set; } = "custom";
        public string ProviderId => "sqlite";
        public StringComparer IdentifierComparer => StringComparer.Ordinal;
        public char? CapturedSqlIdentifierDelimiter => '"';
        public string NormalizeIdentifier(string identifier) => identifier;
        public string? NormalizeSchema(string? schema) => ResetIdentityAfterSubset ? schema ?? CurrentSchema : schema;
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
        public bool IsPostflightIdentityProven(SafeMigrationIntent intent, int ordinal) =>
            HistoricalIdentityProven && (FirstUnprovenOrdinal is null || ordinal < FirstUnprovenOrdinal);

        public ISafeMigrationProviderObjectIdentityNormalizer GetPostflightIdentityNormalizer(
            IReadOnlyList<MigrationOperation> operations
        ) => new IdentitySnapshot(ResetIdentityAfterSubset ? CurrentSchema : null,
            HistoricalIdentityProven, FirstUnprovenOrdinal);
        public void ValidateContext(DbContext context) => ArgumentNullException.ThrowIfNull(context);

        public Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
            DbContext context, CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("The ordered seam receives its environment.");

        public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
            DbContext context, CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("The isolated ordered seam performs no I/O.");

        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context,
            IReadOnlyList<SafeMigrationOperation> operations,
            CancellationToken cancellationToken = default
        )
        {
            Batches.Add(operations.ToArray());
            if (ResetIdentityAfterSubset && Batches.Count > 1)
            {
                CurrentSchema = null;
            }

            if (MalformedSecondResult && Batches.Count > 1)
            {
                return Task.FromResult<IReadOnlyList<SafeMigrationProviderAnalysis>>([]);
            }

            return Task.FromResult<IReadOnlyList<SafeMigrationProviderAnalysis>>(operations.Select(operation =>
            {
                if (operation.Intent is ModelManagedDataIntent managed && FinalManagedContracts is { } contracts)
                {
                    var missing = managed.KeyColumns.Concat(managed.Columns).Any(member =>
                        !finalTable.Columns.Any(column => StringComparer.Ordinal.Equals(column.Name, member)));

                    var rowMatches = !missing && contracts.Any(expected => StringComparer.Ordinal.Equals(
                        SafeMigrationContractFingerprint.Create([Safe(managed)]),
                        SafeMigrationContractFingerprint.Create([Safe(expected)])));

                    var state = rowMatches ? SafeMigrationObservedState.Matching
                        : missing ? SafeMigrationObservedState.PrerequisiteMissing
                        : SafeMigrationObservedState.Different;

                    return new SafeMigrationProviderAnalysis(state, SafeMigrationRepairCapability.None, rowMatches,
                        rowMatches ? "classified_matching" : missing
                            ? "classified_prerequisite_missing" : "classified_different");
                }

                var matching = operation.Intent switch
                {
                    EnsureTableIntent table => SafeMigrationDefinitionEquivalence.Table(table.Definition, finalTable),
                    RenameColumnIntent => RenameSatisfied,
                    EnsureColumnIntent column => finalTable.Columns.Any(actual =>
                        SafeMigrationDefinitionEquivalence.Column(actual, column.Definition)),
                    EnsureCheckConstraintIntent check => finalTable.CheckConstraints.Any(actual =>
                        SafeMigrationDefinitionEquivalence.CheckConstraintSemantics(actual, check.Definition)),
                    _ => true,
                };

                return new SafeMigrationProviderAnalysis(matching
                        ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
                    SafeMigrationRepairCapability.None, matching,
                    matching ? "classified_matching" : "classified_different");
            }).ToArray());
        }

        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context,
            IReadOnlyList<MigrationOperation> operations,
            IReadOnlyList<IModel?>? targetModels,
            CancellationToken cancellationToken = default
        )
        {
            ModelBatches.Add(targetModels?.ToArray());

            return AnalyzeAsync(context, operations.OfType<SafeMigrationOperation>().ToArray(), cancellationToken);
        }

        public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
            DbContext context,
            IReadOnlyList<MigrationOperation> operations,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<SafeMigrationUnexpectedObject>>([]);
    }

    private sealed class IdentitySnapshot(string? schema, bool historicalProven, int? firstUnproven)
        : ISafeMigrationProviderObjectIdentityNormalizer
    {
        public StringComparer IdentifierComparer => StringComparer.Ordinal;
        public char? CapturedSqlIdentifierDelimiter => '"';
        public string NormalizeIdentifier(string identifier) => identifier;
        public string? NormalizeSchema(string? value) => value ?? schema;
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
        public bool IsPostflightIdentityProven(SafeMigrationIntent intent, int ordinal) =>
            historicalProven && (firstUnproven is null || ordinal < firstUnproven);
    }
}
