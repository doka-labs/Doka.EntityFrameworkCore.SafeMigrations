namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        // WHY: Old orphan rows cannot survive an accepted table recreation,
        // but row mutation can invalidate the new owner's empty certificate.
        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported
            || (liveAnalysis.ObservedState == SafeMigrationObservedState.DataBlocked
                && (!_prerequisites.TryGetValue(
                        new TableKey(intent.Definition.Table, intent.Definition.Schema),
                        out var currentDependent)
                    || !currentDependent.NewlyCreated
                    || !ProvesTableEmpty(intent.Definition.Table, intent.Definition.Schema, currentDependent))))
        {
            return liveAnalysis;
        }

        if (IsProjectedTableStructureUnknown(intent.Definition.Table, intent.Definition.Schema)
            || IsProjectedTableStructureUnknown(
                intent.Definition.PrincipalTable,
                intent.Definition.PrincipalSchema))
        {
            return StructureStateUnknown();
        }

        if (!ForeignKeyProjectedColumnsAllowSetNull(intent.Definition))
        {
            return StructureStateUnknown();
        }

        if (!TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            if (_prerequisites.TryGetValue(
                    new TableKey(intent.Definition.Table, intent.Definition.Schema),
                    out var prerequisites))
            {
                if (!prerequisites.NewlyCreated
                    && liveAnalysis is
                    {
                        ObservedState: SafeMigrationObservedState.Matching,
                        MatchedObjectName: { } livePhysicalName,
                    }
                    && prerequisites.ForeignKeys.CanReuseExactLivePhysicalName(
                        intent.Definition.Name,
                        livePhysicalName))
                {
                    // WHY: Two existing physical FKs can have identical semantics.
                    // An untouched exact live identity is not an alias of the first
                    // accepted candidate; touched names retain projected provenance.
                    return liveAnalysis;
                }

                var accepted = AnalyzeAcceptedDefinition(
                    prerequisites.ForeignKeys,
                    intent.Definition.Name,
                    intent.Definition,
                    liveAnalysis);

                var canReuseRemovedProof = CanReuseRemovedForeignKeyPrerequisites(intent, liveAnalysis, prerequisites);

                if (accepted is not null)
                {
                    if (accepted.ObservedState == SafeMigrationObservedState.Matching
                        && prerequisites.ForeignKeys.TryResolvePhysicalName(
                            intent.Definition.Name,
                            intent.Definition,
                            out var physicalName))
                    {
                        accepted = MatchingForeignKey(physicalName);
                    }

                    if (accepted.IsOpaqueProjectionUnknown
                        && (canReuseRemovedProof
                            || (prerequisites.ForeignKeys.ResolveMutation(
                                    intent.Definition.Name,
                                    intent.Definition,
                                    Analysis(SafeMigrationObservedState.Missing)) == ProjectedDefinitionMutation.Unknown
                                && HasCurrentForeignKeyReplacementPrerequisites(intent, prerequisites))))
                    {
                        // WHY: Exact accepted FK proof survives only unchanged endpoints
                        // and rows. Without it, removal proves absence only; independent
                        // projected structure and current row proof must prove safety.
                        accepted = Analysis(SafeMigrationObservedState.Missing);
                    }

                    if (accepted.ObservedState == SafeMigrationObservedState.Missing
                        && !HasForeignKeyStructuralPrerequisites(intent)
                        && !CanReuseMatchingLiveForeignKeyPrerequisites(intent, liveAnalysis)
                        && !canReuseRemovedProof)
                    {
                        return StructureStateUnknown();
                    }

                    return InvalidateForeignKeyDataDependentMissing(intent, accepted);
                }

                if (canReuseRemovedProof)
                {
                    return InvalidateForeignKeyDataDependentMissing(
                        intent,
                        Analysis(SafeMigrationObservedState.Missing));
                }
            }

            var projectedAnalysis = CanProjectMissingForeignKey(intent, liveAnalysis)
                ? Analysis(SafeMigrationObservedState.Missing)
                : liveAnalysis;

            return InvalidateForeignKeyDataDependentMissing(intent, projectedAnalysis);
        }

        var analysis = AnalyzeDefinition(
            table.ForeignKeys,
            intent.Definition.Name,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.ForeignKeySemantics);

        if (analysis.ObservedState is SafeMigrationObservedState.Missing or SafeMigrationObservedState.Matching
            && _prerequisites.TryGetValue(
                new TableKey(intent.Definition.Table, intent.Definition.Schema),
                out var acceptedPrerequisites)
            && acceptedPrerequisites.ForeignKeys.TryResolvePhysicalName(
                intent.Definition.Name,
                intent.Definition,
                out var acceptedPhysicalName))
        {
            analysis = MatchingForeignKey(acceptedPhysicalName);
        }

        if (analysis.ObservedState == SafeMigrationObservedState.Missing
            && !HasForeignKeyStructuralPrerequisites(intent))
        {
            return StructureStateUnknown();
        }

        return InvalidateForeignKeyDataDependentMissing(intent, analysis);
    }

    /// <summary>Retains the actual accepted physical identity for a projected semantic FK match.</summary>
    /// <param name="physicalName">The accepted physical object's name, not the requested alias.</param>
    private static SafeMigrationProviderAnalysis MatchingForeignKey(
        string physicalName
    ) => new(SafeMigrationObservedState.Matching, SafeMigrationRepairCapability.None, true,
        StateCode(SafeMigrationObservedState.Matching))
    {
        MatchedObjectName = physicalName,
    };

    /// <summary>Checks independent accepted structure and current row proof for an exact removed FK.</summary>
    /// <param name="intent">The requested replacement foreign key.</param>
    /// <param name="dependent">The accepted prerequisite state of the dependent table.</param>
    private bool HasCurrentForeignKeyReplacementPrerequisites(
        EnsureForeignKeyIntent intent,
        ProjectedPrerequisites dependent
    ) => HasForeignKeyStructuralPrerequisites(intent)
        && (ProvesTableEmpty(intent.Definition.Table, intent.Definition.Schema, dependent)
            || HasNullPreservingAddedColumn(intent.Definition.Columns, dependent));

    /// <summary>Retains exact accepted FK endpoint and row proof only for unchanged lifetimes.</summary>
    /// <param name="intent">The requested exact semantics, optionally under another physical name.</param>
    /// <param name="liveAnalysis">The original immutable Missing result.</param>
    /// <param name="dependent">The current accepted owner containing removed physical provenance.</param>
    private bool CanReuseRemovedForeignKeyPrerequisites(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        ProjectedPrerequisites dependent
    ) => liveAnalysis.ObservedState == SafeMigrationObservedState.Missing
        && dependent.ForeignKeys.HasRemovedSemantically(intent.Definition)
        && !HasUnanalyzedDataChanges(intent.Definition.Table, intent.Definition.Schema)
        && !HasUnanalyzedDataChanges(intent.Definition.PrincipalTable, intent.Definition.PrincipalSchema)
        && !HasProjectedKeyColumnChange(intent.Definition.Table, intent.Definition.Schema, intent.Definition.Columns)
        && !HasProjectedKeyColumnChange(intent.Definition.PrincipalTable, intent.Definition.PrincipalSchema,
            intent.Definition.PrincipalColumns)
        && !_projectedCandidateKeyMutationTables.Contains(
            new TableKey(intent.Definition.PrincipalTable, intent.Definition.PrincipalSchema));

    /// <summary>Rejects SET NULL when an accepted projected dependent column is required.</summary>
    /// <param name="definition">The requested foreign-key definition.</param>
    private bool ForeignKeyProjectedColumnsAllowSetNull(
        ExpectedForeignKeyDefinition definition
    ) => definition.OnDelete != ReferentialAction.SetNull && definition.OnUpdate != ReferentialAction.SetNull
        || definition.Columns.All(column => !TryGetProjectedColumnDefinition(
                definition.Table,
                definition.Schema,
                column,
                out var dependent)
            || dependent.IsNullable);

    private SafeMigrationProviderAnalysis InvalidateForeignKeyDataDependentMissing(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis analysis
    )
    {
        var hasUnanalyzedDataChanges = HasUnanalyzedDataChanges(
                intent.Definition.Table,
                intent.Definition.Schema)
            || HasUnanalyzedDataChanges(
                intent.Definition.PrincipalTable,
                intent.Definition.PrincipalSchema);

        return hasUnanalyzedDataChanges
            && analysis.ObservedState is SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.PrerequisiteMissing
            ? DataStateUnknown()
            : analysis;
    }

    private SafeMigrationProviderAnalysis Project(
        DropForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
        ? StructureStateUnknown()
        : TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                table.ForeignKeys.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Missing)
            : _prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites)
                && prerequisites.ForeignKeys.TryGetPhysicalState(intent.Name, out var present)
                    ? Analysis(present ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing)
                    : liveAnalysis;

    private void Observe(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Definition.Table, intent.Definition.Schema);

            ObserveAcceptedDefinition(
                prerequisites.ForeignKeys,
                intent.Definition.Name,
                intent.Definition,
                liveAnalysis,
                decision);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.ForeignKeys[intent.Definition.Name] = intent.Definition;
        }
    }

    /// <summary>Preserves an independent native member before selecting another accepted semantic candidate.</summary>
    /// <param name="intent">The accepted semantic contract.</param>
    /// <param name="liveAnalysis">The immutable actual member identity.</param>
    /// <param name="analysis">The current accepted semantic candidate, when independently resolved.</param>
    private SafeMigrationProviderAnalysis SelectAcceptedForeignKeyIdentity(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (liveAnalysis is { ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: { } physicalName }
            && CanReuseMatchingLiveForeignKeyPrerequisites(intent, liveAnalysis)
            && (!_prerequisites.TryGetValue(new TableKey(intent.Definition.Table, intent.Definition.Schema),
                    out var prerequisites)
                || !prerequisites.ForeignKeys.TryGetPhysicalState(physicalName, out var present)
                || (present && prerequisites.ForeignKeys.TryGetValue(physicalName, out var definition)
                    && prerequisites.ForeignKeys.SemanticallyEquals(definition, intent.Definition))))
        {
            // WHY: Two physical FKs may have identical semantics. Replacing an
            // untouched native B with projected candidate A collapses their
            // lifetimes and makes dropping A falsely remove B as well.
            return liveAnalysis;
        }

        return analysis is { ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: not null }
            ? analysis
            : liveAnalysis;
    }

    private void Observe(
        DropForeignKeyIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.ForeignKeys.MarkPhysicalMissing(intent.Name, CurrentOperationOrdinal);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.ForeignKeys.Remove(intent.Name);
        }
    }
}
