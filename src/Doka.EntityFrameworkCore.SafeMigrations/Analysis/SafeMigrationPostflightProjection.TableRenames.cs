namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPostflightProjection
{
    /// <summary>Builds complete final-table probes only for verified safe member-renaming streams.</summary>
    /// <remarks>All unaffected facets remain part of the fresh catalog comparison; no table check is skipped.</remarks>
    internal static List<SafeMigrationPostflightRenameProbe>? CreateRenamedTableProbes(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses,
        ISafeMigrationProviderObjectIdentityNormalizer? identity
    )
    {
        // WHY: Postflight usually has no column renames. Keep that path free
        // of dictionaries, projected definitions and another metadata batch.
        if (!operations.Any(static operation => operation is SafeMigrationOperation { Intent: RenameColumnIntent }))
        {
            return null;
        }

        var normalizer = identity?.GetPostflightIdentityNormalizer(operations);
        Dictionary<PostflightResource, PendingTable>? tables = null;
        Dictionary<int, SafeMigrationPostflightRenameProbe>? rewritten = null;
        var safeOrdinal = 0;
        // WHY: Keep whole-stream positions aligned with safe-only analyses when tracking rename lineage.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal] is not SafeMigrationOperation operation)
            {
                // WHY: Opaque/provider-owned structural effects cannot certify
                // a historical table's exact rename lineage. Existing final
                // catalog checks remain authoritative across this boundary.
                tables?.Clear();
                rewritten?.Clear();

                continue;
            }

            var analysisOrdinal = safeOrdinal++;
            var analysis = analyses[analysisOrdinal];
            if (normalizer?.IsPostflightIdentityProven(operation.Intent, ordinal) == false)
            {
                tables?.Clear();
                rewritten?.Clear();

                continue;
            }

            if (operation.Intent is EnsureTableIntent { Mode: SafeMigrationTableMode.StrictDefinition } table)
            {
                tables ??= new Dictionary<PostflightResource, PendingTable>(new PostflightResourceComparer(normalizer));
                var tableResource = Resource(table.Definition.Table, table.Definition.Schema);
                if (tables.TryGetValue(tableResource, out var previous))
                {
                    rewritten?.Remove(previous.SafeOrdinal);
                }

                tables[tableResource] = new PendingTable(ordinal, analysisOrdinal, operation);

                continue;
            }

            if (tables is null
                || !TryCreateResources(
                    operation.Intent,
                    normalizer,
                    primaryKeyNameIndependent: false,
                    sharedUniqueIndexIdentity: false,
                    analysis,
                    out var resource,
                    out _))
            {
                continue;
            }

            var owner = resource with
            {
                Kind = PostflightResourceKind.Table,
                Name = null,
            };
            if (!tables.TryGetValue(owner, out var pending))
            {
                continue;
            }

            if (PreservesKnownTableShape(
                    operation.Intent,
                    ((EnsureTableIntent)pending.Operation.Intent).Definition,
                    normalizer))
            {
                // WHY: Independent indexes are not strict-table facets. Exact
                // column reassertions also leave this known shape unchanged.
                // Retain lineage so a later rename can transform it again.
                continue;
            }

            if (operation.Intent is not RenameColumnIntent rename
                || !analysis.PostconditionSatisfied
                || !SafeMigrationPreflightProjection.TryProjectKnownColumnRename(
                    ((EnsureTableIntent)pending.Operation.Intent).Definition,
                    rename,
                    normalizer,
                    out var definition))
            {
                tables.Remove(owner);
                rewritten?.Remove(pending.SafeOrdinal);

                continue;
            }

            var rewrittenOperation = new SafeMigrationOperation(
                new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition),
                pending.Operation.Policy)
            {
                IsDestructiveChange = pending.Operation.IsDestructiveChange,
            };

            foreach (var annotation in pending.Operation.GetAnnotations())
            {
                rewrittenOperation.AddAnnotation(annotation.Name, annotation.Value);
            }

            pending = pending with
            {
                Operation = rewrittenOperation,
            };
            tables[owner] = pending;
            rewritten ??= [];
            rewritten[pending.SafeOrdinal] = new SafeMigrationPostflightRenameProbe(
                pending.Ordinal,
                pending.SafeOrdinal,
                rewrittenOperation);
        }

        return rewritten is { Count: > 0 } ? rewritten.Values.ToList() : null;

        PostflightResource Resource(
            string table,
            string? schema
        ) => new(
            PostflightResourceKind.Table,
            normalizer is null ? schema : normalizer.NormalizeSchema(schema),
            table,
            null);
    }

    /// <summary>Recognizes operations that leave every declared strict-table facet unchanged.</summary>
    private static bool PreservesKnownTableShape(
        SafeMigrationIntent intent,
        ExpectedTableDefinition table,
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer
    )
    {
        if (intent is EnsureIndexIntent or DropIndexIntent or RenameIndexIntent or ModelManagedDataIntent)
        {
            // WHY: Managed DML changes rows, not declared table facets. Its
            // separate row contract is reclassified rather than inferred here.
            return true;
        }

        if (intent is EnsureCheckConstraintIntent check)
        {
            var targetCheck = SafeMigrationCapturedExpressionNormalization.NormalizeCheck(
                check.Definition,
                normalizer?.FoldUnquotedCapturedSqlIdentifiers == true);

            // WHY: A certified rename can introduce native member quotes.
            // The same captured CHECK must retain its complete-table probe;
            // changed source roles still terminate this exact shape lineage.
            return table.CheckConstraints.Any(candidate =>
                SafeMigrationPreflightProjection.AreProjectedCheckSemanticsEquivalent(
                    candidate,
                    targetCheck,
                    normalizer));
        }

        if (intent is not EnsureColumnIntent column)
        {
            return false;
        }

        var comparer = normalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal;
        var target = SafeMigrationCapturedExpressionNormalization.NormalizeColumn(
            column.Definition,
            normalizer?.FoldUnquotedCapturedSqlIdentifiers == true);

        foreach (var candidate in table.Columns)
        {
            if (SafeMigrationDefinitionEquivalence.Column(candidate, target, comparer)
                || (normalizer?.CapturedSqlIdentifierDelimiter is { } delimiter
                    && SafeMigrationDefinitionEquivalence.ColumnWithNativeColumnQuoteEquivalence(
                        candidate,
                        target,
                        comparer,
                        delimiter,
                        normalizer.FoldUnquotedCapturedSqlIdentifiers)))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct PendingTable(
        int Ordinal,
        int SafeOrdinal,
        SafeMigrationOperation Operation
    );
}

/// <summary>Aligns a transformed final table or row probe with its untouched original report operation.</summary>
internal readonly record struct SafeMigrationPostflightRenameProbe(
    int OperationOrdinal,
    int SafeOrdinal,
    SafeMigrationOperation Operation
);
