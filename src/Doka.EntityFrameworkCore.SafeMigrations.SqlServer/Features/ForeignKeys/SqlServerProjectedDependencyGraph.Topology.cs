namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerProjectedDependencyGraph
{
    /// <summary>Checks an event-aware graph with capped path counts.</summary>
    /// <param name="foreignKeys">The live and candidate FK edges.</param>
    /// <returns>Whether each DELETE/UPDATE action tree visits each physical table at most once.</returns>
    internal static bool HasValidTopology(
        IEnumerable<ForeignKeyEdge> foreignKeys
    )
    {
        var adjacency = new Dictionary<ActionNode, List<ActionNode>>();
        foreach (var edge in foreignKeys)
        {
            AddAction(edge.Principal, edge.Dependent, edge.OnDelete, isDelete: true);
            AddAction(edge.Principal, edge.Dependent, edge.OnUpdate, isDelete: false);
        }

        var seen = new HashSet<int>();
        var queue = new Queue<ActionNode>();
        foreach (var root in adjacency.Keys)
        {
            seen.Clear();
            queue.Clear();
            seen.Add(root.Table);
            queue.Enqueue(root);
            while (queue.TryDequeue(out var node))
            {
                if (!adjacency.TryGetValue(node, out var children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (!seen.Add(child.Table))
                    {
                        return false;
                    }

                    queue.Enqueue(child);
                }
            }
        }

        return true;

        void AddAction(
            int principal,
            int dependent,
            ReferentialAction action,
            bool isDelete
        )
        {
            if (!SqlServerForeignKeySafety.IsCascading(action))
            {
                return;
            }

            var source = new ActionNode(principal, isDelete);
            if (!adjacency.TryGetValue(source, out var targets))
            {
                targets = [];
                adjacency.Add(source, targets);
            }

            targets.Add(new ActionNode(dependent, isDelete && action == ReferentialAction.Cascade));
        }
    }

    /// <summary>Represents one FK's compact physical cascading contract.</summary>
    /// <param name="Dependent">The dependent physical table id.</param>
    /// <param name="Principal">The principal physical table id.</param>
    /// <param name="OnDelete">The dependent action caused by deletion.</param>
    /// <param name="OnUpdate">The dependent action caused by update.</param>
    /// <param name="PrincipalKeyIndex">The captured backing index, or null for an unbound projected edge.</param>
    internal readonly record struct ForeignKeyEdge(
        int Dependent,
        int Principal,
        ReferentialAction OnDelete,
        ReferentialAction OnUpdate,
        int? PrincipalKeyIndex = null
    );

    private SafeMigrationProviderAnalysis ValidateForeignKeys(
        IReadOnlyList<ExpectedForeignKeyDefinition> foreignKeys,
        SafeMigrationProviderAnalysis analysis,
        ISafeMigrationProjectedColumnSource columns,
        ExpectedTableDefinition? inlineTable = null
    )
    {
        var additions = new List<ForeignKeyEdge>(foreignKeys.Count);
        if (inlineTable is not null
            && !HasAllInlinePrerequisites(inlineTable, columns))
        {
            return Rejected("inline_foreign_key_prerequisite");
        }

        foreach (var foreignKey in foreignKeys)
        {
            var dependentId = GetTable(foreignKey.Table, foreignKey.Schema);
            var principalId = GetTable(foreignKey.PrincipalTable, foreignKey.PrincipalSchema);
            if (_triggers.TryGetValue(dependentId, out var triggers)
                && ((foreignKey.OnDelete == ReferentialAction.Cascade && triggers.Delete)
                    || ((SqlServerForeignKeySafety.IsCascading(foreignKey.OnUpdate)
                            || foreignKey.OnDelete is ReferentialAction.SetNull or ReferentialAction.SetDefault)
                        && triggers.Update)))
            {
                return Rejected("foreign_key_instead_of_trigger");
            }

            // WHY: Validate corresponding FK endpoints at the same composite-key position.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < foreignKey.Columns.Count; ordinal++)
            {
                var dependentColumn = ReadColumn(
                    foreignKey.Table,
                    foreignKey.Schema,
                    foreignKey.Columns[ordinal],
                    columns,
                    inlineTable);

                var principalColumn = ReadColumn(
                    foreignKey.PrincipalTable,
                    foreignKey.PrincipalSchema,
                    foreignKey.PrincipalColumns[ordinal],
                    columns,
                    inlineTable);

                if (SqlServerForeignKeySafety.HasCascadingAction(foreignKey)
                    && (dependentColumn?.RowVersion == true || principalColumn?.RowVersion == true))
                {
                    return Rejected("foreign_key_rowversion_action");
                }

                if ((foreignKey.OnDelete == ReferentialAction.SetNull
                        || foreignKey.OnUpdate == ReferentialAction.SetNull)
                    && dependentColumn?.Nullable == false)
                {
                    return Rejected("foreign_key_set_null_required_column");
                }

                if ((foreignKey.OnDelete == ReferentialAction.SetDefault
                        || foreignKey.OnUpdate == ReferentialAction.SetDefault)
                    && dependentColumn is { Nullable: false, HasDefault: false })
                {
                    return Rejected("foreign_key_set_default_missing_default");
                }
            }

            if (!_foreignKeyNames.TryGetValue(new ForeignKeyKey(dependentId, foreignKey.Name), out var existingId)
                || !_foreignKeys.ContainsKey(existingId))
            {
                additions.Add(new ForeignKeyEdge(dependentId, principalId, foreignKey.OnDelete, foreignKey.OnUpdate));
            }
        }

        var topologyValid = !additions.Any(static edge => SqlServerForeignKeySafety.IsCascading(edge.OnDelete)
                || SqlServerForeignKeySafety.IsCascading(edge.OnUpdate))
            || HasValidTopology(_foreignKeys.Values.Concat(additions));

        if (!topologyValid)
        {
            return Rejected("foreign_key_cascade_topology");
        }

        // WHY: The immutable catalog may reject a cascade that becomes
        // valid after an accepted FK drop. This provider-specific code is
        // emitted only after storage and orphan prerequisites are proven;
        // unrelated missing prerequisites must never become an Apply.

        return analysis is
        {
            ObservedState: SafeMigrationObservedState.PrerequisiteMissing, Code: "foreign_key_cascade_topology"
        }
            ? new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None,
                false,
                "projected_foreign_key_topology_ready")
            : analysis;
    }

    private readonly record struct ActionNode(
        int Table,
        bool Delete
    );
}
