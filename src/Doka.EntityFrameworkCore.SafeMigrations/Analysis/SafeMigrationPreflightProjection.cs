namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection : ISafeMigrationProjectedColumnSource,
    ISafeMigrationProjectedTableSource
{
    private readonly ISafeMigrationProviderOperationProjection? _providerOperationProjection;
    private readonly ISafeMigrationProjectedKeyAnalyzer? _projectedKeyAnalyzer;
    private readonly ISafeMigrationProjectedDependencyAnalyzer? _projectedDependencyAnalyzer;
    private readonly ISafeMigrationIndexPrerequisiteSource? _indexPrerequisiteSource;
    private readonly ISafeMigrationProjectedColumnAnalyzer? _projectedColumnAnalyzer;
    private readonly ISafeMigrationRenamedTableAnalyzer? _renamedTableAnalyzer;
    private readonly ISafeMigrationLivePrerequisiteSource? _livePrerequisiteSource;
    private readonly Dictionary<TableKey, TableKey> _renamedTableSources;
    private readonly HashSet<TableKey> _projectedForeignKeyOwners;
    private Dictionary<TableKey, HashSet<string>>? _projectedForeignKeyColumns;
    private readonly ISafeMigrationProviderObjectIdentityNormalizer? _objectIdentityNormalizer;
    private readonly Dictionary<TableKey, ProjectedTable> _tables;

    // WHY: Strict table projections retain complete definitions. This second view
    // records only prerequisites proven by earlier convergence operations, so
    // a later operation cannot infer safety from an object that was rejected.
    private readonly Dictionary<TableKey, ProjectedPrerequisites> _prerequisites;
    private readonly HashSet<IndexKey> _droppedPhysicalKeys;
    private readonly HashSet<TableKey> _projectedDataMutationTables;
    private readonly Dictionary<TableKey, HashSet<string>> _projectedModelManagedUniqueKeys;

    // WHY: Table-owned column states make cleanup and rename independent of
    // columns accumulated for unrelated tables in a large operation stream.

    private readonly Dictionary<TableKey, Dictionary<string, ProjectedColumnState>> _projectedColumnStates;
    private readonly HashSet<TableKey> _projectedCandidateKeyMutationTables;
    private readonly HashSet<TableKey> _projectedMissingTables;
    private readonly HashSet<TableKey> _projectedUnknownTableStructures;
    private readonly HashSet<TableKey> _projectedUnknownPhysicalKeys;
    private readonly HashSet<TableKey> _projectedStructurallyModifiedTables;
    private HashSet<TableKey>? _projectedColumnDropOnlyTables;
    private HashSet<TableKey>? _projectedForeignKeyDropOnlyTables;
    private HashSet<TableKey>? _projectedIntrinsicColumnSourceTables;
    private HashSet<TableKey>? _projectedIndexDropOnlyTables;
    private readonly Dictionary<TableKey, HashSet<string>> _projectedChangedColumns;
    private bool _hasOpaqueProviderPostcondition;
    private bool _hasOpaqueSqlPostcondition;
    private bool _creationCharacterSetChanged;
    private long _providerDataMutationVersion;
    private int? _deferredProviderOperationOriginOrdinal;

    /// <summary>Gets whether unmodeled SQL has invalidated projected structural postconditions.</summary>
    internal bool HasOpaqueSqlPostcondition => _hasOpaqueSqlPostcondition;

    /// <summary>Gets the revision of globally invalidated row evidence.</summary>
    internal long DataMutationVersion => _providerDataMutationVersion;

    /// <summary>Creates invocation-local ordered evidence without seeding facts from the target model.</summary>
    /// <param name="providerOperationProjection">Provider-certified ordinary-operation effects.</param>
    /// <param name="projectedKeyAnalyzer">Independent native key feasibility checks.</param>
    /// <param name="objectIdentityNormalizer">The active provider's physical identity contract.</param>
    /// <param name="projectedDependencyAnalyzer">Native dependency and trigger-risk qualification.</param>
    /// <param name="indexPrerequisiteSource">Source-bound physical index dependencies.</param>
    /// <param name="projectedColumnAnalyzer">Native column-transition qualification.</param>
    /// <param name="renamedTableAnalyzer">Source-bound renamed owner evidence.</param>
    /// <param name="livePrerequisiteSource">Requested immutable live facts for sparse accepted operations.</param>
    public SafeMigrationPreflightProjection(
        ISafeMigrationProviderOperationProjection? providerOperationProjection = null,
        ISafeMigrationProjectedKeyAnalyzer? projectedKeyAnalyzer = null,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer = null,
        ISafeMigrationProjectedDependencyAnalyzer? projectedDependencyAnalyzer = null,
        ISafeMigrationIndexPrerequisiteSource? indexPrerequisiteSource = null,
        ISafeMigrationProjectedColumnAnalyzer? projectedColumnAnalyzer = null,
        ISafeMigrationRenamedTableAnalyzer? renamedTableAnalyzer = null,
        ISafeMigrationLivePrerequisiteSource? livePrerequisiteSource = null
    )
    {
        _providerOperationProjection = providerOperationProjection;
        _projectedKeyAnalyzer = projectedKeyAnalyzer;
        _objectIdentityNormalizer = objectIdentityNormalizer;
        _projectedDependencyAnalyzer = projectedDependencyAnalyzer;
        _indexPrerequisiteSource = indexPrerequisiteSource;
        _projectedColumnAnalyzer = projectedColumnAnalyzer;
        _renamedTableAnalyzer = renamedTableAnalyzer;
        _livePrerequisiteSource = livePrerequisiteSource;

        var tableComparer = new TableKeyComparer(objectIdentityNormalizer);
        var indexComparer = new IndexKeyComparer(objectIdentityNormalizer);

        _tables = new Dictionary<TableKey, ProjectedTable>(tableComparer);
        _renamedTableSources = new Dictionary<TableKey, TableKey>(tableComparer);
        _projectedForeignKeyOwners = new HashSet<TableKey>(tableComparer);
        _prerequisites = new Dictionary<TableKey, ProjectedPrerequisites>(tableComparer);
        _droppedPhysicalKeys = new HashSet<IndexKey>(indexComparer);
        _projectedDataMutationTables = new HashSet<TableKey>(tableComparer);
        _projectedModelManagedUniqueKeys = new Dictionary<TableKey, HashSet<string>>(tableComparer);
        _projectedColumnStates = new Dictionary<TableKey, Dictionary<string, ProjectedColumnState>>(tableComparer);
        _projectedCandidateKeyMutationTables = new HashSet<TableKey>(tableComparer);
        _projectedMissingTables = new HashSet<TableKey>(tableComparer);
        _projectedUnknownTableStructures = new HashSet<TableKey>(tableComparer);
        _projectedUnknownPhysicalKeys = new HashSet<TableKey>(tableComparer);
        _projectedStructurallyModifiedTables = new HashSet<TableKey>(tableComparer);
        _projectedChangedColumns = new Dictionary<TableKey, HashSet<string>>(tableComparer);
        _modelManagedRows = new Dictionary<ModelManagedRowKey, ProjectedModelManagedRow>(
            new ModelManagedRowKeyComparer(objectIdentityNormalizer));
    }

    /// <summary>Qualifies an immutable live observation against only preceding accepted physical evidence.</summary>
    /// <param name="operation">The current exact safe operation, not a future target-model proposal.</param>
    /// <param name="liveAnalysis">The independently captured initial provider observation.</param>
    /// <returns>The ordered observation, retaining independent unsupported and uncertainty boundaries.</returns>
    public SafeMigrationProviderAnalysis Project(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(liveAnalysis);

        if (_objectIdentityNormalizer?.IsObjectIdentityMismatch(liveAnalysis) == true)
        {
            // WHY: Provider-certified identity rejection remains authoritative
            // while its provider scope is stable. Mutable qualifier scopes are
            // certified by the provider rather than inferred by neutral projection.

            return liveAnalysis;
        }

        if (liveAnalysis.IsInvariantUnsupported)
        {
            // WHY: Opaque SQL may repair live state, but it cannot make an
            // unsupported operation contract or server version supported.
            return liveAnalysis;
        }

        if (_hasOpaqueSqlPostcondition)
        {
            // WHY: Provider analysis is captured before ordered operations run.
            // Raw SQL can invalidate even sequence-aware results computed from
            // the original stream. Safe renames may be opaque to generic
            // projection, but their provider-proven sequence results still hold.
            return StructureStateUnknown();
        }

        if (_deferredProviderOperationOriginOrdinal is { } deferredOrdinal)
        {
            if (liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported)
            {
                // WHY: Deferred safe DDL does not authorize permission or capability
                // changes. Keep an independently captured unsupported contract visible.
                return liveAnalysis;
            }

            // WHY: A deferred key, table or column has no proven postcondition.
            // Later operations may depend on it, but recording it as accepted
            // would invent a physical prerequisite. Preserve the runtime boundary
            // and its real origin instead of misclassifying the dependency as absent.
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationRepairCapability.None,
                false,
                "projected_provider_postcondition_unknown")
            {
                ProviderDeferredOriginOrdinal = deferredOrdinal,
            };
        }

        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported
            && operation.Intent is EnsureForeignKeyIntent foreignKey
            && _livePrerequisiteSource?.CanRequalifyLiveForeignKeyPrerequisites(foreignKey, liveAnalysis, this) == true)
        {
            // WHY: An exact accepted removal can discharge a dynamic native
            // capability blocker. It proves neither the reference key nor rows;
            // continue through the ordinary structural and row-proof gates.
            liveAnalysis = new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.PrerequisiteMissing,
                SafeMigrationRepairCapability.None,
                postconditionSatisfied: false,
                "projected_live_foreign_key_prerequisites_requalified");
        }

        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported
            && operation.Intent is DropIndexIntent dropIndex
            && _livePrerequisiteSource?.CanRequalifyLiveDropIndexPrerequisites(dropIndex, liveAnalysis, this) == true)
        {
            // WHY: Only certified removal of every affected physical dependency
            // discharges this native index-removal blocker. Presence is a metadata
            // fact; normal owner, tombstone and provider-risk checks still follow.
            liveAnalysis = new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None,
                postconditionSatisfied: false,
                "projected_live_drop_index_prerequisites_requalified");
        }

        if (_projectedDependencyAnalyzer?.ValidateCapturedProjection(operation, liveAnalysis, this) is
            { } capturedAnalysis)
        {
            // WHY: Executed DDL or DML may invoke triggers that recreate a dropped
            // owner or change unrelated dependencies. Neither an earlier tombstone
            // nor an immutable Matching result proves the post-trigger catalog.

            return capturedAnalysis;
        }

        // WHY: Accepted table removal invalidates every owned object, including
        // provider analyses that still describe the immutable pre-batch catalog.

        if (ProjectMissingTableOwnership(operation.Intent) is { } missingOwnerAnalysis)
        {
            return missingOwnerAnalysis;
        }

        if (_providerOperationProjection?.IsSequenceAwareAnalysis(operation, liveAnalysis) == true)
        {
            // WHY: A provider result that accounts for the ordered safe stream
            // remains authoritative despite generic structural uncertainty.
            return liveAnalysis;
        }

        var hasRenamedSourceProof = TryBindRenamedTableAnalysis(operation, out var renamedAnalysis);
        if (hasRenamedSourceProof)
        {
            liveAnalysis = renamedAnalysis!;
        }

        if (_hasOpaqueProviderPostcondition && !hasRenamedSourceProof)
        {
            // WHY: A provider can retain physical identities across a safe
            // rename even when neutral projection cannot. Only an explicit
            // ordered proof may discharge this uncertainty; opaque SQL and
            // invariant rejections have already taken precedence above.
            return _projectedDependencyAnalyzer?.ValidateOpaqueProviderPostcondition(operation, liveAnalysis, this)
                ?? StructureStateUnknown();
        }

        var projectedAnalysis = operation.Intent switch
        {
            EnsureSchemaIntent value => Project(value, liveAnalysis),
            DropSchemaIntent value => Project(value, liveAnalysis),
            EnsureTableIntent value => Project(value, liveAnalysis),
            DropTableIntent value => Project(value, liveAnalysis),
            RenameTableIntent value => Project(value, liveAnalysis),
            EnsureColumnIntent value => Project(value, liveAnalysis),
            AlterColumnIntent value => Project(value, liveAnalysis),
            DropColumnIntent value => Project(value, liveAnalysis),
            RenameColumnIntent value => Project(value, liveAnalysis),
            EnsureIndexIntent value => Project(value, liveAnalysis),
            DropIndexIntent value => Project(value, liveAnalysis),
            RenameIndexIntent value => Project(value, liveAnalysis),
            EnsurePrimaryKeyIntent value => Project(value, liveAnalysis),
            DropPrimaryKeyIntent value => Project(value, liveAnalysis),
            EnsureUniqueConstraintIntent value => Project(value, liveAnalysis),
            DropUniqueConstraintIntent value => Project(value, liveAnalysis),
            EnsureCheckConstraintIntent value => Project(value, liveAnalysis),
            DropCheckConstraintIntent value => Project(value, liveAnalysis),
            EnsureForeignKeyIntent value => Project(value, liveAnalysis),
            DropForeignKeyIntent value => Project(value, liveAnalysis),
            ModelManagedDataIntent value => Project(value, liveAnalysis),
            _ => liveAnalysis,
        };

        // WHY: Provider-neutral shape projection cannot prove provider-specific
        // dependency graphs. Recheck those rules after the accepted earlier
        // operations have changed the projected physical state.
        return _projectedDependencyAnalyzer?.ValidateProjectedOperation(operation, projectedAnalysis, this)
            ?? projectedAnalysis;
    }

    private SafeMigrationProviderAnalysis? ProjectMissingTableOwnership(
        SafeMigrationIntent intent
    )
    {
        if (_projectedMissingTables.Count == 0)
        {
            return null;
        }

        (TableKey Key, bool RequiresPresence)? owner = intent switch
        {
            EnsureColumnIntent value => (new TableKey(value.Table, value.Schema), true),
            AlterColumnIntent value => (new TableKey(value.Table, value.Schema), true),
            DropColumnIntent value => (new TableKey(value.Table, value.Schema), false),
            RenameColumnIntent value => (new TableKey(value.Table, value.Schema), false),
            EnsureIndexIntent value => (new TableKey(value.Definition.Table, value.Definition.Schema), true),
            DropIndexIntent value => (new TableKey(value.Table, value.Schema), false),
            RenameIndexIntent value => (new TableKey(value.Table, value.Schema), false),
            EnsurePrimaryKeyIntent value => (new TableKey(value.Definition.Table, value.Definition.Schema), true),
            DropPrimaryKeyIntent value => (new TableKey(value.Table, value.Schema), false),
            EnsureUniqueConstraintIntent value => (new TableKey(value.Definition.Table, value.Definition.Schema), true),
            DropUniqueConstraintIntent value => (new TableKey(value.Table, value.Schema), false),
            EnsureCheckConstraintIntent value => (new TableKey(value.Definition.Table, value.Definition.Schema), true),
            DropCheckConstraintIntent value => (new TableKey(value.Table, value.Schema), false),
            EnsureForeignKeyIntent value => (new TableKey(value.Definition.Table, value.Definition.Schema), true),
            DropForeignKeyIntent value => (new TableKey(value.Table, value.Schema), false),
            ModelManagedDataIntent value => (new TableKey(value.Table, value.Schema),
                value is not DeleteModelManagedDataIntent),
            DropTableIntent value => (new TableKey(value.Table, value.Schema), false),
            RenameTableIntent value => (new TableKey(value.Name, value.Schema), false),
            _ => null,
        };

        if (owner is { } ownedTable
            && _projectedMissingTables.Contains(ownedTable.Key))
        {
            return ownedTable.RequiresPresence
                ? TablePrerequisiteMissing()
                : Analysis(SafeMigrationObservedState.Missing);
        }

        return intent is EnsureForeignKeyIntent foreignKey
            && _projectedMissingTables.Contains(
                new TableKey(foreignKey.Definition.PrincipalTable, foreignKey.Definition.PrincipalSchema))
                ? TablePrerequisiteMissing()
                : null;
    }

    /// <summary>
    /// Records accepted physical effects without minting evidence from rejected or existence-only wishes.
    /// </summary>
    /// <param name="operation">The operation whose policy decision has just been made.</param>
    /// <param name="liveAnalysis">The immutable provider observation used to qualify its native source.</param>
    /// <param name="analysis">The ordered observation used for this decision.</param>
    /// <param name="decision">The actual policy outcome; only accepted effects may change later prerequisites.</param>
    public void Observe(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(liveAnalysis);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Action.RejectsExecution())
        {
            return;
        }

        if (decision.Action == SafeMigrationAction.NoOp
            && analysis.ObservedState == SafeMigrationObservedState.Different)
        {
            // WHY: ExistenceOnly accepts an existing, incompatible object
            // without changing it. Its authored target is not a physical
            // postcondition and must not overwrite any earlier exact evidence.
            // A projected Matching result remains admissible even if the
            // immutable live observation still describes the old definition.

            return;
        }

        var preservesForeignKeyColumnProofs = decision.Action == SafeMigrationAction.Repair
            && liveAnalysis.ObservedState == SafeMigrationObservedState.Different
            && analysis is
            {
                ObservedState: SafeMigrationObservedState.Different,
                RepairCapability: SafeMigrationRepairCapability.Safe,
                RepairMutatesData: false
            }
            && _projectedColumnAnalyzer is not null
            && (operation.Intent switch
            {
                AlterColumnIntent alter => CanReuseColumnProofAfterColumnPreservingChanges(
                    alter,
                    liveAnalysis,
                    new TableKey(alter.Table, alter.Schema),
                    allowUntouchedSource: true),
                EnsureColumnIntent ensure => analysis.RepairPreservesIntrinsicColumnLayout
                    || (!_projectedStructurallyModifiedTables.Contains(new TableKey(ensure.Table, ensure.Schema))
                        && CanReuseColumnProofAfterColumnPreservingChanges(
                            ensure,
                            liveAnalysis,
                            new TableKey(ensure.Table, ensure.Schema),
                            allowUntouchedSource: true)),
                _ => false,
            });

        var preservesIndexDropColumnProofs = decision.Action == SafeMigrationAction.Repair
            && analysis is
            {
                ObservedState: SafeMigrationObservedState.Different,
                RepairCapability: SafeMigrationRepairCapability.Safe,
                RepairMutatesData: false,
            }
            && operation.Intent is AlterColumnIntent indexDropAlter
            && CanReuseColumnProofAfterIndexDrops(indexDropAlter, liveAnalysis, allowUntouchedSource: true);

        _projectedDependencyAnalyzer?.ObserveAcceptedOperation(operation, liveAnalysis, analysis, decision);

        UpdateIndependentColumnDropLineage(operation.Intent, decision, preservesForeignKeyColumnProofs);

        if (decision.Action == SafeMigrationAction.Repair
            && analysis.RepairMutatesData)
        {
            ObserveProviderDataMutation();
        }

        // WHY: Preflight never mutates the database. Accepted operations instead
        // update this in-memory catalog so later operations observe prior ones.
        switch (operation.Intent)
        {
            case EnsureSchemaIntent value:
                Observe(value, decision);
                break;
            case DropSchemaIntent value:
                Observe(value, decision);
                break;
            case EnsureTableIntent value:
                Observe(value, liveAnalysis, analysis, decision, operation.Policy != SafeMigrationPolicy.ExistenceOnly);
                break;
            case DropTableIntent value:
                Observe(value, decision);
                break;
            case RenameTableIntent value:
                Observe(value, decision);
                break;
            case EnsureColumnIntent value:
                Observe(value, liveAnalysis, analysis, decision, preservesForeignKeyColumnProofs);
                break;
            case AlterColumnIntent value:
                Observe(value, analysis, decision, preservesForeignKeyColumnProofs, preservesIndexDropColumnProofs);
                break;
            case DropColumnIntent value:
                Observe(value, liveAnalysis, decision);
                break;
            case RenameColumnIntent value:
                Observe(value, decision);
                break;
            case EnsureIndexIntent value:
                Observe(value, liveAnalysis, decision);
                break;
            case DropIndexIntent value:
                Observe(value, decision);
                break;
            case RenameIndexIntent value:
                Observe(value, decision);
                break;
            case EnsurePrimaryKeyIntent value:
                Observe(value, liveAnalysis, analysis, decision);
                break;
            case DropPrimaryKeyIntent value:
                Observe(value, analysis, decision);
                break;
            case EnsureUniqueConstraintIntent value:
                Observe(value, liveAnalysis, decision);
                break;
            case DropUniqueConstraintIntent value:
                Observe(value, decision);
                break;
            case EnsureCheckConstraintIntent value:
                Observe(value, liveAnalysis, decision);
                break;
            case DropCheckConstraintIntent value:
                Observe(value, decision);
                break;
            case EnsureForeignKeyIntent value:
                Observe(value, SelectAcceptedForeignKeyIdentity(value, liveAnalysis, analysis), decision);
                break;
            case DropForeignKeyIntent value:
                Observe(value, decision);
                break;
            case ModelManagedDataIntent value:
                Observe(value, decision);
                break;
        }

        ObserveProjectedForeignKeyChange(operation.Intent, decision.Action);

        if ((decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair)
            && operation.Intent is not EnsureTableIntent
            && operation.Intent is not DropTableIntent)
        {
            MarkProjectedTableStructureChanged(
                operation.Intent,
                preservesForeignKeyColumnProofs,
                preservesNativeGeneratedSnapshot: operation.Intent is DropIndexIntent
                    or DropPrimaryKeyIntent
                    or DropUniqueConstraintIntent
                    or DropCheckConstraintIntent
                    or DropForeignKeyIntent
                && !operation
                    .GetAnnotations()
                    .Any(),
                preservesIndexDropColumnProofs: preservesIndexDropColumnProofs,
                preservesIndexDropSources: operation.Intent is DropIndexIntent indexDrop
                    && liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                    && !operation.GetAnnotations().Any()
                    && _projectedColumnAnalyzer?.PreservesColumnSourcesAfterIndexDrop(indexDrop, liveAnalysis) == true);
        }
    }

    /// <summary>Retains uncertainty, not an accepted postcondition, after provider-specific deferred DDL.</summary>
    /// <param name="operationOrdinal">The complete-stream ordinal of the deferred structural operation.</param>
    internal void ObserveDeferredProviderOperation(
        int operationOrdinal
    ) => _deferredProviderOperationOriginOrdinal ??= operationOrdinal;

    /// <summary>
    /// Conservatively updates projected evidence after a non-safe operation.
    /// Provider-specific preservation can retain existing evidence; otherwise
    /// recognized postconditions are projected and uncertain facts invalidated.
    /// </summary>
    /// <param name="operation">The non-safe operation that precedes later safe operations.</param>
    public void ObserveProviderPostcondition(
        MigrationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        _projectedDependencyAnalyzer?.ObserveProviderOperation(operation);

        if (operation is AlterDatabaseOperation)
        {
            // WHY: A provider may preserve existing tables while changing the
            // default inherited by tables created later in this same stream.
            _creationCharacterSetChanged = true;
        }

        if (_providerOperationProjection?.PreservesExistingTableState(operation) == true)
        {
            // WHY: The provider owns whether this ordinary operation preserves
            // existing table-scoped evidence. Other operations continue through
            // conservative postcondition projection or invalidation.

            return;
        }

        // WHY: Ordinary DDL has no accepted safe-DROP lineage certificate.
        _certifiedColumnDropOrigins?.Clear();
        _projectedIndexDropOnlyTables?.Clear();

        switch (operation)
        {
            case CreateTableOperation value:
                ObserveProviderPostcondition(value);
                break;
            case AddColumnOperation value:
                ObserveProviderPostcondition(value);
                break;
            case AlterColumnOperation value:
                ObserveProviderPostcondition(value);
                break;
            case AlterTableOperation value:
                ObserveProviderPostcondition(value);
                break;
            case DropColumnOperation value:
                ObserveProviderPostcondition(value);
                break;
            case RenameColumnOperation value:
                ObserveProviderPostcondition(value);
                break;
            case DropIndexOperation value:
                ObserveProviderPostcondition(value);
                break;
            case DropCheckConstraintOperation value:
                ObserveProviderPostcondition(value);
                break;
            case DropTableOperation value:
                ObserveProviderPostcondition(value);
                break;
            case RenameTableOperation value:
                ObserveProviderPostcondition(value);
                break;
            case InsertDataOperation:
            case UpdateDataOperation:
            case DeleteDataOperation:
                ObserveProviderDataMutation();
                break;
            default:
                // WHY: An unrecognized provider operation may contain arbitrary DDL
                // or data changes. Invalidate live row-level proofs as well as
                // inferred structure so an opaque SQL operation cannot make a
                // later additive constraint appear safe.
                SetOpaqueProviderPostcondition(mayMutateData: true);
                _hasOpaqueSqlPostcondition |= operation is SqlOperation;
                break;
        }

        ObserveProjectedForeignKeyChange(operation);
        MarkProjectedTableStructureChanged(operation);
    }

    private static SafeMigrationProviderAnalysis TablePrerequisiteMissing() => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "projected_prerequisite_missing");

    private static SafeMigrationProviderAnalysis DataStateUnknown() => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "projected_data_state_unknown");

    private static SafeMigrationProviderAnalysis StructureStateUnknown() => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "projected_structure_state_unknown")
    {
        IsOpaqueProjectionUnknown = true,
    };

    private static SafeMigrationProviderAnalysis AnalyzeDefinition<T>(
        IReadOnlyDictionary<string, T> definitions,
        string name,
        T expected,
        Func<T, T, bool> equals,
        SafeMigrationRepairCapability repairCapability = SafeMigrationRepairCapability.None
    )
        where T : class
    {
        if (!definitions.TryGetValue(name, out var actual))
        {
            return Analysis(SafeMigrationObservedState.Missing, repairCapability);
        }

        return Analysis(
            equals(actual, expected) ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            repairCapability);
    }

    private static SafeMigrationProviderAnalysis AnalyzeOptional<T>(
        T? actual,
        T expected,
        Func<T, T, bool> equals
    )
        where T : class
    {
        if (actual is null)
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        return Analysis(
            equals(actual, expected) ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different);
    }

    private static SafeMigrationProviderAnalysis Analysis(
        SafeMigrationObservedState state,
        SafeMigrationRepairCapability repairCapability = SafeMigrationRepairCapability.None
    ) => new(state, repairCapability, state == SafeMigrationObservedState.Matching, StateCode(state));

    private static string StateCode(
        SafeMigrationObservedState state
    ) => state switch
    {
        SafeMigrationObservedState.Missing => "projected_missing",
        SafeMigrationObservedState.Matching => "projected_matching",
        SafeMigrationObservedState.Different => "projected_different",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
