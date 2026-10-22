namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationLivePrerequisiteSource
{
    private Dictionary<(string? Schema, string Table, string Name), LivePrerequisiteColumn>? _livePrerequisiteColumns;
    private Dictionary<ExpectedForeignKeyDefinition, LivePrerequisiteKeyProof>? _livePrerequisiteKeys;
    private HashSet<ExpectedForeignKeyDefinition>? _livePrerequisiteFullQualificationTargets;
    private Dictionary<ExpectedForeignKeyDefinition, SafeMigrationProviderAnalysis>? _liveNamespaceMissingAnalyses;
    private MySqlSafeMigrationCatalogSqlBuilder? _livePrerequisiteBuilder;
    private MySqlProjectedForeignKeySupportingIndexes? _livePrerequisiteSupportingIndexes;

    private Dictionary<(string? Schema, string Table), LivePrerequisiteCreation>?
        _acceptedLivePrerequisiteTableCreations;

    private bool _livePrerequisiteCreationDefaultsCurrent;

    /// <summary>Discards previous or partially failed snapshots before another invocation can expose them.</summary>
    private void ClearLivePrerequisites()
    {
        _livePrerequisiteColumns = null;
        _livePrerequisiteKeys = null;
        _livePrerequisiteFullQualificationTargets = null;
        _liveNamespaceMissingAnalyses = null;
        _livePrerequisiteBuilder = null;
        _livePrerequisiteSupportingIndexes = null;
        _acceptedLivePrerequisiteTableCreations = null;
        _livePrerequisiteCreationDefaultsCurrent = false;
        _multiValuedIndexDropProofs = null;
        _multiValuedIndexDropBoundaryUnknown = false;
        _generatedColumnSourceProofs = null;
        _changedGeneratedColumnSources = null;
        _changedGeneratedColumnOwners = null;
        _generatedColumnSourcesUnknown = false;
        ClearGeneratedIndexIdentityFacts();
    }

    /// <inheritdoc />
    bool ISafeMigrationLivePrerequisiteSource.HasLiveColumn(
        string table,
        string? schema,
        string column
    ) => _livePrerequisiteColumns?.TryGetValue((NormalizeProofSchema(schema), table, column), out var captured) == true
        && captured is { OwnerEligible: true, StoreType: not null };

    /// <inheritdoc />
    bool ISafeMigrationLivePrerequisiteSource.RequiresLiveForeignKeyQualification(
        EnsureForeignKeyIntent intent
    )
    {
        var definition = intent.Definition;

        // WHY: Raw projected definitions may both omit their collation even
        // when their existing owners inherit different physical defaults.
        // Actually accepted plain creations are likewise tied to captured
        // native defaults. Authored future/rejected owners cannot borrow them.
        return _livePrerequisiteFullQualificationTargets?.Contains(definition) == true
            && _livePrerequisiteColumns?.TryGetValue(
                (NormalizeProofSchema(definition.Schema), definition.Table, definition.Columns[0]),
                out var dependent) == true
            && (dependent.OwnerPresent || HasAcceptedLivePrerequisiteOwner(definition.Table, definition.Schema))
            && _livePrerequisiteColumns.TryGetValue(
                (NormalizeProofSchema(definition.PrincipalSchema), definition.PrincipalTable,
                    definition.PrincipalColumns[0]),
                out var principal)
            && (principal.OwnerPresent
                || HasAcceptedLivePrerequisiteOwner(definition.PrincipalTable, definition.PrincipalSchema));
    }

    /// <summary>Recognizes only an actually accepted native-default-qualified current owner creation.</summary>
    private bool HasAcceptedLivePrerequisiteOwner(
        string table,
        string? schema
    ) => _acceptedLivePrerequisiteTableCreations?.TryGetValue((NormalizeProofSchema(schema), table), out var creation) == true
        && creation.Eligible;

    /// <inheritdoc />
    bool ISafeMigrationLivePrerequisiteSource.CanRequalifyLiveForeignKeyPrerequisites(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    ) => liveAnalysis.Code == "foreign_key_multi_valued_supporting_index"
        && _livePrerequisiteKeys?.TryGetValue(intent.Definition, out var captured) == true
        && captured.MultiValuedIndexRisk
        && _livePrerequisiteSupportingIndexes?.HasUnsafeMultiValuedIndex(intent.Definition) == false
        && HasReleasedUnsafeMatchingForeignKeys(intent.Definition, captured);

    /// <summary>Requires removal of every original unsafe matching FK before metadata requalification.</summary>
    private bool HasReleasedUnsafeMatchingForeignKeys(
        ExpectedForeignKeyDefinition definition,
        LivePrerequisiteKeyProof captured
    )
    {
        var owner = (NormalizeProofSchema(definition.Schema), definition.Table);
        if (_acceptedLivePrerequisiteTableCreations?.ContainsKey(owner) == true)
        {
            return true;
        }

        foreach (var name in captured.MatchingForeignKeyNames)
        {
            var key = (owner.Item1, owner.Table, name);
            if (_acceptedForeignKeyRemovals?.Contains(key) != true)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    IReadOnlyList<SafeMigrationLiveCandidateKey>? ISafeMigrationLivePrerequisiteSource.GetLiveForeignKeyCandidateKeys(
        EnsureForeignKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var definition = intent.Definition;
        if (_livePrerequisiteKeys?.TryGetValue(definition, out var keys) != true
            || _livePrerequisiteSupportingIndexes?.HasUnsafeMultiValuedIndex(definition) != false
            || (keys.MultiValuedIndexRisk && !HasReleasedUnsafeMatchingForeignKeys(definition, keys))
            || (!keys.NamesFree && !HasReleasedLiveForeignKeyName(definition, keys))
            || _livePrerequisiteSupportingIndexes?.CanEnsure(definition) != true)
        {
            return null;
        }

        // WHY: Compare dependent and principal FK endpoints at the same ordered key position.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < definition.Columns.Count; index++)
        {
            if (!TryGetLiveForeignKeyEndpoint(
                    definition.Table,
                    definition.Schema,
                    definition.Columns[index],
                    columns,
                    out var dependent)
                || !TryGetLiveForeignKeyEndpoint(
                    definition.PrincipalTable,
                    definition.PrincipalSchema,
                    definition.PrincipalColumns[index],
                    columns,
                    out var principal)
                || !ForeignKeyStoreTypesMatch(dependent.StoreType, principal.StoreType)
                || !StringComparer.OrdinalIgnoreCase.Equals(dependent.Collation, principal.Collation)
                || ((definition.OnDelete == ReferentialAction.SetNull
                        || definition.OnUpdate == ReferentialAction.SetNull)
                    && !dependent.IsNullable))
            {
                return null;
            }
        }

        return keys.Keys;
    }

    /// <summary>Allows only a physically accepted same-owner FK removal to release its captured symbol.</summary>
    private bool HasReleasedLiveForeignKeyName(
        ExpectedForeignKeyDefinition definition,
        LivePrerequisiteKeyProof keys
    ) => keys.OwnedCollision
        && (_acceptedLivePrerequisiteTableCreations?.ContainsKey(
                (NormalizeProofSchema(definition.Schema), definition.Table)) == true
            || _acceptedForeignKeyRemovals?.Contains(
                (NormalizeProofSchema(definition.Schema), definition.Table, definition.Name)) == true);

    /// <summary>Combines accepted projected storage with independently observed effective owner defaults.</summary>
    private bool TryGetLiveForeignKeyEndpoint(
        string table,
        string? schema,
        string column,
        ISafeMigrationProjectedColumnSource columns,
        out LiveForeignKeyEndpoint endpoint
    )
    {
        endpoint = default;
        var owner = (NormalizeProofSchema(schema), table);
        if (_livePrerequisiteColumns?.TryGetValue((owner.Item1, table, column), out var live) != true)
        {
            return false;
        }

        var creation = default(LivePrerequisiteCreation);
        var newlyCreated = _acceptedLivePrerequisiteTableCreations?.TryGetValue(owner, out creation) == true;
        if (newlyCreated ? !creation.Eligible : !live.OwnerEligible)
        {
            return false;
        }

        if (columns.TryGetProjectedColumn(table, schema, column, out var projected))
        {
            if (projected.ComputedColumnSql is not null
                || projected.ComputedExpression is not null)
            {
                return false;
            }

            var storeType = _livePrerequisiteBuilder!.ResolveLiveForeignKeyStoreType(projected);
            var collation = IsCharacterForeignKeyType(storeType)
                ? projected.Collation?.Name ?? (newlyCreated ? creation.Collation : live.TableCollation)
                : null;

            endpoint = new LiveForeignKeyEndpoint(storeType, collation, projected.IsNullable);

            return true;
        }

        if (newlyCreated
            || live.StoreType is null
            || live.IsGenerated)
        {
            return false;
        }

        endpoint = new LiveForeignKeyEndpoint(live.StoreType, live.Collation, live.IsNullable);

        return true;
    }

    /// <summary>Preserves native ENUM/SET literals while ignoring only integer display widths.</summary>
    internal static bool ForeignKeyStoreTypesMatch(
        string first,
        string second
    )
    {
        if (HasLiteralMembers(first)
            || HasLiteralMembers(second))
        {
            // WHY: SQL keyword case and whitespace are cosmetic, but member
            // case/order is physical. Without captured literal modes, accept
            // only a canonical form that is identical under both escape modes
            // and never depends on ANSI_QUOTES interpreting double quotes.
            var firstDisplay = ModeIndependentMemberDisplay(first);
            var secondDisplay = ModeIndependentMemberDisplay(second);

            return firstDisplay is not null && StringComparer.Ordinal.Equals(firstDisplay, secondDisplay);
        }

        return StringComparer.OrdinalIgnoreCase.Equals(
            NormalizeForeignKeyIntegerType(first),
            NormalizeForeignKeyIntegerType(second));
    }

    /// <summary>Canonicalizes native member spelling only when session SQL literal modes cannot change it.</summary>
    private static string? ModeIndependentMemberDisplay(
        string storeType
    )
    {
        var escaped = MySqlSafeMigrationCatalogSqlBuilder.BuildEnumSetCatalogDisplay(
            storeType,
            backslashEscapes: true,
            out var escapedQuotes);

        var literal = MySqlSafeMigrationCatalogSqlBuilder.BuildEnumSetCatalogDisplay(
            storeType,
            backslashEscapes: false,
            out var literalQuotes);

        return !escapedQuotes && !literalQuotes && StringComparer.Ordinal.Equals(escaped, literal) ? escaped : null;
    }

    /// <summary>Stamps the native defaults of an actually accepted plain safe table creation.</summary>
    private void ObserveLivePrerequisiteCreation(
        ExpectedTableDefinition definition
    )
    {
        var owner = (NormalizeProofSchema(definition.Schema), definition.Table);
        if (_livePrerequisiteColumns is null)
        {
            return;
        }

        foreach (var column in definition.Columns)
        {
            if (_livePrerequisiteColumns.TryGetValue((owner.Item1, owner.Table, column.Name), out var captured))
            {
                // WHY: Defaults are inherited at CREATE, not at the later FK.
                // A subsequent database-default change cannot rewrite this
                // owner's already accepted physical collation.
                (_acceptedLivePrerequisiteTableCreations ??= new(PhysicalOwnerComparer))[owner] =
                    new LivePrerequisiteCreation(
                        _livePrerequisiteCreationDefaultsCurrent && captured.CreationEngineEligible,
                        captured.CreationCollation);

                return;
            }
        }

        // A separate ADD can introduce the only requested FK member, so its
        // sentinel still carries the same owner's native creation defaults.
        foreach (var pair in _livePrerequisiteColumns)
        {
            if (StringComparer.Ordinal.Equals(pair.Key.Schema, owner.Item1)
                && TableIdentifierComparer.Equals(pair.Key.Table, owner.Table))
            {
                (_acceptedLivePrerequisiteTableCreations ??= new(PhysicalOwnerComparer))[owner] =
                    new LivePrerequisiteCreation(
                        _livePrerequisiteCreationDefaultsCurrent && pair.Value.CreationEngineEligible,
                        pair.Value.CreationCollation);

                return;
            }
        }
    }

    /// <summary>Recognizes character storage whose effective collation is inherited unless explicit.</summary>
    private static bool IsCharacterForeignKeyType(
        string storeType
    )
    {
        var value = storeType
            .AsSpan()
            .TrimStart();

        return value.StartsWith("char(", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("varchar(", StringComparison.OrdinalIgnoreCase)
            || HasLiteralMembers(storeType)
            || value.EndsWith("text", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Recognizes native member-list types without interpreting their literals.</summary>
    private static bool HasLiteralMembers(
        string value
    )
    {
        var text = value.AsSpan().Trim();
        var open = text.IndexOf('(');
        if (open <= 0)
        {
            return false;
        }

        var keyword = text[..open].TrimEnd();

        return keyword.Equals("enum", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("set", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalizes a display width only for an otherwise exact native integer domain.</summary>
    private static string NormalizeForeignKeyIntegerType(
        string value
    )
    {
        var type = value.Trim();
        var open = type.IndexOf('(');
        if (open < 0)
        {
            return type;
        }

        var name = type.AsSpan(0, open);
        var isInteger = name.Equals("tinyint", StringComparison.OrdinalIgnoreCase)
            || name.Equals("smallint", StringComparison.OrdinalIgnoreCase)
            || name.Equals("mediumint", StringComparison.OrdinalIgnoreCase)
            || name.Equals("int", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bigint", StringComparison.OrdinalIgnoreCase);

        var close = type.IndexOf(')', open + 1);
        if (!isInteger
            || close < 0
            || !int.TryParse(type.AsSpan(open + 1, close - open - 1), CultureInfo.InvariantCulture, out _))
        {
            return type;
        }

        return string.Concat(type.AsSpan(0, open), type.AsSpan(close + 1));
    }

    /// <summary>Captures sparse metadata only after an earlier intent can change the prerequisite contract.</summary>
    private async Task CaptureLivePrerequisitesAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        if (!HasSparseLivePrerequisiteRequests(operations, analyses))
        {
            // WHY: A clean matching stream needs neither sparse snapshots nor
            // an allocated owner inventory, even for a very large init rerun.
            return;
        }

        HashSet<(string? Schema, string Table)>? touchedOwners = null;
        HashSet<(string? Schema, string Table)>? namespaceOwners = null;
        HashSet<(string? Schema, string Table, string Name)>? requestedColumns = null;
        HashSet<ExpectedForeignKeyDefinition>? requestedKeys = null;
        HashSet<ExpectedForeignKeyDefinition>? fullQualificationTargets = null;
        Dictionary<ExpectedForeignKeyDefinition, SafeMigrationProviderAnalysis>? namespaceMissing = null;
        var captureCreationDefaults = false;

        // WHY: Pair analyses with operations in stream order so prior namespace changes remain visible.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var intent = operations[ordinal].Intent;
            if (!TargetsCurrentDatabase(intent, _currentDatabase))
            {
                continue;
            }

            if (intent is EnsureColumnIntent ensureColumn
                && analyses[ordinal].ObservedState is not (SafeMigrationObservedState.Unsupported
                    or SafeMigrationObservedState.PrerequisiteMissing))
            {
                (touchedOwners ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(ensureColumn.Schema), ensureColumn.Table));
            }
            else if (intent is EnsureTableIntent ensureTable
                     && analyses[ordinal].ObservedState != SafeMigrationObservedState.Unsupported)
            {
                (touchedOwners ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(ensureTable.Definition.Schema), ensureTable.Definition.Table));

                captureCreationDefaults = true;
            }
            else if (intent is DropIndexIntent dropIndex
                     && MayMutateLiveNamespace(intent, analyses[ordinal])
                     && (analyses[ordinal].ObservedState != SafeMigrationObservedState.Unsupported
                         || RequiresSparseLivePrerequisites(analyses[ordinal])))
            {
                (touchedOwners ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(dropIndex.Schema), dropIndex.Table));
                (namespaceOwners ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(dropIndex.Schema), dropIndex.Table));
            }
            else if (intent is DropForeignKeyIntent dropForeignKey
                     && MayMutateLiveNamespace(intent, analyses[ordinal]))
            {
                // A declaration only requests catalog evidence. Authority is
                // still issued solely by an actually accepted physical drop.
                (touchedOwners ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(dropForeignKey.Schema), dropForeignKey.Table));
                (namespaceOwners ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(dropForeignKey.Schema), dropForeignKey.Table));
            }
            else if (GetPhysicalKeyTable(intent) is { } namespaceTable
                     && MayMutateLiveNamespace(intent, analyses[ordinal]))
            {
                (namespaceOwners ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(GetPhysicalKeySchema(intent)), namespaceTable));
            }
            else if (intent is RenameIndexIntent renameIndex
                     && MayMutateLiveNamespace(intent, analyses[ordinal]))
            {
                (namespaceOwners ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(renameIndex.Schema), renameIndex.Table));
            }
            else if (MayMutateLiveNamespace(intent, analyses[ordinal])
                     && intent is DropPrimaryKeyIntent or DropUniqueConstraintIntent)
            {
                var droppedOwner = intent is DropPrimaryKeyIntent primary
                    ? (primary.Table, primary.Schema)
                    : (((DropUniqueConstraintIntent)intent).Table, ((DropUniqueConstraintIntent)intent).Schema);

                (namespaceOwners ??= new HashSet<(string? Schema, string Table)>(PhysicalOwnerComparer)).Add(
                    (NormalizeProofSchema(droppedOwner.Schema), droppedOwner.Table));
            }

            var requiresFullQualification = RequiresSparseLivePrerequisites(analyses[ordinal]);
            if (RequiresGeneratedSourceCapture(intent, analyses[ordinal])
                && intent is EnsureColumnIntent generated)
            {
                // WHY: A mixed ADD may supply one dependency while another is
                // already live. Capture those source columns independently in
                // the existing bounded batch; a rejected whole-operation result
                // is neither a source certificate nor proof of source absence.
                AddRequestedColumns(
                    generated.Table,
                    generated.Schema,
                    MySqlGeneratedColumnDependencies.Collect(generated.Definition));
            }

            var namespaceRequest = intent is EnsureForeignKeyIntent namespaceForeignKey
                && analyses[ordinal].ObservedState is SafeMigrationObservedState.Missing
                    or SafeMigrationObservedState.Matching
                    or SafeMigrationObservedState.Different
                && namespaceOwners?.Contains(
                    (NormalizeProofSchema(namespaceForeignKey.Definition.Schema), namespaceForeignKey.Definition.Table)) == true;

            if (!requiresFullQualification
                && !namespaceRequest)
            {
                continue;
            }

            if (intent is EnsureForeignKeyIntent { Definition.Columns.Count: <= MySqlProjectedIndexPhysicalShape.MaximumKeyParts } foreignKey
                && (namespaceRequest
                    || touchedOwners?.Contains(
                        (NormalizeProofSchema(foreignKey.Definition.Schema), foreignKey.Definition.Table)) == true
                    || touchedOwners?.Contains(
                        (NormalizeProofSchema(foreignKey.Definition.PrincipalSchema),
                            foreignKey.Definition.PrincipalTable)) == true))
            {
                (requestedKeys ??= new HashSet<ExpectedForeignKeyDefinition>(ReferenceEqualityComparer.Instance)).Add(foreignKey.Definition);
                if (requiresFullQualification)
                {
                    (fullQualificationTargets ??= new(ReferenceEqualityComparer.Instance)).Add(foreignKey.Definition);
                }
                else if (analyses[ordinal].ObservedState == SafeMigrationObservedState.Missing)
                {
                    (namespaceMissing ??= new(ReferenceEqualityComparer.Instance)).TryAdd(
                        foreignKey.Definition,
                        analyses[ordinal]);
                }

                AddRequestedColumns(
                    foreignKey.Definition.Table,
                    foreignKey.Definition.Schema,
                    foreignKey.Definition.Columns);
                AddRequestedColumns(
                    foreignKey.Definition.PrincipalTable,
                    foreignKey.Definition.PrincipalSchema,
                    foreignKey.Definition.PrincipalColumns);
            }
            else if (GetPhysicalKeyTable(intent) is { } table
                     && touchedOwners?.Contains((NormalizeProofSchema(GetPhysicalKeySchema(intent)), table)) == true)
            {
                AddRequestedColumns(table, GetPhysicalKeySchema(intent), GetPhysicalKeyColumns(intent));
            }
            else if (intent is EnsureCheckConstraintIntent check
                     && touchedOwners?.Contains((NormalizeProofSchema(check.Definition.Schema), check.Definition.Table))
                     == true)
            {
                AddRequestedColumns(
                    check.Definition.Table,
                    check.Definition.Schema,
                    SafeMigrationPrerequisiteColumns.Local(check));
            }
        }

        if (requestedColumns is null)
        {
            return;
        }

        var requests = requestedColumns.ToArray();
        var captured =
            new Dictionary<(string? Schema, string Table, string Name), LivePrerequisiteColumn>(PhysicalMemberComparer);

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            context.Database.GetDbConnection(),
            requests.Length,
            maximumPayloadBytes,
            commandTimeout,
            (command, offset) => BuildLivePrerequisiteColumnsProbe(
                command,
                requests,
                offset,
                maximumPayloadBytes,
                captureCreationDefaults),
            async (reader, offset, count, token) =>
            {
                if (reader.FieldCount != (captureCreationDefaults ? 11 : 9))
                {
                    throw new InvalidOperationException("MySQL returned an invalid live-prerequisite column shape.");
                }

                var seen = new bool[count];
                while (await reader.ReadAsync(token))
                {
                    var ordinal = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                    if (ordinal < offset
                        || ordinal >= offset + count
                        || seen[ordinal - offset])
                    {
                        throw new InvalidOperationException("MySQL returned an invalid live-prerequisite ordinal.");
                    }

                    seen[ordinal - offset] = true;
                    var ownerEligible = !reader.IsDBNull(1)
                        && reader.GetString(1) == "BASE TABLE"
                        && !reader.IsDBNull(2)
                        && StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(2), "InnoDB")
                        && !ReadLivePrerequisiteBoolean(reader, 4);

                    captured.Add(
                        requests[ordinal],
                        new LivePrerequisiteColumn(
                            !reader.IsDBNull(1),
                            ownerEligible,
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            reader.IsDBNull(5) ? null : reader.GetString(5),
                            reader.IsDBNull(6) ? null : reader.GetString(6),
                            !reader.IsDBNull(7) && reader.GetString(7) == "YES",
                            !reader.IsDBNull(8)
                            && reader.GetString(8).Length > 0,
                            captureCreationDefaults
                            && !reader.IsDBNull(9)
                            && StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(9), "InnoDB"),
                            captureCreationDefaults && !reader.IsDBNull(10) ? reader.GetString(10) : null));
                }

                if (seen.Any(static value => !value))
                {
                    throw new InvalidOperationException("MySQL omitted a live-prerequisite column certificate.");
                }
            },
            cancellationToken,
            context.Database.CurrentTransaction?.GetDbTransaction());

        Dictionary<ExpectedForeignKeyDefinition, LivePrerequisiteKeyProof>? capturedKeys = null;
        var isMariaDb = false;
        if (requestedKeys is not null)
        {
            (capturedKeys, isMariaDb) = await ReadLiveCandidateKeysAsync(
                context,
                requestedKeys.ToArray(),
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);
        }

        // WHY: Publish only a complete capture. A canceled key query must not
        // expose a partial column snapshot from a failed analysis invocation.
        _livePrerequisiteColumns = captured;
        _livePrerequisiteKeys = capturedKeys;
        _livePrerequisiteFullQualificationTargets = fullQualificationTargets;
        _liveNamespaceMissingAnalyses = namespaceMissing;
        _livePrerequisiteBuilder = capturedKeys is null
            ? null
            : new MySqlSafeMigrationCatalogSqlBuilder(_typeMappingSource, context.GetService<ISqlGenerationHelper>());

        _livePrerequisiteCreationDefaultsCurrent = captureCreationDefaults;
        if (capturedKeys is not null)
        {
            var supporting = new MySqlProjectedForeignKeySupportingIndexes(
                NormalizeProofSchema,
                isMariaDb,
                NormalizeTableIdentifier);

            foreach (var pair in capturedKeys)
            {
                supporting.Capture(
                    pair.Key,
                    pair.Value.LocalIndexNameOccupied,
                    pair.Value.CoveringIndexNames,
                    pair.Value.MultiValuedIndexNames);
            }

            _livePrerequisiteSupportingIndexes = supporting;
        }

        return;

        void AddRequestedColumns(
            string table,
            string? schema,
            IEnumerable<string> columns
        )
        {
            requestedColumns ??= new(PhysicalMemberComparer);
            foreach (var column in columns)
            {
                requestedColumns.Add((NormalizeProofSchema(schema), table, column));
            }
        }
    }

    /// <summary>Targets only missing metadata or the independently identified mutable native MVI boundary.</summary>
    private static bool RequiresSparseLivePrerequisites(
        SafeMigrationProviderAnalysis analysis
    ) => analysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
        || analysis is
        {
            ObservedState: SafeMigrationObservedState.Unsupported,
            IsInvariantUnsupported: false,
            Code: "foreign_key_multi_valued_supporting_index"
        };

    /// <summary>Requests retained-symbol replacement facts without adding matching-only capture overhead.</summary>
    private static bool HasSparseLivePrerequisiteRequests(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses
    )
    {
        var hasNamespaceMutation = false;
        // WHY: Pair analyses with operations in stream order so prior namespace changes remain visible.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var intent = operations[ordinal].Intent;
            if (RequiresSparseLivePrerequisites(analyses[ordinal])
                || RequiresGeneratedSourceCapture(intent, analyses[ordinal])
                || (intent is EnsureForeignKeyIntent
                    && (analyses[ordinal].ObservedState == SafeMigrationObservedState.Different
                        || (hasNamespaceMutation
                            && analyses[ordinal].ObservedState is SafeMigrationObservedState.Missing
                                or SafeMigrationObservedState.Matching))))
            {
                return true;
            }

            hasNamespaceMutation |= MayMutateLiveNamespace(intent, analyses[ordinal]);
        }

        return false;
    }

    /// <summary>
    /// Skips independently matching create/no-op declarations on the ordinary matching-only fast path.
    /// </summary>
    internal static bool MayMutateLiveNamespace(
        SafeMigrationIntent intent,
        SafeMigrationProviderAnalysis analysis
    ) => intent switch
    {
        EnsureIndexIntent or EnsurePrimaryKeyIntent or EnsureUniqueConstraintIntent =>
            analysis.ObservedState is not (SafeMigrationObservedState.Matching or SafeMigrationObservedState.Unsupported
                ),
        DropForeignKeyIntent
            or DropIndexIntent
            or RenameIndexIntent
            or DropPrimaryKeyIntent
            or DropUniqueConstraintIntent => analysis.ObservedState is SafeMigrationObservedState.Matching
                or SafeMigrationObservedState.Different,
        _ => false,
    };

    /// <summary>Gets the physical-key owner's qualifier without normalizing foreign database identities.</summary>
    private static string? GetPhysicalKeySchema(
        SafeMigrationIntent intent
    ) => intent switch
    {
        EnsureIndexIntent value => value.Definition.Schema,
        EnsurePrimaryKeyIntent value => value.Definition.Schema,
        EnsureUniqueConstraintIntent value => value.Definition.Schema,
        _ => null,
    };

    /// <summary>Builds a bounded requested-column inventory with sentinel rows for absent owners or members.</summary>
    /// <param name="command">The owned native or sequential catalog command.</param>
    /// <param name="requests">The deduplicated physical owner/member requests.</param>
    /// <param name="offset">The first requested ordinal that has not yet been submitted.</param>
    /// <param name="maximumPayloadBytes">The current packet-qualified UTF-8 transport ceiling.</param>
    /// <param name="includeCreationDefaults">Whether an earlier plain safe CREATE needs native defaults.</param>
    /// <returns>The exact number and value payload of requests that fit this statement.</returns>
    internal static SafeMigrationCatalogProbeStatement BuildLivePrerequisiteColumnsProbe(
        SafeMigrationCatalogCommand command,
        (string? Schema, string Table, string Name)[] requests,
        int offset,
        int maximumPayloadBytes,
        bool includeCreationDefaults = false
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, requests.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);

        var basePrefix = "SELECT requested.ordinal, t.TABLE_TYPE, t.ENGINE, t.TABLE_COLLATION, "
            + "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.PARTITIONS p WHERE p.TABLE_SCHEMA = DATABASE() "
            + "AND "
            + MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
                "p.TABLE_NAME",
                "requested.table_name")
            + " "
            + "AND p.PARTITION_NAME IS NOT NULL), "
            + "c.COLUMN_TYPE, c.COLLATION_NAME, c.IS_NULLABLE, c.GENERATION_EXPRESSION";

        var prefix = includeCreationDefaults
            ? basePrefix + ", @@SESSION.default_storage_engine, @@collation_database FROM ("
            : basePrefix + " FROM (";

        var suffix = ") requested LEFT JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = DATABASE() "
            + "AND "
            + MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
                "t.TABLE_NAME",
                "requested.table_name")
            + " "
            + "LEFT JOIN INFORMATION_SCHEMA.COLUMNS c "
            + "ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME "
            + "AND CAST(c.TABLE_SCHEMA AS BINARY) = CAST(t.TABLE_SCHEMA AS BINARY) "
            + "AND CAST(c.TABLE_NAME AS BINARY) = CAST(t.TABLE_NAME AS BINARY) "
            + "AND c.COLUMN_NAME = requested.column_name AND "
            + MySqlSafeMigrationCatalogSqlBuilder.BuildNativeCaseInsensitiveIdentifierMatches(
                "c.COLUMN_NAME",
                "requested.column_name")
            + " ORDER BY requested.ordinal;";

        const string separator = " UNION ALL ";
        var capacity = Math.Min(
            SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
            requests.Length - offset);
        var selections = new List<string>(capacity);
        var valueBytes = 0;
        var sqlBytes = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(suffix);

        // WHY: Keep the bounded request window and emitted result ordinals aligned.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < capacity; index++)
        {
            var request = requests[offset + index];
            var originalParameters = command.Parameters.Count;
            var originalValueBytes = valueBytes;
            var table = MySqlSafeMigrationCatalogSqlBuilder.AddLivePrerequisiteParameter(
                command,
                request.Table,
                ref valueBytes);

            var column =
                MySqlSafeMigrationCatalogSqlBuilder.AddLivePrerequisiteParameter(command, request.Name, ref valueBytes);

            var selection = "SELECT "
                + (offset + index).ToString(CultureInfo.InvariantCulture)
                + " AS ordinal, "
                + table
                + " AS table_name, "
                + column
                + " AS column_name";

            var prospectiveSqlBytes = checked(sqlBytes
                + Encoding.UTF8.GetByteCount(selection)
                + (selections.Count == 0 ? 0 : separator.Length));

            var prospectiveBytes = checked(prospectiveSqlBytes + valueBytes);
            if (SafeMigrationCatalogQueryLimits.Exceeded(
                    command.Parameters.Count,
                    prospectiveBytes,
                    maximumPayloadBytes))
            {
                if (selections.Count == 0)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                        offset,
                        command.Parameters.Count,
                        prospectiveBytes);
                }

                // WHY: A large valid inventory may fit only part of this
                // optimizer window. Roll back the unsubmitted parameters and
                // let the bounded transport continue at the next real ordinal.
                while (command.Parameters.Count > originalParameters)
                {
                    command.Parameters.RemoveAt(command.Parameters.Count - 1);
                }

                valueBytes = originalValueBytes;

                break;
            }

            selections.Add(selection);
            sqlBytes = prospectiveSqlBytes;
        }

        // WHY: LEFT JOIN emits one owned sentinel for each request. Missing
        // tables/columns cannot silently disappear and masquerade as complete
        // evidence; no application table identifier is bound in this query.
        command.CommandText = prefix + string.Join(separator, selections) + suffix;

        return new SafeMigrationCatalogProbeStatement(selections.Count, valueBytes, offset);
    }

    /// <summary>Reads complete candidate identities under the same bounded, cancellable catalog transport.</summary>
    private static async Task<(Dictionary<ExpectedForeignKeyDefinition, LivePrerequisiteKeyProof> Keys, bool IsMariaDb)>
        ReadLiveCandidateKeysAsync(
            DbContext context,
            ExpectedForeignKeyDefinition[] requests,
            int maximumPayloadBytes,
            int? commandTimeout,
            CancellationToken cancellationToken
        )
    {
        var version = MySqlServerVersion.AutoDetect(
            context.Database.GetDbConnection(),
            MySqlServerVersionCompatibilityMode.AllowUnsupported);

        var databaseScopedNames = !version.IsMariaDb || version.Version < new Version(12, 1);
        var captured = new Dictionary<ExpectedForeignKeyDefinition, LivePrerequisiteKeyProof>(
            ReferenceEqualityComparer.Instance);

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            context.Database.GetDbConnection(),
            requests.Length,
            maximumPayloadBytes,
            commandTimeout,
            (
                command,
                ordinal
            ) => new SafeMigrationCatalogProbeStatement(
                1,
                MySqlSafeMigrationCatalogSqlBuilder.BuildLiveForeignKeyCandidateProbe(
                    command,
                    requests[ordinal],
                    databaseScopedNames,
                    version.IsMariaDb),
                ordinal),
            async (reader, ordinal, _, token) =>
            {
                captured.Add(requests[ordinal], await ReadLiveCandidateKeyProofAsync(reader, token));
            },
            cancellationToken,
            context.Database.CurrentTransaction?.GetDbTransaction());

        return (captured, version.IsMariaDb);
    }

    /// <summary>
    /// Rejects incomplete, contradictory or lossy native certificates before projection consumes them.
    /// </summary>
    /// <param name="reader">Exactly one owned six-field candidate/supporting-index result set.</param>
    /// <param name="cancellationToken">The token that cancels every streamed native alias.</param>
    /// <returns>The complete candidate list, including independent constraint/backing-index identities.</returns>
    internal static async Task<LivePrerequisiteKeyProof> ReadLiveCandidateKeyProofAsync(
        DbDataReader reader,
        CancellationToken cancellationToken
    )
    {
        if (reader.FieldCount != 6)
        {
            throw new InvalidOperationException("MySQL returned an invalid candidate-key result shape.");
        }

        bool? namesFree = null;
        bool? ownedCollision = null;
        bool? localIndexNameOccupied = null;
        bool? multiValuedIndexRisk = null;
        List<SafeMigrationLiveCandidateKey>? keys = null;
        List<string>? covering = null;
        List<string>? unsafeIndexes = null;
        List<string>? matchingForeignKeys = null;
        HashSet<string>? names = null;
        HashSet<string>? localNames = null;
        HashSet<string>? unsafeNames = null;
        HashSet<string>? foreignKeyNames = null;
        var sawSentinel = false;

        while (await reader.ReadAsync(cancellationToken))
        {
            var available = ReadLivePrerequisiteBoolean(reader, 0);
            var owned = ReadLivePrerequisiteBoolean(reader, 1);
            var occupied = ReadLivePrerequisiteBoolean(reader, 2);
            var unsafeNativeIndex = ReadLivePrerequisiteBoolean(reader, 3);
            if ((available && owned)
                || (namesFree is not null && namesFree != available)
                || (ownedCollision is not null && ownedCollision != owned)
                || (localIndexNameOccupied is not null && localIndexNameOccupied != occupied)
                || (multiValuedIndexRisk is not null && multiValuedIndexRisk != unsafeNativeIndex))
            {
                throw new InvalidOperationException("MySQL returned inconsistent live-prerequisite key facts.");
            }

            namesFree = available;
            ownedCollision = owned;
            localIndexNameOccupied = occupied;
            multiValuedIndexRisk = unsafeNativeIndex;
            if (reader.IsDBNull(4))
            {
                if (!reader.IsDBNull(5)
                    || keys is not null
                    || covering is not null
                    || unsafeIndexes is not null
                    || matchingForeignKeys is not null
                    || sawSentinel)
                {
                    throw new InvalidOperationException("MySQL returned an invalid candidate-key sentinel.");
                }

                sawSentinel = true;

                continue;
            }

            var name = reader.GetString(4);
            var rawKind = reader.IsDBNull(5) ? null : reader.GetString(5);
            var isLocal = rawKind == "local_covering_index";
            var isUnsafe = rawKind == "local_multi_valued_index";
            var isForeignKey = rawKind == "local_matching_foreign_key";
            var identities = isForeignKey
                ? foreignKeyNames ??= new(StringComparer.OrdinalIgnoreCase)
                : isUnsafe
                    ? unsafeNames ??= new(StringComparer.OrdinalIgnoreCase)
                    : isLocal
                        ? localNames ??= new(StringComparer.OrdinalIgnoreCase)
                        : names ??= new(StringComparer.OrdinalIgnoreCase);

            if (name.Length == 0
                || sawSentinel
                || !identities.Add(name))
            {
                throw new InvalidOperationException("MySQL returned an unowned or duplicate candidate key.");
            }

            if (isLocal)
            {
                (covering ??= []).Add(name);

                continue;
            }

            if (isUnsafe)
            {
                (unsafeIndexes ??= []).Add(name);

                continue;
            }

            if (isForeignKey)
            {
                (matchingForeignKeys ??= []).Add(name);

                continue;
            }

            var kind = rawKind switch
            {
                "primary_key" => SafeMigrationDatabaseObjectKind.PrimaryKey,
                "unique_constraint" => SafeMigrationDatabaseObjectKind.UniqueConstraint,
                "index" => SafeMigrationDatabaseObjectKind.Index,
                _ => throw new InvalidOperationException("MySQL returned an unsupported candidate-key kind."),
            };

            if (kind == SafeMigrationDatabaseObjectKind.PrimaryKey
                && !StringComparer.OrdinalIgnoreCase.Equals(name, "PRIMARY"))
            {
                throw new InvalidOperationException("MySQL returned an invalid native primary-key identity.");
            }

            (keys ??= []).Add(
                new SafeMigrationLiveCandidateKey(
                    kind,
                    name,
                    kind == SafeMigrationDatabaseObjectKind.UniqueConstraint ? name : null));
        }

        if (namesFree is null)
        {
            throw new InvalidOperationException("MySQL omitted a live-prerequisite candidate-key certificate.");
        }

        if (multiValuedIndexRisk != (unsafeIndexes is not null))
        {
            throw new InvalidOperationException("MySQL returned an incomplete multi-valued index certificate.");
        }

        return new LivePrerequisiteKeyProof(
            namesFree.Value,
            ownedCollision!.Value,
            localIndexNameOccupied!.Value,
            multiValuedIndexRisk!.Value,
            keys is null ? [] : keys.ToArray(),
            covering is null ? [] : covering.ToArray(),
            unsafeIndexes is null ? [] : unsafeIndexes.ToArray(),
            matchingForeignKeys is null ? [] : matchingForeignKeys.ToArray());
    }

    /// <summary>Rejects non-Boolean native flags rather than interpreting arbitrary nonzero values as proof.</summary>
    private static bool ReadLivePrerequisiteBoolean(
        DbDataReader reader,
        int ordinal
    )
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException("MySQL omitted a live-prerequisite Boolean certificate.");
        }

        var raw = reader.GetValue(ordinal);
        if (raw is not (bool or byte or sbyte or short or ushort or int or uint or long or ulong))
        {
            throw new InvalidOperationException("MySQL returned a non-integral live-prerequisite Boolean value.");
        }

        // WHY: Decimal conversion is exact for every native integer width.
        // ToInt64 would overflow unsigned values or round a forged fractional
        // flag, neither of which can constitute Boolean catalog authority.
        var value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);

        return value switch
        {
            0m => false,
            1m => true,
            _ => throw new InvalidOperationException("MySQL returned a non-Boolean live-prerequisite certificate."),
        };
    }

    /// <summary>Stores requested owner, column and inherited-collation facts, never target-model guesses.</summary>
    private readonly record struct LivePrerequisiteColumn(
        bool OwnerPresent,
        bool OwnerEligible,
        string? TableCollation,
        string? StoreType,
        string? Collation,
        bool IsNullable,
        bool IsGenerated,
        bool CreationEngineEligible,
        string? CreationCollation
    );

    /// <summary>Stores compatible physical endpoint storage without claiming row safety.</summary>
    private readonly record struct LiveForeignKeyEndpoint(
        string StoreType,
        string? Collation,
        bool IsNullable
    );

    /// <summary>Retains the actual creation-time defaults independently of later ambient changes.</summary>
    private readonly record struct LivePrerequisiteCreation(
        bool Eligible,
        string? Collation
    );

    /// <summary>Retains all independently surviving native candidates and the exact FK symbol boundary.</summary>
    /// <param name="NamesFree">Whether no native FK owns the requested symbol.</param>
    /// <param name="OwnedCollision">Whether only the exact requested table owns a releasable symbol.</param>
    /// <param name="LocalIndexNameOccupied">Whether a dependent index already owns the FK symbol.</param>
    /// <param name="MultiValuedIndexRisk">
    /// Whether native FK prefix selection can use an unsafe multivalued index.
    /// </param>
    /// <param name="Keys">The complete independently captured native candidates.</param>
    /// <param name="CoveringIndexNames">Every independently captured usable dependent supporting-index alias.</param>
    /// <param name="MultiValuedIndexNames">Every independently captured native MVI lookup-risk alias.</param>
    /// <param name="MatchingForeignKeyNames">Every original physically matching FK alias.</param>
    internal readonly record struct LivePrerequisiteKeyProof(
        bool NamesFree,
        bool OwnedCollision,
        bool LocalIndexNameOccupied,
        bool MultiValuedIndexRisk,
        SafeMigrationLiveCandidateKey[] Keys,
        string[] CoveringIndexNames,
        string[] MultiValuedIndexNames,
        string[] MatchingForeignKeyNames
    );
}
