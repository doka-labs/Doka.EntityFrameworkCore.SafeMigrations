namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Associates one unexpected inventory entry with a renamed semantic probe.</summary>
/// <param name="UnexpectedObjectIndex">The zero-based inventory position.</param>
/// <param name="Operation">The provider-comparable probe operation.</param>
internal readonly record struct SafeMigrationSemanticCandidate(
    int UnexpectedObjectIndex,
    SafeMigrationOperation Operation
);

/// <summary>Creates bounded semantic probes for differently named physical objects.</summary>
internal static class SafeMigrationSemanticCandidateFactory
{
    /// <summary>Enumerates semantic probes without materializing the candidate cross-product.</summary>
    /// <param name="operations">The migration operations that define expected object shapes.</param>
    /// <param name="unexpectedObjects">The unexpected physical objects to compare.</param>
    /// <param name="defaultSchema">The provider default schema for unqualified expectations.</param>
    /// <param name="projectUniqueIndexesAsUniqueConstraints">
    /// Whether the provider inventories standalone unique indexes as unique constraints.
    /// </param>
    /// <returns>A lazy sequence of provider-comparable semantic candidates.</returns>
    public static IEnumerable<SafeMigrationSemanticCandidate> Create(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<SafeMigrationUnexpectedObject> unexpectedObjects,
        string? defaultSchema = null,
        bool projectUniqueIndexesAsUniqueConstraints = false
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(unexpectedObjects);

        return Enumerate(operations, unexpectedObjects, defaultSchema, projectUniqueIndexesAsUniqueConstraints);
    }

    private static IEnumerable<SafeMigrationSemanticCandidate> Enumerate(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<SafeMigrationUnexpectedObject> unexpectedObjects,
        string? defaultSchema,
        bool projectUniqueIndexesAsUniqueConstraints
    )
    {
        var expected = IndexExpectedOperations(operations, defaultSchema, projectUniqueIndexesAsUniqueConstraints);

        for (var index = 0; index < unexpectedObjects.Count; index++)
        {
            var unexpected = unexpectedObjects[index];
            if (unexpected.Table is not { } table
                || !expected.TryGetValue((unexpected.ObjectKind, unexpected.Schema, table), out var expectedOperations))
            {
                continue;
            }

            foreach (var operation in expectedOperations)
            {
                var candidate = RenameForCandidate(
                    operation,
                    unexpected,
                    defaultSchema,
                    projectUniqueIndexesAsUniqueConstraints);

                if (candidate is not null)
                {
                    yield return new SafeMigrationSemanticCandidate(index, candidate);
                }
            }
        }
    }

    private static Dictionary<
            (SafeMigrationDatabaseObjectKind Kind, string? Schema, string Table), List<SafeMigrationOperation>>
        IndexExpectedOperations(
            IReadOnlyList<MigrationOperation> operations,
            string? defaultSchema,
            bool projectUniqueIndexesAsUniqueConstraints
        )
    {
        var result =
            new Dictionary<(SafeMigrationDatabaseObjectKind Kind, string? Schema, string Table),
                List<SafeMigrationOperation>>();

        var seen =
            new HashSet<(SafeMigrationDatabaseObjectKind Kind, string? Schema, string Table, string Fingerprint)>();

        foreach (var operation in EnumerateExpectedOperations(operations))
        {
            foreach (var key in Identities(operation.Intent, defaultSchema, projectUniqueIndexesAsUniqueConstraints))
            {
                var fingerprint = SemanticFingerprint(
                    operation,
                    key,
                    defaultSchema,
                    projectUniqueIndexesAsUniqueConstraints);

                if (!seen.Add((key.Kind, key.Schema, key.Table, fingerprint)))
                {
                    continue;
                }

                if (!result.TryGetValue(key, out var values))
                {
                    values = [];
                    result.Add(key, values);
                }

                values.Add(operation);
            }
        }

        return result;
    }

    private static string SemanticFingerprint(
        SafeMigrationOperation operation,
        (SafeMigrationDatabaseObjectKind Kind, string? Schema, string Table) identity,
        string? defaultSchema,
        bool projectUniqueIndexesAsUniqueConstraints
    )
    {
        var normalized = RenameForCandidate(
                operation,
                new SafeMigrationUnexpectedObject(
                    identity.Kind,
                    identity.Schema,
                    identity.Table,
                    "doka_semantic_candidate",
                    "semantic_candidate"),
                defaultSchema,
                projectUniqueIndexesAsUniqueConstraints)
            ?? throw new InvalidOperationException(
                "The indexed SafeMigrations operation has no semantic-candidate projection.");

        return SafeMigrationContractFingerprint.Create([normalized]);
    }

    private static IEnumerable<(SafeMigrationDatabaseObjectKind Kind, string? Schema, string Table)> Identities(
        SafeMigrationIntent intent,
        string? defaultSchema,
        bool projectUniqueIndexesAsUniqueConstraints
    )
    {
        switch (intent)
        {
            case EnsureIndexIntent value:
                yield return (SafeMigrationDatabaseObjectKind.Index, value.Definition.Schema ?? defaultSchema,
                    value.Definition.Table);

                // A provider adapter may expose standalone unique indexes in
                // the same inventory family as UNIQUE constraints. Keep that
                // physical projection explicit instead of teaching Core a
                // provider identity.
                if (projectUniqueIndexesAsUniqueConstraints && value.Definition.Unique)
                {
                    yield return (SafeMigrationDatabaseObjectKind.UniqueConstraint,
                        value.Definition.Schema ?? defaultSchema, value.Definition.Table);
                }

                yield break;
            case EnsurePrimaryKeyIntent value:
                yield return (SafeMigrationDatabaseObjectKind.PrimaryKey, value.Definition.Schema ?? defaultSchema,
                    value.Definition.Table);
                yield break;
            case EnsureUniqueConstraintIntent value:
                yield return (SafeMigrationDatabaseObjectKind.UniqueConstraint,
                    value.Definition.Schema ?? defaultSchema, value.Definition.Table);
                yield break;
            case EnsureCheckConstraintIntent value:
                yield return (SafeMigrationDatabaseObjectKind.CheckConstraint, value.Definition.Schema ?? defaultSchema,
                    value.Definition.Table);
                yield break;
            case EnsureForeignKeyIntent value:
                yield return (SafeMigrationDatabaseObjectKind.ForeignKey, value.Definition.Schema ?? defaultSchema,
                    value.Definition.Table);
                yield break;
            default:
                throw new ArgumentOutOfRangeException(nameof(intent));
        }
    }

    private static IEnumerable<SafeMigrationOperation> EnumerateExpectedOperations(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        foreach (var operation in operations.OfType<SafeMigrationOperation>())
        {
            if (operation.Intent is EnsureIndexIntent
                or EnsurePrimaryKeyIntent
                or EnsureUniqueConstraintIntent
                or EnsureCheckConstraintIntent
                or EnsureForeignKeyIntent)
            {
                yield return operation;
            }

            if (operation.Intent is not EnsureTableIntent table)
            {
                continue;
            }

            if (table.Definition.PrimaryKey is not null)
            {
                yield return CreateOperation(new EnsurePrimaryKeyIntent(table.Definition.PrimaryKey));
            }

            foreach (var definition in table.Definition.UniqueConstraints)
            {
                yield return CreateOperation(new EnsureUniqueConstraintIntent(definition));
            }

            foreach (var definition in table.Definition.CheckConstraints)
            {
                yield return CreateOperation(new EnsureCheckConstraintIntent(definition));
            }

            foreach (var definition in table.Definition.ForeignKeys)
            {
                yield return CreateOperation(new EnsureForeignKeyIntent(definition));
            }
        }
    }

    private static SafeMigrationOperation? RenameForCandidate(
        SafeMigrationOperation operation,
        SafeMigrationUnexpectedObject unexpected,
        string? defaultSchema,
        bool projectUniqueIndexesAsUniqueConstraints
    ) => operation.Intent switch
    {
        EnsureIndexIntent value when
            Matches(
                unexpected,
                SafeMigrationDatabaseObjectKind.Index,
                value.Definition.Table,
                value.Definition.Schema,
                defaultSchema) => CreateOperation(new EnsureIndexIntent(Copy(value.Definition, unexpected.Name))),
        EnsureIndexIntent value when
            projectUniqueIndexesAsUniqueConstraints
            && value.Definition.Unique
            && Matches(
                unexpected,
                SafeMigrationDatabaseObjectKind.UniqueConstraint,
                value.Definition.Table,
                value.Definition.Schema,
                defaultSchema) => CreateOperation(new EnsureIndexIntent(Copy(value.Definition, unexpected.Name))),
        EnsurePrimaryKeyIntent value when
            Matches(
                unexpected,
                SafeMigrationDatabaseObjectKind.PrimaryKey,
                value.Definition.Table,
                value.Definition.Schema,
                defaultSchema) =>
            CreateOperation(
                new EnsurePrimaryKeyIntent(
                    new ExpectedPrimaryKeyDefinition(
                        unexpected.Name,
                        value.Definition.Table,
                        value.Definition.Columns,
                        value.Definition.Schema))),
        EnsureUniqueConstraintIntent value when
            Matches(
                unexpected,
                SafeMigrationDatabaseObjectKind.UniqueConstraint,
                value.Definition.Table,
                value.Definition.Schema,
                defaultSchema) =>
            CreateOperation(
                new EnsureUniqueConstraintIntent(
                    new ExpectedUniqueConstraintDefinition(
                        unexpected.Name,
                        value.Definition.Table,
                        value.Definition.Columns,
                        value.Definition.Schema))),
        EnsureCheckConstraintIntent value when
            Matches(
                unexpected,
                SafeMigrationDatabaseObjectKind.CheckConstraint,
                value.Definition.Table,
                value.Definition.Schema,
                defaultSchema) =>
            CreateOperation(new EnsureCheckConstraintIntent(Copy(value.Definition, unexpected.Name))),
        EnsureForeignKeyIntent value when Matches(
            unexpected,
            SafeMigrationDatabaseObjectKind.ForeignKey,
            value.Definition.Table,
            value.Definition.Schema,
            defaultSchema) => CreateOperation(
            new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition(
                    unexpected.Name,
                    value.Definition.Table,
                    value.Definition.Columns,
                    value.Definition.PrincipalTable,
                    value.Definition.PrincipalColumns,
                    value.Definition.Schema,
                    value.Definition.PrincipalSchema,
                    value.Definition.OnUpdate,
                    value.Definition.OnDelete))),
        _ => null,
    };

    private static bool Matches(
        SafeMigrationUnexpectedObject unexpected,
        SafeMigrationDatabaseObjectKind kind,
        string table,
        string? schema,
        string? defaultSchema
    ) => unexpected.ObjectKind == kind
        && StringComparer.Ordinal.Equals(unexpected.Table, table)
        && StringComparer.Ordinal.Equals(unexpected.Schema, schema ?? defaultSchema);

    private static ExpectedIndexDefinition Copy(
        ExpectedIndexDefinition definition,
        string name
    ) => new(
        name,
        definition.Table,
        definition.Keys,
        definition.Schema,
        definition.Unique,
        definition.Filter,
        definition.IncludedColumns,
        definition.Method,
        definition.NullsDistinct,
        definition.StructuredFilter);

    private static ExpectedCheckConstraintDefinition Copy(
        ExpectedCheckConstraintDefinition definition,
        string name
    ) => definition.Expression is null
        ? new ExpectedCheckConstraintDefinition(name, definition.Table, definition.Sql!, definition.Schema)
        : ExpectedCheckConstraintDefinition.FromExpression(
            name,
            definition.Table,
            definition.Expression,
            definition.Schema);

    private static SafeMigrationOperation CreateOperation(
        SafeMigrationIntent intent
    ) => new(intent, SafeMigrationPolicy.ThrowIfDifferent);
}
