namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private static SafeMigrationProviderAnalysis? AnalyzeAcceptedDefinition<T>(
        ProjectedDefinitionSet<T> definitions,
        string name,
        T expected,
        SafeMigrationProviderAnalysis liveAnalysis
    )
        where T : class
    {
        if (definitions.TryGetValue(name, out var exact))
        {
            return Analysis(
                definitions.SemanticallyEquals(exact, expected)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Different);
        }

        if (definitions.ContainsSemantically(expected))
        {
            // WHY: An unchanged table-matched shape proves a semantic Ensure
            // postcondition without proving an authored physical drop target.
            // Observation binds names only from separate actual identity proof.
            return Analysis(SafeMigrationObservedState.Matching);
        }

        if (definitions.HasUnprovenMutatedIdentity(liveAnalysis))
        {
            return ConstraintIdentityUnknown(definitions.IdentityUnknownOriginOrdinal);
        }

        var mutation = definitions.ResolveMutation(name, expected, liveAnalysis);

        return mutation switch
        {
            ProjectedDefinitionMutation.Missing => Analysis(SafeMigrationObservedState.Missing),
            ProjectedDefinitionMutation.Unknown => definitions.IdentityUnknownOriginOrdinal is { } origin
                ? ConstraintIdentityUnknown(origin)
                : StructureStateUnknown(),
            ProjectedDefinitionMutation.None => null,
            _ => throw new InvalidOperationException(
                $"Unsupported projected definition mutation '{mutation}'."),
        };
    }

    /// <summary>Defers only the member family whose historical physical alias cannot be reconstructed.</summary>
    /// <param name="origin">The accepted physical removal's complete-stream ordinal, when captured.</param>
    private static SafeMigrationProviderAnalysis ConstraintIdentityUnknown(int? origin) => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "projected_constraint_identity_unknown")
    {
        ProviderDeferredOriginOrdinal = origin,
    };

    private void CaptureConstraintPrerequisites(
        ProjectedPrerequisites prerequisites,
        ExpectedTableDefinition definition,
        SafeMigrationProviderAnalysis? liveAnalysis = null
    )
    {
        if (definition.PrimaryKey is not null && !prerequisites.PrimaryKeyWasDropped)
        {
            var physicalName = GetCapturedPrimaryKeyPhysicalName(definition.PrimaryKey, prerequisites.NewlyCreated)
                ?? CapturedTableMemberName(liveAnalysis, SafeMigrationDatabaseObjectKind.PrimaryKey,
                    definition.PrimaryKey.Name);

            prerequisites.AcceptPrimaryKey(definition.PrimaryKey,
                physicalName, createdByProjection: prerequisites.NewlyCreated);
        }

        foreach (var constraint in definition.UniqueConstraints)
        {
            CaptureTableMember(prerequisites.UniqueConstraints, constraint.Name, constraint,
                prerequisites.NewlyCreated, SafeMigrationDatabaseObjectKind.UniqueConstraint, liveAnalysis);
        }

        foreach (var constraint in definition.CheckConstraints)
        {
            CaptureTableMember(prerequisites.CheckConstraints, constraint.Name, constraint,
                prerequisites.NewlyCreated, SafeMigrationDatabaseObjectKind.CheckConstraint, liveAnalysis);
        }

        foreach (var constraint in definition.ForeignKeys)
        {
            CaptureTableMember(prerequisites.ForeignKeys, constraint.Name, constraint,
                prerequisites.NewlyCreated, SafeMigrationDatabaseObjectKind.ForeignKey, liveAnalysis);
        }

        CaptureSharedUniqueKeys(prerequisites, definition, liveAnalysis);
    }

    /// <summary>Separates semantic table matching from independently certified physical member identity.</summary>
    private static void CaptureTableMember<T>(
        ProjectedDefinitionSet<T> definitions,
        string authoredName,
        T definition,
        bool newlyCreated,
        SafeMigrationDatabaseObjectKind kind,
        SafeMigrationProviderAnalysis? liveAnalysis
    )
        where T : class
    {
        if (newlyCreated)
        {
            definitions.AcceptPhysical(authoredName, definition, createdByProjection: true);

            return;
        }

        if (CapturedTableMemberName(liveAnalysis, kind, authoredName) is { } physicalName)
        {
            // WHY: A snapshot certificate predates the ordered stream. It may
            // bind untouched physical members but cannot resurrect a tombstone.
            if (!definitions.TryGetPhysicalState(physicalName, out var present) || present)
            {
                _ = definitions.BindAlias(authoredName, physicalName, definition);
            }

            return;
        }

        definitions.AcceptShapeOnly(definition);
    }

    /// <summary>Reads only matching provider evidence for the exact authored table-member contract.</summary>
    private static string? CapturedTableMemberName(
        SafeMigrationProviderAnalysis? analysis,
        SafeMigrationDatabaseObjectKind kind,
        string authoredName
    )
    {
        if (analysis is not
            { ObservedState: SafeMigrationObservedState.Matching, TableConstraintIdentities: { } bindings })
        {
            return null;
        }

        foreach (var binding in bindings)
        {
            if (binding.Kind == kind && StringComparer.Ordinal.Equals(binding.AuthoredName, authoredName)
                && !string.IsNullOrEmpty(binding.PhysicalName))
            {
                return binding.PhysicalName;
            }
        }

        return null;
    }

    private static void ObserveAcceptedDefinition<T>(
        ProjectedDefinitionSet<T> definitions,
        string name,
        T definition,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
        where T : class
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            definitions.AcceptPhysical(name, definition, createdByProjection: true);
            return;
        }

        if (decision.Action != SafeMigrationAction.NoOp)
        {
            return;
        }

        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
            && liveAnalysis.MatchedObjectName is { } physicalName
            && definitions.BindAlias(name, physicalName, definition))
        {
            return;
        }

        // WHY: A projected semantic match can bind another requested name only
        // when exactly one physical object supplies that postcondition. An
        // ambiguous or unresolved NoOp must not become prerequisite evidence.
        _ = definitions.BindUniqueSemanticAlias(name, definition);
    }

    private bool CanProjectMissingPrimaryKey(
        EnsurePrimaryKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
        && TryGetConstraintPrerequisites(
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.Columns,
            out var prerequisites)
        && ProvesTableEmpty(intent.Definition.Table, intent.Definition.Schema, prerequisites);

    private bool CanProjectMissingUniqueConstraint(
        EnsureUniqueConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
        && TryGetConstraintPrerequisites(
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.Columns,
            out var prerequisites)
        && (ProvesTableEmpty(intent.Definition.Table, intent.Definition.Schema, prerequisites)
            || HasNullPreservingAddedColumn(intent.Definition.Columns, prerequisites));

    private bool CanProjectMissingCheckConstraint(
        EnsureCheckConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
        && TryGetConstraintPrerequisites(
            intent.Definition.Table,
            intent.Definition.Schema,
            SafeMigrationPrerequisiteColumns.Local(intent),
            out var prerequisites)
        && ProvesTableEmpty(intent.Definition.Table, intent.Definition.Schema, prerequisites);

    private bool CanProjectMissingForeignKey(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (liveAnalysis.ObservedState != SafeMigrationObservedState.PrerequisiteMissing
            || !HasForeignKeyStructuralPrerequisites(intent)
            || !_prerequisites.TryGetValue(
                new TableKey(intent.Definition.Table, intent.Definition.Schema),
                out var dependent))
        {
            return false;
        }

        return ProvesTableEmpty(intent.Definition.Table, intent.Definition.Schema, dependent)
            || HasNullPreservingAddedColumn(intent.Definition.Columns, dependent);
    }

    private bool HasForeignKeyStructuralPrerequisites(
        EnsureForeignKeyIntent intent
    )
    {
        var definition = intent.Definition;
        if (!HasConstraintColumns(definition.Table, definition.Schema, definition.Columns)
            || !HasConstraintColumns(definition.PrincipalTable, definition.PrincipalSchema,
                definition.PrincipalColumns))
        {
            return false;
        }

        var acceptedKey = HasAcceptedCandidateKey(definition.PrincipalTable,
            definition.PrincipalSchema, definition.PrincipalColumns);

        // WHY: A new owner can inherit native defaults absent from its raw
        // column definitions. An explicit provider certificate requirement
        // therefore remains authoritative for an accepted new lifetime too;
        // the default-false contract preserves unrelated projected owners.
        var requiresLiveQualification = _livePrerequisiteSource?.RequiresLiveForeignKeyQualification(intent) == true;

        if (!requiresLiveQualification && acceptedKey && ForeignKeyColumnStorageMatches(definition))
        {
            return true;
        }

        // WHY: A pending stream need not converge an unchanged live principal.
        // Only independently captured provider metadata may fill that sparse
        // contract; Core retains ordered lifetimes and separate row evidence.
        var liveKeys = _livePrerequisiteSource?.GetLiveForeignKeyCandidateKeys(intent, this);
        if (liveKeys is null)
        {
            return false;
        }

        // WHY: A matching/no-op shape can describe an unusable native key
        // (for example SQLite UNIQUE with another equality collation). It cannot
        // replace a missing native candidate certificate. Only fresh accepted
        // Apply supplies an independently established new physical key lifetime.
        if (acceptedKey && (!requiresLiveQualification || HasCreatedCandidateKey(definition)))
        {
            return true;
        }

        // WHY: Index the retained candidate-key snapshot without allocating an interface enumerator.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < liveKeys.Count; ordinal++)
        {
            if (LiveCandidateKeySurvives(definition, liveKeys[ordinal]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Checks exact surviving native key lifetimes, not names reused by replacements.</summary>
    private bool LiveCandidateKeySurvives(
        ExpectedForeignKeyDefinition foreignKey,
        SafeMigrationLiveCandidateKey candidate
    )
    {
        var owner = new TableKey(foreignKey.PrincipalTable, foreignKey.PrincipalSchema);
        if (_projectedUnknownPhysicalKeys.Contains(owner)
            || _droppedPhysicalKeys.Contains(new IndexKey(owner.Table, owner.Schema, candidate.PhysicalName))
            || (candidate.BackingIndexName is { } backing
                && _droppedPhysicalKeys.Contains(new IndexKey(owner.Table, owner.Schema, backing))))
        {
            return false;
        }

        if (!_prerequisites.TryGetValue(owner, out var prerequisites))
        {
            return true;
        }

        if (prerequisites.NewlyCreated)
        {
            // WHY: A recreated table cannot inherit a key from its previous
            // live lifetime, even when the authored name happens to match.
            return false;
        }

        return candidate.Kind switch
        {
            SafeMigrationDatabaseObjectKind.PrimaryKey => !prerequisites.PrimaryKeyWasDropped
                && (prerequisites.PrimaryKey is null
                    || SameColumns(prerequisites.PrimaryKey.Columns, foreignKey.PrincipalColumns,
                        _objectIdentityNormalizer)),
            SafeMigrationDatabaseObjectKind.UniqueConstraint =>
                SurvivingLiveUniqueConstraint(prerequisites, candidate.PhysicalName, foreignKey.PrincipalColumns)
                && SurvivingLiveBackingIndex(prerequisites, candidate.BackingIndexName, foreignKey.PrincipalColumns),
            SafeMigrationDatabaseObjectKind.Index =>
                SurvivingLiveBackingIndex(prerequisites, candidate.PhysicalName, foreignKey.PrincipalColumns),
            _ => false,
        };
    }

    /// <summary>Rejects a removed or differently replaced native unique constraint.</summary>
    private bool SurvivingLiveUniqueConstraint(
        ProjectedPrerequisites prerequisites,
        string name,
        IReadOnlyList<string> columns
    ) => !prerequisites.UniqueConstraints.TryGetPhysicalState(name, out var present)
        || (present && prerequisites.UniqueConstraints.TryGetPhysicalDefinition(name, out var definition)
            && SameColumns(definition.Columns, columns, _objectIdentityNormalizer));

    /// <summary>Rejects a removed or differently replaced backing index, including a constraint-owned index.</summary>
    private bool SurvivingLiveBackingIndex(
        ProjectedPrerequisites prerequisites,
        string? name,
        IReadOnlyList<string> columns
    ) => name is null || !prerequisites.Indexes.TryGetPhysicalState(name, out var present)
        || (present && prerequisites.Indexes.TryGetPhysicalDefinition(name, out var definition)
            && IsUnfilteredCandidateIndex(definition, columns));

    /// <summary>Recognizes only ordinary complete keys without unproven provider-specific equality semantics.</summary>
    private bool IsUnfilteredCandidateIndex(ExpectedIndexDefinition index, IReadOnlyList<string> columns)
    {
        if (!index.Unique || index.Filter is not null || index.StructuredFilter is not null
            || index.Keys.Count != columns.Count)
        {
            return false;
        }

        // WHY: Match each index part to the corresponding candidate-key column ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            var part = index.Keys[ordinal];
            // WHY: A unique index can use equality different from the parent
            // column (SQLite COLLATE or a PostgreSQL operator class). Uniqueness
            // alone is not an FK certificate for those provider-specific keys.
            if (part.Column is null || part.PrefixLength is not null
                || part.Collation is not null || part.OperatorClass is not null
                || !(_objectIdentityNormalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal)
                    .Equals(part.Column, columns[ordinal]))
            {
                return false;
            }
        }

        return true;
    }

    private bool CanReuseMatchingLiveColumnPrerequisites(
        string table,
        string? schema,
        IReadOnlyList<string> columns,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (liveAnalysis.ObservedState != SafeMigrationObservedState.Matching
            || IsProjectedTableStructureUnknown(table, schema))
        {
            return false;
        }

        var tableKey = new TableKey(table, schema);

        return (!_projectedChangedColumns.TryGetValue(tableKey, out var changedColumns)
                || columns.All(column => !changedColumns.Contains(column)))
            && columns.All(column => !IsProjectedColumnMissing(table, schema, column)
                && !IsProjectedColumnUnknown(table, schema, column));
    }

    private bool CanReuseMatchingLiveForeignKeyPrerequisites(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => CanReuseMatchingLiveColumnPrerequisites(
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.Columns,
            liveAnalysis)
        && CanReuseMatchingLiveColumnPrerequisites(
            intent.Definition.PrincipalTable,
            intent.Definition.PrincipalSchema,
            intent.Definition.PrincipalColumns,
            liveAnalysis)
        && (!_projectedCandidateKeyMutationTables.Contains(
                new TableKey(intent.Definition.PrincipalTable, intent.Definition.PrincipalSchema))
            || HasAcceptedCandidateKey(
                intent.Definition.PrincipalTable,
                intent.Definition.PrincipalSchema,
                intent.Definition.PrincipalColumns));

    private bool TryGetConstraintPrerequisites(
        string table,
        string? schema,
        IReadOnlyList<string> columns,
        [NotNullWhen(true)] out ProjectedPrerequisites? prerequisites
    )
    {
        if (!_prerequisites.TryGetValue(new TableKey(table, schema), out prerequisites)
            || !HasConstraintColumns(table, schema, columns))
        {
            prerequisites = null;
            return false;
        }

        return true;
    }

    /// <summary>Combines accepted members with independently live untouched members of one surviving owner.</summary>
    private bool HasConstraintColumns(string table, string? schema, IReadOnlyList<string> columns)
    {
        var owner = new TableKey(table, schema);
        if (_projectedMissingTables.Contains(owner) || IsProjectedTableStructureUnknown(table, schema))
        {
            return false;
        }

        foreach (var column in columns)
        {
            if (IsProjectedColumnMissing(table, schema, column) || IsProjectedColumnUnknown(table, schema, column))
            {
                return false;
            }

            if (TryGetProjectedColumnDefinition(table, schema, column, out _))
            {
                continue;
            }

            if ((_prerequisites.TryGetValue(owner, out var prerequisites) && prerequisites.NewlyCreated)
                || (_projectedChangedColumns.TryGetValue(owner, out var changed) && changed.Contains(column))
                || _livePrerequisiteSource?.HasLiveColumn(table, schema, column) != true)
            {
                return false;
            }
        }

        return true;
    }

    private bool ProvesTableEmpty(
        string table,
        string? schema,
        ProjectedPrerequisites prerequisites
    )
    {
        var proofVersion = prerequisites.NewlyCreated
            ? prerequisites.DataMutationVersion
            : prerequisites.EmptyTableProofVersion;

        return proofVersion == _providerDataMutationVersion
            && !_projectedDataMutationTables.Contains(new TableKey(table, schema));
    }

    private bool HasNullPreservingAddedColumn(
        IReadOnlyList<string> columns,
        ProjectedPrerequisites prerequisites
    ) => prerequisites.DataMutationVersion == _providerDataMutationVersion
        && columns.Any(column => prerequisites.Columns.TryGetValue(column, out var projected) && projected is
        {
            AddedToExistingTable: true,
            IsNullable: true,
            PreservesNullForExistingRows: true,
            IsComputed: false,
        });

    private bool HasAcceptedCandidateKey(
        string table,
        string? schema,
        IReadOnlyList<string> columns
    )
    {
        if (_tables.TryGetValue(new TableKey(table, schema), out var projectedTable))
        {
            var hasPrimaryKey = projectedTable.PrimaryKey is not null
                && SameColumns(projectedTable.PrimaryKey.Columns, columns, _objectIdentityNormalizer);

            if (hasPrimaryKey
                || projectedTable.UniqueConstraints.Values.Any(constraint => SameColumns(
                    constraint.Columns,
                    columns,
                    _objectIdentityNormalizer)))
            {
                return true;
            }
        }

        if (!_prerequisites.TryGetValue(new TableKey(table, schema), out var prerequisites))
        {
            return false;
        }

        return prerequisites.HasCandidateKey(table, schema, columns)
            || prerequisites.Indexes.Any((Projection: this, Columns: columns),
                static (index, state) => state.Projection.IsUnfilteredCandidateIndex(index, state.Columns));
    }

    /// <summary>Distinguishes newly established physical keys from reused live matching semantics.</summary>
    private bool HasCreatedCandidateKey(ExpectedForeignKeyDefinition foreignKey)
    {
        if (!_prerequisites.TryGetValue(new TableKey(foreignKey.PrincipalTable, foreignKey.PrincipalSchema),
                out var prerequisites))
        {
            return false;
        }

        var columns = foreignKey.PrincipalColumns;
        if (prerequisites.PrimaryKeyCreatedByProjection && prerequisites.PrimaryKey is { } primary
            && SameColumns(primary.Columns, columns, _objectIdentityNormalizer))
        {
            return true;
        }

        var state = (Projection: this, Columns: columns);

        return prerequisites.UniqueConstraints.AnyCreated(state,
                static (key, value) => SameColumns(key.Columns, value.Columns,
                    value.Projection._objectIdentityNormalizer))
            || prerequisites.Indexes.AnyCreated(state,
                static (key, value) => value.Projection.IsUnfilteredCandidateIndex(key, value.Columns));
    }

    private bool ForeignKeyColumnStorageMatches(
        ExpectedForeignKeyDefinition definition
    )
    {
        // WHY: Pair dependent and principal foreign-key members at their declared ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
        {
            if (!TryGetProjectedColumnDefinition(
                    definition.Table,
                    definition.Schema,
                    definition.Columns[ordinal],
                    out var dependent)
                || !TryGetProjectedColumnDefinition(
                    definition.PrincipalTable,
                    definition.PrincipalSchema,
                    definition.PrincipalColumns[ordinal],
                    out var principal)
                || !SafeMigrationDefinitionEquivalence.ForeignKeyColumnStorage(dependent, principal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameColumns(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        // WHY: Compare the two ordered key-member lists at the same ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < left.Count; index++)
        {
            if (!(objectIdentityNormalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal)
                .Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}
