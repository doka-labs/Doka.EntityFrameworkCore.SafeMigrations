namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Identifies safe operations whose resource state is replaced by a later safe
/// operation before postflight observes the final database catalog.
/// </summary>
internal sealed partial class SafeMigrationPostflightProjection
{
    private readonly HashSet<int> _supersededOrdinals = [];
    private HashSet<int>? _identityUnprovenOrdinals;

    /// <summary>Creates the final-writer projection for one ordered migration operation stream.</summary>
    /// <param name="operations">The complete operation stream in execution order.</param>
    /// <param name="objectIdentityNormalizer">
    /// The optional provider identity normalizer established by live analysis.
    /// </param>
    /// <param name="projectedKeyAnalyzer">The optional provider capability for name-independent PK identity.</param>
    /// <param name="liveAnalyses">Fresh final-catalog analyses aligned with the safe subset only.</param>
    /// <param name="renamedManagedIntents">Verified renamed row metadata keyed by original stream position.</param>
    public SafeMigrationPostflightProjection(
        IReadOnlyList<MigrationOperation> operations,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer = null,
        ISafeMigrationProjectedKeyAnalyzer? projectedKeyAnalyzer = null,
        IReadOnlyList<SafeMigrationProviderAnalysis>? liveAnalyses = null,
        IReadOnlyDictionary<int, ModelManagedDataIntent>? renamedManagedIntents = null
    )
    {
        var postflightNormalizer = objectIdentityNormalizer?.GetPostflightIdentityNormalizer(operations);
        var finalWriters = new HashSet<PostflightResource>(
            new PostflightResourceComparer(postflightNormalizer));
        var safeOrdinal = liveAnalyses?.Count ?? 0;
        var primaryKeyNameIndependent = projectedKeyAnalyzer?.PrimaryKeyUsesNameIndependentIdentity == true;
        var sharedUniqueIndexIdentity = projectedKeyAnalyzer?.SharesUniqueConstraintAndIndexIdentity == true;

        // Postflight observes only the final catalog. Walk backwards so the
        // final safe writer for one exact resource remains authoritative while
        // earlier transient states cannot make an ordered replacement fail.
        // Provider-certified pass-through operations never enter this
        // reduction because they own no SafeMigrations catalog resource.
        // WHY: Reverse traversal gives the final operation precedence when proving final catalog state.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = operations.Count - 1; ordinal >= 0; ordinal--)
        {
            if (operations[ordinal] is not SafeMigrationOperation safeOperation)
            {
                continue;
            }

            SafeMigrationProviderAnalysis? analysis = null;
            if (liveAnalyses is not null)
            {
                if (safeOrdinal == 0)
                {
                    throw new ArgumentException(
                        "Final-catalog analyses must match the safe operation subset.", nameof(liveAnalyses));
                }

                analysis = liveAnalyses[--safeOrdinal];
            }

            if (postflightNormalizer?.IsPostflightIdentityProven(safeOperation.Intent, ordinal) == false)
            {
                // WHY: A final search path can resolve an earlier unqualified
                // contract to another physical owner. Neither its fresh Missing
                // result nor a later writer can certify that historical identity.
                _identityUnprovenOrdinals ??= [];
                _identityUnprovenOrdinals.Add(ordinal);

                continue;
            }

            // WHY: Resource reduction must use the same certified renamed key
            // metadata as its fresh row probe. Reports and historical identity
            // qualification still refer to the untouched authored operation.
            var resourceIntent = renamedManagedIntents?.GetValueOrDefault(ordinal) ?? safeOperation.Intent;
            if (!TryCreateResources(
                    resourceIntent,
                    postflightNormalizer,
                    primaryKeyNameIndependent,
                    sharedUniqueIndexIdentity,
                    analysis,
                    out var primaryResource,
                    out var secondaryResource))
            {
                continue;
            }

            var superseded = !finalWriters.Add(primaryResource);
            if (secondaryResource.Kind != PostflightResourceKind.Unknown)
            {
                // WHY: A rename makes earlier source and target writers
                // transient, but its own postcondition proves only source
                // absence. A later target-only writer must not suppress that
                // source check.
                finalWriters.Add(secondaryResource);
            }

            if (superseded)
            {
                _supersededOrdinals.Add(ordinal);
            }
        }

        if (safeOrdinal != 0)
        {
            throw new ArgumentException(
                "Final-catalog analyses must match the safe operation subset.", nameof(liveAnalyses));
        }
    }

    /// <summary>Determines whether a later safe operation owns the final state of the same resource.</summary>
    /// <param name="ordinal">The zero-based operation position in the projected stream.</param>
    /// <returns>
    /// <see langword="true" /> when postflight must evaluate a later safe writer instead of this operation;
    /// otherwise, <see langword="false" />.
    /// </returns>
    public bool IsSuperseded(
        int ordinal
    ) => _supersededOrdinals.Contains(ordinal);

    /// <summary>Determines whether final-catalog lookup cannot prove this historical physical identity.</summary>
    /// <param name="ordinal">The complete-stream operation position.</param>
    /// <returns>True when verification must retain an identity failure instead of trusting final lookup.</returns>
    public bool IsIdentityUnproven(int ordinal) => _identityUnprovenOrdinals?.Contains(ordinal) == true;

    private static bool TryCreateResources(
        SafeMigrationIntent intent,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer,
        bool primaryKeyNameIndependent,
        bool sharedUniqueIndexIdentity,
        SafeMigrationProviderAnalysis? analysis,
        out PostflightResource primaryResource,
        out PostflightResource secondaryResource
    )
    {
        string? Normalize(
            string? schema
        ) => objectIdentityNormalizer is null
            ? schema
            : objectIdentityNormalizer.NormalizeSchema(schema);

        primaryResource = intent switch
        {
            EnsureSchemaIntent value =>
                new PostflightResource(PostflightResourceKind.Schema, Normalize(value.Name), null, null),
            DropSchemaIntent value =>
                new PostflightResource(PostflightResourceKind.Schema, Normalize(value.Name), null, null),
            EnsureTableIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Table,
                    Normalize(value.Definition.Schema),
                    value.Definition.Table,
                    null),
            DropTableIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Table,
                    Normalize(value.Schema),
                    value.Table,
                    null),
            RenameTableIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Table,
                    Normalize(value.Schema),
                    value.Name,
                    null),
            EnsureColumnIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Column,
                    Normalize(value.Schema),
                    value.Table,
                    value.Definition.Name),
            DropColumnIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Column,
                    Normalize(value.Schema),
                    value.Table,
                    value.Name),
            RenameColumnIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Column,
                    Normalize(value.Schema),
                    value.Table,
                    value.Name),
            AlterColumnIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Column,
                    Normalize(value.Schema),
                    value.Table,
                    value.Definition.Name),
            EnsureIndexIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Index,
                    Normalize(value.Definition.Schema),
                    value.Definition.Table,
                    PhysicalResourceName(value.Definition.Name, analysis)),
            DropIndexIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Index,
                    Normalize(value.Schema),
                    value.Table,
                    value.Name),
            RenameIndexIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Index,
                    Normalize(value.Schema),
                    value.Table,
                    value.Name),
            EnsurePrimaryKeyIntent value =>
                new PostflightResource(
                    PostflightResourceKind.PrimaryKey,
                    Normalize(value.Definition.Schema),
                    value.Definition.Table,
                    PrimaryKeyResourceName(value.Definition.Name, primaryKeyNameIndependent, analysis)),
            DropPrimaryKeyIntent value =>
                new PostflightResource(
                    PostflightResourceKind.PrimaryKey,
                    Normalize(value.Schema),
                    value.Table,
                    PrimaryKeyResourceName(value.Name, primaryKeyNameIndependent, analysis)),
            EnsureUniqueConstraintIntent value =>
                new PostflightResource(
                    sharedUniqueIndexIdentity ? PostflightResourceKind.Index : PostflightResourceKind.UniqueConstraint,
                    Normalize(value.Definition.Schema),
                    value.Definition.Table,
                    PhysicalResourceName(value.Definition.Name, analysis)),
            DropUniqueConstraintIntent value =>
                new PostflightResource(
                    sharedUniqueIndexIdentity ? PostflightResourceKind.Index : PostflightResourceKind.UniqueConstraint,
                    Normalize(value.Schema),
                    value.Table,
                    value.Name),
            EnsureCheckConstraintIntent value => new PostflightResource(
                PostflightResourceKind.CheckConstraint,
                Normalize(value.Definition.Schema),
                value.Definition.Table,
                value.Definition.Name),
            DropCheckConstraintIntent value => new PostflightResource(
                PostflightResourceKind.CheckConstraint,
                Normalize(value.Schema),
                value.Table,
                value.Name),
            EnsureForeignKeyIntent value => new PostflightResource(
                PostflightResourceKind.ForeignKey,
                Normalize(value.Definition.Schema),
                value.Definition.Table,
                value.Definition.Name),
            DropForeignKeyIntent value => new PostflightResource(
                PostflightResourceKind.ForeignKey,
                Normalize(value.Schema),
                value.Table,
                value.Name),
            ModelManagedDataIntent value => new PostflightResource(
                PostflightResourceKind.ModelManagedData,
                Normalize(value.Schema),
                value.Table,
                ModelManagedKeySet(value, objectIdentityNormalizer)),
            _ => default,
        };

        // WHY: A rename owns the final state of both its source and target.
        // Registering both identities prevents an earlier target drop or source
        // ensure from failing postflight after the ordered rename succeeds.
        secondaryResource = intent switch
        {
            RenameTableIntent value => new PostflightResource(
                PostflightResourceKind.Table,
                Normalize(value.NewSchema ?? value.Schema),
                value.NewName ?? value.Name,
                null),
            RenameColumnIntent value => new PostflightResource(
                PostflightResourceKind.Column,
                Normalize(value.Schema),
                value.Table,
                value.NewName),
            RenameIndexIntent value => new PostflightResource(
                PostflightResourceKind.Index,
                Normalize(value.Schema),
                value.Table,
                value.NewName),
            _ => default,
        };

        return primaryResource.Kind != PostflightResourceKind.Unknown;
    }

    /// <summary>Uses exact named contracts or verified physical aliases, never diagnostic mismatch names.</summary>
    /// <param name="authoredName">The named Ensure or Drop contract.</param>
    /// <param name="nameIndependent">Whether the provider explicitly certifies singleton PK identity.</param>
    /// <param name="analysis">The fresh final-catalog evidence for this exact operation.</param>
    private static string? PrimaryKeyResourceName(
        string authoredName,
        bool nameIndependent,
        SafeMigrationProviderAnalysis? analysis
    )
    {
        if (nameIndependent)
        {
            return null;
        }

        // WHY: A wrong-name NoOp Drop(B) does not replace Ensure(A). A final
        // Matching result can independently bind a semantic alias to its real
        // physical name; Missing/Different cannot reconstruct a removed alias's
        // historical identity and must retain the unsatisfied named contract.

        return PhysicalResourceName(authoredName, analysis);
    }

    /// <summary>Uses a physical semantic alias only when fresh matching evidence proves that exact identity.</summary>
    /// <param name="authoredName">The unsuperseded authored contract when physical identity is unproven.</param>
    /// <param name="analysis">The final live result, not an earlier diagnostic mismatch.</param>
    private static string PhysicalResourceName(string authoredName, SafeMigrationProviderAnalysis? analysis) =>
        analysis is
        {
            ObservedState: SafeMigrationObservedState.Matching,
            MatchedObjectName: { Length: > 0 } physicalName,
        }
            ? physicalName
            : authoredName;

    private enum PostflightResourceKind
    {
        Unknown,
        Schema,
        Table,
        Column,
        Index,
        PrimaryKey,
        UniqueConstraint,
        CheckConstraint,
        ForeignKey,
        ModelManagedData,
    }

    private readonly record struct PostflightResource(
        PostflightResourceKind Kind,
        string? Schema,
        string? Table,
        string? Name
    );

    private sealed class PostflightResourceComparer(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer
    ) : IEqualityComparer<PostflightResource>
    {
        private readonly StringComparer _identifierComparer = normalizer?.IdentifierComparer ?? StringComparer.Ordinal;
        private readonly StringComparer _columnIdentifierComparer =
            normalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal;

        private readonly StringComparer _foreignKeyIdentifierComparer =
            normalizer?.ForeignKeyIdentifierComparer ?? StringComparer.Ordinal;

        private readonly StringComparer _indexIdentifierComparer =
            normalizer?.IndexIdentifierComparer ?? StringComparer.Ordinal;

        public bool Equals(
            PostflightResource left,
            PostflightResource right
        ) => left.Kind == right.Kind
            && StringComparer.Ordinal.Equals(left.Schema, right.Schema)
            && _identifierComparer.Equals(left.Table, right.Table)
            && NameComparer(left.Kind).Equals(left.Name, right.Name);

        public int GetHashCode(
            PostflightResource value
        ) => HashCode.Combine(
            value.Kind,
            value.Schema is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Schema),
            value.Table is null ? 0 : _identifierComparer.GetHashCode(value.Table),
            value.Name is null ? 0 : NameComparer(value.Kind).GetHashCode(value.Name));

        private StringComparer NameComparer(
            PostflightResourceKind kind
        ) => kind switch
        {
            PostflightResourceKind.Column => _columnIdentifierComparer,
            PostflightResourceKind.ForeignKey => _foreignKeyIdentifierComparer,
            PostflightResourceKind.Index => _indexIdentifierComparer,
            _ => _identifierComparer,
        };
    }

    private static string ModelManagedKeySet(
        ModelManagedDataIntent intent,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer
    )
    {
        var keyColumns = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(
            intent.KeyColumns, objectIdentityNormalizer, out var keyOrdinals);

        var rowFingerprints = new string[intent.RowCount];
        // WHY: Build one canonical fingerprint for each authored row without copying the matrix.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var row = 0; row < intent.RowCount; row++)
        {
            using var rowWriter = new CanonicalHashWriter();
            // WHY: Pair each canonical key label with its value through the same ordinal mapping.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                var ordinal = keyOrdinals?[column] ?? column;
                rowWriter.Add(keyColumns[ordinal]);
                SafeMigrationModelManagedValue.Write(rowWriter, intent.KeyValues.GetUnsafeValue(row, ordinal));
            }

            rowFingerprints[row] = rowWriter.GetHash();
        }

        Array.Sort(rowFingerprints, StringComparer.Ordinal);
        using var writer = new CanonicalHashWriter();
        foreach (var fingerprint in rowFingerprints)
        {
            writer.Add(fingerprint);
        }

        return writer.GetHash();
    }
}
