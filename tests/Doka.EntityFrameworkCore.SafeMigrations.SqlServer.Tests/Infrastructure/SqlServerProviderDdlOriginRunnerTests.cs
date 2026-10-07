namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises provider-specific DDL provenance through Core's actual report pipeline without a live engine.</summary>
public sealed class SqlServerProviderDdlOriginRunnerTests
{
    private static readonly int[] s_expectedOrdinals = [0, 1, 2];

    /// <summary>Both ordinary and safe DDL retain complete-stream ordinals and distinct uncertainty diagnostics.</summary>
    /// <param name="typed">Whether the origin is an ordinary provider operation.</param>
    /// <param name="trigger">Whether a visible trigger rather than filtered metadata causes uncertainty.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DdlOrigin_ProducesTraceableRuntimeValidationReport(bool typed, bool trigger)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        await using var context = Context(connection);
        var operations = Operations(typed);
        using var analyzer = new OrderedAnalyzer(context, trigger);
        var runner = new SafeMigrationRunner(analyzer);

        // Act
        var report = await runner.AnalyzeAsync(context, operations, new SafeMigrationRunOptions("ddl-origin-contract"));
        var json = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var block = Record.Exception(report.ThrowIfBlocked);

        // Assert
        Assert.Null(block);
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(3, report.Assessments.Count);
        var assessment = report.Assessments[2];
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, assessment.Action);
        Assert.Equal("runtime_validation_required", assessment.Code);
        Assert.Equal(trigger ? "projected_ddl_trigger_data_unknown" : "projected_ddl_visibility_data_unknown",
            assessment.AnalysisCode);
        Assert.Null(assessment.ObservedState);
        Assert.Null(assessment.PostconditionSatisfied);
        Assert.Equal(1, assessment.DeferredOrigin?.OperationOrdinal);
        Assert.Equal(typed ? typeof(DropIndexOperation).FullName : typeof(SafeMigrationOperation).FullName,
            assessment.DeferredOrigin?.OperationType);
        Assert.Null(assessment.DeferredOrigin?.MigrationId);
        Assert.Contains("\"operationOrdinal\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"validate_at_runtime\"", json, StringComparison.Ordinal);
        Assert.Equal(s_expectedOrdinals, analyzer.ObservedOrdinals);
    }

    /// <summary>An invalid provider origin cannot be reported as a runtime-validation certificate.</summary>
    /// <param name="invalidOrdinal">The negative, current or future position claimed by a faulty provider.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InvalidProviderOrigin_FailsClosed(int invalidOrdinal)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection();
        await using var context = Context(connection);
        using var analyzer = new OrderedAnalyzer(context, trigger: false) { OverrideOrigin = invalidOrdinal };
        var runner = new SafeMigrationRunner(analyzer);

        // Act
        var failure = await Record.ExceptionAsync(() => runner.AnalyzeAsync(context, Operations(typed: false),
            new SafeMigrationRunOptions("ddl-invalid-origin-contract")));

        // Assert
        Assert.Contains("invalid deferred-validation origin", Assert.IsType<InvalidOperationException>(failure).Message,
            StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    private static SafeMigrationDbContext Context(SqlServerCatalogTestConnection connection)
        => new(new DbContextOptionsBuilder<SafeMigrationDbContext>().UseSqlServer(connection).Options);

    private static MigrationOperation[] Operations(bool typed)
        =>
        [
            new SafeMigrationOperation(new EnsureSchemaIntent("dbo"), SafeMigrationPolicy.ThrowIfDifferent),
            typed
                ? new DropIndexOperation { Name = "IX_source", Table = "source" }
                : new SafeMigrationOperation(new DropIndexIntent("IX_source", "source"),
                    SafeMigrationPolicy.ThrowIfDifferent),
            new SafeMigrationOperation(new EnsureCheckConstraintIntent(
                new ExpectedCheckConstraintDefinition("CK_checked", "checked", "[Value]>=0")),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

    /// <summary>Supplies immutable live states while delegating ordered effects to the real SQL Server analyzer.</summary>
    private sealed class OrderedAnalyzer : ISafeMigrationProviderAnalyzer, ISafeMigrationProjectedDependencyAnalyzer,
        IDisposable
    {
        private readonly SqlServerSafeMigrationProviderAnalyzer _projection;
        private readonly SqlServerSafeMigrationCatalogSqlBuilder _catalog;
        private readonly bool _trigger;

        /// <summary>Connects immutable test classifications to the provider's real ordered projection.</summary>
        public OrderedAnalyzer(DbContext context, bool trigger)
        {
            _trigger = trigger;
            _projection = new SqlServerSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
                context.GetService<ISqlGenerationHelper>());
            _catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
                context.GetService<ISqlGenerationHelper>());
        }

        /// <inheritdoc />
        public string ProviderId => "sqlserver";

        /// <summary>Gets complete-stream positions delivered by neutral projection.</summary>
        public List<int> ObservedOrdinals { get; } = [];

        /// <summary>Gets or sets faulty provenance injected only for the negative contract tests.</summary>
        public int? OverrideOrigin { get; init; }

        /// <inheritdoc />
        public void ValidateContext(DbContext context) => _projection.ValidateContext(context);

        /// <inheritdoc />
        public Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
            DbContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new SafeMigrationProviderEnvironment(ProviderId, "sqlserver", "16.0.0"));

        /// <inheritdoc />
        public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
            DbContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable>(new EmptyScope());

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context, IReadOnlyList<SafeMigrationOperation> operations,
            CancellationToken cancellationToken = default)
        {
            _projection.CaptureProjectedDdlRowEffects(_trigger
                ? SqlServerDdlRowEffectRisk.EnabledTrigger : SqlServerDdlRowEffectRisk.VisibilityUnproven);
            foreach (var operation in operations)
            {
                _projection.CaptureProjectedDdlRowDependency(operation, _catalog.Build(operation));
            }

            return Task.FromResult<IReadOnlyList<SafeMigrationProviderAnalysis>>(operations.Select(operation =>
                new SafeMigrationProviderAnalysis(operation.Intent is EnsureCheckConstraintIntent
                        ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching,
                    SafeMigrationRepairCapability.None, operation.Intent is not EnsureCheckConstraintIntent,
                    "captured_live_state")).ToArray());
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
            DbContext context, IReadOnlyList<MigrationOperation> operations,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SafeMigrationUnexpectedObject>>([]);

        /// <inheritdoc />
        public void SetCurrentOperationOrdinal(int operationOrdinal)
        {
            ObservedOrdinals.Add(operationOrdinal);
            _projection.SetCurrentOperationOrdinal(operationOrdinal);
        }

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedOperation(
            SafeMigrationOperation operation, SafeMigrationProviderAnalysis projectedAnalysis,
            ISafeMigrationProjectedColumnSource columns)
        {
            var analysis = _projection.ValidateProjectedOperation(operation, projectedAnalysis, columns);

            return analysis.ProviderDeferredOriginOrdinal is not null && OverrideOrigin is { } invalid
                ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                    SafeMigrationRepairCapability.None, false, analysis.Code) { ProviderDeferredOriginOrdinal = invalid }
                : analysis;
        }

        /// <inheritdoc />
        public void ObserveAcceptedOperation(SafeMigrationOperation operation, SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis analysis, SafeMigrationDecision decision)
            => _projection.ObserveAcceptedOperation(operation, liveAnalysis, analysis, decision);

        /// <inheritdoc />
        public void ObserveProviderOperation(MigrationOperation operation) => _projection.ObserveProviderOperation(operation);

        /// <inheritdoc />
        public void Dispose() => _projection.Dispose();
    }

    private sealed class EmptyScope : IAsyncDisposable
    {
        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
