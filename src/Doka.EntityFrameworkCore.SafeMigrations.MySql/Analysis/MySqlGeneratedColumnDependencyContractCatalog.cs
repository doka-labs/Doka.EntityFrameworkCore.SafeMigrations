namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Captures untrusted generated-member candidates for exact authored rename and removal operations.</summary>
/// <remarks>Native complete-set equality must certify these candidates before dependency reuse.</remarks>
internal sealed class MySqlGeneratedColumnDependencyContractCatalog
{
    private static readonly MySqlGeneratedColumnDependencyContractCatalog s_empty = new([]);
    private static readonly MySqlGeneratedColumnDependencyContract s_parsedEmpty = new([], true, false);
    private readonly Dictionary<SafeMigrationOperation, MySqlGeneratedColumnDependencyContract?> _contracts;
    private readonly Dictionary<SafeMigrationOperation, MySqlGeneratedColumnDependencyContract>? _modelContracts;

    private MySqlGeneratedColumnDependencyContractCatalog(
        Dictionary<SafeMigrationOperation, MySqlGeneratedColumnDependencyContract?> contracts,
        Dictionary<SafeMigrationOperation, MySqlGeneratedColumnDependencyContract>? modelContracts = null
    )
    {
        _contracts = contracts;
        _modelContracts = modelContracts;
    }

    /// <summary>Gets the conditional prefix for this exact instance, never an equal-looking replacement.</summary>
    internal MySqlGeneratedColumnDependencyContract? Get(
        SafeMigrationOperation operation
    ) => _contracts.GetValueOrDefault(operation);

    /// <summary>Gets only this operation's whole target-model candidate, never an accepted prefix.</summary>
    internal MySqlGeneratedColumnDependencyContract? GetModelCandidate(
        SafeMigrationOperation operation
    ) => _modelContracts?.GetValueOrDefault(operation);

    /// <summary>Builds candidates whose complete identity must be independently certified by native metadata.</summary>
    /// <param name="operations">The complete authored stream, including ordinary provider and SQL boundaries.</param>
    /// <param name="currentDatabase">The independently guarded database used to bind equivalent qualifiers.</param>
    /// <param name="tableComparer">The verified owner comparer; column identity stays case-insensitive.</param>
    /// <param name="targetModelCandidates">Whole model shapes bound to exact original operation instances.</param>
    internal static MySqlGeneratedColumnDependencyContractCatalog Create(
        IReadOnlyList<MigrationOperation> operations,
        string? currentDatabase = null,
        StringComparer? tableComparer = null,
        IReadOnlyDictionary<SafeMigrationOperation, IReadOnlyList<ExpectedTableDefinition>>? targetModelCandidates = null
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!operations.Any(static operation => operation is SafeMigrationOperation
            {
                Intent: RenameColumnIntent or DropColumnIntent
            }))
        {
            return s_empty;
        }

        var owners = new Dictionary<MySqlTableIdentity, Owner>(
            new MySqlTableIdentityComparer(tableComparer ?? StringComparer.Ordinal));

        var contracts = new Dictionary<SafeMigrationOperation, MySqlGeneratedColumnDependencyContract?>(
            ReferenceEqualityComparer.Instance);

        Dictionary<SafeMigrationOperation, MySqlGeneratedColumnDependencyContract>? modelContracts = null;
        Dictionary<ExpectedTableDefinition, Owner>? modelOwners = null;
        var repeatedOperation = false;

        foreach (var candidate in operations)
        {
            if (candidate is SafeMigrationOperation { Intent: RenameColumnIntent or DropColumnIntent } keyed)
            {
                // WHY: The same object can occur under different prefixes or
                // historical models. A later occurrence cannot rebind either
                // candidate merely because its reference key is identical.
                repeatedOperation = !contracts.TryAdd(keyed, null);
                if (repeatedOperation)
                {
                    contracts[keyed] = null;
                    modelContracts?.Remove(keyed);
                }
                else if (!keyed.GetAnnotations().Any()
                         && CaptureModelCandidate(keyed) is { } modelContract)
                {
                    modelContracts ??= new Dictionary<SafeMigrationOperation, MySqlGeneratedColumnDependencyContract>(
                        ReferenceEqualityComparer.Instance);

                    modelContracts.Add(keyed, modelContract);
                }
            }

            if (candidate is InsertDataOperation or UpdateDataOperation or DeleteDataOperation
                && !candidate.GetAnnotations().Any())
            {
                // WHY: Typed row changes do not redefine generated members.
                // Retaining this authored candidate grants no metadata proof:
                // runtime still certifies the complete actual generated set.
                continue;
            }

            if (candidate is not SafeMigrationOperation operation
                || operation.GetAnnotations().Any())
            {
                // WHY: An ordinary operation or unknown annotation has no
                // accepted SafeMigrations target contract. It cannot extend
                // another owner's conditional generated-member lifetime.
                owners.Clear();

                continue;
            }

            switch (operation.Intent)
            {
                case EnsureTableIntent table:
                    var tableKey = Key(table.Definition.Table, table.Definition.Schema);
                    if (table.Mode != SafeMigrationTableMode.StrictDefinition
                        || operation.Policy == SafeMigrationPolicy.ExistenceOnly)
                    {
                        owners.Remove(tableKey);
                    }
                    else
                    {
                        // WHY: Reaching a later command under an exact-target
                        // policy requires this declaration to have succeeded.
                        // Runtime still proves the complete actual generated
                        // set; this expected prefix is not itself live evidence.
                        owners[tableKey] = new Owner(table.Definition.Columns);
                    }

                    break;
                case EnsureColumnIntent column:
                    ChangeColumn(operation.Policy, column.Table, column.Schema, column.Definition);
                    break;
                case AlterColumnIntent column:
                    ChangeColumn(operation.Policy, column.Table, column.Schema, column.Definition);
                    break;
                case DropColumnIntent column:
                    CaptureDrop(operation, column);
                    break;
                case RenameColumnIntent rename:
                    CaptureRename(operation, rename);
                    break;
                case DropTableIntent table:
                    owners.Remove(Key(table.Table, table.Schema));
                    break;
                case RenameTableIntent table:
                    var source = Key(table.Name, table.Schema);
                    var target = Key(table.NewName ?? table.Name, table.NewSchema ?? table.Schema);
                    if (owners.Remove(source, out var owner))
                    {
                        if (owners.Remove(target))
                        {
                            // A rejected destination collision cannot certify
                            // either owner's later conditional source state.
                        }
                        else
                        {
                            owners[target] = owner;
                        }
                    }

                    break;
                case DropSchemaIntent:
                    owners.Clear();
                    break;
                case ModelManagedDataIntent:
                case EnsureSchemaIntent:
                case EnsureIndexIntent:
                case DropIndexIntent:
                case RenameIndexIntent:
                case EnsurePrimaryKeyIntent:
                case DropPrimaryKeyIntent:
                case EnsureUniqueConstraintIntent:
                case DropUniqueConstraintIntent:
                case EnsureCheckConstraintIntent:
                case DropCheckConstraintIntent:
                case EnsureForeignKeyIntent:
                case DropForeignKeyIntent:
                    // These typed operations declare no generated-member effect.
                    // Their own guards remain authoritative before this rename.
                    break;
                default:
                    owners.Clear();
                    break;
            }
        }

        if (modelContracts is null or { Count: 0 })
        {
            var hasAuthoredCandidate = false;
            foreach (var contract in contracts.Values)
            {
                if (contract is not null)
                {
                    hasAuthoredCandidate = true;

                    break;
                }
            }

            if (!hasAuthoredCandidate)
            {
                return s_empty;
            }
        }

        return new MySqlGeneratedColumnDependencyContractCatalog(contracts, modelContracts);

        MySqlTableIdentity Key(
            string table,
            string? schema
        ) => MySqlTableIdentity.Create(table, schema, currentDatabase);

        MySqlGeneratedColumnDependencyContract? CaptureModelCandidate(
            SafeMigrationOperation operation
        )
        {
            if (targetModelCandidates is null
                || !targetModelCandidates.TryGetValue(operation, out var candidates))
            {
                return null;
            }

            var (table, schema, source, excludeSource) = operation.Intent switch
            {
                DropColumnIntent drop => (drop.Table, drop.Schema, drop.Name, true),
                RenameColumnIntent rename => (rename.Table, rename.Schema, rename.Name, false),
                _ => throw new UnreachableException(),
            };

            var ownerKey = Key(table, schema);
            ExpectedTableDefinition? selected = null;
            foreach (var candidate in candidates)
            {
                if (!owners.Comparer.Equals(ownerKey, Key(candidate.Table, candidate.Schema)))
                {
                    continue;
                }

                if (selected is not null)
                {
                    // Never union models or resolve a native owner collision
                    // by whichever declaration happened to be listed first.
                    return null;
                }

                selected = candidate;
            }

            if (selected is null)
            {
                return null;
            }

            modelOwners ??= new Dictionary<ExpectedTableDefinition, Owner>(ReferenceEqualityComparer.Instance);
            if (!modelOwners.TryGetValue(selected, out var owner))
            {
                owner = new Owner(selected.Columns);
                modelOwners.Add(selected, owner);
            }

            // Model views are never mutated by the authored stream. Every
            // returned whole candidate needs its own fresh native certificate.
            return owner.Snapshot(source, excludeSource);
        }

        void StoreAuthored(
            SafeMigrationOperation operation,
            MySqlGeneratedColumnDependencyContract contract
        )
        {
            if (!repeatedOperation)
            {
                contracts[operation] = contract;
            }
        }

        void ChangeColumn(
            SafeMigrationPolicy policy,
            string table,
            string? schema,
            ExpectedColumnDefinition definition
        )
        {
            var key = Key(table, schema);
            if (policy == SafeMigrationPolicy.ExistenceOnly)
            {
                owners.Remove(key);
            }
            else
            {
                if (!owners.TryGetValue(key, out var owner))
                {
                    // A partial declaration only proposes a candidate. Native
                    // count and exact facets must prove every actual member.
                    owner = new Owner([]);
                    owners[key] = owner;
                }

                owner.Columns[definition.Name] = definition;
            }
        }

        void CaptureDrop(
            SafeMigrationOperation operation,
            DropColumnIntent drop
        )
        {
            var key = Key(drop.Table, drop.Schema);
            if (!owners.TryGetValue(key, out var owner))
            {
                // WHY: An empty candidate does not assert native absence.
                // Only the fresh complete zero-survivor count qualifies it.
                owner = new Owner([]);
                owners[key] = owner;
            }

            var contract = owner.Snapshot(drop.Name, excludeSource: true);
            StoreAuthored(operation, contract);

            if (!contract.HasOnlyParsedExpressions
                || contract.ReferencesSource)
            {
                owners.Remove(key);

                return;
            }

            owner.Columns.Remove(drop.Name);
        }

        void CaptureRename(
            SafeMigrationOperation operation,
            RenameColumnIntent rename
        )
        {
            var key = Key(rename.Table, rename.Schema);
            if (!owners.TryGetValue(key, out var owner))
            {
                return;
            }

            var contract = owner.Snapshot(rename.Name);
            StoreAuthored(operation, contract);

            if (!contract.HasOnlyParsedExpressions
                || contract.ReferencesSource
                || owner.Columns.ContainsKey(rename.NewName))
            {
                // MySQL's single-column rename cannot redefine dependent
                // generated expressions in the same ALTER. A refused or
                // unknown transition supplies no later complete prefix.
                owners.Remove(key);

                return;
            }

            if (owner.Columns.Remove(rename.Name, out var sourceColumn))
            {
                owner.Columns[rename.NewName] = SafeMigrationPreflightProjection.CopyProjectedColumn(
                    sourceColumn,
                    rename.NewName);
            }
        }
    }

    /// <summary>Tracks conditional members without native completeness, row, key or layout authority.</summary>
    private sealed class Owner
    {
        internal Owner(
            IReadOnlyList<ExpectedColumnDefinition> columns
        )
        {
            Columns = new Dictionary<string, ExpectedColumnDefinition>(columns.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                if (!Columns.TryAdd(column.Name, column))
                {
                    // Ambiguous native member identity cannot become an
                    // apparently complete generated dependency declaration.
                    IsAmbiguous = true;
                }
            }
        }

        internal Dictionary<string, ExpectedColumnDefinition> Columns { get; }
        private bool IsAmbiguous { get; }

        internal MySqlGeneratedColumnDependencyContract Snapshot(
            string source,
            bool excludeSource = false
        )
        {
            var generatedCount = 0;
            var parsed = !IsAmbiguous;
            var referencesSource = false;
            foreach (var column in Columns.Values)
            {
                if (!IncludesGeneratedMember(column, source, excludeSource))
                {
                    continue;
                }

                generatedCount++;
                if (column.ComputedExpression is not { } expression
                    || !SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression)
                    || !MySqlGeneratedColumnDependencies.ReferencesOnlyLocalColumns(expression))
                {
                    parsed = false;

                    continue;
                }

                referencesSource |= SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
                    expression,
                    source,
                    StringComparer.OrdinalIgnoreCase);
            }

            if (generatedCount == 0 && parsed)
            {
                // Every operation keeps its own reference-keyed entry. Only
                // this immutable, fully parsed empty value is interchangeable.
                return s_parsedEmpty;
            }

            // WHY: Snapshot only the immutable generated-member references.
            // Two allocation-free dictionary walks avoid a captured predicate
            // and a growable intermediate buffer for each DROP/rename.
            var generated = generatedCount == 0 ? [] : new ExpectedColumnDefinition[generatedCount];
            var ordinal = 0;
            foreach (var column in Columns.Values)
            {
                if (IncludesGeneratedMember(column, source, excludeSource))
                {
                    generated[ordinal++] = column;
                }
            }

            return new MySqlGeneratedColumnDependencyContract(generated, parsed, referencesSource);
        }

        private static bool IncludesGeneratedMember(
            ExpectedColumnDefinition column,
            string source,
            bool excludeSource
        ) => (!excludeSource || !StringComparer.OrdinalIgnoreCase.Equals(column.Name, source))
            && (column.ComputedExpression is not null || column.ComputedColumnSql is not null);
    }
}

/// <summary>Retains an immutable untrusted surviving-member candidate and its parsed dependency roles.</summary>
internal sealed class MySqlGeneratedColumnDependencyContract
{
    internal MySqlGeneratedColumnDependencyContract(
        ExpectedColumnDefinition[] generatedColumns,
        bool hasOnlyParsedExpressions,
        bool referencesSource
    )
    {
        GeneratedColumns = Array.AsReadOnly(generatedColumns);
        HasOnlyParsedExpressions = hasOnlyParsedExpressions;
        ReferencesSource = referencesSource;
    }

    /// <summary>Gets candidate surviving generated members; native completeness is not implied.</summary>
    internal IReadOnlyList<ExpectedColumnDefinition> GeneratedColumns { get; }

    /// <summary>Gets whether all members have structured, local, role-proven expressions.</summary>
    internal bool HasOnlyParsedExpressions { get; }

    /// <summary>Gets whether a parsed surviving generated member references the source as an identifier.</summary>
    internal bool ReferencesSource { get; }
}
