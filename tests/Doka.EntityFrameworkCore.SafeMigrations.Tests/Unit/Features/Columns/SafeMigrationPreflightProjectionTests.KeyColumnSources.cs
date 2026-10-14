namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Accepted key metadata changes preserve only independently captured intrinsic column results.</summary>
    /// <param name="history">The accepted removal and ordinary index history.</param>
    /// <param name="ensure">Whether the operation has no authored old column definition.</param>
    /// <param name="matching">Whether the native result already matches the requested target.</param>
    [Theory]
    [InlineData("primary", false, false)]
    [InlineData("primary", false, true)]
    [InlineData("primary", true, false)]
    [InlineData("primary", true, true)]
    [InlineData("foreign-index-apply-primary", false, false)]
    [InlineData("foreign-index-apply-primary", false, true)]
    [InlineData("foreign-index-apply-primary", true, false)]
    [InlineData("foreign-index-apply-primary", true, true)]
    [InlineData("foreign-index-noop-primary", false, false)]
    [InlineData("foreign-index-noop-primary", false, true)]
    [InlineData("foreign-index-noop-primary", true, false)]
    [InlineData("foreign-index-noop-primary", true, true)]
    public void CertifiedKeyHistoryRetainsOnlyExactIndependentColumnProof(
        string history,
        bool ensure,
        bool matching
    )
    {
        // Arrange
        var provider = new KeyColumnSourceProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = KeyColumnSource("reference");
        var target = KeyColumnSource("reference", nullable: !matching);
        KeyColumnObserveSources(projection, source, KeyColumnSource("question"));
        KeyColumnObserveHistory(projection, history);
        var operation = KeyColumnOperation(source, target, ensure);
        var live = provider.Capture(operation.Intent, source, matching);

        // Act
        var result = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, result.ObservedState,
            operation.Policy, result.RepairCapability);

        // Assert
        Assert.Equal(matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            result.ObservedState);
        Assert.Equal(matching ? SafeMigrationAction.NoOp : SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(live.Code, result.Code);
        Assert.Equal(ensure && !matching, result.RepairPreservesIntrinsicColumnLayout);
        Assert.Equal(ensure && !matching, result.RepairPreservesPhysicalKeys);
        Assert.False(result.RepairPreservesLiveValueDomain);
        Assert.False(live.RepairPreservesIntrinsicColumnLayout);
        Assert.False(live.RepairPreservesPhysicalKeys);
        Assert.True(provider.ReuseQueries > 0);
        Assert.Equal(0, provider.ValidationQueries);
        if (!ensure || matching)
        {
            Assert.Same(live, result);
        }
    }

    /// <summary>A certified nullable-only repair retains independently proven unchanged sibling columns.</summary>
    /// <param name="ensure">Whether the accepted repair uses an Ensure target without authored source metadata.</param>
    /// <param name="siblingMatching">Whether the later sibling result is matching rather than repairable.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CertifiedKeyColumnRepairPreservesUnchangedSiblingProof(
        bool ensure,
        bool siblingMatching
    )
    {
        // Arrange
        var provider = new KeyColumnSourceProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = KeyColumnSource("reference");
        var siblingSource = KeyColumnSource("question");
        KeyColumnObserveSources(projection, source, siblingSource);
        KeyColumnObserveHistory(projection, "foreign-index-apply-primary");
        var operation = KeyColumnOperation(source, KeyColumnSource("reference", nullable: true), ensure);
        var live = provider.Capture(operation.Intent, source);
        var projected = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, projected.ObservedState,
            operation.Policy, projected.RepairCapability);
        var sibling = KeyColumnOperation(siblingSource,
            KeyColumnSource("question", nullable: !siblingMatching), ensure);
        var siblingLive = provider.Capture(sibling.Intent, siblingSource, siblingMatching);
        var index = new SafeMigrationOperation(KeyColumnIndex(), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        projection.Observe(operation, live, projected, decision);
        var siblingResult = projection.Project(sibling, siblingLive);
        var indexResult = projection.Project(index, Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(siblingMatching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            siblingResult.ObservedState);
        Assert.Equal(siblingMatching ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            siblingResult.RepairCapability);
        Assert.Equal(ensure && !siblingMatching, siblingResult.RepairPreservesIntrinsicColumnLayout);
        Assert.Equal(SafeMigrationObservedState.Matching, indexResult.ObservedState);
        Assert.False(live.RepairPreservesIntrinsicColumnLayout);
        Assert.False(siblingLive.RepairPreservesIntrinsicColumnLayout);
    }

    /// <summary>Providers without preservation opt-ins retain conservative projected column behavior.</summary>
    /// <param name="history">The accepted key history that cannot supply a default certificate.</param>
    /// <param name="ensure">Whether the later target has no authored old definition.</param>
    [Theory]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("foreign-index-apply-primary", false)]
    [InlineData("foreign-index-apply-primary", true)]
    public void KeyColumnSourcesRemainDisabledWithoutProviderOptIn(
        string history,
        bool ensure
    )
    {
        // Arrange
        var provider = new KeyColumnConservativeAnalyzer();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = KeyColumnSource("reference");
        KeyColumnObserveSources(projection, source, KeyColumnSource("question"));
        KeyColumnObserveHistory(projection, history);
        var operation = KeyColumnOperation(source, KeyColumnSource("reference", nullable: true), ensure);
        var live = KeyColumnLiveProof();

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
        Assert.False(result.RepairPreservesIntrinsicColumnLayout);
        Assert.False(result.RepairPreservesPhysicalKeys);
        Assert.NotSame(live, result);
    }

    /// <summary>A rejected key mutation creates no accepted source history or provider preservation request.</summary>
    [Fact]
    public void RejectedKeyMutationDoesNotMintIntrinsicColumnSourceHistory()
    {
        // Arrange
        var provider = new KeyColumnSourceProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = KeyColumnSource("reference");
        KeyColumnObserveSources(projection, source, KeyColumnSource("question"));
        var drop = new SafeMigrationOperation(new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var rejected = Live(SafeMigrationObservedState.Unsupported);
        var rejection = SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, rejected.ObservedState,
            drop.Policy, rejected.RepairCapability);
        var operation = KeyColumnOperation(source, KeyColumnSource("reference", nullable: true), ensure: true);
        var live = KeyColumnLiveProof();

        // Act
        projection.Observe(drop, rejected, rejected, rejection);
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectUnsupported, rejection.Action);
        Assert.Equal(0, provider.PreservationQueries);
        Assert.Equal(0, provider.ReuseQueries);
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
        Assert.False(result.RepairPreservesIntrinsicColumnLayout);
    }

    /// <summary>Key histories cannot replace independent source evidence or bypass row and column lifetimes.</summary>
    /// <param name="boundary">The independent proof or lifetime boundary invalidating this result.</param>
    /// <param name="ensure">Whether the target relies on a provider-captured rather than authored old source.</param>
    [Theory]
    [InlineData("unknown-source", false)]
    [InlineData("unknown-source", true)]
    [InlineData("source-drift", false)]
    [InlineData("source-drift", true)]
    [InlineData("unbound-live", false)]
    [InlineData("unbound-live", true)]
    [InlineData("storage-change", false)]
    [InlineData("storage-change", true)]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData("opaque", false)]
    [InlineData("opaque", true)]
    [InlineData("column-drop", false)]
    [InlineData("column-drop", true)]
    [InlineData("column-recreate", false)]
    [InlineData("column-recreate", true)]
    [InlineData("table-recreate", false)]
    [InlineData("table-recreate", true)]
    [InlineData("incoming-foreign-key", false)]
    [InlineData("incoming-foreign-key", true)]
    [InlineData("mutating-repair", false)]
    [InlineData("mutating-repair", true)]
    public void KeyColumnProofNeverBypassesIndependentSourceOrLifetimeBoundary(
        string boundary,
        bool ensure
    )
    {
        // Arrange
        var provider = new KeyColumnSourceProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = KeyColumnSource("reference");
        KeyColumnObserveSources(projection, source, KeyColumnSource("question"));
        KeyColumnObserveHistory(projection, "primary");
        switch (boundary)
        {
            case "data":
                projection.ObserveProviderPostcondition(new InsertDataOperation
                {
                    Table = "other",
                    Columns = ["id"],
                    Values = new object?[,] { { 1 } },
                });
                break;
            case "opaque":
                projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1" });
                break;
            case "column-drop":
            case "column-recreate":
                ObserveAccepted(projection, new DropColumnIntent("reference", "items"),
                    SafeMigrationObservedState.Matching);
                if (boundary == "column-recreate")
                {
                    Apply(projection, new EnsureColumnIntent("items", source));
                }

                break;
            case "table-recreate":
                ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
                // WHY: A complete recreated owner with the exact source can be
                // independently repaired. Drift forces this probe to distinguish
                // new physical evidence from the old owner-bound certificate.
                var recreatedSource = new ExpectedColumnDefinition("reference", typeof(Guid), false, "char(36)",
                    comment: "recreated-owner");

                Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [recreatedSource]),
                    SafeMigrationTableMode.StrictDefinition));
                break;
            case "incoming-foreign-key":
                Apply(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                    "fk_incoming", "other", ["reference"], "items", ["reference"])));
                break;
        }

        var authoredSource = boundary == "source-drift"
            ? new ExpectedColumnDefinition("reference", typeof(Guid), false, "char(36)", comment: "unproven")
            : source;
        var target = boundary == "storage-change"
            ? new ExpectedColumnDefinition("reference", typeof(Guid), true, "char(64)")
            : KeyColumnSource("reference", nullable: true);
        var operation = KeyColumnOperation(authoredSource, target, ensure);
        var live = provider.Capture(operation.Intent,
            boundary == "unknown-source" ? null : authoredSource,
            mutatesData: boundary == "mutating-repair");
        if (boundary == "unbound-live")
        {
            live = KeyColumnLiveProof();
        }

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
        Assert.False(result.RepairPreservesIntrinsicColumnLayout);
        Assert.False(result.RepairPreservesPhysicalKeys);
        Assert.False(result.RepairPreservesLiveValueDomain);
        if (boundary is "data" or "opaque" or "column-drop" or "column-recreate" or "table-recreate"
            or "incoming-foreign-key")
        {
            Assert.Equal(0, provider.ReuseQueries);
        }

        if (boundary == "opaque")
        {
            Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
            Assert.True(result.IsOpaqueProjectionUnknown);
        }
    }

    /// <summary>
    /// A fully declared recreated owner can prove its own safe transition without reusing old live evidence.
    /// </summary>
    [Fact]
    public void RecreatedOwnerUsesCompleteNewDefinitionInsteadOfOldKeyColumnCertificate()
    {
        // Arrange
        var provider = new KeyColumnSourceProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = KeyColumnSource("reference");
        KeyColumnObserveSources(projection, source, KeyColumnSource("question"));
        KeyColumnObserveHistory(projection, "primary");
        ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [source]),
            SafeMigrationTableMode.StrictDefinition));
        var operation = KeyColumnOperation(source, KeyColumnSource("reference", nullable: true), ensure: false);
        var oldLive = provider.Capture(operation.Intent, source);

        // Act
        var result = projection.Project(operation, oldLive);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, result.RepairCapability);
        Assert.NotSame(oldLive, result);
        Assert.Equal(0, provider.ReuseQueries);
        Assert.False(result.RepairPreservesIntrinsicColumnLayout);
    }

    /// <summary>Column source preservation never folds a different physical table or schema into the history.</summary>
    /// <param name="boundary">The owner identity that must remain independent of the accepted key removal.</param>
    /// <param name="ensure">Whether the later request lacks authored old metadata.</param>
    [Theory]
    [InlineData("table-case", false)]
    [InlineData("table-case", true)]
    [InlineData("schema-case", false)]
    [InlineData("schema-case", true)]
    [InlineData("schema-other", false)]
    [InlineData("schema-other", true)]
    public void KeyColumnSourceHistoryDoesNotCrossOwnerOrSchemaIdentity(
        string boundary,
        bool ensure
    )
    {
        // Arrange
        var provider = new KeyColumnSourceProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = KeyColumnSource("reference");
        var table = boundary == "table-case" ? "ITEMS" : "items";
        var schema = boundary == "schema-case" ? "APP" : boundary == "schema-other" ? "archive" : "app";
        KeyColumnObserveSources(projection, source, KeyColumnSource("question"), schema: "app");
        ObserveAccepted(projection, new EnsureColumnIntent(table, source, schema),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new DropIndexIntent("ix_unrelated", table, schema),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items", "app"),
            SafeMigrationObservedState.Matching);
        var operation = KeyColumnOperation(source, KeyColumnSource("reference", nullable: true), ensure,
            table, schema);
        var live = provider.Capture(operation.Intent, source);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
        Assert.False(result.RepairPreservesIntrinsicColumnLayout);
        Assert.Equal(0, provider.ReuseQueries);
        Assert.NotSame(live, result);
    }

    /// <summary>Later key mutations cannot revive sources invalidated by ordinary DDL or a recreated PK.</summary>
    /// <param name="invalidation">The accepted operation that ended the intrinsic source history.</param>
    /// <param name="laterMutation">The later qualified mutation that must not recreate the expired history.</param>
    /// <param name="ensure">Whether the later column operation lacks an authored old source.</param>
    [Theory]
    [InlineData("ordinary-index-drop", "foreign", false)]
    [InlineData("ordinary-index-drop", "foreign", true)]
    [InlineData("ordinary-index-drop", "primary", false)]
    [InlineData("ordinary-index-drop", "primary", true)]
    [InlineData("ordinary-index-drop", "index", false)]
    [InlineData("ordinary-index-drop", "index", true)]
    [InlineData("recreated-primary", "foreign", false)]
    [InlineData("recreated-primary", "foreign", true)]
    [InlineData("recreated-primary", "primary", false)]
    [InlineData("recreated-primary", "primary", true)]
    [InlineData("recreated-primary", "index", false)]
    [InlineData("recreated-primary", "index", true)]
    public void LaterPreservingKeyMutationCannotReviveInvalidatedSource(
        string invalidation,
        string laterMutation,
        bool ensure
    )
    {
        // Arrange
        var provider = new KeyColumnSourceProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = KeyColumnSource("reference");
        KeyColumnObserveSources(projection, source, KeyColumnSource("question"));
        if (invalidation == "recreated-primary")
        {
            KeyColumnObserveHistory(projection, "primary");
            ObserveAccepted(projection, new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition(
                "pk_items", "items", ["reference", "question"])), SafeMigrationObservedState.Matching);
        }
        else
        {
            ObserveAccepted(projection, new DropIndexIntent("ix_unrelated", "items"),
                SafeMigrationObservedState.Matching);
        }

        switch (laterMutation)
        {
            case "foreign":
                ObserveAccepted(projection, new DropForeignKeyIntent("fk_later", "items"),
                    SafeMigrationObservedState.Matching);
                break;
            case "primary":
                ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
                    SafeMigrationObservedState.Matching);
                break;
            case "index":
                ObserveAccepted(projection, KeyColumnIndex(), SafeMigrationObservedState.Missing);
                break;
        }

        var operation = KeyColumnOperation(source, KeyColumnSource("reference", nullable: true), ensure);
        var live = provider.Capture(operation.Intent, source);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.None, result.RepairCapability);
        Assert.False(result.RepairPreservesIntrinsicColumnLayout);
        Assert.Equal(0, provider.ReuseQueries);
        Assert.NotSame(live, result);
    }

    /// <summary>Intrinsic nullable-only source certificates do not prove candidate-key replacement safety.</summary>
    [Fact]
    public void IntrinsicKeyColumnSourceDoesNotCertifyCandidateKeyReplacement()
    {
        // Arrange
        var provider = new KeyColumnSourceProbe();
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = KeyColumnSource("reference");
        KeyColumnObserveSources(projection, source, KeyColumnSource("question"));
        KeyColumnObserveHistory(projection, "foreign-index-apply-primary");
        var operation = KeyColumnOperation(source, KeyColumnSource("reference", nullable: true), ensure: false);
        var live = provider.Capture(operation.Intent, source);
        var projected = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, projected.ObservedState,
            operation.Policy, projected.RepairCapability);
        var key = new SafeMigrationOperation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition(
            "pk_replacement", "items", ["question"])), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        projection.Observe(operation, live, projected, decision);
        var keyResult = projection.Project(key, Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, keyResult.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, keyResult.RepairCapability);
        Assert.False(keyResult.PostconditionSatisfied);
    }

    /// <summary>Creates the exact independently captured intrinsic source used by the fake provider.</summary>
    /// <param name="name">The physical column member.</param>
    /// <param name="nullable">The intrinsic nullable facet.</param>
    private static ExpectedColumnDefinition KeyColumnSource(
        string name,
        bool nullable = false
    ) => new(name, typeof(Guid), nullable, "char(36)");

    /// <summary>Creates an exact immutable column request under one physical owner.</summary>
    /// <param name="source">The authored old source when the request is an Alter.</param>
    /// <param name="target">The unchanged target analyzed by the fake provider.</param>
    /// <param name="ensure">Whether the operation omits authored old metadata.</param>
    /// <param name="table">The physical table identity.</param>
    /// <param name="schema">The physical schema identity.</param>
    private static SafeMigrationOperation KeyColumnOperation(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target,
        bool ensure,
        string table = "items",
        string? schema = null
    ) => new(ensure ? new EnsureColumnIntent(table, target, schema)
        : new AlterColumnIntent(table, target, source, schema), SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Creates an ordinary nonunique BTREE over an unchanged sibling column.</summary>
    private static EnsureIndexIntent KeyColumnIndex() => new(new ExpectedIndexDefinition("ix_question", "items",
        [new ExpectedIndexKeyDefinition("question")], method: "BTREE"));

    /// <summary>Records accepted unchanged intrinsic sources without claiming complete physical table shape.</summary>
    /// <param name="projection">The ordered accepted-state projection.</param>
    /// <param name="source">The independently matching primary source member.</param>
    /// <param name="sibling">The independently matching sibling source member.</param>
    /// <param name="schema">The exact physical schema of both accepted observations.</param>
    private static void KeyColumnObserveSources(
        SafeMigrationPreflightProjection projection,
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition sibling,
        string? schema = null
    )
    {
        ObserveAccepted(projection, new EnsureColumnIntent("items", source, schema),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new EnsureColumnIntent("items", sibling, schema),
            SafeMigrationObservedState.Matching);
    }

    /// <summary>Records only accepted narrow metadata effects, with independent Apply and NoOp index cases.</summary>
    /// <param name="projection">The ordered accepted-state projection.</param>
    /// <param name="history">The exact accepted removal and index sequence.</param>
    private static void KeyColumnObserveHistory(
        SafeMigrationPreflightProjection projection,
        string history
    )
    {
        if (history != "primary")
        {
            ObserveAccepted(projection, new DropForeignKeyIntent("fk_reference", "items"),
                SafeMigrationObservedState.Matching);
            ObserveAccepted(projection, KeyColumnIndex(), history == "foreign-index-noop-primary"
                ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing);
        }

        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);
    }

    /// <summary>Creates favorable native evidence without any key, row-domain or history certificate.</summary>
    private static SafeMigrationProviderAnalysis KeyColumnLiveProof() => new(
        SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, false, "captured_key_column")
    {
        RequiresLiveDataProof = true,
    };

    /// <summary>Models exact source-bound provider opt-ins, never a complete table or candidate-key proof.</summary>
    private sealed class KeyColumnSourceProbe : ISafeMigrationProjectedColumnAnalyzer
    {
        private readonly Dictionary<SafeMigrationIntent, ExpectedColumnDefinition?> _sources = new();
        private readonly Dictionary<SafeMigrationIntent, SafeMigrationProviderAnalysis> _analyses = new();

        /// <summary>Gets the number of accepted mutation preservation requests.</summary>
        public int PreservationQueries { get; private set; }

        /// <summary>Gets the number of current source-bound reuse requests.</summary>
        public int ReuseQueries { get; private set; }

        /// <summary>Gets the number of conservative projected-structure validation requests.</summary>
        public int ValidationQueries { get; private set; }

        /// <summary>Captures one independently analyzed source under the exact immutable operation identity.</summary>
        /// <param name="intent">The independently analyzed immutable operation.</param>
        /// <param name="source">The independently captured source, or null when no source is proven.</param>
        /// <param name="matching">Whether the captured source already matches the target.</param>
        /// <param name="mutatesData">Whether the repair changes existing rows.</param>
        /// <returns>The immutable native result bound to this source and request.</returns>
        public SafeMigrationProviderAnalysis Capture(
            SafeMigrationIntent intent,
            ExpectedColumnDefinition? source,
            bool matching = false,
            bool mutatesData = false
        )
        {
            var live = new SafeMigrationProviderAnalysis(
                matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
                matching ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
                matching, "captured_key_column")
            {
                RequiresLiveDataProof = !matching,
                RepairMutatesData = mutatesData,
            };

            _sources[intent] = source;
            _analyses[intent] = live;

            return live;
        }

        /// <inheritdoc />
        public bool PreservesIntrinsicColumnSources(
            SafeMigrationIntent intent
        )
        {
            PreservationQueries++;

            return intent is DropForeignKeyIntent or DropPrimaryKeyIntent
                || intent is EnsureIndexIntent index
                && !index.Definition.Unique
                && index.Definition.Method == "BTREE"
                && index.Definition.Keys.All(static key => key.Column is not null);
        }

        /// <inheritdoc />
        public bool CanReuseLiveColumnProofAfterKeyChanges(
            AlterColumnIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis
        )
        {
            ReuseQueries++;

            return IsCapturedAlterProof(intent, liveAnalysis);
        }

        /// <inheritdoc />
        public bool CanReuseLiveColumnProofAfterKeyChanges(
            EnsureColumnIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis
        )
        {
            ReuseQueries++;

            return TryGetCapturedSource(intent, liveAnalysis, out var source)
                && HasNarrowCapturedTarget(source, intent.Definition, liveAnalysis);
        }

        /// <inheritdoc />
        public bool CanReuseLiveColumnProofAfterForeignKeyDrops(
            AlterColumnIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis
        ) => CanReuseLiveColumnProofAfterKeyChanges(intent, liveAnalysis);

        /// <inheritdoc />
        public bool CanReuseLiveColumnProofAfterForeignKeyDrops(
            EnsureColumnIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis
        ) => CanReuseLiveColumnProofAfterKeyChanges(intent, liveAnalysis);

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(
            AlterColumnIntent intent,
            ExpectedColumnDefinition source,
            SafeMigrationProjectedAlterColumnContext context,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        )
        {
            ValidationQueries++;

            if (context.HasCompleteTable && context.ProvesEmpty)
            {
                return projectedAnalysis;
            }

            return context.CanReuseLiveProof
                && IsCapturedAlterProof(intent, liveAnalysis)
                && intent.OldDefinition is not null
                && SafeMigrationDefinitionEquivalence.Column(source, intent.OldDefinition)
                    ? liveAnalysis
                    : new SafeMigrationProviderAnalysis(projectedAnalysis.ObservedState,
                        SafeMigrationRepairCapability.None, false, "projected_key_column_source_unproven");
        }

        /// <summary>Requires the exact native result and independently captured old definition for an Alter.</summary>
        /// <param name="intent">The immutable authored source and target request.</param>
        /// <param name="liveAnalysis">The exact independently captured native result.</param>
        /// <returns>Whether this result proves matching or a same-layout nullable-only transition.</returns>
        private bool IsCapturedAlterProof(
            AlterColumnIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis
        ) => TryGetCapturedSource(intent, liveAnalysis, out var source)
            && (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                || intent.OldDefinition is not null
                && SafeMigrationDefinitionEquivalence.Column(source, intent.OldDefinition))
            && HasNarrowCapturedTarget(source, intent.Definition, liveAnalysis);

        /// <summary>Reads a source only from the exact analysis captured for this unchanged request.</summary>
        /// <param name="intent">The immutable request including exact physical owner identity.</param>
        /// <param name="liveAnalysis">The exact native result associated with this request.</param>
        /// <param name="source">The independently captured source when its identity and metadata are known.</param>
        /// <returns>Whether independent source provenance exists for this exact native result.</returns>
        private bool TryGetCapturedSource(
            SafeMigrationIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? source
        )
        {
            source = null;

            return _analyses.TryGetValue(intent, out var captured)
                && ReferenceEquals(captured, liveAnalysis)
                && _sources.TryGetValue(intent, out source)
                && source is not null;
        }

        /// <summary>Certifies only exact GUID storage and independently proven nullable-only targets.</summary>
        /// <param name="source">The independently captured intrinsic source.</param>
        /// <param name="target">The independently analyzed unchanged target.</param>
        /// <param name="liveAnalysis">The independently proven matching or non-data repair result.</param>
        /// <returns>Whether no facet except nullability differs from this exact captured source.</returns>
        private static bool HasNarrowCapturedTarget(
            ExpectedColumnDefinition source,
            ExpectedColumnDefinition target,
            SafeMigrationProviderAnalysis liveAnalysis
        ) => !liveAnalysis.RepairMutatesData
            && SafeMigrationDefinitionEquivalence.Column(source, KeyColumnSource(source.Name, source.IsNullable))
            && (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                ? SafeMigrationDefinitionEquivalence.Column(source, target)
                : liveAnalysis.ObservedState == SafeMigrationObservedState.Different
                    && liveAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
                    && SafeMigrationDefinitionEquivalence.Column(target,
                        KeyColumnSource(source.Name, !source.IsNullable)));
    }

    /// <summary>Leaves preservation hooks at their interface defaults and rejects unproven projected repairs.</summary>
    private sealed class KeyColumnConservativeAnalyzer : ISafeMigrationProjectedColumnAnalyzer
    {
        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(
            AlterColumnIntent intent,
            ExpectedColumnDefinition source,
            SafeMigrationProjectedAlterColumnContext context,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => new(projectedAnalysis.ObservedState, SafeMigrationRepairCapability.None, false,
            "projected_key_column_source_unproven");
    }
}
