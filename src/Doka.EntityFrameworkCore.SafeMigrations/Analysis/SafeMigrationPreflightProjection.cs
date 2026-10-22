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
    private readonly Dictionary<TableKey, HashSet<string>> _projectedChangedColumns;
    private bool _hasOpaqueProviderPostcondition;
    private bool _hasOpaqueSqlPostcondition;
    private bool _creationCharacterSetChanged;
    private long _providerDataMutationVersion;
    private int? _deferredProviderOperationOriginOrdinal;

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
                Observe(value, analysis, decision, preservesForeignKeyColumnProofs);
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
                    .Any());
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

    private bool Contains(
        string table,
        string? schema
    ) => _tables.ContainsKey(new TableKey(table, schema));

    private bool TryGet(
        string table,
        string? schema,
        [NotNullWhen(true)] out ProjectedTable? projection
    ) => _tables.TryGetValue(new TableKey(table, schema), out projection);

    private bool TryGetProjectedColumnDefinition(
        string table,
        string? schema,
        string column,
        [NotNullWhen(true)] out ExpectedColumnDefinition? definition
    )
    {
        definition = TryGetProjectedColumnState(table, schema, column, out var state) ? state.Definition : null;

        return definition is not null;
    }

    private bool TryGetProjectedColumnState(
        string table,
        string? schema,
        string column,
        out ProjectedColumnState state
    )
    {
        if (_projectedColumnStates.TryGetValue(new TableKey(table, schema), out var columns))
        {
            return columns.TryGetValue(column, out state);
        }

        state = default;

        return false;
    }

    private Dictionary<string, ProjectedColumnState> GetOrCreateProjectedColumnStates(
        string table,
        string? schema
    )
    {
        var key = new TableKey(table, schema);
        if (!_projectedColumnStates.TryGetValue(key, out var columns))
        {
            columns = new Dictionary<string, ProjectedColumnState>(
                new IdentifierComparer(_objectIdentityNormalizer, _objectIdentityNormalizer?.ColumnIdentifierComparer));
            _projectedColumnStates.Add(key, columns);
        }

        return columns;
    }

    bool ISafeMigrationProjectedColumnSource.TryGetProjectedColumn(
        string table,
        string? schema,
        string column,
        [NotNullWhen(true)] out ExpectedColumnDefinition? definition
    ) => TryGetProjectedColumnDefinition(table, schema, column, out definition);

    bool? ISafeMigrationProjectedTableSource.HasProjectedGeneratedColumnDependency(
        string table,
        string? schema,
        string column,
        IEqualityComparer<string> columnComparer
    )
    {
        if (_hasOpaqueSqlPostcondition || _hasOpaqueProviderPostcondition)
        {
            return null;
        }

        var key = new TableKey(table, schema);
        var comparable = true;
        if (_projectedColumnStates.TryGetValue(key, out var acceptedColumns))
        {
            foreach (var state in acceptedColumns.Values)
            {
                if (state.Definition is null)
                {
                    comparable &= state.IsMissing;

                    continue;
                }

                if (state.Definition.ComputedColumnSql is not null)
                {
                    comparable = false;
                }

                if (state.Definition.ComputedExpression is { } captured
                    && !SafeMigrationSqlExpressionInspector.IsStructurallyComparable(captured))
                {
                    comparable = false;
                }

                if (state.Definition?.ComputedExpression is { } expression
                    && SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression)
                    && SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
                        expression,
                        column,
                        columnComparer))
                {
                    // A current accepted generated member proves a positive
                    // dependency even when the rest of its owner is sparse.
                    return true;
                }
            }
        }

        if (_prerequisites.TryGetValue(key, out var prerequisites)
            && prerequisites.GeneratedColumnsComplete)
        {
            // WHY: Known member removals invalidate physical keys and layout,
            // but not this separately certified complete generated-member set.
            // No creation, emptiness or row proof follows from this bit.
            return comparable ? false : null;
        }

        if (IsProjectedTableStructureUnknown(table, schema)
            || !_tables.TryGetValue(key, out var owner))
        {
            return null;
        }

        foreach (var definition in owner.Columns.Values)
        {
            if (definition.ComputedColumnSql is not null)
            {
                comparable = false;
            }

            if (definition.ComputedExpression is not { } expression)
            {
                continue;
            }

            if (!SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression))
            {
                comparable = false;

                continue;
            }

            if (SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(expression, column, columnComparer))
            {
                return true;
            }
        }

        // WHY: Sparse accepted columns cannot prove that another generated
        // member is absent. Only this complete, current owner view establishes
        // nondependency; opaque nodes never become lexical absence evidence.
        return comparable ? false : null;
    }

    bool ISafeMigrationProjectedTableSource.TryGetProjectedTableState(
        string table,
        string? schema,
        out SafeMigrationProjectedTableState state
    )
    {
        var key = new TableKey(table, schema);
        var hasPrerequisites = _prerequisites.TryGetValue(key, out var prerequisites);

        if (!hasPrerequisites
            && !_tables.ContainsKey(key))
        {
            state = default;

            return false;
        }

        // WHY: A created table is empty only until an accepted data mutation.
        // Providers must consume both facts rather than infer row safety from
        // the creation marker alone.
        state = new SafeMigrationProjectedTableState(
            prerequisites?.NewlyCreated == true,
            HasUnanalyzedDataChanges(table, schema),
            prerequisites?.PrimaryKeyWasDropped == true,
            IsProjectedTableStructureUnknown(table, schema))
        {
            ColumnDropOriginOrdinal = _certifiedColumnDropOrigins?.TryGetValue(key, out var origin) == true
                ? origin
                : null,
        };

        return true;
    }

    private void SetProjectedColumnDefinition(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        bool preservesLiveValueDomain = false,
        int valueMutationOriginOrdinal = -1
    )
    {
        definition = SafeMigrationCapturedExpressionNormalization.NormalizeColumn(
            definition,
            _objectIdentityNormalizer?.FoldUnquotedCapturedSqlIdentifiers == true);

        GetOrCreateProjectedColumnStates(table, schema)[definition.Name] = new ProjectedColumnState(
            definition,
            IsMissing: false,
            preservesLiveValueDomain,
            valueMutationOriginOrdinal);
    }

    private SafeMigrationProviderAnalysis SelectProjectedKeyEnvironment(
        string table,
        string? schema,
        SafeMigrationProviderAnalysis analysis
    ) => _prerequisites.TryGetValue(new TableKey(table, schema), out var prerequisites)
        && prerequisites.NewlyCreated
        && analysis.IndexPhysicalEnvironment?.NewTableEnvironment is { } creationEnvironment
            ? analysis.WithIndexPhysicalEnvironment(creationEnvironment)
            : analysis;

    private void SetProjectedColumnMissing(
        string table,
        string? schema,
        string column
    ) => GetOrCreateProjectedColumnStates(table, schema)[column] =
        new ProjectedColumnState(Definition: null, IsMissing: true);

    private void SetProjectedColumnUnknown(
        string table,
        string? schema,
        string column
    ) => GetOrCreateProjectedColumnStates(table, schema)[column] =
        new ProjectedColumnState(Definition: null, IsMissing: false);

    private bool IsProjectedColumnMissing(
        string table,
        string? schema,
        string column
    ) => TryGetProjectedColumnState(table, schema, column, out var state) && state.IsMissing;

    private bool IsProjectedColumnUnknown(
        string table,
        string? schema,
        string column
    ) => TryGetProjectedColumnState(table, schema, column, out var state)
        && state.Definition is null
        && !state.IsMissing;

    private bool IsProjectedTableStructureUnknown(
        string table,
        string? schema
    ) => _projectedUnknownTableStructures.Contains(new TableKey(table, schema));

    private void SetProjectedTableStructureUnknown(
        string table,
        string? schema,
        bool preservesGeneratedColumns = false
    )
    {
        var key = new TableKey(table, schema);

        _tables.Remove(key);
        _projectedMissingTables.Remove(key);
        _projectedUnknownTableStructures.Add(key);
        if (_prerequisites.TryGetValue(key, out var prerequisites))
        {
            prerequisites.CheckDefinitionsFollowKnownRename = false;
            if (!preservesGeneratedColumns)
            {
                prerequisites.GeneratedColumnsComplete = false;
            }
        }
    }

    /// <summary>Invalidates live structure evidence while retaining bounded removal-only histories.</summary>
    /// <param name="table">The physical table whose accepted structure changed.</param>
    /// <param name="schema">The normalized schema identity.</param>
    /// <param name="safeColumnDrop">Whether this accepted mutation is a safe column drop, not an opaque change.</param>
    /// <param name="safeForeignKeyDrop">Whether this accepted mutation removes only a safe foreign key.</param>
    /// <param name="preservesForeignKeyColumnProofs">Whether certified nullability retains sibling proofs.</param>
    /// <param name="preservesIntrinsicColumnSources">Whether native intrinsic sources remain unchanged.</param>
    /// <param name="preservesRenamedCheckDefinitions">Whether this rename just rebound accepted CHECK shapes.</param>
    /// <param name="preservesNativeGeneratedSnapshot">Whether only a guarded index or constraint was removed.</param>
    private void MarkProjectedTableStructureChanged(
        string table,
        string? schema,
        bool safeColumnDrop = false,
        bool safeForeignKeyDrop = false,
        bool preservesForeignKeyColumnProofs = false,
        bool preservesIntrinsicColumnSources = false,
        bool preservesRenamedCheckDefinitions = false,
        bool preservesNativeGeneratedSnapshot = false
    )
    {
        var key = new TableKey(table, schema);
        if (!preservesRenamedCheckDefinitions
            && _prerequisites.TryGetValue(key, out var prerequisites))
        {
            prerequisites.CheckDefinitionsFollowKnownRename = false;
        }

        var firstMutation = _projectedStructurallyModifiedTables.Add(key);
        if (_prerequisites.TryGetValue(key, out var generatedOwner))
        {
            // WHY: Guarded index and named-constraint removal cannot add or
            // rewrite generated members, but still invalidates independent
            // physical layout and key evidence. No later removal may revive
            // this narrow prefix after another structural or opaque effect.
            generatedOwner.NativeGeneratedSnapshotPreservedByIndexOrConstraintRemoval = preservesNativeGeneratedSnapshot
                && (firstMutation || generatedOwner.NativeGeneratedSnapshotPreservedByIndexOrConstraintRemoval);
        }

        if (safeColumnDrop && (firstMutation || _projectedColumnDropOnlyTables?.Contains(key) == true))
        {
            // WHY: SQLite can certify an unchanged column through a validated
            // rebuild after sibling drops, but no other mutation shares that proof.
            _projectedColumnDropOnlyTables ??= new HashSet<TableKey>(_projectedStructurallyModifiedTables.Comparer);
            _projectedColumnDropOnlyTables.Add(key);
        }
        else
        {
            // WHY: SQLite's drop-only rebuild certificate expires on any other
            // structural mutation, even a nullability-only repair certified by
            // the separate FK/intrinsic column-source contracts.
            _projectedColumnDropOnlyTables?.Remove(key);
        }

        if (safeForeignKeyDrop && (firstMutation || _projectedForeignKeyDropOnlyTables?.Contains(key) == true))
        {
            // WHY: FK removal changes table structure without changing an exact
            // column's storage or values. Only provider opt-in may reuse that proof.
            _projectedForeignKeyDropOnlyTables ??= new HashSet<TableKey>(_projectedStructurallyModifiedTables.Comparer);
            _projectedForeignKeyDropOnlyTables.Add(key);
        }
        else if (!preservesForeignKeyColumnProofs)
        {
            _projectedForeignKeyDropOnlyTables?.Remove(key);
        }

        if (preservesIntrinsicColumnSources
            && (firstMutation || _projectedIntrinsicColumnSourceTables?.Contains(key) == true
                || (safeColumnDrop && _certifiedColumnDropOrigins?.ContainsKey(key) == true)))
        {
            // WHY: Removing a PRIMARY or creating an ordinary index can preserve
            // column storage while invalidating candidate-key proofs. Keep those
            // lifetimes separate, and never revive a source after opaque mutation.
            _projectedIntrinsicColumnSourceTables ??= new HashSet<TableKey>(
                _projectedStructurallyModifiedTables.Comparer);

            _projectedIntrinsicColumnSourceTables.Add(key);
        }
        else if (!preservesForeignKeyColumnProofs)
        {
            _projectedIntrinsicColumnSourceTables?.Remove(key);
        }
    }

    private void MarkProjectedColumnChanged(
        string table,
        string? schema,
        string column
    )
    {
        var key = new TableKey(table, schema);
        if (!_projectedChangedColumns.TryGetValue(key, out var columns))
        {
            columns = new HashSet<string>(
                new IdentifierComparer(_objectIdentityNormalizer, _objectIdentityNormalizer?.ColumnIdentifierComparer));
            _projectedChangedColumns.Add(key, columns);
        }

        columns.Add(column);
    }

    private bool HasProjectedStrictTableDifference(
        ExpectedTableDefinition definition
    )
    {
        var key = new TableKey(definition.Table, definition.Schema);
        if (!_projectedChangedColumns.TryGetValue(key, out var changedColumns))
        {
            return false;
        }

        var expectedChangedColumnCount = 0;
        foreach (var expectedColumn in definition.Columns)
        {
            if (!changedColumns.Contains(expectedColumn.Name))
            {
                continue;
            }

            expectedChangedColumnCount++;
            if (!TryGetProjectedColumnDefinition(
                    definition.Table,
                    definition.Schema,
                    expectedColumn.Name,
                    out var projectedColumn)
                || !SafeMigrationDefinitionEquivalence.Column(projectedColumn, expectedColumn))
            {
                return true;
            }
        }

        // WHY: An added or repaired column outside the strict definition remains
        // an observable extra column. This proves drift without rescanning every
        // projected column as large migrations accumulate operations.

        return expectedChangedColumnCount != changedColumns.Count;
    }

    private void MarkProjectedTableStructureChanged(
        SafeMigrationIntent intent,
        bool preservesForeignKeyColumnProofs = false,
        bool preservesNativeGeneratedSnapshot = false
    )
    {
        var preservesIntrinsicColumnSources = _projectedColumnAnalyzer?.PreservesIntrinsicColumnSources(intent) == true;

        switch (intent)
        {
            case RenameTableIntent value:
                MarkProjectedTableStructureChanged(value.Name, value.Schema);
                MarkProjectedTableStructureChanged(value.NewName ?? value.Name, value.NewSchema ?? value.Schema);
                break;
            case EnsureColumnIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesForeignKeyColumnProofs: preservesForeignKeyColumnProofs,
                    preservesIntrinsicColumnSources: preservesForeignKeyColumnProofs);
                break;
            case AlterColumnIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesForeignKeyColumnProofs: preservesForeignKeyColumnProofs,
                    preservesIntrinsicColumnSources: preservesForeignKeyColumnProofs);
                break;
            case DropColumnIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema, safeColumnDrop: true,
                    preservesIntrinsicColumnSources: preservesIntrinsicColumnSources);
                break;
            case RenameColumnIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema, preservesRenamedCheckDefinitions: true);
                break;
            case EnsureIndexIntent value:
                MarkProjectedTableStructureChanged(
                    value.Definition.Table,
                    value.Definition.Schema,
                    preservesIntrinsicColumnSources: preservesIntrinsicColumnSources);
                break;
            case DropIndexIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
            case RenameIndexIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case EnsurePrimaryKeyIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropPrimaryKeyIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesIntrinsicColumnSources: preservesIntrinsicColumnSources,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
            case EnsureUniqueConstraintIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropUniqueConstraintIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
            case EnsureCheckConstraintIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropCheckConstraintIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
            case EnsureForeignKeyIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropForeignKeyIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    safeForeignKeyDrop: true,
                    preservesIntrinsicColumnSources: preservesIntrinsicColumnSources,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
        }
    }

    private void MarkProjectedTableStructureChanged(
        MigrationOperation operation
    )
    {
        switch (operation)
        {
            case AddColumnOperation value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case AlterColumnOperation value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case AlterTableOperation value:
                MarkProjectedTableStructureChanged(value.Name, value.Schema);
                break;
            case DropColumnOperation value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case RenameColumnOperation value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case DropIndexOperation { Table: not null } value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case DropCheckConstraintOperation { Table: not null } value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case RenameTableOperation value:
                MarkProjectedTableStructureChanged(value.Name, value.Schema);
                MarkProjectedTableStructureChanged(value.NewName ?? value.Name, value.NewSchema ?? value.Schema);
                break;
        }
    }

    private void RemoveProjectedColumnDefinition(
        string table,
        string? schema,
        string column
    )
    {
        var key = new TableKey(table, schema);
        if (_projectedColumnStates.TryGetValue(key, out var columns))
        {
            columns.Remove(column);
            if (columns.Count == 0)
            {
                _projectedColumnStates.Remove(key);
            }
        }
    }

    private void RemoveProjectedColumnDefinitions(
        string table,
        string? schema
    ) => _projectedColumnStates.Remove(new TableKey(table, schema));

    private void RenameProjectedColumnDefinition(
        string table,
        string? schema,
        string source,
        string target
    )
    {
        TryGetProjectedColumnDefinition(table, schema, source, out var definition);
        RemoveProjectedColumnDefinition(table, schema, source);
        RemoveProjectedColumnDefinition(table, schema, target);

        if (definition is not null)
        {
            SetProjectedColumnDefinition(table, schema, CopyProjectedColumn(definition, target));
        }
        else
        {
            SetProjectedColumnUnknown(table, schema, target);
        }

        SetProjectedColumnMissing(table, schema, source);
    }

    private void RenameProjectedTableColumnDefinitions(
        string table,
        string? schema,
        string targetTable,
        string? targetSchema
    )
    {
        _projectedColumnStates.Remove(new TableKey(table, schema), out var states);

        var target = new TableKey(targetTable, targetSchema);

        _projectedColumnStates.Remove(target);
        if (states is not null)
        {
            _projectedColumnStates[target] = states;
        }
    }

    private bool SameSchema(
        string? left,
        string? right
    ) => StringComparer.Ordinal.Equals(
        NormalizeSchema(_objectIdentityNormalizer, left),
        NormalizeSchema(_objectIdentityNormalizer, right));

    private static string? NormalizeSchema(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string? schema
    ) => normalizer is null ? schema : normalizer.NormalizeSchema(schema);

    private static bool IdentifierEquals(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string left,
        string right
    ) => normalizer is null
        ? StringComparer.Ordinal.Equals(left, right)
        : normalizer.IdentifierComparer.Equals(left, right);

    private static int IdentifierHashCode(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string identifier
    ) => normalizer is null
        ? StringComparer.Ordinal.GetHashCode(identifier)
        : normalizer.IdentifierComparer.GetHashCode(identifier);

    /// <summary>Copies every immutable column facet while changing only its certified physical member name.</summary>
    internal static ExpectedColumnDefinition CopyProjectedColumn(
        ExpectedColumnDefinition value,
        string name
    ) => new(
        name,
        value.ClrType,
        value.IsNullable,
        value.StoreType,
        value.IsUnicode,
        value.MaxLength,
        value.IsFixedLength,
        value.IsRowVersion,
        value.Precision,
        value.Scale,
        value.Collation,
        value.Comment,
        value.DefaultValue,
        value.ComputedColumnSql,
        value.IsStored,
        value.ComputedExpression)
    {
        ProviderAnnotations = value.ProviderAnnotations,
        CapturedComputedSql = value.CapturedComputedSql,
    };

    private bool HasUnanalyzedDataChanges(
        string table,
        string? schema
    )
    {
        var key = new TableKey(table, schema);

        return _projectedDataMutationTables.Contains(key)
            || (_tables.TryGetValue(key, out var projectedTable)
                && projectedTable.DataMutationVersion < _providerDataMutationVersion)
            || (_prerequisites.TryGetValue(key, out var prerequisites)
                && prerequisites.DataMutationVersion < _providerDataMutationVersion)
            || (!_tables.ContainsKey(key) && !_prerequisites.ContainsKey(key) && _providerDataMutationVersion > 0);
    }

    private bool HasProjectedModelManagedUniqueKey(
        ExpectedIndexDefinition definition
    )
    {
        if (definition.NullsDistinct == false
            || definition.Keys.Any(static key => key.Column is null))
        {
            return false;
        }

        var table = new TableKey(definition.Table, definition.Schema);
        if (!_projectedModelManagedUniqueKeys.TryGetValue(table, out var uniqueKeys))
        {
            return false;
        }

        var columns = definition
            .Keys
            .Select(static key => key.Column!)
            .ToArray();

        return uniqueKeys.Contains(ModelManagedUniqueKeyFingerprint(columns));
    }

    private string ModelManagedUniqueKeyFingerprint(
        IReadOnlyList<string> columns
    )
    {
        using var writer = new CanonicalHashWriter();
        var canonicalColumns = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(
            columns,
            _objectIdentityNormalizer,
            out var ordinals);

        writer.Add(canonicalColumns.Count);
        for (var column = 0; column < canonicalColumns.Count; column++)
        {
            writer.Add(canonicalColumns[ordinals?[column] ?? column]);
        }

        return writer.GetHash();
    }

    private SafeMigrationProviderAnalysis InvalidateDataDependentMissing(
        string table,
        string? schema,
        SafeMigrationProviderAnalysis analysis
    ) => HasUnanalyzedDataChanges(table, schema)
        && analysis.ObservedState is SafeMigrationObservedState.Missing
            or SafeMigrationObservedState.PrerequisiteMissing
            ? DataStateUnknown()
            : analysis;

    private SafeMigrationProviderAnalysis InvalidateStaleLiveDataProof(
        string table,
        string? schema,
        SafeMigrationProviderAnalysis analysis
    ) => analysis.RequiresLiveDataProof && HasUnanalyzedDataChanges(table, schema) ? DataStateUnknown() : analysis;

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

    private readonly record struct TableKey(
        string Table,
        string? Schema
    );

    private readonly record struct IndexKey(
        string Table,
        string? Schema,
        string Name
    );

    private sealed class TableKeyComparer(ISafeMigrationProviderObjectIdentityNormalizer? normalizer)
        : IEqualityComparer<TableKey>
    {
        public bool Equals(
            TableKey left,
            TableKey right
        ) => IdentifierEquals(normalizer, left.Table, right.Table)
            && StringComparer.Ordinal.Equals(Normalize(left.Schema), Normalize(right.Schema));

        public int GetHashCode(
            TableKey value
        ) => HashCode.Combine(
            IdentifierHashCode(normalizer, value.Table),
            Normalize(value.Schema) is { } schema ? StringComparer.Ordinal.GetHashCode(schema) : 0);

        private string? Normalize(
            string? schema
        ) => NormalizeSchema(normalizer, schema);
    }

    private sealed class IndexKeyComparer(ISafeMigrationProviderObjectIdentityNormalizer? normalizer)
        : IEqualityComparer<IndexKey>
    {
        private readonly TableKeyComparer _tableComparer = new(normalizer);
        private readonly StringComparer _nameComparer = normalizer?.IndexIdentifierComparer ?? StringComparer.Ordinal;

        public bool Equals(
            IndexKey left,
            IndexKey right
        ) => _tableComparer.Equals(new TableKey(left.Table, left.Schema), new TableKey(right.Table, right.Schema))
            && _nameComparer.Equals(left.Name, right.Name);

        public int GetHashCode(
            IndexKey value
        ) => HashCode.Combine(
            _tableComparer.GetHashCode(new TableKey(value.Table, value.Schema)),
            _nameComparer.GetHashCode(value.Name));
    }

    private sealed class IdentifierComparer(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        StringComparer? memberComparer = null
    ) : IEqualityComparer<string>
    {
        private readonly StringComparer _comparer = memberComparer
            ?? normalizer?.IdentifierComparer ?? StringComparer.Ordinal;

        public bool Equals(
            string? left,
            string? right
        ) => ReferenceEquals(left, right) || (left is not null && right is not null && _comparer.Equals(left, right));

        public int GetHashCode(
            string value
        ) => _comparer.GetHashCode(value);
    }

    private readonly record struct ProjectedColumnState(
        ExpectedColumnDefinition? Definition,
        bool IsMissing,
        bool PreservesLiveValueDomain = false,
        int ValueMutationOriginOrdinal = -1
    );

    private sealed class ProjectedPrerequisites(
        bool newlyCreated,
        long dataMutationVersion,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer,
        bool sharesUniqueIndexIdentity
    )
    {
        private readonly ISafeMigrationProviderObjectIdentityNormalizer? _objectIdentityNormalizer =
            objectIdentityNormalizer;

        public ProjectedDefinitionSet<ExpectedCheckConstraintDefinition> CheckConstraints { get; } = new(
            SafeMigrationSemanticDefinitionComparers.CheckConstraint,
            objectIdentityNormalizer);

        public Dictionary<string, ProjectedColumn> Columns { get; } = new(
            new IdentifierComparer(objectIdentityNormalizer, objectIdentityNormalizer?.ColumnIdentifierComparer));

        public long DataMutationVersion { get; } = dataMutationVersion;

        public long ModelManagedDataMutationVersion { get; set; } = dataMutationVersion;

        public long? EmptyTableProofVersion { get; set; }

        /// <summary>Gets whether a certified rename retained accepted CHECK shapes through unknown structure.</summary>
        public bool CheckDefinitionsFollowKnownRename { get; set; }

        /// <summary>Gets whether current states exhaust generated members, not physical layout or rows.</summary>
        public bool GeneratedColumnsComplete { get; set; }

        /// <summary>Gets whether only guarded index or constraint removals precede the native snapshot.</summary>
        public bool NativeGeneratedSnapshotPreservedByIndexOrConstraintRemoval { get; set; }

        public ProjectedDefinitionSet<ExpectedForeignKeyDefinition> ForeignKeys { get; } = new(
            SafeMigrationSemanticDefinitionComparers.ForeignKey,
            objectIdentityNormalizer,
            objectIdentityNormalizer?.ForeignKeyIdentifierComparer);

        public ProjectedDefinitionSet<ExpectedIndexDefinition> Indexes { get; } = new(
            SafeMigrationSemanticDefinitionComparers.IndexForColumns(
                objectIdentityNormalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal),
            objectIdentityNormalizer,
            objectIdentityNormalizer?.IndexIdentifierComparer);

        public bool NewlyCreated { get; } = newlyCreated;

        public ExpectedPrimaryKeyDefinition? PrimaryKey { get; private set; }

        /// <summary>Gets the separately proven physical PK name, or null when only semantic shape is known.</summary>
        public string? PrimaryKeyPhysicalName { get; private set; }

        public bool PrimaryKeyWasDropped { get; private set; }

        /// <summary>Gets whether accepted Apply established the current PK's physical lifetime.</summary>
        public bool PrimaryKeyCreatedByProjection { get; private set; }

        public ExpectedPrimaryKeyDefinition? RemovedPrimaryKey { get; private set; }

        public ProjectedDefinitionSet<ExpectedUniqueConstraintDefinition> UniqueConstraints { get; } = new(
            SafeMigrationSemanticDefinitionComparers.UniqueForColumns(
                objectIdentityNormalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal),
            objectIdentityNormalizer,
            sharesUniqueIndexIdentity ? objectIdentityNormalizer?.IndexIdentifierComparer : null);

        /// <summary>Accepts key shape with separate authored-alias and physical-identity proof.</summary>
        /// <param name="definition">The accepted key shape.</param>
        /// <param name="physicalName">The separately proven physical name, when available.</param>
        /// <param name="createdByProjection">Whether an accepted Apply created this exact physical lifetime.</param>
        public void AcceptPrimaryKey(
            ExpectedPrimaryKeyDefinition definition,
            string? physicalName,
            bool createdByProjection = false
        )
        {
            PrimaryKeyCreatedByProjection = createdByProjection
                || (PrimaryKeyCreatedByProjection
                    && PrimaryKey is not null
                    && SameColumns(PrimaryKey.Columns, definition.Columns, _objectIdentityNormalizer)
                    && StringComparer.Ordinal.Equals(PrimaryKeyPhysicalName, physicalName));
            PrimaryKey = definition;
            PrimaryKeyPhysicalName = physicalName;
            PrimaryKeyWasDropped = false;
            RemovedPrimaryKey = null;
        }

        public void DropPrimaryKey()
        {
            RemovedPrimaryKey = PrimaryKey;
            PrimaryKey = null;
            PrimaryKeyPhysicalName = null;
            PrimaryKeyWasDropped = true;
            PrimaryKeyCreatedByProjection = false;
        }

        /// <summary>Rebinds accepted key owners while preserving physical names and semantic aliases.</summary>
        public void RenameKeys(
            string table,
            string? schema
        )
        {
            PrimaryKey = PrimaryKey is null
                ? null
                : new ExpectedPrimaryKeyDefinition(PrimaryKey.Name, table, PrimaryKey.Columns, schema);
            RemovedPrimaryKey = RemovedPrimaryKey is null
                ? null
                : new ExpectedPrimaryKeyDefinition(RemovedPrimaryKey.Name, table, RemovedPrimaryKey.Columns, schema);
            UniqueConstraints.Rebind(value => new ExpectedUniqueConstraintDefinition(
                value.Name,
                table,
                value.Columns,
                schema));
            Indexes.Rebind(value => new ExpectedIndexDefinition(
                value.Name,
                table,
                value.Keys,
                schema,
                value.Unique,
                value.Filter,
                value.IncludedColumns,
                value.Method,
                value.NullsDistinct,
                value.StructuredFilter));
        }

        public bool HasCandidateKey(
            string table,
            string? schema,
            IReadOnlyList<string> columns
        )
        {
            if (PrimaryKey is not null
                && IdentifierEquals(_objectIdentityNormalizer, PrimaryKey.Table, table)
                && StringComparer.Ordinal.Equals(
                    NormalizeSchema(_objectIdentityNormalizer, PrimaryKey.Schema),
                    NormalizeSchema(_objectIdentityNormalizer, schema))
                && SameColumns(PrimaryKey.Columns, columns, _objectIdentityNormalizer))
            {
                return true;
            }

            return UniqueConstraints.Any(constraint =>
                IdentifierEquals(_objectIdentityNormalizer, constraint.Table, table)
                && StringComparer.Ordinal.Equals(
                    NormalizeSchema(_objectIdentityNormalizer, constraint.Schema),
                    NormalizeSchema(_objectIdentityNormalizer, schema))
                && SameColumns(constraint.Columns, columns, _objectIdentityNormalizer));
        }
    }

    private sealed class ProjectedDefinitionSet<T>(
        IEqualityComparer<T> semanticComparer,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer,
        StringComparer? memberComparer = null
    )
        where T : class
    {
        private readonly IdentifierComparer _identifierComparer = new(objectIdentityNormalizer, memberComparer);

        private readonly Dictionary<string, PhysicalDefinition> _aliases = new(
            new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        private readonly HashSet<string> _missingNames =
            new(new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        private readonly Dictionary<string, PhysicalDefinition> _physicalDefinitions =
            new(new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        private readonly Dictionary<string, T> _removedDefinitions =
            new(new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        private readonly HashSet<string> _unresolvedMissingNames =
            new(new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        // WHY: A semantic NoOp is an alias for an existing physical object, not
        // another object. Keeping that binding lets one drop invalidate every
        // alias without hiding a distinct physical object with the same shape.
        private readonly Dictionary<T, HashSet<PhysicalDefinition>> _semanticDefinitions = new(semanticComparer);
        private readonly HashSet<T> _mutatedSemanticDefinitions = new(semanticComparer);
        private HashSet<T>? _unboundDefinitions;
        private bool _unboundIdentityInvalidated;

        /// <summary>Gets the accepted removal ordinal that invalidated unresolved member identities.</summary>
        public int? IdentityUnknownOriginOrdinal { get; private set; }

        /// <summary>Gets whether accepted physical or unbound semantic shapes require column invalidation.</summary>
        public bool HasAcceptedDefinitions => _physicalDefinitions.Count > 0 || _unboundDefinitions?.Count > 0;

        /// <summary>Enumerates physical and unbound semantic prerequisites without cloning either set.</summary>
        public IEnumerable<T> Definitions =>
            _unboundDefinitions is null
                ? _physicalDefinitions.Values.Select(static value => value.Definition)
                : _physicalDefinitions
                    .Values
                    .Select(static value => value.Definition)
                    .Concat(_unboundDefinitions);

        /// <summary>Tests accepted prerequisite shapes without materializing a second collection.</summary>
        public bool Any(
            Func<T, bool> predicate
        ) => _physicalDefinitions.Values.Any(value => predicate(value.Definition))
            || _unboundDefinitions?.Any(predicate) == true;

        /// <summary>Tests accepted shapes with explicit state instead of allocating a per-query closure.</summary>
        /// <typeparam name="TState">The immutable caller's lookup state.</typeparam>
        /// <param name="state">The state consumed by the static predicate.</param>
        /// <param name="predicate">The non-capturing accepted-shape predicate.</param>
        public bool Any<TState>(
            TState state,
            Func<T, TState, bool> predicate
        )
        {
            foreach (var physical in _physicalDefinitions.Values)
            {
                if (predicate(physical.Definition, state))
                {
                    return true;
                }
            }

            if (_unboundDefinitions is not null)
            {
                foreach (var definition in _unboundDefinitions)
                {
                    if (predicate(definition, state))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Tests only fresh accepted Apply lifetimes, never matching aliases or shape-only facts.</summary>
        /// <typeparam name="TState">The immutable caller's lookup state.</typeparam>
        /// <param name="state">The state consumed by the static predicate.</param>
        /// <param name="predicate">The non-capturing current-shape predicate.</param>
        public bool AnyCreated<TState>(
            TState state,
            Func<T, TState, bool> predicate
        )
        {
            foreach (var physical in _physicalDefinitions.Values)
            {
                if (physical.CreatedByProjection
                    && predicate(physical.Definition, state))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Retains semantic prerequisite shape without inventing a physical member name.</summary>
        /// <param name="definition">The shape proven by a matching existing-table contract.</param>
        public void AcceptShapeOnly(
            T definition
        )
        {
            if (_unboundIdentityInvalidated || _semanticDefinitions.ContainsKey(definition))
            {
                return;
            }

            // WHY: Table matching can ignore constraint symbols. Allocate this
            // separate set only for live shapes that lack physical-name proof.
            _unboundDefinitions ??= new HashSet<T>(semanticComparer);
            _unboundDefinitions.Add(definition);
        }

        /// <summary>Checks whether immutable live shape may describe an unresolved removed physical member.</summary>
        /// <param name="analysis">The original live analysis.</param>
        public bool HasUnprovenMutatedIdentity(
            SafeMigrationProviderAnalysis analysis
        ) => _unboundIdentityInvalidated
            && (analysis.ObservedState == SafeMigrationObservedState.Different
                || analysis is { ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: null });

        /// <summary>Rebinds immutable identities without discarding accepted aliases or removal provenance.</summary>
        public void Rebind(
            Func<T, T> transform
        )
        {
            _semanticDefinitions.Clear();
            foreach (var physical in _physicalDefinitions.Values)
            {
                physical.Definition = transform(physical.Definition);
                if (!_semanticDefinitions.TryGetValue(physical.Definition, out var definitions))
                {
                    definitions = [];
                    _semanticDefinitions.Add(physical.Definition, definitions);
                }

                definitions.Add(physical);
            }

            foreach (var name in _removedDefinitions.Keys.ToArray())
            {
                _removedDefinitions[name] = transform(_removedDefinitions[name]);
            }

            var mutated = _mutatedSemanticDefinitions.ToArray();
            _mutatedSemanticDefinitions.Clear();
            foreach (var definition in mutated)
            {
                _mutatedSemanticDefinitions.Add(transform(definition));
            }

            if (_unboundDefinitions is not null)
            {
                var unbound = _unboundDefinitions.ToArray();
                _unboundDefinitions.Clear();
                foreach (var definition in unbound)
                {
                    _unboundDefinitions.Add(transform(definition));
                }
            }
        }

        public void AcceptPhysical(
            string physicalName,
            T definition,
            bool createdByProjection = false
        )
        {
            if (_physicalDefinitions.Remove(physicalName, out var previous))
            {
                RemovePhysical(previous);
            }

            RemoveAlias(physicalName);
            RemoveMissingName(physicalName);

            var physical = new PhysicalDefinition(physicalName, definition, _identifierComparer, createdByProjection);
            physical.Aliases.Add(physicalName);
            _physicalDefinitions.Add(physicalName, physical);
            _aliases[physicalName] = physical;

            if (!_semanticDefinitions.TryGetValue(definition, out var definitions))
            {
                definitions = [];
                _semanticDefinitions.Add(definition, definitions);
            }

            definitions.Add(physical);
            _unboundDefinitions?.Remove(definition);
        }

        public bool BindAlias(
            string alias,
            string physicalName,
            T definition
        )
        {
            if (_missingNames.Contains(physicalName)
                && !_physicalDefinitions.ContainsKey(physicalName))
            {
                // WHY: Immutable native Matching can still name a removed
                // candidate while a different semantic candidate survives.
                // Only accepted creation may reintroduce that physical lifetime.
                return false;
            }

            if (!_physicalDefinitions.TryGetValue(physicalName, out var physical))
            {
                AcceptPhysical(physicalName, definition);
                physical = _physicalDefinitions[physicalName];
            }
            else if (!semanticComparer.Equals(physical.Definition, definition))
            {
                return false;
            }

            if (_physicalDefinitions.ContainsKey(alias)
                && !_identifierComparer.Equals(alias, physicalName))
            {
                return false;
            }

            RemoveAlias(alias);
            RemoveMissingName(alias);
            _aliases[alias] = physical;
            physical.Aliases.Add(alias);

            return true;
        }

        public bool BindUniqueSemanticAlias(
            string alias,
            T definition
        )
        {
            if (!_semanticDefinitions.TryGetValue(definition, out var definitions)
                || definitions.Count != 1)
            {
                return false;
            }

            var physical = definitions.Single();

            return BindAlias(alias, physical.Name, definition);
        }

        public bool ContainsSemantically(
            T definition
        ) => _semanticDefinitions.ContainsKey(definition) || _unboundDefinitions?.Contains(definition) == true;

        /// <summary>Checks exact semantic provenance retained from an accepted physical removal.</summary>
        /// <param name="expected">The requested semantics, excluding only the logical member name.</param>
        public bool HasRemovedSemantically(
            T expected
        )
        {
            foreach (var removed in _removedDefinitions.Values)
            {
                if (semanticComparer.Equals(removed, expected))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Resolves an exact bound name or a deterministic accepted semantic physical candidate.</summary>
        /// <param name="name">The requested logical or physical name.</param>
        /// <param name="expected">The required accepted semantics.</param>
        /// <param name="physicalName">The actual accepted physical object's name.</param>
        public bool TryResolvePhysicalName(
            string name,
            T expected,
            [NotNullWhen(true)] out string? physicalName
        )
        {
            if (_aliases.TryGetValue(name, out var exact)
                && semanticComparer.Equals(exact.Definition, expected))
            {
                physicalName = exact.Name;

                return true;
            }

            physicalName = null;

            if (!_semanticDefinitions.TryGetValue(expected, out var candidates))
            {
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (physicalName is null
                    || StringComparer.Ordinal.Compare(candidate.Name, physicalName) < 0)
                {
                    physicalName = candidate.Name;
                }
            }

            return physicalName is not null;
        }

        /// <summary>Distinguishes accepted physical presence from tombstones and logical-only aliases.</summary>
        /// <param name="name">The requested physical name.</param>
        /// <param name="present">Whether that exact physical object is accepted as present.</param>
        public bool TryGetPhysicalState(
            string name,
            out bool present
        )
        {
            present = _physicalDefinitions.ContainsKey(name);

            return present || _missingNames.Contains(name) || _aliases.ContainsKey(name);
        }

        /// <summary>Reads an accepted exact physical definition without following a logical alias.</summary>
        /// <param name="name">The independently certified physical key name.</param>
        /// <param name="definition">The definition of that accepted physical lifetime.</param>
        public bool TryGetPhysicalDefinition(
            string name,
            [NotNullWhen(true)] out T? definition
        )
        {
            definition = _physicalDefinitions.TryGetValue(name, out var physical) ? physical.Definition : null;

            return definition is not null;
        }

        /// <summary>Checks whether an accepted physical creation proves the current object's fresh lifetime.</summary>
        /// <param name="name">The exact physical member name, not a semantic alias.</param>
        public bool WasCreatedByProjection(
            string name
        ) => _physicalDefinitions.TryGetValue(name, out var physical) && physical.CreatedByProjection;

        /// <summary>Records actual known owned index names without allocating an iterator or a name snapshot.</summary>
        /// <param name="tombstones">The existing physical-key removal set.</param>
        /// <param name="owner">The physical owner whose lifetime is being removed.</param>
        public void CaptureIndexTombstones(
            HashSet<IndexKey> tombstones,
            TableKey owner
        )
        {
            foreach (var physical in _physicalDefinitions.Values)
            {
                tombstones.Add(new IndexKey(owner.Table, owner.Schema, physical.Name));
            }
        }

        /// <summary>Allows exact live identity only for physical names untouched by accepted bookkeeping.</summary>
        /// <param name="name">The requested physical name.</param>
        /// <param name="physicalName">The immutable provider's resolved physical name.</param>
        public bool CanReuseExactLivePhysicalName(
            string name,
            string physicalName
        ) => _identifierComparer.Equals(name, physicalName) && !TryGetPhysicalState(physicalName, out _);

        public void MarkPhysicalMissing(
            string physicalName,
            int removalOrdinal = -1
        )
        {
            if (_unboundDefinitions?.Count > 0 || _unboundIdentityInvalidated)
            {
                // WHY: A literal physical drop cannot identify which unnamed
                // semantic shape it removed. Invalidate only this member family;
                // exact physical bindings in the same owner remain authoritative.
                _unboundDefinitions?.Clear();
                _unboundIdentityInvalidated = true;
                IdentityUnknownOriginOrdinal = removalOrdinal >= 0 ? removalOrdinal : IdentityUnknownOriginOrdinal;
            }

            if (RemovePhysical(physicalName, out _))
            {
                return;
            }

            RemoveAlias(physicalName);
            _missingNames.Add(physicalName);
            _removedDefinitions.Remove(physicalName);
            _unresolvedMissingNames.Add(physicalName);
        }

        public bool SemanticallyEquals(
            T left,
            T right
        ) => semanticComparer.Equals(left, right);

        public bool RemovePhysical(
            string physicalName,
            [NotNullWhen(true)] out T? definition
        )
        {
            if (!_physicalDefinitions.Remove(physicalName, out var physical))
            {
                definition = null;
                return false;
            }

            definition = physical.Definition;
            RemovePhysical(physical);

            return true;
        }

        public bool RemoveWhere(
            Func<T, bool> predicate
        )
        {
            ArgumentNullException.ThrowIfNull(predicate);

            var names = _physicalDefinitions
                .Where(pair => predicate(pair.Value.Definition))
                .Select(static pair => pair.Key)
                .ToArray();

            foreach (var name in names)
            {
                _ = RemovePhysical(name, out _);
            }

            var unboundRemoved = _unboundDefinitions?.RemoveWhere(value => predicate(value)) ?? 0;

            return names.Length > 0 || unboundRemoved > 0;
        }

        public ProjectedDefinitionMutation ResolveMutation(
            string name,
            T expected,
            SafeMigrationProviderAnalysis liveAnalysis
        )
        {
            if (liveAnalysis.ObservedState is not (SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.Matching
                or SafeMigrationObservedState.Different))
            {
                return ProjectedDefinitionMutation.None;
            }

            if (_missingNames.Contains(name))
            {
                if (_removedDefinitions.TryGetValue(name, out var removed)
                    && semanticComparer.Equals(removed, expected))
                {
                    return ProjectedDefinitionMutation.Missing;
                }

                return liveAnalysis is
                    {
                        ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: not null
                    }
                    && _identifierComparer.Equals(liveAnalysis.MatchedObjectName, name)
                        ? ProjectedDefinitionMutation.Missing
                        : ProjectedDefinitionMutation.Unknown;
            }

            if (liveAnalysis.MatchedObjectName is { } physicalName
                && _missingNames.Contains(physicalName))
            {
                return liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                    ? ProjectedDefinitionMutation.Missing
                    : ProjectedDefinitionMutation.Unknown;
            }

            // WHY: A missing provider identity means the immutable live match
            // may have represented more than one semantic candidate. Once one
            // such candidate was mutated, the remaining live state is unknown.

            return liveAnalysis is { ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: null }
                && (_unresolvedMissingNames.Count > 0 || _mutatedSemanticDefinitions.Contains(expected))
                    ? ProjectedDefinitionMutation.Unknown
                    : ProjectedDefinitionMutation.None;
        }

        public bool TryGetValue(
            string name,
            [NotNullWhen(true)] out T? definition
        )
        {
            if (_aliases.TryGetValue(name, out var physical))
            {
                definition = physical.Definition;
                return true;
            }

            definition = null;
            return false;
        }

        private void RemoveAlias(
            string alias
        )
        {
            if (!_aliases.Remove(alias, out var physical))
            {
                return;
            }

            physical.Aliases.Remove(alias);
        }

        private void RemoveMissingName(
            string name
        )
        {
            _missingNames.Remove(name);
            _removedDefinitions.Remove(name);
            _unresolvedMissingNames.Remove(name);
        }

        private void RemovePhysical(
            PhysicalDefinition physical
        )
        {
            foreach (var alias in physical.Aliases)
            {
                _aliases.Remove(alias);
                _missingNames.Add(alias);
                _removedDefinitions[alias] = physical.Definition;
                _unresolvedMissingNames.Remove(alias);
            }

            _mutatedSemanticDefinitions.Add(physical.Definition);

            if (_semanticDefinitions.TryGetValue(physical.Definition, out var definitions))
            {
                definitions.Remove(physical);
                if (definitions.Count == 0)
                {
                    _semanticDefinitions.Remove(physical.Definition);
                }
            }
        }

        private sealed class PhysicalDefinition(
            string name,
            T definition,
            IEqualityComparer<string> identifierComparer,
            bool createdByProjection
        )
        {
            public HashSet<string> Aliases { get; } = new(identifierComparer);

            public T Definition { get; set; } = definition;

            public string Name { get; } = name;

            /// <summary>Gets whether accepted DDL created this lifetime rather than observing a live alias.</summary>
            public bool CreatedByProjection { get; } = createdByProjection;
        }
    }

    private enum ProjectedDefinitionMutation : byte
    {
        None = 0,
        Missing = 1,
        Unknown = 2,
    }

    // WHY: These four row-safety flags have value semantics. Inline dictionary storage
    // avoids allocating one short-lived prerequisite object for every accepted column.
    private readonly record struct ProjectedColumn(
        bool IsNullable,
        bool PreservesNullForExistingRows,
        bool IsComputed,
        bool AddedToExistingTable
    )
    {
        public static ProjectedColumn From(
            ExpectedColumnDefinition definition,
            bool addedToExistingTable
        ) => new(
            definition.IsNullable,
            SafeMigrationPreflightProjection.PreservesNullForExistingRows(definition.DefaultValue),
            definition.ComputedColumnSql is not null || definition.ComputedExpression is not null,
            addedToExistingTable);

        public static ProjectedColumn From(
            ColumnOperation operation,
            bool addedToExistingTable
        ) => new(
            operation.IsNullable,
            operation.DefaultValue is null && operation.DefaultValueSql is null,
            operation.ComputedColumnSql is not null,
            addedToExistingTable);

        public static ProjectedColumn Unknown { get; } = new(
            IsNullable: false,
            PreservesNullForExistingRows: false,
            IsComputed: true,
            AddedToExistingTable: false);
    }

    private sealed partial class ProjectedTable : ISafeMigrationProjectedAlterTable
    {
        private readonly List<string> _columnOrder;
        private readonly ISafeMigrationProviderObjectIdentityNormalizer? _objectIdentityNormalizer;
        private string _table;
        private string? _schema;
        private readonly string? _comment;

        public ProjectedTable(
            ExpectedTableDefinition definition,
            long dataMutationVersion,
            ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer,
            bool sharesUniqueIndexIdentity
        )
        {
            definition = SafeMigrationCapturedExpressionNormalization.NormalizeTable(
                definition,
                objectIdentityNormalizer?.FoldUnquotedCapturedSqlIdentifiers == true);
            _objectIdentityNormalizer = objectIdentityNormalizer;
            _table = definition.Table;
            _schema = definition.Schema;
            _comment = definition.Comment;
            _columnOrder = definition
                .Columns
                .Select(static value => value.Name)
                .ToList();

            var identifierComparer = new IdentifierComparer(objectIdentityNormalizer);

            Columns = definition.Columns.ToDictionary(
                static value => value.Name,
                new IdentifierComparer(objectIdentityNormalizer, objectIdentityNormalizer?.ColumnIdentifierComparer));

            PrimaryKey = definition.PrimaryKey;

            UniqueConstraints = definition.UniqueConstraints.ToDictionary(
                static value => value.Name,
                new IdentifierComparer(
                    objectIdentityNormalizer,
                    sharesUniqueIndexIdentity ? objectIdentityNormalizer?.IndexIdentifierComparer : null));

            CheckConstraints = definition.CheckConstraints.ToDictionary(static value => value.Name, identifierComparer);

            ForeignKeys = definition.ForeignKeys.ToDictionary(
                static value => value.Name,
                new IdentifierComparer(
                    objectIdentityNormalizer,
                    objectIdentityNormalizer?.ForeignKeyIdentifierComparer));

            Indexes = new Dictionary<string, ExpectedIndexDefinition>(
                new IdentifierComparer(objectIdentityNormalizer, objectIdentityNormalizer?.IndexIdentifierComparer));
            DataMutationVersion = dataMutationVersion;
        }

        public ExpectedTableDefinition Definition =>
            new(
                _table,
                _columnOrder.Select(name => Columns[name]),
                _schema,
                _comment,
                PrimaryKey,
                UniqueConstraints.Values,
                CheckConstraints.Values,
                ForeignKeys.Values);

        public Dictionary<string, ExpectedColumnDefinition> Columns { get; }

        public long DataMutationVersion { get; }

        public bool CanReuseCreationCharacterSet { get; init; } = true;

        public string Table => _table;

        public string? Schema => _schema;

        public ExpectedPrimaryKeyDefinition? PrimaryKey { get; set; }

        public Dictionary<string, ExpectedUniqueConstraintDefinition> UniqueConstraints { get; }

        public Dictionary<string, ExpectedCheckConstraintDefinition> CheckConstraints { get; }

        public Dictionary<string, ExpectedForeignKeyDefinition> ForeignKeys { get; }

        public Dictionary<string, ExpectedIndexDefinition> Indexes { get; }

        IEnumerable<ExpectedColumnDefinition> ISafeMigrationProjectedAlterTable.Columns => Columns.Values;

        IEnumerable<ExpectedUniqueConstraintDefinition> ISafeMigrationProjectedAlterTable.UniqueConstraints =>
            UniqueConstraints.Values;

        IEnumerable<ExpectedIndexDefinition> ISafeMigrationProjectedAlterTable.Indexes => Indexes.Values;

        bool ISafeMigrationProjectedColumnSource.TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        ) => Columns.TryGetValue(column, out definition);

        private static void ReplaceValues<T>(
            Dictionary<string, T> dictionary,
            Func<T, T> transform
        )
        {
            foreach (var key in dictionary.Keys.ToArray())
            {
                dictionary[key] = transform(dictionary[key]);
            }
        }

        private string[] Rename(
            IReadOnlyList<string> values,
            string source,
            string target
        ) => values
            .Select(value => Columns.Comparer.Equals(value, source) ? target : value)
            .ToArray();

        private bool SameIdentity(
            string leftTable,
            string? leftSchema,
            string rightTable,
            string? rightSchema
        ) => IdentifierEquals(_objectIdentityNormalizer, leftTable, rightTable)
            && StringComparer.Ordinal.Equals(
                NormalizeSchema(_objectIdentityNormalizer, leftSchema),
                NormalizeSchema(_objectIdentityNormalizer, rightSchema));

        private static ExpectedColumnDefinition Copy(
            ExpectedColumnDefinition value,
            string? name = null,
            string? computedColumnSql = null,
            SafeMigrationSqlExpression? computedExpression = null,
            string? capturedComputedSql = null,
            bool replaceComputed = false
        ) => new(
            name ?? value.Name,
            value.ClrType,
            value.IsNullable,
            value.StoreType,
            value.IsUnicode,
            value.MaxLength,
            value.IsFixedLength,
            value.IsRowVersion,
            value.Precision,
            value.Scale,
            value.Collation,
            value.Comment,
            value.DefaultValue,
            replaceComputed ? computedColumnSql : computedColumnSql ?? value.ComputedColumnSql,
            value.IsStored,
            replaceComputed ? computedExpression : computedExpression ?? value.ComputedExpression)
        {
            ProviderAnnotations = value.ProviderAnnotations,
            CapturedComputedSql = replaceComputed ? capturedComputedSql : value.CapturedComputedSql,
        };

        private static ExpectedPrimaryKeyDefinition Copy(
            ExpectedPrimaryKeyDefinition value,
            string? table = null,
            string? schema = null,
            IReadOnlyList<string>? columns = null
        ) => new(value.Name, table ?? value.Table, columns ?? value.Columns, schema ?? value.Schema);

        private static ExpectedUniqueConstraintDefinition Copy(
            ExpectedUniqueConstraintDefinition value,
            string? table = null,
            string? schema = null,
            IReadOnlyList<string>? columns = null
        ) => new(value.Name, table ?? value.Table, columns ?? value.Columns, schema ?? value.Schema);

        private static ExpectedCheckConstraintDefinition Copy(
            ExpectedCheckConstraintDefinition value,
            string? table = null,
            string? schema = null,
            string? sql = null,
            SafeMigrationSqlExpression? expression = null,
            string? capturedSql = null,
            bool replaceExpression = false
        )
        {
            var selectedSql = replaceExpression ? sql : sql ?? value.Sql;
            var selectedExpression = replaceExpression ? expression : expression ?? value.Expression;
            var selectedCapturedSql = replaceExpression ? capturedSql : value.CapturedSql;

            if (selectedSql is not null)
            {
                return new ExpectedCheckConstraintDefinition(
                    value.Name,
                    table ?? value.Table,
                    selectedSql,
                    schema ?? value.Schema);
            }

            var structured = selectedExpression
                ?? throw new InvalidOperationException("A check constraint has no expression.");

            return selectedCapturedSql is not null
                ? ExpectedCheckConstraintDefinition.FromCapturedExpression(
                    value.Name,
                    table ?? value.Table,
                    structured,
                    selectedCapturedSql,
                    schema ?? value.Schema)
                : ExpectedCheckConstraintDefinition.FromExpression(
                    value.Name,
                    table ?? value.Table,
                    structured,
                    schema ?? value.Schema);
        }

        private static ExpectedForeignKeyDefinition Copy(
            ExpectedForeignKeyDefinition value,
            string? table = null,
            string? schema = null,
            IReadOnlyList<string>? columns = null,
            string? principalTable = null,
            string? principalSchema = null,
            IReadOnlyList<string>? principalColumns = null
        ) => new(
            value.Name,
            table ?? value.Table,
            columns ?? value.Columns,
            principalTable ?? value.PrincipalTable,
            principalColumns ?? value.PrincipalColumns,
            schema ?? value.Schema,
            principalSchema ?? value.PrincipalSchema,
            value.OnUpdate,
            value.OnDelete);

        private static ExpectedIndexDefinition Copy(
            ExpectedIndexDefinition value,
            string? name = null,
            string? table = null,
            string? schema = null,
            IEnumerable<ExpectedIndexKeyDefinition>? keys = null,
            IReadOnlyList<string>? includedColumns = null,
            string? filter = null,
            SafeMigrationSqlExpression? structuredFilter = null,
            bool replaceFilter = false
        ) => new(
            name ?? value.Name,
            table ?? value.Table,
            keys ?? value.Keys,
            schema ?? value.Schema,
            value.Unique,
            replaceFilter ? filter : filter ?? value.Filter,
            includedColumns ?? value.IncludedColumns,
            value.Method,
            value.NullsDistinct,
            replaceFilter ? structuredFilter : structuredFilter ?? value.StructuredFilter);

        private ExpectedIndexKeyDefinition Copy(
            ExpectedIndexKeyDefinition value,
            string source,
            string target
        ) => new(
            value.Column is not null && Columns.Comparer.Equals(value.Column, source) ? target : value.Column,
            expression: null,
            value.SortOrder,
            value.NullOrder,
            value.PrefixLength,
            value.Collation,
            value.OperatorClass,
            value.Expression is not null
                ? SafeMigrationSql.OpaqueAfterRename(value.Expression)
                : value.StructuredExpression is null
                    ? null
                    : RenameColumnExpression(value.StructuredExpression, source, target));
    }
}
