namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private Dictionary<LiveColumnIdentity, LiveColumnStorage>? _livePrerequisiteColumns;
    private Dictionary<LiveColumnTypeIdentity, LiveColumnStorage>? _livePrerequisiteTypes;
    private Dictionary<ExpectedForeignKeyDefinition, LiveForeignKeyPrerequisites>? _liveForeignKeyPrerequisites;
    private HashSet<LiveForeignKeyNameIdentity>? _requestedLiveConstraintNames;
    private Dictionary<LiveForeignKeyNameIdentity, string>? _liveConstraintCollisionKinds;
    private HashSet<LiveForeignKeyNameIdentity>? _removedLiveConstraintNames;
    private Dictionary<LiveForeignKeyNameIdentity, string>? _occupiedProjectedConstraintNames;

    /// <inheritdoc />
    public bool HasLiveColumn(string table, string? schema, string column)
        => _livePrerequisiteColumns?.ContainsKey(LiveColumnKey(table, schema, column)) == true;

    /// <inheritdoc />
    public bool RequiresLiveForeignKeyQualification(EnsureForeignKeyIntent intent)
        => _liveForeignKeyPrerequisites?.TryGetValue(intent.Definition, out var captured) == true
            && captured.BothOwnersExist;

    /// <inheritdoc />
    public IReadOnlyList<SafeMigrationLiveCandidateKey>? GetLiveForeignKeyCandidateKeys(
        EnsureForeignKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (_liveForeignKeyPrerequisites is null
            || !_liveForeignKeyPrerequisites.TryGetValue(intent.Definition, out var captured)
            || !captured.OwnersCompatible
            || _occupiedProjectedConstraintNames?.ContainsKey(LiveForeignKeyName(
                intent.Definition.Table, intent.Definition.Schema, intent.Definition.Name)) == true
            || !captured.DependentExists && !IsAcceptedNewForeignKeyOwner(intent.Definition, columns)
            || captured.CollisionKind is not null && !IsAcceptedNewForeignKeyOwner(intent.Definition, columns)
                && _removedLiveConstraintNames?.Contains(
                    LiveForeignKeyName(intent.Definition.Table, intent.Definition.Schema,
                        intent.Definition.Name)) != true)
        {
            return null;
        }

        var definition = intent.Definition;
        for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
        {
            if (!TryGetCurrentForeignKeyStorage(definition.Table, definition.Schema,
                    definition.Columns[ordinal], columns, out var dependent)
                || !TryGetCurrentForeignKeyStorage(definition.PrincipalTable, definition.PrincipalSchema,
                    definition.PrincipalColumns[ordinal], columns, out var principal)
                || !ForeignKeyStorageCompatible(dependent, principal))
            {
                // WHY: A newly NULL-filled endpoint proves row compatibility,
                // not native type/collation compatibility or parent ownership.
                return null;
            }
        }

        return captured.CandidateKeys;
    }

    /// <summary>Starts an invocation without historical live facts, including empty or cancelled analyses.</summary>
    private void ResetLivePrerequisiteEvidence()
    {
        _livePrerequisiteColumns = null;
        _livePrerequisiteTypes = null;
        _liveForeignKeyPrerequisites = null;
        _requestedLiveConstraintNames = null;
        _liveConstraintCollisionKinds = null;
        _removedLiveConstraintNames = null;
        _occupiedProjectedConstraintNames = null;
        _sparseCheckTypeFacts = null;
        _indexInputProofs = null;
        _indexInputShortCircuitCodes = null;
        _indexInputOrderedReplacementOrdinals = null;
        _providerIndexColumnProposals = null;
    }

    /// <summary>Tracks only requested native constraint symbols through accepted executable family changes.</summary>
    private void ObserveAcceptedLiveConstraintName(SafeMigrationOperation operation, SafeMigrationDecision decision)
    {
        if (_requestedLiveConstraintNames is null || decision.Action != SafeMigrationAction.Apply)
        {
            return;
        }

        var identity = operation.Intent switch
        {
            DropForeignKeyIntent value => LiveForeignKeyName(value.Table, value.Schema, value.Name),
            DropPrimaryKeyIntent value => LiveForeignKeyName(value.Table, value.Schema, value.Name),
            DropUniqueConstraintIntent value => LiveForeignKeyName(value.Table, value.Schema, value.Name),
            DropCheckConstraintIntent value => LiveForeignKeyName(value.Table, value.Schema, value.Name),
            EnsurePrimaryKeyIntent value => LiveForeignKeyName(value.Definition.Table,
                value.Definition.Schema, value.Definition.Name),
            EnsureUniqueConstraintIntent value => LiveForeignKeyName(value.Definition.Table,
                value.Definition.Schema, value.Definition.Name),
            EnsureCheckConstraintIntent value => LiveForeignKeyName(value.Definition.Table,
                value.Definition.Schema, value.Definition.Name),
            _ => (LiveForeignKeyNameIdentity?)null,
        };

        if (identity is { } name && _requestedLiveConstraintNames.Contains(name))
        {
            var kind = operation.Intent switch
            {
                DropForeignKeyIntent => "f",
                DropPrimaryKeyIntent or EnsurePrimaryKeyIntent => "p",
                DropUniqueConstraintIntent or EnsureUniqueConstraintIntent => "u",
                DropCheckConstraintIntent or EnsureCheckConstraintIntent => "c",
                _ => throw new UnreachableException(),
            };

            if (operation.Intent is DropForeignKeyIntent or DropPrimaryKeyIntent
                or DropUniqueConstraintIntent or DropCheckConstraintIntent)
            {
                if (_liveConstraintCollisionKinds?.TryGetValue(name, out var actualKind) == true && actualKind == kind)
                {
                    // WHY: Only a correctly typed executable drop of the
                    // independently observed owned symbol frees its name.
                    // DropFK against a CHECK/UQ/PK never obtains this proof.
                    (_removedLiveConstraintNames ??= []).Add(name);
                }

                if (_occupiedProjectedConstraintNames?.TryGetValue(name, out var occupiedKind) == true
                    && occupiedKind == kind)
                {
                    _occupiedProjectedConstraintNames.Remove(name);
                }
            }
            else
            {
                // WHY: An earlier FK removal certifies absence only until a
                // later executable constraint reoccupies the same owned name.
                (_occupiedProjectedConstraintNames ??= [])[name] = kind;
            }
        }

        if (operation.Intent is EnsureTableIntent table)
        {
            if (table.Definition.PrimaryKey is { } primary)
            {
                Occupy(primary.Name, "p");
            }

            foreach (var unique in table.Definition.UniqueConstraints)
            {
                Occupy(unique.Name, "u");
            }

            foreach (var check in table.Definition.CheckConstraints)
            {
                Occupy(check.Name, "c");
            }

            void Occupy(string name, string kind)
            {
                var occupied = LiveForeignKeyName(table.Definition.Table, table.Definition.Schema, name);
                if (_requestedLiveConstraintNames.Contains(occupied))
                {
                    (_occupiedProjectedConstraintNames ??= [])[occupied] = kind;
                }
            }
        }
    }

    /// <summary>Uses an accepted new owner, never an authored future or rejected table proposal.</summary>
    private static bool IsAcceptedNewForeignKeyOwner(
        ExpectedForeignKeyDefinition definition,
        ISafeMigrationProjectedColumnSource columns
    ) => columns is ISafeMigrationProjectedTableSource tables
        && tables.TryGetProjectedTableState(definition.Table, definition.Schema, out var state)
        && state.IsNewlyCreated && !state.HasUnknownStructure;

    /// <summary>Resolves an owned FK symbol through the same proven schema normalization as its catalog row.</summary>
    private LiveForeignKeyNameIdentity LiveForeignKeyName(string table, string? schema, string name)
        => new(table, PostgreSqlSafeMigrationSchemaIdentity.Normalize(schema, _currentSchema), name);

    /// <summary>Qualifies native builtin equality families and PostgreSQL's deterministic-collation rule.</summary>
    private static bool ForeignKeyStorageCompatible(LiveColumnStorage dependent, LiveColumnStorage principal)
    {
        // WHY: PostgreSQL's default btree integer family provides cross-width
        // equality; text/varchar share text_ops. No arbitrary cast or nondefault
        // opclass is inferred. The engine permits different deterministic
        // collations but requires equal identities when either is nondeterministic.
        var typesCompatible = dependent.TypeOid == principal.TypeOid
            || dependent.TypeOid is 20 or 21 or 23 && principal.TypeOid is 20 or 21 or 23
            || dependent.TypeOid is 25 or 1043 && principal.TypeOid is 25 or 1043;

        return typesCompatible
            && (dependent.CollationOid == 0) == (principal.CollationOid == 0)
            && (dependent.CollationOid == principal.CollationOid
                || dependent.CollationDeterministic && principal.CollationDeterministic);
    }

    /// <summary>Reads only accepted current definitions or independently captured unchanged live endpoints.</summary>
    private bool TryGetCurrentForeignKeyStorage(
        string table,
        string? schema,
        string column,
        ISafeMigrationProjectedColumnSource columns,
        out LiveColumnStorage storage
    )
    {
        storage = default;
        if (columns.TryGetProjectedColumn(table, schema, column, out var projected))
        {
            var identity = ProjectedColumnTypeIdentity(projected);

            return identity is { } type && _livePrerequisiteTypes?.TryGetValue(type, out storage) == true;
        }

        return _livePrerequisiteColumns?.TryGetValue(LiveColumnKey(table, schema, column), out storage) == true;
    }

    /// <summary>Captures requested sparse dependencies in one bounded metadata phase, never application rows.</summary>
    /// <param name="connection">The open analysis connection inside its existing snapshot scope.</param>
    /// <param name="operations">The safe operation stream in authored order.</param>
    /// <param name="results">The corresponding initial live classifications.</param>
    /// <param name="commandTimeout">The caller's finite or explicitly configured command timeout.</param>
    /// <param name="cancellationToken">Cancellation for selection, transport and protocol validation.</param>
    internal async Task PopulateLivePrerequisiteEvidenceAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> results,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        SelectOrderedIndexInputRequests(operations, results, cancellationToken);

        var requests = CollectLivePrerequisiteRequests(operations, results, cancellationToken);
        if (requests is null || requests.Count == 0)
        {
            _indexInputProofs = await CaptureIndexInputProofsAsync(connection, operations, results,
                null, null, commandTimeout, cancellationToken);

            return;
        }

        var liveColumns = new Dictionary<LiveColumnIdentity, LiveColumnStorage>();
        var liveTypes = new Dictionary<LiveColumnTypeIdentity, LiveColumnStorage>();
        var foreignKeys = new Dictionary<ExpectedForeignKeyDefinition, LiveForeignKeyPrerequisites>(
            new LiveForeignKeyComparer(_currentSchema));

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            requests.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
            commandTimeout,
            (command, offset) => BuildLivePrerequisiteStatement(command, requests, offset),
            async (reader, offset, count, token) =>
            {
                if (reader.FieldCount != 9)
                {
                    throw InvalidLivePrerequisiteEvidence();
                }

                var nextOrdinal = offset;
                var currentOrdinal = -1;
                bool? compatible = null;
                List<SafeMigrationLiveCandidateKey>? candidates = null;
                HashSet<SafeMigrationLiveCandidateKey>? candidateIdentities = null;
                var absentCandidate = false;
                var bothOwnersExist = false;
                var dependentExists = false;
                string? collisionKind = null;
                while (await reader.ReadAsync(token))
                {
                    var ordinal = reader.GetInt32(0);
                    if (ordinal < offset || ordinal >= offset + count
                        || ordinal != currentOrdinal && ordinal != nextOrdinal
                        || reader.GetValue(1) is not bool valid)
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }

                    var request = requests[ordinal];
                    if (ordinal != currentOrdinal)
                    {
                        PublishForeignKey();
                        currentOrdinal = ordinal;
                        nextOrdinal = ordinal + 1;
                        compatible = valid;
                        candidates = request is LiveForeignKeyRequest ? [] : null;
                        candidateIdentities = request is LiveForeignKeyRequest ? [] : null;
                        absentCandidate = false;
                        bothOwnersExist = false;
                        dependentExists = false;
                        collisionKind = null;
                    }
                    else if (request is not LiveForeignKeyRequest || compatible != valid || absentCandidate)
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }

                    if (request is LiveForeignKeyRequest)
                    {
                        if (!reader.IsDBNull(2) && (reader.GetValue(2) is not long dependentOid || dependentOid <= 0)
                            || !reader.IsDBNull(3) && (reader.GetValue(3) is not long principalOid || principalOid <= 0)
                            || !reader.IsDBNull(8))
                        {
                            throw InvalidLivePrerequisiteEvidence();
                        }

                        var ownersExist = !reader.IsDBNull(2) && !reader.IsDBNull(3);
                        var collision = reader.IsDBNull(7) ? null : reader.GetString(7);
                        if (candidates!.Count > 0 && (bothOwnersExist != ownersExist || collisionKind != collision)
                            || collision is { Length: not 1 })
                        {
                            throw InvalidLivePrerequisiteEvidence();
                        }

                        bothOwnersExist = ownersExist;
                        dependentExists = !reader.IsDBNull(2);
                        collisionKind = collision;

                        if (reader.IsDBNull(4))
                        {
                            if (!reader.IsDBNull(5) || !reader.IsDBNull(6) || candidates!.Count != 0)
                            {
                                throw InvalidLivePrerequisiteEvidence();
                            }

                            absentCandidate = true;

                            continue;
                        }

                        var kind = reader.GetString(4) switch
                        {
                            "p" => SafeMigrationDatabaseObjectKind.PrimaryKey,
                            "u" => SafeMigrationDatabaseObjectKind.UniqueConstraint,
                            "i" => SafeMigrationDatabaseObjectKind.Index,
                            _ => throw InvalidLivePrerequisiteEvidence(),
                        };

                        var name = reader.GetString(5);
                        var backingIndex = reader.IsDBNull(6) ? null : reader.GetString(6);
                        if (name.Length == 0
                            || kind == SafeMigrationDatabaseObjectKind.Index && backingIndex is not null
                            || kind != SafeMigrationDatabaseObjectKind.Index && string.IsNullOrEmpty(backingIndex))
                        {
                            throw InvalidLivePrerequisiteEvidence();
                        }

                        var key = new SafeMigrationLiveCandidateKey(kind, name, backingIndex);
                        if (!candidateIdentities!.Add(key))
                        {
                            throw InvalidLivePrerequisiteEvidence();
                        }

                        candidates!.Add(key);

                        continue;
                    }

                    if (!reader.IsDBNull(4) || !reader.IsDBNull(5) || !reader.IsDBNull(6) || !reader.IsDBNull(7))
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }

                    if (!valid)
                    {
                        continue;
                    }

                    if (reader.GetValue(2) is not long typeOid || typeOid <= 0
                        || reader.GetValue(3) is not long collationOid || collationOid < 0
                        || reader.GetValue(8) is not bool deterministic)
                    {
                        throw InvalidLivePrerequisiteEvidence();
                    }

                    var storage = new LiveColumnStorage(typeOid, collationOid, deterministic);
                    switch (request)
                    {
                        case LiveColumnRequest column:
                            liveColumns.Add(column.Identity, storage);
                            break;
                        case LiveColumnTypeRequest type:
                            liveTypes.Add(type.Identity, storage);
                            break;
                        default:
                            throw InvalidLivePrerequisiteEvidence();
                    }
                }

                PublishForeignKey();
                if (nextOrdinal != offset + count)
                {
                    throw InvalidLivePrerequisiteEvidence();
                }

                void PublishForeignKey()
                {
                    if (currentOrdinal >= offset && requests[currentOrdinal] is LiveForeignKeyRequest foreignKey)
                    {
                        foreignKeys.Add(foreignKey.Definition,
                            new LiveForeignKeyPrerequisites(compatible!.Value, bothOwnersExist, dependentExists,
                                collisionKind, candidates!.AsReadOnly()));
                    }
                }
            },
            cancellationToken);

        var indexFacts = await CaptureIndexInputProofsAsync(connection, operations, results,
            liveColumns, liveTypes, commandTimeout, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var checkFacts = await CaptureSparseCheckTypeFactsAsync(connection, operations, results,
            requests, liveTypes, commandTimeout, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<LiveForeignKeyNameIdentity, string>? collisionKinds = null;
        foreach (var (definition, captured) in foreignKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (captured.CollisionKind is { } kind)
            {
                var name = LiveForeignKeyName(definition.Table, definition.Schema, definition.Name);
                collisionKinds ??= [];
                if (collisionKinds.TryGetValue(name, out var previous) && previous != kind)
                {
                    throw InvalidLivePrerequisiteEvidence();
                }

                collisionKinds[name] = kind;
            }
        }

        var requestedNames = foreignKeys.Count == 0 ? null : foreignKeys.Keys
            .Select(definition => LiveForeignKeyName(definition.Table, definition.Schema, definition.Name))
            .ToHashSet();

        cancellationToken.ThrowIfCancellationRequested();

        // WHY: A malformed later row or cancellation cannot publish a partly
        // certified snapshot. These dictionaries are invocation-local and are
        // consulted only after Core checks current physical lifetimes.
        _livePrerequisiteColumns = liveColumns;
        _livePrerequisiteTypes = liveTypes;
        _liveForeignKeyPrerequisites = foreignKeys;
        _requestedLiveConstraintNames = requestedNames;
        _liveConstraintCollisionKinds = collisionKinds;
        _sparseCheckTypeFacts = checkFacts;
        _indexInputProofs = indexFacts;
    }

    /// <summary>Deduplicates only sparse operations whose missing live member has an earlier authored ADD.</summary>
    private List<LivePrerequisiteRequest>? CollectLivePrerequisiteRequests(
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> results,
        CancellationToken cancellationToken
    )
    {
        var hasMissingPrerequisite = false;
        for (var ordinal = 0; ordinal < results.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results[ordinal].ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                || operations[ordinal].Intent is EnsureIndexIntent
                    && NeedsIndexInputCapture(results[ordinal], ordinal))
            {
                hasMissingPrerequisite = true;

                break;
            }
        }

        if (!hasMissingPrerequisite)
        {
            // WHY: Matching normal migrations neither allocate a sparse cache
            // nor add catalog transport solely because this bridge is present.
            return null;
        }

        var relevantColumns = new HashSet<LiveColumnIdentity>();
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var intent = operations[ordinal].Intent;
            if (!(results[ordinal].ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                    || intent is EnsureIndexIntent && NeedsIndexInputCapture(results[ordinal], ordinal))
                || intent is not (EnsureForeignKeyIntent or EnsureIndexIntent or EnsurePrimaryKeyIntent
                    or EnsureUniqueConstraintIntent or EnsureCheckConstraintIntent))
            {
                continue;
            }

            var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(intent);
            var local = intent is EnsureIndexIntent index
                ? GetIndexPrerequisiteColumns(index) : SafeMigrationPrerequisiteColumns.Local(intent);

            foreach (var column in local)
            {
                relevantColumns.Add(LiveColumnKey(owner.Table!, owner.Schema, column));
            }

            if (intent is EnsureForeignKeyIntent foreign)
            {
                foreach (var column in foreign.Definition.PrincipalColumns)
                {
                    relevantColumns.Add(LiveColumnKey(foreign.Definition.PrincipalTable,
                        foreign.Definition.PrincipalSchema, column));
                }
            }
        }

        if (relevantColumns.Count == 0)
        {
            return null;
        }

        CaptureProviderIndexColumnProposals(operations, results, cancellationToken);

        for (var ordinal = operations.Count - 1; ordinal >= 0; ordinal--)
        {
            if (operations[ordinal].Intent is RenameColumnIntent rename && relevantColumns.Contains(
                LiveColumnKey(rename.Table, rename.Schema, rename.NewName)))
            {
                // WHY: Keep only the requested member's prefix aliases. This
                // captures type identities, not permission to accept the rename
                // or to bypass its separate provider postcondition boundary.
                relevantColumns.Add(LiveColumnKey(rename.Table, rename.Schema, rename.Name));
            }
        }

        var requests = new List<LivePrerequisiteRequest>();
        var priorColumns = new Dictionary<LiveColumnIdentity, ExpectedColumnDefinition>();
        var priorTypes = new Dictionary<LiveColumnIdentity, HashSet<LiveColumnTypeIdentity>>();
        var requestedColumns = new HashSet<LiveColumnIdentity>();
        var requestedTypes = new HashSet<LiveColumnTypeIdentity>();
        var requestedForeignKeys = new HashSet<ExpectedForeignKeyDefinition>(
            new LiveForeignKeyComparer(_currentSchema));

        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_providerIndexColumnProposals?.TryGetValue(ordinal, out var proposals) == true)
            {
                foreach (var proposal in proposals)
                {
                    RecordColumn(LiveColumnKey(proposal.Table, proposal.Schema, proposal.Definition.Name),
                        proposal.Definition);
                }
            }

            var intent = operations[ordinal].Intent;
            if (results[ordinal].ObservedState == SafeMigrationObservedState.Unsupported)
            {
                // WHY: An unsupported proposal cannot be accepted later and
                // must not introduce even a metadata type-resolution request.
                continue;
            }

            if (intent is EnsureTableIntent table)
            {
                foreach (var column in table.Definition.Columns)
                {
                    RecordColumn(LiveColumnKey(table.Definition.Table, table.Definition.Schema, column.Name), column);
                }

                continue;
            }

            if (intent is EnsureColumnIntent added)
            {
                RecordColumn(LiveColumnKey(added.Table, added.Schema, added.Definition.Name), added.Definition);

                continue;
            }

            if (intent is AlterColumnIntent altered)
            {
                var key = LiveColumnKey(altered.Table, altered.Schema, altered.Definition.Name);
                RecordColumn(key, altered.Definition);

                continue;
            }

            if (intent is RenameColumnIntent rename
                && priorColumns.TryGetValue(LiveColumnKey(rename.Table, rename.Schema, rename.Name), out var renamed))
            {
                var renamedKey = LiveColumnKey(rename.Table, rename.Schema, rename.NewName);
                RecordColumn(renamedKey, renamed);
                if (priorTypes.TryGetValue(LiveColumnKey(rename.Table, rename.Schema, rename.Name), out var oldTypes))
                {
                    if (!priorTypes.TryGetValue(renamedKey, out var renamedTypes))
                    {
                        renamedTypes = [];
                        priorTypes.Add(renamedKey, renamedTypes);
                    }

                    renamedTypes.UnionWith(oldTypes);
                }

                continue;
            }

            if (!(results[ordinal].ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                    || intent is EnsureIndexIntent && NeedsIndexInputCapture(results[ordinal], ordinal))
                || intent is not (EnsureForeignKeyIntent or EnsureIndexIntent or EnsurePrimaryKeyIntent
                    or EnsureUniqueConstraintIntent or EnsureCheckConstraintIntent))
            {
                continue;
            }

            var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(intent);
            var local = intent is EnsureIndexIntent index
                ? GetIndexPrerequisiteColumns(index)
                : SafeMigrationPrerequisiteColumns.Local(intent);

            var hasPriorColumn = local.Any(column => priorColumns.ContainsKey(
                LiveColumnKey(owner.Table!, owner.Schema, column)));

            if (intent is EnsureForeignKeyIntent foreignKey)
            {
                hasPriorColumn |= foreignKey.Definition.PrincipalColumns.Any(column => priorColumns.ContainsKey(
                    LiveColumnKey(foreignKey.Definition.PrincipalTable,
                        foreignKey.Definition.PrincipalSchema, column)));
            }

            if (!hasPriorColumn && intent is not EnsureIndexIntent)
            {
                continue;
            }

            AddColumns(owner.Table!, owner.Schema, local);
            if (intent is EnsureCheckConstraintIntent check && check.Definition.Expression is { } expression)
            {
                foreach (var type in CheckExpressionTypeIdentities(expression))
                {
                    if (requestedTypes.Add(type))
                    {
                        requests.Add(new LiveColumnTypeRequest(type));
                    }
                }
            }

            if (intent is EnsureForeignKeyIntent foreign)
            {
                AddColumns(foreign.Definition.PrincipalTable, foreign.Definition.PrincipalSchema,
                    foreign.Definition.PrincipalColumns);

                if (requestedForeignKeys.Add(foreign.Definition))
                {
                    requests.Add(new LiveForeignKeyRequest(foreign.Definition));
                }
            }
        }

        return requests;

        void AddColumns(string table, string? schema, IReadOnlyList<string> columns)
        {
            foreach (var column in columns)
            {
                var key = LiveColumnKey(table, schema, column);
                if (requestedColumns.Add(key))
                {
                    requests.Add(new LiveColumnRequest(key));
                }

                if (priorTypes.TryGetValue(key, out var types))
                {
                    foreach (var type in types)
                    {
                        if (requestedTypes.Add(type))
                        {
                            requests.Add(new LiveColumnTypeRequest(type));
                        }
                    }
                }
            }
        }

        void RecordColumn(LiveColumnIdentity key, ExpectedColumnDefinition definition)
        {
            if (!relevantColumns.Contains(key))
            {
                // WHY: Preserve all preceding supported type alternatives
                // only for requested sparse members, not every authored column.
                return;
            }

            priorColumns[key] = definition;
            if (ProjectedColumnTypeIdentity(definition) is not { } type)
            {
                return;
            }

            if (!priorTypes.TryGetValue(key, out var history))
            {
                history = [];
                priorTypes.Add(key, history);
            }

            // WHY: ExistenceOnly or a rejected later proposal does not replace
            // the accepted definition. Native type OIDs certify no presence;
            // only Core's current accepted column chooses which historical
            // declaration, if any, can consume this independent metadata.
            history.Add(type);
        }
    }

    /// <summary>Shares the normalizer's proven schema alias without folding exact quoted identifiers.</summary>
    private LiveColumnIdentity LiveColumnKey(string table, string? schema, string column)
        => new(table, PostgreSqlSafeMigrationSchemaIdentity.Normalize(schema, _currentSchema), column);

    /// <summary>Maps projected definitions to native OIDs rather than comparing CLR or alias spellings.</summary>
    private LiveColumnTypeIdentity? ProjectedColumnTypeIdentity(ExpectedColumnDefinition definition)
    {
        var mapping = _typeMappingSource.FindMapping(definition.ClrType, definition.StoreType,
            keyOrIndex: false, unicode: definition.IsUnicode, size: definition.MaxLength,
            rowVersion: definition.IsRowVersion, fixedLength: definition.IsFixedLength,
            precision: definition.Precision, scale: definition.Scale);

        return mapping is null
            ? null
            : new LiveColumnTypeIdentity(mapping.StoreTypeNameBase, definition.Collation?.Name,
                definition.Collation?.Schema);
    }

    /// <summary>Builds a bounded mixed metadata statement with one ordered protocol for every request family.</summary>
    private SafeMigrationCatalogProbeStatement BuildLivePrerequisiteStatement(
        SafeMigrationCatalogCommand command,
        IReadOnlyList<LivePrerequisiteRequest> requests,
        int offset
    )
    {
        var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
        var selections = new List<string>();
        var sqlBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (offset + selections.Count < requests.Count
            && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
        {
            var ordinal = offset + selections.Count;
            var checkpoint = parameters.Capture();
            var selection = requests[ordinal] switch
            {
                LiveColumnRequest column => LiveColumnSelection(ordinal, column.Identity, parameters),
                LiveColumnTypeRequest type => LiveColumnTypeSelection(ordinal, type.Identity, parameters),
                LiveForeignKeyRequest foreignKey => LiveForeignKeySelection(ordinal, foreignKey.Definition, parameters),
                _ => throw InvalidLivePrerequisiteEvidence(),
            };

            var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                + (selections.Count == 0 ? 0 : Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator));

            var payload = checked(sqlBytes + selectionBytes + parameters.Utf8PayloadBytes);
            if (SafeMigrationCatalogQueryLimits.Exceeded(parameters.Count, payload,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
            {
                var count = parameters.Count;
                parameters.Rollback(checkpoint);
                if (selections.Count == 0)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(ordinal, count, payload);
                }

                break;
            }

            selections.Add(selection);
            sqlBytes += selectionBytes;
        }

        command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
            + SafeMigrationCatalogQueryLimits.Trailer;

        return new SafeMigrationCatalogProbeStatement(selections.Count, parameters.Utf8PayloadBytes, offset);
    }

    /// <summary>Observes exact requested live member storage while excluding unsupported owner lifetimes.</summary>
    private static string LiveColumnSelection(
        int ordinal,
        LiveColumnIdentity identity,
        PostgreSqlCatalogQueryParameters parameters
    ) => $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
        + $"COALESCE(({LiveOrdinaryOwner("c")}) AND a.attnum IS NOT NULL, FALSE), "
        + "a.atttypid::bigint, a.attcollation::bigint, NULL::text, NULL::text, NULL::text, NULL::text, "
        + "a.attcollation = 0 OR (SELECT coll.collisdeterministic FROM pg_catalog.pg_collation coll "
        + "WHERE coll.oid = a.attcollation) "
        + "FROM (SELECT 1) seed LEFT JOIN pg_catalog.pg_namespace n "
        + $"ON n.nspname = {LiveSchemaExpression(identity.Schema, parameters)} "
        + "LEFT JOIN pg_catalog.pg_class c "
        + $"ON c.relnamespace = n.oid AND c.relname = {parameters.AddString(identity.Table)} "
        + "LEFT JOIN pg_catalog.pg_attribute a "
        + $"ON a.attrelid = c.oid AND a.attname = {parameters.AddString(identity.Column)} "
        + "AND a.attnum > 0 AND NOT a.attisdropped";

    /// <summary>Resolves effective type-default or explicit visible collation with native catalog OIDs.</summary>
    private static string LiveColumnTypeSelection(
        int ordinal,
        LiveColumnTypeIdentity identity,
        PostgreSqlCatalogQueryParameters parameters
    )
    {
        var collation = identity.Collation is null
            ? "t.typcollation"
            : "(SELECT coll.oid FROM pg_catalog.pg_collation coll "
                + "JOIN pg_catalog.pg_namespace cn ON cn.oid = coll.collnamespace "
                + $"WHERE coll.collname = {parameters.AddString(identity.Collation)} "
                + (identity.CollationSchema is null
                    ? "AND pg_catalog.pg_collation_is_visible(coll.oid))"
                    : $"AND cn.nspname = {parameters.AddString(identity.CollationSchema)})");

        return $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
            + "t.oid IS NOT NULL AND expected.collation IS NOT NULL, t.oid::bigint, expected.collation::bigint, "
            + "NULL::text, NULL::text, NULL::text, NULL::text, "
            + "expected.collation = 0 OR coll.collisdeterministic FROM (SELECT 1) seed "
            + "LEFT JOIN pg_catalog.pg_type t "
            + $"ON t.oid = pg_catalog.to_regtype({parameters.AddString(identity.StoreType)}) "
            + $"LEFT JOIN LATERAL (SELECT ({collation}) AS collation) expected ON TRUE "
            + "LEFT JOIN pg_catalog.pg_collation coll ON coll.oid = expected.collation";
    }

    /// <summary>Streams actual ordered keys without conflating constraint and backing-index identities.</summary>
    private static string LiveForeignKeySelection(
        int ordinal,
        ExpectedForeignKeyDefinition definition,
        PostgreSqlCatalogQueryParameters parameters
    )
    {
        var principalColumns = "ARRAY["
            + string.Join(", ", definition.PrincipalColumns.Select(parameters.AddString)) + "]::name[]";

        // WHY: Metadata alone cannot prove an arbitrary opclass, partial or
        // expression index is a native reference key. Native owner/type/collation
        // and immediate validated keys remain independent from the NULL-row gate.
        var owners = $"(d.oid IS NULL OR ({LiveOrdinaryOwner("d")})) AND ({LiveOrdinaryOwner("p")})";
        var collision = "(SELECT collision.contype::text FROM pg_catalog.pg_constraint collision "
            + $"WHERE collision.conrelid = d.oid AND collision.conname = {parameters.AddString(definition.Name)}), "
            + "NULL::boolean";

        return $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, COALESCE(({owners}), FALSE), "
            + "d.oid::bigint, p.oid::bigint, keys.kind, keys.name, keys.backing, " + collision + " "
            + "FROM (SELECT 1) seed LEFT JOIN pg_catalog.pg_namespace dn "
            + $"ON dn.nspname = {LiveSchemaExpression(definition.Schema, parameters)} "
            + "LEFT JOIN pg_catalog.pg_class d ON d.relnamespace = dn.oid "
            + $"AND d.relname = {parameters.AddString(definition.Table)} "
            + "LEFT JOIN pg_catalog.pg_namespace pn "
            + $"ON pn.nspname = {LiveSchemaExpression(definition.PrincipalSchema, parameters)} "
            + "LEFT JOIN pg_catalog.pg_class p ON p.relnamespace = pn.oid "
            + $"AND p.relname = {parameters.AddString(definition.PrincipalTable)} "
            + "LEFT JOIN LATERAL (SELECT COALESCE(co.contype::text, 'i') AS kind, "
            + "COALESCE(co.conname, ic.relname)::text AS name, "
            + "CASE WHEN co.oid IS NULL THEN NULL::text ELSE ic.relname::text END AS backing "
            + "FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class ic ON ic.oid = i.indexrelid "
            + "JOIN pg_catalog.pg_am am ON am.oid = ic.relam "
            + "LEFT JOIN pg_catalog.pg_constraint co ON co.conindid = i.indexrelid "
            + "AND co.conrelid = p.oid AND co.contype IN ('p', 'u') "
            + $"WHERE ({owners}) AND i.indrelid = p.oid AND i.indisunique AND i.indisvalid "
            + "AND i.indisready AND i.indislive AND i.indimmediate AND i.indpred IS NULL "
            + "AND i.indexprs IS NULL AND am.amname = 'btree' AND NOT ic.relispartition "
            + $"AND i.indnkeyatts = {definition.PrincipalColumns.Count.ToString(CultureInfo.InvariantCulture)} "
            + "AND (co.oid IS NULL OR (co.conislocal AND co.coninhcount = 0 AND co.conparentid = 0 "
            + "AND co.convalidated AND NOT co.condeferrable AND NOT co.condeferred "
            + "AND NOT COALESCE((to_jsonb(co) ->> 'conperiod')::boolean, FALSE))) "
            + "AND ARRAY(SELECT a.attname FROM unnest(i.indkey) WITH ORDINALITY key(attnum, ord) "
            + "JOIN pg_catalog.pg_attribute a ON a.attrelid = p.oid AND a.attnum = key.attnum "
            + "WHERE key.ord <= i.indnkeyatts AND NOT a.attisdropped ORDER BY key.ord) = "
            + principalColumns + " AND NOT EXISTS (SELECT 1 FROM unnest(i.indkey) WITH ORDINALITY key(attnum, ord) "
            + "JOIN pg_catalog.pg_attribute a ON a.attrelid = p.oid AND a.attnum = key.attnum "
            + "JOIN pg_catalog.pg_opclass opc ON opc.oid = i.indclass[key.ord::integer - 1] "
            + "WHERE key.ord <= i.indnkeyatts AND ((i.indcollation[key.ord::integer - 1] <> a.attcollation "
            + "AND NOT ((SELECT coll.collisdeterministic FROM pg_catalog.pg_collation coll "
            + "WHERE coll.oid = i.indcollation[key.ord::integer - 1]) "
            + "AND (SELECT coll.collisdeterministic FROM pg_catalog.pg_collation coll "
            + "WHERE coll.oid = a.attcollation))) "
            + "OR NOT opc.opcdefault))) keys ON TRUE";
    }

    /// <summary>Resolves only the active creation schema, never another visible search-path owner.</summary>
    private static string LiveSchemaExpression(string? schema, PostgreSqlCatalogQueryParameters parameters)
        => schema is null ? "current_schema()" : parameters.AddString(schema);

    /// <summary>Excludes views, partitions and inheritance which need a separate native ownership contract.</summary>
    private static string LiveOrdinaryOwner(string alias)
        => $"{alias}.relkind = 'r' AND NOT {alias}.relispartition AND NOT EXISTS "
            + $"(SELECT 1 FROM pg_catalog.pg_inherits inheritance WHERE inheritance.inhrelid = {alias}.oid "
            + $"OR inheritance.inhparent = {alias}.oid)";

    /// <summary>Reports malformed metadata without emitting user identifiers or native SQL.</summary>
    private static InvalidOperationException InvalidLivePrerequisiteEvidence()
        => new("The PostgreSQL live prerequisite query returned malformed or incomplete evidence.");

    /// <summary>An exact provider-normalized requested physical member, not a model declaration.</summary>
    private readonly record struct LiveColumnIdentity(string Table, string? Schema, string Column);

    /// <summary>The independently resolved PostgreSQL type and effective comparison collation.</summary>
    private readonly record struct LiveColumnStorage(long TypeOid, long CollationOid, bool CollationDeterministic);

    /// <summary>The provider-mapped type spelling and requested collation used for native OID resolution.</summary>
    private readonly record struct LiveColumnTypeIdentity(string StoreType, string? Collation, string? CollationSchema);

    /// <summary>Immutable independent owner/name qualification and actual surviving-candidate identities.</summary>
    private sealed record LiveForeignKeyPrerequisites(
        bool OwnersCompatible,
        bool BothOwnersExist,
        bool DependentExists,
        string? CollisionKind,
        IReadOnlyList<SafeMigrationLiveCandidateKey> CandidateKeys);

    /// <summary>The exact owned foreign-key name discharged by an accepted native drop.</summary>
    private readonly record struct LiveForeignKeyNameIdentity(string Table, string? Schema, string Name);

    /// <summary>A deduplicated metadata request carrying no application row values.</summary>
    private abstract record LivePrerequisiteRequest;
    private sealed record LiveColumnRequest(LiveColumnIdentity Identity) : LivePrerequisiteRequest;
    private sealed record LiveColumnTypeRequest(LiveColumnTypeIdentity Identity) : LivePrerequisiteRequest;
    private sealed record LiveForeignKeyRequest(ExpectedForeignKeyDefinition Definition) : LivePrerequisiteRequest;

    /// <summary>Deduplicates exact ordered FK metadata bindings without delimiter-sensitive string encodings.</summary>
    private sealed class LiveForeignKeyComparer(string? currentSchema) : IEqualityComparer<ExpectedForeignKeyDefinition>
    {
        public bool Equals(ExpectedForeignKeyDefinition? left, ExpectedForeignKeyDefinition? right)
            => ReferenceEquals(left, right) || left is not null && right is not null
                && left.Name == right.Name && left.Table == right.Table && left.PrincipalTable == right.PrincipalTable
                && PostgreSqlSafeMigrationSchemaIdentity.Normalize(left.Schema, currentSchema)
                    == PostgreSqlSafeMigrationSchemaIdentity.Normalize(right.Schema, currentSchema)
                && PostgreSqlSafeMigrationSchemaIdentity.Normalize(left.PrincipalSchema, currentSchema)
                    == PostgreSqlSafeMigrationSchemaIdentity.Normalize(right.PrincipalSchema, currentSchema)
                && left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal)
                && left.PrincipalColumns.SequenceEqual(right.PrincipalColumns, StringComparer.Ordinal);

        public int GetHashCode(ExpectedForeignKeyDefinition definition)
        {
            var hash = new HashCode();
            hash.Add(definition.Name, StringComparer.Ordinal);
            hash.Add(definition.Table, StringComparer.Ordinal);
            hash.Add(PostgreSqlSafeMigrationSchemaIdentity.Normalize(definition.Schema, currentSchema),
                StringComparer.Ordinal);
            hash.Add(definition.PrincipalTable, StringComparer.Ordinal);
            hash.Add(PostgreSqlSafeMigrationSchemaIdentity.Normalize(definition.PrincipalSchema, currentSchema),
                StringComparer.Ordinal);
            hash.Add(definition.Columns.Count);
            foreach (var column in definition.Columns)
            {
                hash.Add(column, StringComparer.Ordinal);
            }

            foreach (var column in definition.PrincipalColumns)
            {
                hash.Add(column, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }
}
