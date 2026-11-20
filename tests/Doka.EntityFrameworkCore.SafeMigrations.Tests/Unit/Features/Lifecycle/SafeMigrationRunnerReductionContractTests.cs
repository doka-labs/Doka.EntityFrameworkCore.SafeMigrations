namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Protects compact/full ordinal mapping and provider-origin boundaries in the actual runner.</summary>
public sealed class SafeMigrationRunnerReductionContractTests
{
    /// <summary>Only preceding complete-stream positions are valid provider provenance certificates.</summary>
    /// <param name="originOrdinal">The invalid negative, current or future complete-stream position.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InvalidProviderOriginIsRejectedBeforeInventory(
        int originOrdinal
    )
    {
        // Arrange
        var analyzer = new OrderedAnalyzer(Analysis(originOrdinal));
        var operations = Operations();

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CaptureAsync(
            analyzer,
            operations,
            CancellationToken.None));

        // Assert
        Assert.Equal("The provider returned an invalid deferred-validation origin.", exception.Message);
        Assert.Equal(["classify"], analyzer.Events);
        Assert.Same(operations[2], Assert.Single(analyzer.CapturedSafeOperations!));
    }

    /// <summary>
    /// Provider-certified provenance overrides local provenance without confusing compact safe ordinals.
    /// </summary>
    /// <param name="originOrdinal">The certified preceding complete-stream position.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ProviderOriginUsesFullStreamPositionAndOverridesLocalOrigin(
        int originOrdinal
    )
    {
        // Arrange
        var analyzer = new OrderedAnalyzer(Analysis(originOrdinal));
        var operations = Operations();

        // Act
        var report = await CaptureAsync(analyzer, operations, CancellationToken.None);

        // Assert
        Assert.Equal(3, report.Assessments.Count);
        Assert.Equal([0, 1, 2], report.Assessments.Select(static assessment => assessment.Ordinal));
        Assert.False(report.Assessments[0].IsSafeOperation);
        Assert.False(report.Assessments[1].IsSafeOperation);
        Assert.True(report.Assessments[2].IsSafeOperation);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal("owner", report.Assessments[2].Identity!.Table);
        Assert.Equal("migration-2", report.Assessments[2].MigrationId);
        Assert.Equal(originOrdinal, report.Assessments[2].DeferredOrigin!.OperationOrdinal);
        Assert.Equal($"migration-{originOrdinal}", report.Assessments[2].DeferredOrigin!.MigrationId);
        Assert.Equal(typeof(EnsureSchemaOperation).FullName, report.Assessments[2].DeferredOrigin!.OperationType);
        Assert.Equal(["classify", "inventory"], analyzer.Events);
        Assert.Same(operations[2], Assert.Single(analyzer.CapturedSafeOperations!));
        Assert.Same(operations, analyzer.InventoryOperations);
    }

    /// <summary>Cancellation arriving after provider classification prevents reduction and final inventory.</summary>
    [Fact]
    public async Task CancellationAfterClassificationDoesNotPublishPartialEvidence()
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource();
        var analyzer = new OrderedAnalyzer(Analysis(0), cancellationSource);
        var operations = Operations();

        // Act
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => CaptureAsync(
            analyzer,
            operations,
            cancellationSource.Token));

        // Assert
        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.Equal(["classify"], analyzer.Events);
        Assert.Null(analyzer.InventoryOperations);
    }

    /// <summary>Public empty-stream analysis still honors cancellation before accessing provider services.</summary>
    /// <param name="postflight">Whether the public verification or preflight entry is exercised.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicEmptyStreamRejectsPreCancellationBeforeProviderAccess(
        bool postflight
    )
    {
        // Arrange
        await using var context = new DbContext(new DbContextOptionsBuilder().Options);
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        var analyzer = new OrderedAnalyzer(Analysis(0));
        var runner = new SafeMigrationRunner(analyzer);
        var options = new SafeMigrationRunOptions("runner-test");

        // Act
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => postflight
            ? runner.VerifyAsync(context, [], options, cancellationSource.Token)
            : runner.AnalyzeAsync(context, [], options, cancellationSource.Token));

        // Assert
        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.Empty(analyzer.Events);
        Assert.Null(analyzer.CapturedSafeOperations);
        Assert.Null(analyzer.InventoryOperations);
    }

    /// <summary>Creates a sequence-aware result with deliberately competing local and provider origins.</summary>
    private static SafeMigrationProviderAnalysis Analysis(
        int providerOriginOrdinal
    ) => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        false,
        "captured_provider_uncertainty")
    {
        IsModelManagedProjectionUnknown = true,
        ModelManagedLocalOriginOrdinal = 0,
        ProviderDeferredOriginOrdinal = providerOriginOrdinal,
    };

    /// <summary>Separates full-stream report positions from the single compact safe-operation position.</summary>
    private static MigrationOperation[] Operations() =>
    [
        new EnsureSchemaOperation { Name = "first" },
        new EnsureSchemaOperation { Name = "provider" },
        new SafeMigrationOperation(
            new EnsureModelManagedDataIntent(
                "owner",
                ["Id"],
                ["int"],
                ["Id"],
                ["int"],
                new object?[,] { { 1 } },
                schema: null,
                uniqueKeys: null),
            SafeMigrationPolicy.ThrowIfDifferent),
    ];

    /// <summary>
    /// Invokes the unchanged production seam without requiring live history or a database connection.
    /// </summary>
    /// <param name="analyzer">The deterministic provider owning the classifications and inventory observations.</param>
    /// <param name="operations">The original stream, including provider-owned operations.</param>
    /// <param name="cancellationToken">The token passed to classification, ordered reduction and inventory.</param>
    /// <returns>The production report task, including its original exception or cancellation.</returns>
    private static async Task<SafeMigrationRunReport> CaptureAsync(
        OrderedAnalyzer analyzer,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        await using var context = new DbContext(new DbContextOptionsBuilder().Options);
        var runner = new SafeMigrationRunner(analyzer);
        var method = typeof(SafeMigrationRunner).GetMethod(
            "AnalyzeOperationsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        List<string> migrationIds = ["migration-0", "migration-1", "migration-2"];

        var task = (Task<SafeMigrationRunReport>)method.Invoke(
            runner,
            [
                context, operations, SafeMigrationReportMode.Preflight, new SafeMigrationRunOptions("runner-test"),
                $"safe-relational-model:v1:doka_mysql:sha256:{new string('a', 64)}", new string('b', 64),
                DateTimeOffset.UnixEpoch, new SafeMigrationProviderEnvironment("doka_mysql", "mariadb", "12.3"),
                null, migrationIds, cancellationToken,
            ])!;

        return await task;
    }

    /// <summary>
    /// Supplies immutable sequence-aware evidence and records only observable phase boundaries.
    /// </summary>
    private sealed class OrderedAnalyzer : ISafeMigrationProviderAnalyzer, ISafeMigrationProviderOperationProjection
    {
        private readonly SafeMigrationProviderAnalysis _analysis;
        private readonly CancellationTokenSource? _cancelAfterClassification;

        /// <summary>
        /// Retains the exact provider result and an optional classification-time cancellation signal.
        /// </summary>
        /// <param name="analysis">The immutable analysis returned for the single safe operation.</param>
        /// <param name="cancelAfterClassification">The test-owned signal, or null to complete normally.</param>
        internal OrderedAnalyzer(
            SafeMigrationProviderAnalysis analysis,
            CancellationTokenSource? cancelAfterClassification = null
        )
        {
            _analysis = analysis;
            _cancelAfterClassification = cancelAfterClassification;
        }

        /// <summary>Gets the observed provider I/O phases in call order.</summary>
        internal List<string> Events { get; } = [];

        /// <summary>Gets the exact compact safe subset supplied by the runner.</summary>
        internal IReadOnlyList<SafeMigrationOperation>? CapturedSafeOperations { get; private set; }

        /// <summary>Gets the complete original stream supplied to final inventory, when reached.</summary>
        internal IReadOnlyList<MigrationOperation>? InventoryOperations { get; private set; }

        /// <inheritdoc />
        public string ProviderId => "doka_mysql";

        /// <inheritdoc />
        public void ValidateContext(
            DbContext context
        )
        {
            ArgumentNullException.ThrowIfNull(context);
            Events.Add("validate");
        }

        /// <inheritdoc />
        public Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
            DbContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("The isolated runner seam receives the captured environment.");

        /// <inheritdoc />
        public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
            DbContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("The isolated runner seam does not acquire database resources.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context,
            IReadOnlyList<SafeMigrationOperation> operations,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedSafeOperations = operations;
            Events.Add("classify");
            _cancelAfterClassification?.Cancel();

            return Task.FromResult<IReadOnlyList<SafeMigrationProviderAnalysis>>([_analysis]);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
            DbContext context,
            IReadOnlyList<MigrationOperation> operations,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            InventoryOperations = operations;
            Events.Add("inventory");

            return Task.FromResult<IReadOnlyList<SafeMigrationUnexpectedObject>>([]);
        }

        /// <inheritdoc />
        public bool PreservesExistingTableState(
            MigrationOperation operation
        ) => operation is EnsureSchemaOperation;

        /// <inheritdoc />
        public bool IsSequenceAwareAnalysis(
            SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis analysis
        ) => ReferenceEquals(analysis, _analysis);
    }
}
