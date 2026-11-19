namespace Doka.EntityFrameworkCore.SafeMigrations;

public sealed partial class SafeMigrationRunner
{
    /// <summary>Reduces immutable live evidence in complete-stream order without performing provider I/O.</summary>
    /// <param name="operations">The untouched complete stream whose ordinals own every assessment.</param>
    /// <param name="mode">The preflight or postflight interpretation of decisions and postconditions.</param>
    /// <param name="migrationIds">Optional migration identities aligned with the complete stream.</param>
    /// <param name="classification">The provider results and qualified identity views.</param>
    /// <param name="assessments">The existing invocation-owned output buffer.</param>
    /// <param name="cancellationToken">The token checked before each ordered operation is reduced.</param>
    /// <returns>The ordered assessments and aggregate status, without publishing incomplete evidence.</returns>
    private OrderedAssessmentResult ReduceOrderedAssessments(
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationReportMode mode,
        List<string>? migrationIds,
        ClassifiedOperationStream classification,
        List<SafeMigrationAssessment> assessments,
        CancellationToken cancellationToken
    )
    {
        using var activity = SafeMigrationTelemetry.StartAnalysisStage("ordered-projection", operations.Count);
        var reducer = new OrderedAssessmentReducer(
            _providerAnalyzer,
            operations,
            mode,
            migrationIds,
            classification,
            assessments);

        return reducer.Reduce(classification.Analyses, cancellationToken);
    }

    /// <summary>Owns one synchronous stream reduction; it never escapes into provider I/O or a shared cache.</summary>

    // WHY: Keep evolving origins and aggregate flags together without allocating a heap run context
    // or capturing mutable reduction state in closures across the asynchronous provider phases.
    private partial struct OrderedAssessmentReducer
    {
        private readonly IReadOnlyList<MigrationOperation> _operations;
        private readonly SafeMigrationReportMode _mode;
        private readonly List<string>? _migrationIds;
        private readonly List<SafeMigrationAssessment> _assessments;
        private readonly SafeMigrationPostflightProjection? _postflightProjection;
        private readonly SafeMigrationPreflightProjection? _preflightProjection;
        private bool _blocked;
        private bool _hasProviderOperations;
        private bool _hasDeferredOperations;
        private SafeMigrationDeferredOrigin? _rawSqlOrigin;
        private SafeMigrationDeferredOrigin? _managedDataOrigin;
        private SafeMigrationDeferredOrigin? _globalDataOrigin;

        /// <summary>Creates exactly the projection appropriate to this invocation's report mode.</summary>
        /// <param name="providerAnalyzer">
        /// The established provider capabilities, not an additional analyzer invocation.
        /// </param>
        /// <param name="operations">The complete stream retained for original ordinals and provenance.</param>
        /// <param name="mode">The report mode controlling projection and final-condition interpretation.</param>
        /// <param name="migrationIds">Optional authored migration identities aligned with the complete stream.</param>
        /// <param name="classification">The already captured provider classifications and identity views.</param>
        /// <param name="assessments">The existing invocation-owned output buffer.</param>
        internal OrderedAssessmentReducer(
            ISafeMigrationProviderAnalyzer providerAnalyzer,
            IReadOnlyList<MigrationOperation> operations,
            SafeMigrationReportMode mode,
            List<string>? migrationIds,
            ClassifiedOperationStream classification,
            List<SafeMigrationAssessment> assessments
        )
        {
            _operations = operations;
            _mode = mode;
            _migrationIds = migrationIds;
            _assessments = assessments;

            _postflightProjection = mode == SafeMigrationReportMode.Postflight
                ? new SafeMigrationPostflightProjection(
                    operations,
                    classification.PostflightIdentityNormalizer,
                    providerAnalyzer as ISafeMigrationProjectedKeyAnalyzer,
                    classification.Analyses,
                    classification.RenamedManagedIntents)
                : null;

            _preflightProjection = mode == SafeMigrationReportMode.Preflight
                ? new SafeMigrationPreflightProjection(
                    providerAnalyzer as ISafeMigrationProviderOperationProjection,
                    providerAnalyzer as ISafeMigrationProjectedKeyAnalyzer,
                    classification.ObjectIdentityNormalizer,
                    providerAnalyzer as ISafeMigrationProjectedDependencyAnalyzer,
                    providerAnalyzer as ISafeMigrationIndexPrerequisiteSource,
                    providerAnalyzer as ISafeMigrationProjectedColumnAnalyzer,
                    providerAnalyzer as ISafeMigrationRenamedTableAnalyzer,
                    providerAnalyzer as ISafeMigrationLivePrerequisiteSource)
                : null;
        }

        /// <summary>
        /// Visits every complete-stream ordinal while consuming only safe operations from the compact batch.
        /// </summary>
        /// <param name="liveAnalyses">The safe-subset results with their previously verified cardinality.</param>
        /// <param name="cancellationToken">The invocation token checked before each reduction step.</param>
        /// <returns>All assessments and the status derived from the same accumulated decisions.</returns>
        internal OrderedAssessmentResult Reduce(
            IReadOnlyList<SafeMigrationProviderAnalysis> liveAnalyses,
            CancellationToken cancellationToken
        )
        {
            var safeOperationOrdinal = 0;

            // WHY: Compact provider ordinals advance only for safe operations; reports retain full-stream positions.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < _operations.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var operation = _operations[ordinal];
                _preflightProjection?.CurrentOperationOrdinal = ordinal;

                var managedDataVersion = _preflightProjection?.ModelManagedDataChangeVersion;
                var globalDataVersion = _preflightProjection?.DataMutationVersion;
                if (operation is not SafeMigrationOperation safeOperation)
                {
                    ObserveProviderOperation(operation, ordinal, managedDataVersion, globalDataVersion);

                    continue;
                }

                ReduceSafeOperation(
                    safeOperation,
                    liveAnalyses[safeOperationOrdinal++],
                    ordinal,
                    managedDataVersion,
                    globalDataVersion);
            }

            return new OrderedAssessmentResult(_assessments, GetStatus());
        }

        /// <summary>
        /// Projects one safe contract, resolves deferred provenance, then records its decision or runtime guard.
        /// </summary>
        private void ReduceSafeOperation(
            SafeMigrationOperation safeOperation,
            SafeMigrationProviderAnalysis liveAnalysis,
            int ordinal,
            long? managedDataVersion,
            long? globalDataVersion
        )
        {
            var analysis = _preflightProjection?.Project(safeOperation, liveAnalysis) ?? liveAnalysis;
            var deferredOrigin = GetDeferredOrigin(analysis, ordinal);

            if (_mode == SafeMigrationReportMode.Preflight
                && deferredOrigin is not null)
            {
                // WHY: Opaque SQL, provider DDL effects and unconfined writes invalidate earlier live
                // evidence. Runtime guards inspect the actual ordered state.
                // A deferred seed write may itself fire triggers, so discard
                // earlier row proofs without inventing its postconditions.
                if (analysis.IsModelManagedProjectionUnknown)
                {
                    _preflightProjection!.ObserveDeferredModelManagedMutation();
                    _managedDataOrigin = new SafeMigrationDeferredOrigin(
                        _migrationIds?[ordinal],
                        ordinal,
                        typeof(SafeMigrationOperation).FullName!);

                    _globalDataOrigin = _managedDataOrigin;
                }

                else if (analysis.ProviderDeferredOriginOrdinal is not null)
                {
                    _preflightProjection!.ObserveDeferredProviderOperation(ordinal);
                }

                _hasDeferredOperations = true;
                _assessments.Add(
                    new SafeMigrationAssessment(
                        ordinal,
                        typeof(SafeMigrationOperation).FullName!,
                        isSafeOperation: true,
                        safeOperation.Intent.Kind,
                        safeOperation.Intent.ObjectName,
                        observedState: null,
                        SafeMigrationAction.ValidateAtRuntime,
                        postconditionSatisfied: null,
                        "runtime_validation_required",
                        analysis.Code,
                        "runtime_validation_required",
                        SafeMigrationOperationalImpact.Unknown,
                        differences: null,
                        deferredOrigin: deferredOrigin,
                        identity: SafeMigrationOperationIdentity.Create(safeOperation.Intent),
                        migrationId: _migrationIds?[ordinal],
                        policy: safeOperation.Policy,
                        explanation: SafeMigrationDiagnosticMessages.GetExplanation(
                            safeOperation.Intent,
                            state: null,
                            analysis.Code,
                            "runtime_validation_required")));

                return;
            }

            var decision = SafeMigrationDecisionPlanner.Plan(
                safeOperation.Intent.Kind,
                analysis.ObservedState,
                safeOperation.Policy,
                analysis.RepairCapability);

            var postconditionSuperseded = _postflightProjection?.IsSuperseded(ordinal) == true;
            var postflightIdentityUnproven = _postflightProjection?.IsIdentityUnproven(ordinal) == true;
            var postconditionSatisfied = !postflightIdentityUnproven
                && (analysis.PostconditionSatisfied || postconditionSuperseded);

            var operationBlocked = _mode == SafeMigrationReportMode.Preflight
                ? decision.Action.RejectsExecution()
                : !postconditionSatisfied;

            _blocked |= operationBlocked;
            _preflightProjection?.Observe(safeOperation, liveAnalysis, analysis, decision);

            if (_preflightProjection is not null
                && _preflightProjection.ModelManagedDataChangeVersion != managedDataVersion)
            {
                _managedDataOrigin = new SafeMigrationDeferredOrigin(
                    _migrationIds?[ordinal],
                    ordinal,
                    typeof(SafeMigrationOperation).FullName!);
            }

            if (_preflightProjection is not null
                && _preflightProjection.DataMutationVersion != globalDataVersion)
            {
                _globalDataOrigin = _managedDataOrigin;
            }

            // WHY: Final search-path metadata cannot establish the owner of
            // an earlier unqualified writer across an opaque schema change.
            // Neither live absence nor final-writer reduction supplies that proof.
            var assessmentCode = postflightIdentityUnproven switch
            {
                true => "postcondition_identity_unproven",
                false when postconditionSuperseded => "postcondition_superseded",
                false when operationBlocked && _mode == SafeMigrationReportMode.Postflight => "postcondition_failed",
                false when operationBlocked && analysis.ObservedState == SafeMigrationObservedState.Unsupported
                    => analysis.Code,
                false when operationBlocked => decision.Code,
                false => analysis.Code,
            };

            _assessments.Add(
                new SafeMigrationAssessment(
                    ordinal,
                    typeof(SafeMigrationOperation).FullName!,
                    isSafeOperation: true,
                    safeOperation.Intent.Kind,
                    safeOperation.Intent.ObjectName,
                    analysis.ObservedState,
                    decision.Action,
                    postconditionSatisfied,
                    assessmentCode,
                    analysis.Code,
                    decision.Code,
                    analysis.OperationalImpact,
                    analysis.Differences,
                    deferredOrigin: null,
                    identity: SafeMigrationOperationIdentity.Create(safeOperation.Intent),
                    migrationId: _migrationIds?[ordinal],
                    policy: safeOperation.Policy,
                    explanation: SafeMigrationDiagnosticMessages.GetExplanation(
                        safeOperation.Intent,
                        analysis.ObservedState,

                        // WHY: Aggregate preflight codes identify a planner decision,
                        // not its provider-proven cause. Postflight phase overrides
                        // instead describe the established final-condition result.
                        _mode == SafeMigrationReportMode.Postflight
                            ? assessmentCode
                            : analysis.Code,
                        decision.Code)));
        }

        /// <summary>
        /// Resolves aggregate report status without allowing deferred or provider-owned work to hide a blocker.
        /// </summary>
        private readonly SafeMigrationReportStatus GetStatus() => _operations.Count switch
        {
            0 => SafeMigrationReportStatus.NoOperations,
            _ when _blocked => SafeMigrationReportStatus.Blocked,
            _ when _hasDeferredOperations => SafeMigrationReportStatus.RuntimeValidationRequired,
            _ when _hasProviderOperations => SafeMigrationReportStatus.ReadyWithProviderOperations,
            _ => SafeMigrationReportStatus.Ready,
        };
    }

    /// <summary>Transfers the existing output buffer and aggregate status between synchronous and I/O phases.</summary>
    /// <param name="Assessments">The original invocation-owned assessment list in full-stream order.</param>
    /// <param name="Status">The status derived after the complete ordered reduction.</param>
    private readonly record struct OrderedAssessmentResult(
        List<SafeMigrationAssessment> Assessments,
        SafeMigrationReportStatus Status
    );
}
