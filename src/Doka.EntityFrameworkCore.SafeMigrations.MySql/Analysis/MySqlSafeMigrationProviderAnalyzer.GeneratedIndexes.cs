namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    private Dictionary<SafeMigrationOperation, GeneratedIndexIdentityProof>? _generatedIndexIdentityProofs;
    private HashSet<(string? Schema, string Table)>? _generatedIndexNamespaceUnknownOwners;
    private Dictionary<(string? Schema, string Table, string Column), bool>? _generatedIndexDroppedMembers;

    /// <summary>Releases namespace and accepted-member lifetime evidence between independent analyses.</summary>
    private void ClearGeneratedIndexIdentityFacts()
    {
        _generatedIndexIdentityProofs = null;
        _generatedIndexNamespaceUnknownOwners = null;
        _generatedIndexDroppedMembers = null;
    }

    /// <summary>Selects only ordered nonunique indexes that consume an earlier authored generated ADD.</summary>
    private void PrepareGeneratedIndexIdentityCapture(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        ClearGeneratedIndexIdentityFacts();
        Dictionary<(string? Schema, string Table, string Column), SafeMigrationOperation>? generated = null;
        HashSet<(string? Schema, string Table)>? memberChanges = null;
        foreach (var operation in operations)
        {
            var changedOwner = operation.Intent switch
            {
                DropColumnIntent drop => (drop.Schema, drop.Table),
                RenameColumnIntent rename => (rename.Schema, rename.Table),
                _ => default,
            };

            if (changedOwner.Table is not null)
            {
                (memberChanges ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(changedOwner.Schema), changedOwner.Table));
            }

            if (operation.Intent is EnsureColumnIntent { Definition.ComputedExpression: not null } column)
            {
                (generated ??=
                    new Dictionary<(string? Schema, string Table, string Column), SafeMigrationOperation>(
                        PhysicalMemberComparer))[(NormalizeProofSchema(column.Schema), column.Table,
                    column.Definition.Name)] = operation;

                continue;
            }

            if (generated is null
                || operation.Intent is not EnsureIndexIntent index
                || memberChanges?.Contains((NormalizeProofSchema(index.Definition.Schema), index.Definition.Table)) != true
                || !IsGeneratedIndexRecoveryCandidate(index.Definition))
            {
                continue;
            }

            List<SafeMigrationOperation>? sources = null;
            foreach (var key in index.Definition.Keys)
            {
                if (generated.TryGetValue(
                        (NormalizeProofSchema(index.Definition.Schema), index.Definition.Table, key.Column!),
                        out var source))
                {
                    (sources ??= []).Add(source);
                }
            }

            if (sources is not null)
            {
                (_generatedIndexIdentityProofs ??= new(ReferenceEqualityComparer.Instance))[operation] =
                    new GeneratedIndexIdentityProof(sources.ToArray());

                foreach (var source in sources)
                {
                    var target = (EnsureColumnIntent)source.Intent;
                    (_generatedIndexDroppedMembers ??= new(PhysicalMemberComparer)).TryAdd(
                        (NormalizeProofSchema(target.Schema), target.Table, target.Definition.Name),
                        false);
                }
            }
        }
    }

    /// <summary>Excludes row-dependent and provider-specific index contracts from the narrow recovery path.</summary>
    internal static bool IsGeneratedIndexRecoveryCandidate(
        ExpectedIndexDefinition definition
    )
    {
        if (definition.Unique
            || definition.Name.Length > 64
            || StringComparer.OrdinalIgnoreCase.Equals(definition.Name, "PRIMARY")
            || definition.Keys.Count > MySqlProjectedIndexPhysicalShape.MaximumKeyParts
            || definition.Filter is not null
            || definition.StructuredFilter is not null
            || definition.IncludedColumns.Count != 0
            || (definition.Method is not null && !StringComparer.OrdinalIgnoreCase.Equals(definition.Method, "BTREE")))
        {
            return false;
        }

        for (var ordinal = 0; ordinal < definition.Keys.Count; ordinal++)
        {
            var key = definition.Keys[ordinal];
            if (key.Column is null or { Length: > 64 }
                || key.Expression is not null
                || key.StructuredExpression is not null
                || key.Collation is not null
                || key.OperatorClass is not null
                || key.NullOrder != SafeMigrationIndexNullOrder.ProviderDefault)
            {
                return false;
            }

            for (var prior = 0; prior < ordinal; prior++)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(key.Column, definition.Keys[prior].Column))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Expires successful identity and absence proofs after an implicit native namespace change.</summary>
    internal static bool CanReuseGeneratedIndexNamespace(
        SafeMigrationObservedState? identity,
        bool namespaceChanged
    ) => !namespaceChanged || identity == SafeMigrationObservedState.Different;

    /// <summary>Requests namespace evidence within the existing catalog prerequisite dispatch.</summary>
    private void AttachGeneratedIndexIdentityCapture(
        SafeMigrationOperation[] operations,
        MySqlSafeMigrationRuntimePlan[] plans
    )
    {
        if (_generatedIndexIdentityProofs is null)
        {
            return;
        }

        for (var ordinal = 0; ordinal < operations.Length; ordinal++)
        {
            if (_generatedIndexIdentityProofs.ContainsKey(operations[ordinal])
                && !plans[ordinal].IsStaticallyUnsupported)
            {
                plans[ordinal] = plans[ordinal] with { CaptureProjectedIndexIdentity = true };
            }
        }
    }

    /// <summary>Binds independently observed namespace facts to their exact immutable operation identities.</summary>
    private void CaptureGeneratedIndexIdentityFacts(
        SafeMigrationOperation[] operations,
        MySqlSafeMigrationRuntimePlan[] plans
    )
    {
        if (_generatedIndexIdentityProofs is null)
        {
            return;
        }

        for (var ordinal = 0; ordinal < operations.Length; ordinal++)
        {
            if (_generatedIndexIdentityProofs.TryGetValue(operations[ordinal], out var proof))
            {
                proof.NamespaceState = plans[ordinal].CapturedIndexIdentity;
            }
        }
    }

    /// <summary>Retains native Matching aliases and physical facts after prerequisite classification.</summary>
    private void CompleteGeneratedIndexIdentityFacts(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses
    )
    {
        if (_generatedIndexIdentityProofs is null)
        {
            return;
        }

        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (_generatedIndexIdentityProofs.TryGetValue(operations[ordinal], out var proof))
            {
                proof.LiveAnalysis = analyses[ordinal];
            }
        }
    }

    /// <summary>Combines accepted generated storage, namespace evidence and captured physical key limits.</summary>
    private SafeMigrationProviderAnalysis ValidateProjectedGeneratedIndex(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projected,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (operation.Intent is not EnsureIndexIntent index
            || projected.ObservedState != SafeMigrationObservedState.PrerequisiteMissing
            || projected.Code != "projected_structure_state_unknown"
            || _generatedIndexIdentityProofs is not { } proofs
            || !proofs.TryGetValue(operation, out var proof)
            || proof.LiveAnalysis is not { } live
            || proof.NamespaceState is null
            || live.IsInvariantUnsupported
            || live.ObservedState == SafeMigrationObservedState.Unsupported
            || _generatedColumnSourcesUnknown
            || _changedGeneratedColumnOwners?.Contains(
                (NormalizeProofSchema(index.Definition.Schema), index.Definition.Table)) == true)
        {
            return projected;
        }

        if (columns is ISafeMigrationProjectedTableSource tables
            && tables.TryGetProjectedTableState(index.Definition.Table, index.Definition.Schema, out var owner)
            && owner.IsNewlyCreated)
        {
            // WHY: These namespace and storage facts belong to an observed
            // existing owner, not a newly created physical lifetime. Complete
            // new-owner projection keeps its existing separate qualification.
            return projected;
        }

        var hasNewPhysicalGeneratedKey = false;
        var hasDroppedPhysicalGeneratedKey = false;
        foreach (var source in proof.GeneratedSources)
        {
            var target = (EnsureColumnIntent)source.Intent;
            if (!columns.TryGetProjectedColumn(target.Table, target.Schema, target.Definition.Name, out var accepted)
                || !ReferenceEquals(accepted, target.Definition)
                || _generatedColumnSourceProofs is not { } sources
                || !sources.TryGetValue(source, out var sourceProof)
                || !GeneratedColumnSourcesAreCurrent(sourceProof, columns))
            {
                return projected;
            }

            var key = (NormalizeProofSchema(target.Schema), target.Table, target.Definition.Name);
            var wasDropped = _generatedIndexDroppedMembers?.TryGetValue(key, out var dropped) == true && dropped;
            hasNewPhysicalGeneratedKey |= sourceProof.OriginallyMissing || wasDropped;
            hasDroppedPhysicalGeneratedKey |= wasDropped;
        }

        foreach (var key in index.Definition.Keys)
        {
            if (!columns.TryGetProjectedColumn(index.Definition.Table, index.Definition.Schema, key.Column!, out _)
                && (_changedGeneratedColumnSources?.Contains(
                        (NormalizeProofSchema(index.Definition.Schema), index.Definition.Table, key.Column!)) == true
                    || live.IndexPhysicalEnvironment?.Columns?.ContainsKey(key.Column!) != true))
            {
                return projected;
            }
        }

        var identity = columns.GetProjectedIndexIdentityAnalysis(index.Definition, live);
        var namespaceChanged = _generatedIndexNamespaceUnknownOwners?.Contains(
                (NormalizeProofSchema(index.Definition.Schema), index.Definition.Table)) == true;

        if (!CanReuseGeneratedIndexNamespace(identity?.ObservedState, namespaceChanged))
        {
            // WHY: A later FK can occupy the physical index name without an
            // explicit EnsureIndex. That invalidates both a Core tombstone and
            // a retained matching alias: a new exact-name clash takes precedence
            // over that surviving semantic alias. Known conflicts stay blocking.
            return projected;
        }

        // WHY: A missing key column suppresses ordinary live classification,
        // not the independent catalog namespace query. Only captured absence
        // can authorize creation; Matching keeps its completed physical alias
        // only while its key lifetime survives. DROP/reADD can remove that
        // native index; an accepted DropIndex supplies a separate tombstone.
        // An exact-name clash never becomes Missing without such evidence.
        identity ??= proof.NamespaceState == SafeMigrationObservedState.Matching
            ? !hasDroppedPhysicalGeneratedKey
            && live is { ObservedState: SafeMigrationObservedState.Matching, PostconditionSatisfied: true }
                ? live
                : null
            : new SafeMigrationProviderAnalysis(
                proof.NamespaceState.Value,
                SafeMigrationRepairCapability.None,
                false,
                "projected_index_namespace_qualified");

        if (identity?.ObservedState == SafeMigrationObservedState.Missing
            && !hasNewPhysicalGeneratedKey)
        {
            // WHY: Dropping another column can shrink an existing composite
            // index into a new semantic alias. Initial absence stays conclusive
            // only when a requested generated key has a new physical lifetime;
            // no surviving native index can already contain that new member.
            return projected;
        }

        return identity is null
            ? projected
            : ((ISafeMigrationProjectedKeyAnalyzer)this).ValidateProjectedIndex(index, columns, live, identity);
    }

    /// <summary>Expires captured absence after constraints may introduce untracked native supporting indexes.</summary>
    private void ObserveGeneratedIndexIdentity(
        SafeMigrationOperation operation,
        SafeMigrationDecision decision
    )
    {
        if (_generatedIndexIdentityProofs is null
            || decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.Repair))
        {
            return;
        }

        if (operation.Intent is DropColumnIntent drop
            && _generatedIndexDroppedMembers is { } droppedMembers)
        {
            var key = (NormalizeProofSchema(drop.Schema), drop.Table, drop.Name);
            if (droppedMembers.ContainsKey(key))
            {
                droppedMembers[key] = true;
            }
        }

        if (operation.Intent is RenameColumnIntent rename
            && _generatedIndexDroppedMembers is { } renamedMembers)
        {
            // WHY: Renaming an existing member can carry its native indexes
            // into a formerly dropped spelling; it is not a fresh ADD lifetime.
            renamedMembers.Remove((NormalizeProofSchema(rename.Schema), rename.Table, rename.Name));
            renamedMembers.Remove((NormalizeProofSchema(rename.Schema), rename.Table, rename.NewName));
        }

        var owner = operation.Intent switch
        {
            EnsureForeignKeyIntent foreignKey => (foreignKey.Definition.Schema, foreignKey.Definition.Table),
            EnsureUniqueConstraintIntent unique => (unique.Definition.Schema, unique.Definition.Table),
            RenameColumnIntent renamedOwner => (renamedOwner.Schema, renamedOwner.Table),
            _ => default,
        };

        if (owner.Table is not null)
        {
            (_generatedIndexNamespaceUnknownOwners ??= new(PhysicalOwnerComparer)).Add(
                (NormalizeProofSchema(owner.Schema), owner.Table));
        }
    }

    /// <summary>Retains source identities and catalog namespace/storage evidence, without application data.</summary>
    private sealed class GeneratedIndexIdentityProof(SafeMigrationOperation[] generatedSources)
    {
        internal SafeMigrationOperation[] GeneratedSources { get; } = generatedSources;
        internal SafeMigrationObservedState? NamespaceState { get; set; }
        internal SafeMigrationProviderAnalysis? LiveAnalysis { get; set; }
    }
}
