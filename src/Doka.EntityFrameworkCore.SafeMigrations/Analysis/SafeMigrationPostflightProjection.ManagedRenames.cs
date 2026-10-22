namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPostflightProjection
{
    /// <summary>Rebinds immutable managed-row metadata across independently verified member renames.</summary>
    internal static List<SafeMigrationPostflightRenameProbe>? CreateRenamedManagedDataProbes(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses,
        ISafeMigrationProviderObjectIdentityNormalizer? identity
    )
    {
        if (!operations.Any(static operation => operation is SafeMigrationOperation { Intent: RenameColumnIntent })
            || !operations.Any(static operation => operation is SafeMigrationOperation { Intent: ModelManagedDataIntent }))
        {
            return null;
        }

        var normalizer = identity?.GetPostflightIdentityNormalizer(operations);
        var resourceComparer = new PostflightResourceComparer(normalizer);
        var owners = new Dictionary<PostflightResource, List<PendingManagedData>>(resourceComparer);

        Dictionary<int, SafeMigrationPostflightRenameProbe>? rewritten = null;
        var safeOrdinal = 0;
        // WHY: Keep whole-stream positions aligned with safe-only analysis positions across rename boundaries.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal] is not SafeMigrationOperation operation)
            {
                owners.Clear();
                rewritten?.Clear();

                continue;
            }

            var analysis = analyses[safeOrdinal++];
            if (normalizer?.IsPostflightIdentityProven(operation.Intent, ordinal) == false)
            {
                owners.Clear();
                rewritten?.Clear();

                continue;
            }

            if (operation.Intent is ModelManagedDataIntent managed)
            {
                var pending = new PendingManagedData(ordinal, safeOrdinal - 1, operation);
                Register(Owner(managed.Table, managed.Schema), pending);
                if (managed is DeleteModelManagedDataIntent delete)
                {
                    foreach (var foreignKey in delete.ForeignKeys)
                    {
                        Register(Owner(foreignKey.Table, foreignKey.Schema), pending);
                    }
                }

                continue;
            }

            if (!TryCreateResources(
                    operation.Intent,
                    normalizer,
                    primaryKeyNameIndependent: false,
                    sharedUniqueIndexIdentity: false,
                    analysis,
                    out var resource,
                    out _))
            {
                // WHY: An unscoped safe structural effect cannot establish the
                // historical owner of a row probe any more than opaque SQL can.
                owners.Clear();
                rewritten?.Clear();

                continue;
            }

            var owner = resource with
            {
                Kind = PostflightResourceKind.Table,
                Name = null,
            };

            if (!owners.TryGetValue(owner, out var affected))
            {
                continue;
            }

            if (operation.Intent is EnsureIndexIntent or DropIndexIntent or RenameIndexIntent)
            {
                continue;
            }

            var comparer = normalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal;
            foreach (var pending in affected)
            {
                if (!pending.Valid)
                {
                    continue;
                }

                if (operation.Intent is not RenameColumnIntent rename
                    || !analysis.PostconditionSatisfied
                    || !TryRenameManagedData(
                        (ModelManagedDataIntent)pending.Operation.Intent,
                        rename,
                        owner,
                        normalizer,
                        resourceComparer,
                        comparer,
                        out var rebound))
                {
                    pending.Valid = false;
                    rewritten?.Remove(pending.SafeOrdinal);

                    continue;
                }

                if (ReferenceEquals(rebound, pending.Operation.Intent))
                {
                    continue;
                }

                var reboundOperation = new SafeMigrationOperation(rebound, pending.Operation.Policy)
                {
                    IsDestructiveChange = pending.Operation.IsDestructiveChange,
                };

                foreach (var annotation in pending.Operation.GetAnnotations())
                {
                    reboundOperation.AddAnnotation(annotation.Name, annotation.Value);
                }

                pending.Operation = reboundOperation;
                rewritten ??= [];
                rewritten[pending.SafeOrdinal] = new SafeMigrationPostflightRenameProbe(
                    pending.Ordinal,
                    pending.SafeOrdinal,
                    reboundOperation);
            }
        }

        return rewritten is { Count: > 0 } ? rewritten.Values.ToList() : null;

        PostflightResource Owner(
            string table,
            string? schema
        ) => new(
            PostflightResourceKind.Table,
            normalizer is null ? schema : normalizer.NormalizeSchema(schema),
            table,
            null);

        void Register(
            PostflightResource owner,
            PendingManagedData pending
        )
        {
            if (!owners.TryGetValue(owner, out var entries))
            {
                entries = [];
                owners.Add(owner, entries);
            }

            // WHY: Self-references and several FKs to one dependent owner must
            // transform the same immutable row contract only once per rename.
            if (entries.Count == 0
                || !ReferenceEquals(entries[^1], pending))
            {
                entries.Add(pending);
            }
        }
    }

    /// <summary>Changes column roles only; row matrices, store types and physical owner names remain exact.</summary>
    private static bool TryRenameManagedData(
        ModelManagedDataIntent source,
        RenameColumnIntent rename,
        PostflightResource renamedOwner,
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        PostflightResourceComparer ownerComparer,
        StringComparer comparer,
        out ModelManagedDataIntent result
    )
    {
        result = source;
        var ownColumns = ownerComparer.Equals(renamedOwner, Owner(source.Table, source.Schema));
        var keyColumns = source.KeyColumns;
        var columns = source.Columns;
        if (ownColumns
            && (!TryRenameMembers(keyColumns, rename, comparer, out keyColumns)
                || !TryRenameMembers(columns, rename, comparer, out columns)
                || ((source.KeyColumns.Contains(rename.Name, comparer)
                        || source.Columns.Contains(rename.Name, comparer))
                    && (source.KeyColumns.Contains(rename.NewName, comparer)
                        || source.Columns.Contains(rename.NewName, comparer)))))
        {
            return false;
        }

        var uniqueKeys = source switch
        {
            EnsureModelManagedDataIntent ensure => ensure.UniqueKeys,
            UpdateModelManagedDataIntent update => update.UniqueKeys,
            _ => null,
        };

        if (ownColumns && uniqueKeys is not null)
        {
            ExpectedModelManagedDataUniqueKeyDefinition[]? rewrittenKeys = null;
            // WHY: Replace only changed unique-key entries at their original snapshot positions.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < uniqueKeys.Count; index++)
            {
                var key = uniqueKeys[index];
                if (!TryRenameMembers(key.Columns, rename, comparer, out var members))
                {
                    return false;
                }

                if (!ReferenceEquals(members, key.Columns))
                {
                    rewrittenKeys ??= uniqueKeys.ToArray();
                    rewrittenKeys[index] = new ExpectedModelManagedDataUniqueKeyDefinition(members);
                }
            }

            if (rewrittenKeys is not null)
            {
                uniqueKeys = Array.AsReadOnly(rewrittenKeys);
            }
        }

        var foreignKeys = (source as DeleteModelManagedDataIntent)?.ForeignKeys;
        if (foreignKeys is not null)
        {
            ExpectedModelManagedDataForeignKeyDefinition[]? rewrittenKeys = null;
            // WHY: Replace only changed foreign-key entries without reordering the retained snapshot.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < foreignKeys.Count; index++)
            {
                var key = foreignKeys[index];
                var dependentMembers = key.Columns;
                var principalMembers = key.PrincipalColumns;
                if ((ownColumns && !TryRenameMembers(principalMembers, rename, comparer, out principalMembers))
                    || (ownerComparer.Equals(renamedOwner, Owner(key.Table, key.Schema))
                        && !TryRenameMembers(dependentMembers, rename, comparer, out dependentMembers)))
                {
                    return false;
                }

                if (!ReferenceEquals(principalMembers, key.PrincipalColumns)
                    || !ReferenceEquals(dependentMembers, key.Columns))
                {
                    rewrittenKeys ??= foreignKeys.ToArray();
                    rewrittenKeys[index] = new ExpectedModelManagedDataForeignKeyDefinition(
                        key.Table,
                        dependentMembers,
                        principalMembers,
                        key.Schema);
                }
            }

            if (rewrittenKeys is not null)
            {
                foreignKeys = Array.AsReadOnly(rewrittenKeys);
            }
        }

        result = source switch
        {
            EnsureModelManagedDataIntent ensure when
                !ReferenceEquals(keyColumns, source.KeyColumns)
                || !ReferenceEquals(columns, source.Columns)
                || !ReferenceEquals(uniqueKeys, ensure.UniqueKeys) =>
                new EnsureModelManagedDataIntent(ensure, keyColumns, columns, uniqueKeys!),
            UpdateModelManagedDataIntent update when !ReferenceEquals(keyColumns, source.KeyColumns)
                || !ReferenceEquals(columns, source.Columns)
                || !ReferenceEquals(uniqueKeys, update.UniqueKeys) =>
                new UpdateModelManagedDataIntent(update, keyColumns, columns, uniqueKeys!),
            DeleteModelManagedDataIntent delete when !ReferenceEquals(keyColumns, source.KeyColumns)
                || !ReferenceEquals(columns, source.Columns)
                || !ReferenceEquals(foreignKeys, delete.ForeignKeys) => new DeleteModelManagedDataIntent(
                    delete,
                    keyColumns,
                    columns,
                    foreignKeys!),
            _ => source,
        };

        return true;

        PostflightResource Owner(
            string table,
            string? schema
        ) => new(
            PostflightResourceKind.Table,
            normalizer is null ? schema : normalizer.NormalizeSchema(schema),
            table,
            null);
    }

    /// <summary>Snapshots just a renamed identifier list, rejecting a non-injective native member mapping.</summary>
    private static bool TryRenameMembers(
        IReadOnlyList<string> members,
        RenameColumnIntent rename,
        StringComparer comparer,
        out IReadOnlyList<string> result
    )
    {
        result = members;
        string[]? rewritten = null;
        // WHY: Rewrite the matching member in its original position without reordering composite keys.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < members.Count; index++)
        {
            if (!comparer.Equals(members[index], rename.Name))
            {
                continue;
            }

            if (members.Any(member => comparer.Equals(member, rename.NewName)))
            {
                return false;
            }

            rewritten ??= members.ToArray();
            rewritten[index] = rename.NewName;
        }

        if (rewritten is not null)
        {
            result = Array.AsReadOnly(rewritten);
        }

        return true;
    }

    private sealed class PendingManagedData(
        int ordinal,
        int safeOrdinal,
        SafeMigrationOperation operation
    )
    {
        public int Ordinal { get; } = ordinal;

        public int SafeOrdinal { get; } = safeOrdinal;

        public SafeMigrationOperation Operation { get; set; } = operation;

        public bool Valid { get; set; } = true;
    }
}
