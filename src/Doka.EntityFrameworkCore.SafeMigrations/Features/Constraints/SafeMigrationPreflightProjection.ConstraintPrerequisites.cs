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
                physicalName);
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
    ) => TryGetConstraintPrerequisites(
                intent.Definition.Table,
                intent.Definition.Schema,
                intent.Definition.Columns,
                out _)
            && TryGetConstraintPrerequisites(
                intent.Definition.PrincipalTable,
                intent.Definition.PrincipalSchema,
                intent.Definition.PrincipalColumns,
                out _)
            && HasAcceptedCandidateKey(
                intent.Definition.PrincipalTable,
                intent.Definition.PrincipalSchema,
                intent.Definition.PrincipalColumns)
            && ForeignKeyColumnStorageMatches(intent.Definition);

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
            || !ContainsAllColumns(prerequisites, columns))
        {
            prerequisites = null;
            return false;
        }

        return columns.All(column => TryGetProjectedColumnDefinition(table, schema, column, out _));
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
        && columns.Any(column => prerequisites.Columns[column] is
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

        return prerequisites.HasCandidateKey(table, schema, columns);
    }

    private bool ForeignKeyColumnStorageMatches(
        ExpectedForeignKeyDefinition definition
    )
    {
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
