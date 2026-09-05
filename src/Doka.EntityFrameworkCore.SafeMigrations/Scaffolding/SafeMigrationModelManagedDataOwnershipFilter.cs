namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Applies the explicit excluded-table ownership boundary to provider-generated
/// model-managed-data differences.
/// </summary>
internal static class SafeMigrationModelManagedDataOwnershipFilter
{
    public static IReadOnlyList<MigrationOperation> Filter(
        IReadOnlyList<MigrationOperation> operations,
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        var sourceTables = IndexTables(source);
        var targetTables = IndexTables(target);
        List<MigrationOperation>? retained = null;

        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var operation = operations[ordinal]
                ?? throw new ArgumentException(
                    "The provider model difference cannot contain null operations.",
                    nameof(operations));

            if (IsExternallyOwned(operation, sourceTables, targetTables))
            {
                if (retained is null)
                {
                    retained = new List<MigrationOperation>(operations.Count - 1);

                    for (var retainedOrdinal = 0; retainedOrdinal < ordinal; retainedOrdinal++)
                    {
                        retained.Add(operations[retainedOrdinal]);
                    }
                }

                continue;
            }

            retained?.Add(operation);
        }

        return retained is null
            ? operations
            : retained.AsReadOnly();
    }

    private static Dictionary<TableIdentity, bool> IndexTables(
        IRelationalModel? model
    )
    {
        var tables = new Dictionary<TableIdentity, bool>();
        if (model is null)
        {
            return tables;
        }

        foreach (var table in model.Tables)
        {
            var identity = new TableIdentity(table.Name, table.Schema);
            if (!tables.TryAdd(identity, table.IsExcludedFromMigrations))
            {
                throw new InvalidOperationException(
                    $"SafeMigrations found ambiguous relational table metadata for {identity.DisplayName}.");
            }
        }

        return tables;
    }

    private static bool IsExternallyOwned(
        MigrationOperation operation,
        IReadOnlyDictionary<TableIdentity, bool> sourceTables,
        IReadOnlyDictionary<TableIdentity, bool> targetTables
    ) => operation switch
    {
        InsertDataOperation insert => RequiredConsistentExclusion(
            targetTables,
            sourceTables,
            new TableIdentity(insert.Table, insert.Schema),
            "insert target",
            "insert source"),
        DeleteDataOperation delete => RequiredConsistentExclusion(
            sourceTables,
            targetTables,
            new TableIdentity(delete.Table, delete.Schema),
            "delete source",
            "delete target"),
        UpdateDataOperation update => IsUpdateExternallyOwned(update, sourceTables, targetTables),
        _ => false,
    };

    private static bool IsUpdateExternallyOwned(
        UpdateDataOperation operation,
        IReadOnlyDictionary<TableIdentity, bool> sourceTables,
        IReadOnlyDictionary<TableIdentity, bool> targetTables
    )
    {
        var identity = new TableIdentity(operation.Table, operation.Schema);
        var sourceExcluded = RequiredExclusion(sourceTables, identity, "update source");
        var targetExcluded = RequiredExclusion(targetTables, identity, "update target");

        if (sourceExcluded != targetExcluded)
        {
            throw OwnershipTransition(
                identity,
                "source",
                sourceExcluded,
                "target",
                targetExcluded);
        }

        return sourceExcluded;
    }

    private static bool RequiredConsistentExclusion(
        IReadOnlyDictionary<TableIdentity, bool> requiredTables,
        IReadOnlyDictionary<TableIdentity, bool> transitionTables,
        TableIdentity identity,
        string requiredContext,
        string transitionContext
    )
    {
        var excluded = RequiredExclusion(requiredTables, identity, requiredContext);
        if (!transitionTables.TryGetValue(identity, out var transitionExcluded))
        {
            return excluded;
        }

        if (excluded != transitionExcluded)
        {
            throw OwnershipTransition(identity, transitionContext, transitionExcluded, requiredContext, excluded);
        }

        return excluded;
    }

    private static bool RequiredExclusion(
        IReadOnlyDictionary<TableIdentity, bool> tables,
        TableIdentity identity,
        string context
    ) => tables.TryGetValue(identity, out var excluded)
        ? excluded
        : throw new InvalidOperationException(
            $"SafeMigrations could not resolve the {context} table {identity.DisplayName} "
            + "while applying excluded-table model-managed-data ownership.");

    private static InvalidOperationException OwnershipTransition(
        TableIdentity identity,
        string firstContext,
        bool firstExcluded,
        string secondContext,
        bool secondExcluded
    ) => new(
        "SafeMigrations rejected model-managed-data ownership transition "
        + $"'model_managed_data_ownership_transition' for {identity.DisplayName}: "
        + $"{firstContext} excluded={firstExcluded}, {secondContext} excluded={secondExcluded}.");

    private readonly record struct TableIdentity(
        string Table,
        string? Schema
    )
    {
        public string DisplayName => Schema is null
            ? $"'{Table}'"
            : $"'{Schema}.{Table}'";
    }
}
