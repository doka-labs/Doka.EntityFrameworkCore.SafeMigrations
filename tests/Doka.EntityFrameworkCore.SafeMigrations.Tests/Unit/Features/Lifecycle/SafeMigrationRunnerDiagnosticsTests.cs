namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies that report creation captures only known, immutable authored identities.</summary>
public sealed class SafeMigrationRunnerDiagnosticsTests
{
    /// <summary>Ordinary EF operations keep their owner without being relabeled as analyzed safe operations.</summary>
    [Fact]
    public void OrdinaryColumnIdentityDoesNotRetainDefaultPayload()
    {
        // Arrange
        var operation = new AddColumnOperation
        {
            Name = "Id", Table = "items", Schema = "authored", ClrType = typeof(string),
            DefaultValue = "private-default-value",
        };

        // Act
        var identity = SafeMigrationRunner.CaptureProviderIdentity(operation);
        var text = SafeMigrationDiagnosticMessages.FormatIdentity(identity!);

        // Assert
        Assert.NotNull(identity);
        Assert.Equal("authored", identity.Schema);
        Assert.Equal("items", identity.Table);
        Assert.Equal("Id", identity.ObjectName);
        Assert.DoesNotContain("private-default-value", text, StringComparison.Ordinal);
    }

    /// <summary>Foreign-key endpoint snapshots are unaffected by later caller array mutation.</summary>
    [Fact]
    public void OrdinaryForeignKeyIdentitySnapshotsBothEndpoints()
    {
        // Arrange
        var operation = new AddForeignKeyOperation
        {
            Name = "fk_child_parent", Table = "children", Schema = "dependent",
            Columns = ["Tenant", "OwnerId"], PrincipalSchema = "principal", PrincipalTable = "parents",
            PrincipalColumns = ["Tenant", "Id"],
        };

        // Act
        var identity = SafeMigrationRunner.CaptureProviderIdentity(operation);
        operation.Columns[1] = "mutated";
        operation.PrincipalColumns![1] = "mutated";

        // Assert
        Assert.NotNull(identity);
        Assert.Equal(["Tenant", "OwnerId"], identity.Columns);
        Assert.Equal(["Tenant", "Id"], identity.PrincipalColumns);
        Assert.Equal("dependent", identity.Schema);
        Assert.Equal("principal", identity.PrincipalSchema);
        Assert.Equal("parents", identity.PrincipalTable);
    }

    /// <summary>Unclassified ordinary table changes still expose a known authored table.</summary>
    [Fact]
    public void UnsupportedOrdinaryTableOperationKeepsOwnerWithoutInventingSafeKind()
    {
        // Arrange
        var operation = new AlterTableOperation { Name = "items", Schema = "authored", Comment = "private-comment", };

        // Act
        var identity = SafeMigrationRunner.CaptureProviderIdentity(operation);
        var text = SafeMigrationDiagnosticMessages.FormatIdentity(identity!);

        // Assert
        Assert.NotNull(identity);
        Assert.Null(identity.OperationKind);
        Assert.Equal("items", identity.Table);
        Assert.DoesNotContain("private-comment", text, StringComparison.Ordinal);
    }

    /// <summary>Opaque SQL has no inferable owner, even if its text resembles an ALTER statement.</summary>
    [Fact]
    public void OpaqueSqlIdentityRemainsUnknown()
    {
        // Arrange
        var operation = new SqlOperation { Sql = "UPDATE private_owner SET Secret = 'private-row-value';", };

        // Act
        var identity = SafeMigrationRunner.CaptureProviderIdentity(operation);

        // Assert
        Assert.Null(identity);
    }

    /// <summary>The owning migration is assigned per operation, never copied from the whole report target.</summary>
    /// <param name="postflight">Whether the ordered capture occurs during final verification.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedAssessmentsRetainActualMigrationIds(bool postflight)
    {
        // Arrange
        MigrationOperation[] operations =
        [
            new SafeMigrationOperation(new EnsureColumnIntent("first_owner", Column("Id")),
                SafeMigrationPolicy.ThrowIfDifferent),
            new AlterTableOperation { Name = "ordinary_owner", Schema = "authored", },
            new SafeMigrationOperation(new EnsureColumnIntent("second_owner", Column("Id")),
                SafeMigrationPolicy.RepairIfSafe),
        ];

        List<string> migrationIds = ["202610140001_First", "202610140002_Ordinary", "202610140003_Second"];

        // Act
        var report = await CaptureAsync(operations, migrationIds,
            postflight ? SafeMigrationReportMode.Postflight : SafeMigrationReportMode.Preflight);
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));

        // Assert
        Assert.Equal(migrationIds, report.Assessments.Select(static assessment => assessment.MigrationId));
        Assert.Equal("202610140099_Target", report.TargetMigrationId);
        Assert.Equal("first_owner", report.Assessments[0].Identity!.Table);
        Assert.Equal("ordinary_owner", report.Assessments[1].Identity!.Table);
        Assert.Equal("second_owner", report.Assessments[2].Identity!.Table);
        Assert.False(report.Assessments[1].IsSafeOperation);
        Assert.Null(report.Assessments[1].ObjectName);
        Assert.Null(report.Assessments[1].ObservedState);
        Assert.Null(report.Assessments[1].Policy);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("assessments")[1]
            .GetProperty("objectName").ValueKind);
        Assert.Equal("ordinary_owner", document.RootElement.GetProperty("assessments")[1]
            .GetProperty("identityObjectName").GetString());
        Assert.Equal("provider_owned_not_analyzed", report.Assessments[1].Explanation.ReasonCode);
        Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, report.Assessments[0].Policy);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, report.Assessments[2].Policy);
    }

    /// <summary>Deferred entries retain both the actual target owner and the separate invalidating operation.</summary>
    [Fact]
    public async Task OpaqueOriginAndLaterOwnerAreCapturedIndependently()
    {
        // Arrange
        MigrationOperation[] operations =
        [
            new SqlOperation { Sql = "SELECT 'private-payload';", },
            new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition("ix_items", "items",
                [new ExpectedIndexKeyDefinition(column: "Id")])), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        List<string> migrationIds = ["202610140001_Preparation", "202610140002_Index"];

        // Act
        var report = await CaptureAsync(operations, migrationIds, SafeMigrationReportMode.Preflight);
        var text = SafeMigrationReportText.Format(report, SafeMigrationReportSelection.NonMatching);

        // Assert
        var deferred = report.Assessments[1];
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, deferred.Action);
        Assert.Null(deferred.ObservedState);
        Assert.Null(deferred.PostconditionSatisfied);
        Assert.Equal("items", deferred.Identity!.Table);
        Assert.Equal("202610140002_Index", deferred.MigrationId);
        Assert.Equal("202610140001_Preparation", deferred.DeferredOrigin!.MigrationId);
        Assert.Equal(0, deferred.DeferredOrigin.OperationOrdinal);
        Assert.DoesNotContain("private-payload", text, StringComparison.Ordinal);
        Assert.Contains("202610140001_Preparation", text, StringComparison.Ordinal);
    }

    /// <summary>Ad-hoc batches do not invent an owning migration from a supplied overall target.</summary>
    [Fact]
    public async Task AdHocStreamKeepsUnknownAssessmentMigration()
    {
        // Arrange
        MigrationOperation[] operations =
        [
            new SafeMigrationOperation(new EnsureColumnIntent("items", Column("Id")),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

        // Act
        var report = await CaptureAsync(operations, migrationIds: null, SafeMigrationReportMode.Preflight);

        // Assert
        Assert.Null(Assert.Single(report.Assessments).MigrationId);
        Assert.Equal("202610140099_Target", report.TargetMigrationId);
    }

    /// <summary>Postflight failures identify the final condition without implying earlier DDL rolled back.</summary>
    [Fact]
    public async Task FailedPostconditionKeepsOwnerAndRecoveryExplanation()
    {
        // Arrange
        MigrationOperation[] operations =
        [
            new SafeMigrationOperation(new EnsureColumnIntent("items", Column("Id")),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

        // Act
        var report = await CaptureAsync(operations, ["202610140001_Target"], SafeMigrationReportMode.Postflight,
            postconditionsSatisfied: false);

        var text = SafeMigrationReportText.Format(report);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("postcondition_failed", assessment.Code);
        Assert.Equal("postcondition_failed", assessment.Explanation.ReasonCode);
        Assert.Equal("items", assessment.Identity!.Table);
        Assert.Contains("Earlier DDL may already have applied", text, StringComparison.Ordinal);
        Assert.Contains("blockers=1", text, StringComparison.Ordinal);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.False(assessment.PostconditionSatisfied);
    }

    /// <summary>Blocked preflight entries retain the proven cause rather than their aggregate decision code.</summary>
    /// <param name="analysisCode">The specific provider or projected data-safety reason.</param>
    [Theory]
    [InlineData("required_column_has_rows")]
    [InlineData("projected_foreign_key_replacement_data_blocked")]
    public async Task BlockedPreflightKeepsSpecificProviderExplanation(string analysisCode)
    {
        // Arrange
        SafeMigrationIntent intent = analysisCode == "required_column_has_rows"
            ? new EnsureColumnIntent("items", new ExpectedColumnDefinition("Id", typeof(int), false, "int"))
            : new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition("fk_children_parents", "children",
                ["OwnerId"], "parents", ["Id"]));

        MigrationOperation[] operations = [new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent)];
        var providerAnalysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.DataBlocked,
            SafeMigrationRepairCapability.None, false, analysisCode);

        // Act
        var report = await CaptureAsync(operations, ["202610140001_Target"], SafeMigrationReportMode.Preflight,
            capturedAnalysis: providerAnalysis);
        var exception = Assert.Throws<SafeMigrationPreflightException>(report.ThrowIfBlocked);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("data_blocked", assessment.Code);
        Assert.Equal("data_blocked", assessment.DecisionCode);
        Assert.Equal(providerAnalysis.Code, assessment.AnalysisCode);
        Assert.Equal(providerAnalysis.Code, assessment.Explanation.ReasonCode);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);
        Assert.Contains(providerAnalysis.Code, exception.Message, StringComparison.Ordinal);
        Assert.Contains(assessment.Explanation.Reason, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Malformed ordinary metadata remains unavailable instead of breaking diagnostic formatting.</summary>
    /// <param name="missingName">Whether the authored column name or a composite index member is unavailable.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MalformedOrdinaryMetadataDoesNotReplaceTheOriginalReport(bool missingName)
    {
        // Arrange
        MigrationOperation malformed = missingName
            ? new AddColumnOperation { Name = null!, Table = "items", ClrType = typeof(int), }
            : new CreateIndexOperation { Name = "ix_items", Table = "items", Columns = ["Id", null!], };

        MigrationOperation[] operations = [malformed];

        // Act
        // WHY: Postflight leaves ordinary EF operations unclassified. This
        // verifies diagnostic fallback without changing the existing rejection
        // of malformed authored operations inside preflight projection.
        var report = await CaptureAsync(operations, ["202610140001_Target"], SafeMigrationReportMode.Postflight);
        var text = SafeMigrationReportText.Format(report, SafeMigrationReportSelection.Complete);
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.False(assessment.IsSafeOperation);
        Assert.Null(assessment.Identity);
        Assert.Null(assessment.ObservedState);
        Assert.Null(assessment.Policy);
        Assert.Equal("provider_owned_not_analyzed", assessment.Code);
        Assert.Equal("provider_owned_not_analyzed", assessment.Explanation.ReasonCode);
        Assert.Contains("owner=<unavailable>", text, StringComparison.Ordinal);
        Assert.False(document.RootElement.GetProperty("assessments")[0].GetProperty("identityKnown").GetBoolean());
    }

    /// <summary>Builds an expected structural column without data payloads.</summary>
    private static ExpectedColumnDefinition Column(string name) => new(name, typeof(int), true, "int");

    /// <summary>Exercises ordered report creation without bypassing it through a constructed assessment.</summary>
    private static async Task<SafeMigrationRunReport> CaptureAsync(
        IReadOnlyList<MigrationOperation> operations,
        List<string>? migrationIds,
        SafeMigrationReportMode mode,
        bool postconditionsSatisfied = true,
        SafeMigrationProviderAnalysis? capturedAnalysis = null
    )
    {
        using var context = new DbContext(new DbContextOptionsBuilder().Options);
        var runner = new SafeMigrationRunner(new CapturedAnalyzer(postconditionsSatisfied, capturedAnalysis));
        var method = typeof(SafeMigrationRunner).GetMethod("AnalyzeOperationsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // WHY: This isolated seam exercises the real ordinal/projection/report
        // loop without fabricating EF history services or requiring a live
        // database merely to verify identifier-only context capture.
        var task = (Task<SafeMigrationRunReport>)method.Invoke(runner,
        [
            context, operations, mode, new SafeMigrationRunOptions("instance-test", "202610140099_Target"),
            $"safe-relational-model:v1:doka_mysql:sha256:{new string('a', 64)}", new string('b', 64),
            DateTimeOffset.UnixEpoch, new SafeMigrationProviderEnvironment("doka_mysql", "mariadb", "11.8.9"),
            null, migrationIds, CancellationToken.None,
        ])!;

        return await task;
    }

    /// <summary>Returns fixed captured evidence; it performs no I/O or fabricated projection inference.</summary>
    private sealed class CapturedAnalyzer(
        bool postconditionsSatisfied,
        SafeMigrationProviderAnalysis? capturedAnalysis
    ) : ISafeMigrationProviderAnalyzer
    {
        /// <inheritdoc />
        public string ProviderId => "doka_mysql";

        /// <inheritdoc />
        public void ValidateContext(DbContext context) => ArgumentNullException.ThrowIfNull(context);

        /// <inheritdoc />
        public Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
            DbContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("The ordered-report seam receives already captured environment evidence.");

        /// <inheritdoc />
        public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
            DbContext context,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("The ordered-report seam does not acquire live database scopes.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context,
            IReadOnlyList<SafeMigrationOperation> operations,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<SafeMigrationProviderAnalysis>>(operations.Select(_ =>
            capturedAnalysis ?? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None, postconditionsSatisfied, "classified_matching")).ToArray());

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
            DbContext context,
            IReadOnlyList<MigrationOperation> operations,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<SafeMigrationUnexpectedObject>>([]);
    }
}
