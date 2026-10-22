namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        var key = new TableKey(intent.Table, intent.Schema);
        if (HasColumnPreservingHistory(key)
            && liveAnalysis.ObservedState is SafeMigrationObservedState.DataBlocked
                or SafeMigrationObservedState.Unsupported)
        {
            return liveAnalysis;
        }

        if (_projectedColumnAnalyzer is { } provider
            && CanReuseColumnProofAfterColumnPreservingChanges(intent, liveAnalysis, key))
        {
            // WHY: Ensure has no authored old definition. Complete native
            // nullable-only evidence after key changes, or an accepted exact
            // source after FK-only drops, can preserve layout without widening.
            return liveAnalysis is { ObservedState: SafeMigrationObservedState.Different, RepairMutatesData: false }
                && ((_projectedIntrinsicColumnSourceTables?.Contains(key) == true
                        && _projectedForeignKeyDropOnlyTables?.Contains(key) != true
                        && provider.CanReuseLiveColumnProofAfterKeyChanges(intent, liveAnalysis))
                    || (TryGetAcceptedColumnSource(intent, out var source)
                        && provider.PreservesSiblingColumnProofsAfterForeignKeyDrops(intent, source, liveAnalysis)))
                    ? liveAnalysis.WithRepairPreservesIntrinsicColumnLayout()
                    : liveAnalysis;
        }

        if (TryGetProjectedColumnDefinition(
                intent.Table,
                intent.Schema,
                intent.Definition.Name,
                out var projectedDefinition))
        {
            // WHY: Provider repair evidence describes the historical live
            // definition. Once an earlier operation changes the projected
            // definition, only exact target equality remains proven.
            return Analysis(
                AreProjectedColumnTargetsEquivalent(projectedDefinition, intent.Definition)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Different);
        }

        if (IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Definition.Name))
        {
            return ValidateProjectedMissingColumnAdd(
                intent,
                liveAnalysis,
                Analysis(SafeMigrationObservedState.Missing));
        }

        if (IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.Definition.Name))
        {
            return StructureStateUnknown();
        }

        if (IsProjectedTableStructureUnknown(intent.Table, intent.Schema))
        {
            // WHY: A drop or provider-certified rename can change dependent
            // definitions. Only absence of an unrelated, safely additive
            // column survives that structural uncertainty.
            var unknownOwnerAnalysis = liveAnalysis.ObservedState == SafeMigrationObservedState.Missing
                && SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition)
                    ? liveAnalysis
                    : StructureStateUnknown();

            return ValidateProjectedMissingColumnAdd(intent, liveAnalysis, unknownOwnerAnalysis);
        }

        if (!TryGet(intent.Table, intent.Schema, out var table))
        {
            if (_projectedIntrinsicColumnSourceTables?.Contains(key) == true
                && _projectedForeignKeyDropOnlyTables?.Contains(key) != true
                && (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                    || liveAnalysis is
                    {
                        ObservedState: SafeMigrationObservedState.Different,
                        RepairCapability: SafeMigrationRepairCapability.Safe
                    }))
            {
                // WHY: A declined key-change certificate cannot fall through to
                // generic Ensure repair. Its earlier physical width/key proof
                // may be stale even though adding the nullable target is safe.
                return StructureStateUnknown();
            }

            var unprojectedAnalysis = SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition)
                ? liveAnalysis
                : InvalidateDataDependentMissing(intent.Table, intent.Schema, liveAnalysis);

            return ValidateProjectedMissingColumnAdd(
                intent,
                liveAnalysis,
                InvalidateStaleLiveDataProof(intent.Table, intent.Schema, unprojectedAnalysis));
        }

        var analysis = !table.Columns.TryGetValue(intent.Definition.Name, out var actual)
            ? Analysis(SafeMigrationObservedState.Missing)
            : Analysis(
                AreProjectedColumnTargetsEquivalent(actual, intent.Definition)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Different);

        analysis = SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition)
            ? analysis
            : InvalidateDataDependentMissing(table.Table, table.Schema, analysis);

        return ValidateProjectedMissingColumnAdd(
            intent,
            liveAnalysis,
            InvalidateStaleLiveDataProof(table.Table, table.Schema, analysis));
    }

    /// <summary>Uses provider member identity only for names after an accepted column lookup succeeds.</summary>
    private bool AreProjectedColumnTargetsEquivalent(
        ExpectedColumnDefinition accepted,
        ExpectedColumnDefinition target
    )
    {
        var fold = _objectIdentityNormalizer?.FoldUnquotedCapturedSqlIdentifiers == true;
        accepted = SafeMigrationCapturedExpressionNormalization.NormalizeColumn(accepted, fold);
        target = SafeMigrationCapturedExpressionNormalization.NormalizeColumn(target, fold);

        return SafeMigrationDefinitionEquivalence.Column(
                accepted,
                target,
                _objectIdentityNormalizer?.ColumnIdentifierComparer)
            || (_objectIdentityNormalizer?.CapturedSqlIdentifierDelimiter is { } delimiter
                && SafeMigrationDefinitionEquivalence.ColumnWithNativeColumnQuoteEquivalence(
                    accepted,
                    target,
                    _objectIdentityNormalizer.ColumnIdentifierComparer,
                    delimiter,
                    fold));
    }

    /// <summary>Separates Missing-add qualification from target equality and unknown owner state.</summary>
    private SafeMigrationProviderAnalysis ValidateProjectedMissingColumnAdd(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        if (projectedAnalysis.ObservedState != SafeMigrationObservedState.Missing
            || _projectedColumnAnalyzer is null)
        {
            return projectedAnalysis;
        }

        var key = new TableKey(intent.Table, intent.Schema);
        var tableWasCreated = false;
        var provesEmpty = false;
        if (_prerequisites.TryGetValue(key, out var prerequisites))
        {
            tableWasCreated = prerequisites.NewlyCreated;
            provesEmpty = ProvesTableEmpty(intent.Table, intent.Schema, prerequisites);
        }

        return _projectedColumnAnalyzer.ValidateProjectedMissingColumnAdd(
            intent,
            new SafeMigrationProjectedMissingColumnContext(
                TableWasCreated: tableWasCreated,
                ProvesEmpty: provesEmpty,
                HasDataMutation: HasUnanalyzedDataChanges(intent.Table, intent.Schema),
                ColumnWasDropped: IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Definition.Name)),
            liveAnalysis,
            projectedAnalysis);
    }

    private SafeMigrationProviderAnalysis Project(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        var key = new TableKey(intent.Table, intent.Schema);
        if (HasColumnPreservingHistory(key)
            && liveAnalysis.ObservedState is SafeMigrationObservedState.DataBlocked
                or SafeMigrationObservedState.Unsupported)
        {
            // WHY: Removing a dependency cannot discharge an independent
            // provider rejection, even when the authored target has an exact shape.
            return liveAnalysis;
        }

        if (CanReuseColumnProofAfterColumnPreservingChanges(intent, liveAnalysis, key))
        {
            return liveAnalysis;
        }

        if (TryGetProjectedColumnDefinition(
                intent.Table,
                intent.Schema,
                intent.Definition.Name,
                out var projectedDefinition))
        {
            return AnalyzeAlterColumn(projectedDefinition, intent, liveAnalysis);
        }

        if (IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Definition.Name))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.Definition.Name))
        {
            return StructureStateUnknown();
        }

        if (!TryGet(intent.Table, intent.Schema, out var table))
        {
            if (liveAnalysis.CanReuseAfterUnrelatedColumnDrops
                && liveAnalysis is
                {
                    ObservedState: SafeMigrationObservedState.Different,
                    RepairCapability: SafeMigrationRepairCapability.Safe
                }
                && intent.OldDefinition is not null
                && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition)
                && _projectedColumnDropOnlyTables?.Contains(key) == true
                && (!_prerequisites.TryGetValue(key, out var sourcePrerequisites) || !sourcePrerequisites.NewlyCreated)
                && !_projectedUnknownPhysicalKeys.Contains(key)
                && !HasProjectedAlterForeignKeyDependency(intent))
            {
                return HasUnanalyzedDataChanges(intent.Table, intent.Schema) ? DataStateUnknown() : liveAnalysis;
            }

            if (_projectedColumnAnalyzer is not null
                && intent.OldDefinition is not null
                && liveAnalysis is
                {
                    RepairCapability: SafeMigrationRepairCapability.Safe,
                    ObservedState: SafeMigrationObservedState.Different
                })
            {
                return AnalyzeAlterColumn(intent.OldDefinition, intent, liveAnalysis);
            }

            if (_projectedStructurallyModifiedTables.Contains(new TableKey(intent.Table, intent.Schema))
                || IsProjectedTableStructureUnknown(intent.Table, intent.Schema))
            {
                return StructureStateUnknown();
            }

            return HasUnanalyzedDataChanges(intent.Table, intent.Schema)
                && liveAnalysis.ObservedState == SafeMigrationObservedState.Different
                && intent.OldDefinition?.IsNullable == true
                && !intent.Definition.IsNullable
                    ? DataStateUnknown()
                    : InvalidateStaleLiveDataProof(intent.Table, intent.Schema, liveAnalysis);
        }

        return table.Columns.TryGetValue(intent.Definition.Name, out var actual)
            ? AnalyzeAlterColumn(actual, intent, liveAnalysis)
            : Analysis(SafeMigrationObservedState.Missing);
    }

    /// <summary>Checks the lifetime before asking the provider to reuse one exact column certificate.</summary>
    private bool CanReuseColumnProofAfterColumnPreservingChanges(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        TableKey key,
        bool allowUntouchedSource = false
    ) => _projectedColumnAnalyzer is { } provider
        && HasCurrentColumnProofAfterColumnPreservingChanges(
            key, intent.Definition.Name, liveAnalysis, allowUntouchedSource)
        && (_projectedForeignKeyDropOnlyTables?.Contains(key) == true
            ? provider.CanReuseLiveColumnProofAfterForeignKeyDrops(intent, liveAnalysis)
            : provider.CanReuseLiveColumnProofAfterKeyChanges(intent, liveAnalysis))
        && (!TryGetProjectedColumnDefinition(intent.Table, intent.Schema, intent.Definition.Name, out var actual)
            || provider.AreProjectedColumnSourcesEquivalent(
                actual,
                liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                    ? intent.Definition
                    : intent.OldDefinition ?? intent.Definition));

    /// <summary>Consumes captured Ensure evidence without inventing an authored source definition.</summary>
    private bool CanReuseColumnProofAfterColumnPreservingChanges(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        TableKey key,
        bool allowUntouchedSource = false
    ) => _projectedColumnAnalyzer is { } provider
        && HasCurrentColumnProofAfterColumnPreservingChanges(
            key, intent.Definition.Name, liveAnalysis, allowUntouchedSource)
        && (_projectedForeignKeyDropOnlyTables?.Contains(key) == true
            ? provider.CanReuseLiveColumnProofAfterForeignKeyDrops(intent, liveAnalysis)
            : provider.CanReuseLiveColumnProofAfterKeyChanges(intent, liveAnalysis));

    /// <summary>Separates intrinsic column provenance from complete table and candidate-key evidence.</summary>
    private bool HasColumnPreservingHistory(
        TableKey key
    ) => _projectedForeignKeyDropOnlyTables?.Contains(key) == true
        || _projectedIntrinsicColumnSourceTables?.Contains(key) == true;

    /// <summary>Checks an untouched source or retained column-proof lifetime without changing owner identity.</summary>
    /// <remarks>
    /// An interrupted stream can begin with no-op removals. Its first independently certified repair
    /// may establish intrinsic lineage, but a later certificate must not revive an invalidated history.
    /// </remarks>
    private bool HasCurrentColumnProofAfterColumnPreservingChanges(
        TableKey key,
        string column,
        SafeMigrationProviderAnalysis liveAnalysis,
        bool allowUntouchedSource
    ) => (HasColumnPreservingHistory(key)
            || (allowUntouchedSource && !_projectedStructurallyModifiedTables.Contains(key)))
        && (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
            || liveAnalysis is
            {
                ObservedState: SafeMigrationObservedState.Different,
                RepairCapability: SafeMigrationRepairCapability.Safe
            })
        && (!_prerequisites.TryGetValue(key, out var prerequisites) || !prerequisites.NewlyCreated)
        && (!IsProjectedTableStructureUnknown(key.Table, key.Schema)
            || _certifiedColumnDropOrigins?.ContainsKey(key) == true)
        && (!_projectedUnknownPhysicalKeys.Contains(key)
            || _certifiedColumnDropOrigins?.ContainsKey(key) == true)
        && _providerDataMutationVersion == 0
        && !HasUnanalyzedDataChanges(key.Table, key.Schema)
        && !IsProjectedColumnMissing(key.Table, key.Schema, column)
        && !IsProjectedColumnUnknown(key.Table, key.Schema, column)
        && (!_projectedChangedColumns.TryGetValue(key, out var changedColumns)
            || (!changedColumns.Contains(column) && (!liveAnalysis.RepairMutatesData || changedColumns.Count == 0)))
        && !HasProjectedColumnForeignKeyDependency(key, column);

    /// <summary>Borrows a known accepted source only after the current proof's lifetime has been checked.</summary>
    private bool TryGetAcceptedColumnSource(
        EnsureColumnIntent intent,
        [NotNullWhen(true)] out ExpectedColumnDefinition? source
    ) => TryGetProjectedColumnDefinition(intent.Table, intent.Schema, intent.Definition.Name, out source)
        || (TryGet(intent.Table, intent.Schema, out var table)
            && table.Columns.TryGetValue(intent.Definition.Name, out source));

    private SafeMigrationProviderAnalysis Project(
        DropColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (TryGetProjectedColumnDefinition(intent.Table, intent.Schema, intent.Name, out _))
        {
            return Analysis(SafeMigrationObservedState.Matching);
        }

        if (IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Name))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.Name))
        {
            return StructureStateUnknown();
        }

        return TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                table.Columns.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Missing)
            : liveAnalysis;
    }

    private SafeMigrationProviderAnalysis Project(
        RenameColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Name))
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.Name)
            || IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.NewName))
        {
            return StructureStateUnknown();
        }

        if (TryGet(intent.Table, intent.Schema, out var table))
        {
            return Analysis(
                !table.Columns.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Missing
                    : table.Columns.ContainsKey(intent.NewName)
                        ? SafeMigrationObservedState.Different
                        : SafeMigrationObservedState.Matching);
        }

        var sourceExists = TryGetProjectedColumnDefinition(intent.Table, intent.Schema, intent.Name, out _)
            ? true
            : (bool?)null;
        var targetExists = TryGetProjectedColumnDefinition(intent.Table, intent.Schema, intent.NewName, out _)
            ? true
            : IsProjectedColumnMissing(intent.Table, intent.Schema, intent.NewName)
                ? false
                : (bool?)null;

        return AnalyzeSparseMemberRename(sourceExists, targetExists, liveAnalysis);
    }

    /// <summary>Combines exact member lifetimes with independently captured rename destination evidence.</summary>
    /// <param name="sourceExists">Accepted physical source presence or absence, when known.</param>
    /// <param name="targetExists">Accepted physical destination presence or absence, when known.</param>
    /// <param name="liveAnalysis">The immutable pre-batch rename result and any destination certificate.</param>
    private static SafeMigrationProviderAnalysis AnalyzeSparseMemberRename(
        bool? sourceExists,
        bool? targetExists,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (sourceExists == false)
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (sourceExists is null
            && liveAnalysis.ObservedState == SafeMigrationObservedState.Missing)
        {
            return liveAnalysis;
        }

        if (liveAnalysis.ObservedState is not (SafeMigrationObservedState.Missing
            or SafeMigrationObservedState.Matching
            or SafeMigrationObservedState.Different))
        {
            return liveAnalysis;
        }

        if (targetExists is { } occupied)
        {
            return Analysis(occupied ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching);
        }

        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Different
            || liveAnalysis.RenameTargetExists == true)
        {
            return Analysis(SafeMigrationObservedState.Different);
        }

        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
            || liveAnalysis.RenameTargetExists == false)
        {
            return Analysis(SafeMigrationObservedState.Matching);
        }

        // WHY: A sparse source certificate proves existence, not that an
        // unobserved destination is free. Immutable Missing is also the replay
        // state, so it cannot authorize a rename after this stream adds a source.

        return sourceExists == true ? StructureStateUnknown() : liveAnalysis;
    }

    private void Observe(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision,
        bool preservesForeignKeyColumnProofs
    )
    {
        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.Columns[intent.Definition.Name] = ProjectedColumn.From(
                intent.Definition,
                addedToExistingTable: decision.Action == SafeMigrationAction.Apply && !prerequisites.NewlyCreated);

            if (decision.Action == SafeMigrationAction.Apply
                && liveAnalysis is
                {
                    ObservedState: SafeMigrationObservedState.Missing, RequiredColumnAddResumesOwnedStaging: false
                }
                && !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition))
            {
                // WHY: A fresh unsafe add requires an empty table. Completion
                // of an owned interrupted add is also applicable, but it can
                // already contain explicit writer values and proves no emptiness.
                prerequisites.EmptyTableProofVersion = _providerDataMutationVersion;
            }
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            ObserveAcceptedColumnDefinition(
                intent.Table,
                intent.Schema,
                intent.Definition,
                analysis,
                decision,
                preservesForeignKeyColumnProofs);
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.AddColumn(intent.Definition);
        }

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair)
        {
            if (!analysis.RepairPreservesPhysicalKeys
                && !preservesForeignKeyColumnProofs)
            {
                InvalidateAcceptedIndexesForColumn(intent.Table, intent.Schema, intent.Definition.Name);
            }

            MarkProjectedColumnChanged(intent.Table, intent.Schema, intent.Definition.Name);
        }
    }

    private void Observe(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision,
        bool preservesForeignKeyColumnProofs
    )
    {
        if (decision.Action is SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.Columns[intent.Definition.Name] = ProjectedColumn.From(
                intent.Definition,
                addedToExistingTable: false);
        }

        if (decision.Action is SafeMigrationAction.NoOp or SafeMigrationAction.Repair)
        {
            ObserveAcceptedColumnDefinition(
                intent.Table,
                intent.Schema,
                intent.Definition,
                analysis,
                decision,
                preservesForeignKeyColumnProofs);
        }

        if (decision.Action == SafeMigrationAction.Repair
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.Columns[intent.Definition.Name] = intent.Definition;
        }

        if (decision.Action == SafeMigrationAction.Repair)
        {
            if (!analysis.RepairPreservesPhysicalKeys
                && !preservesForeignKeyColumnProofs)
            {
                InvalidateAcceptedIndexesForColumn(intent.Table, intent.Schema, intent.Definition.Name);
            }

            MarkProjectedColumnChanged(intent.Table, intent.Schema, intent.Definition.Name);
        }
    }

    private void Observe(
        DropColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            ObserveIndependentColumnDrop(intent);
            var key = new TableKey(intent.Table, intent.Schema);
            _ = _prerequisites.TryGetValue(key, out var nativeOwner);
            var nativeEmptyGeneratedSet =
                liveAnalysis is
                {
                    DropLeavesNoGeneratedColumns: true, ObservedState: SafeMigrationObservedState.Matching
                }
                && !_hasOpaqueSqlPostcondition
                && !_hasOpaqueProviderPostcondition
                && (!_projectedStructurallyModifiedTables.Contains(key)
                    || nativeOwner?.NativeGeneratedSnapshotPreservedByIndexOrConstraintRemoval == true)
                && nativeOwner?.NewlyCreated != true
                && !IsProjectedTableStructureUnknown(intent.Table, intent.Schema);

            // WHY: The provider may remove local indexes or constraints together
            // with the column. Their exact post-state must remain unknown.
            InvalidateModelManagedDataProjection();
            SetProjectedTableStructureUnknown(intent.Table, intent.Schema, preservesGeneratedColumns: true);
            RemoveDroppedPhysicalKeys(intent.Table, intent.Schema);
            SetProjectedColumnMissing(intent.Table, intent.Schema, intent.Name);
            if (nativeEmptyGeneratedSet)
            {
                // Never remove accepted definitions to fit the native snapshot:
                // a newer known generated member must still prove dependency.
                GetOrCreateProviderPrerequisites(intent.Table, intent.Schema)
                    .GeneratedColumnsComplete = true;
            }
        }

        if (decision.Action == SafeMigrationAction.Apply
            && _prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites))
        {
            prerequisites.Columns.Remove(intent.Name);
        }
    }

    private void Observe(
        RenameColumnIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action != SafeMigrationAction.Apply)
        {
            return;
        }

        var key = new TableKey(intent.Table, intent.Schema);
        if (_prerequisites.TryGetValue(key, out var prerequisites)
            && prerequisites.NewlyCreated
            && _tables.TryGetValue(key, out var table))
        {
            InvalidateModelManagedDataProjection();
            RenameProjectedColumnDefinition(intent.Table, intent.Schema, intent.Name, intent.NewName);

            if (prerequisites.Columns.Remove(intent.Name, out var prerequisite))
            {
                prerequisites.Columns[intent.NewName] = prerequisite;
            }

            table.RenameColumn(intent.Name, intent.NewName);

            // WHY: Accepted column facts are consulted before the full table.
            // They must share its transformed dependent expressions, including
            // opaque fragments whose original contract is no longer proven.
            foreach (var column in table.Columns.Values)
            {
                if (column.ComputedColumnSql is not null
                    || column.ComputedExpression is not null)
                {
                    SetProjectedColumnDefinition(intent.Table, intent.Schema, column);
                }
            }

            foreach (var projection in _tables.Values)
            {
                projection.RenamePrincipalColumn(table.Table, table.Schema, intent.Name, intent.NewName);
            }

            return;
        }

        // WHY: A rename on an existing table can rewrite provider-owned indexes,
        // expressions, and foreign keys outside the locally projected table.
        // Later safe operations must not trust the catalog snapshot captured
        // before the ordered migration started.
        if (_providerOperationProjection?.PreservesUnrelatedColumnAbsence(intent) == true)
        {
            var knownColumns = CaptureAcceptedColumnRename(intent);

            InvalidateModelManagedDataProjection();
            SetProjectedTableStructureUnknown(
                intent.Table,
                intent.Schema,
                preservesGeneratedColumns: knownColumns is not null);

            RemoveProjectedColumnDefinitions(intent.Table, intent.Schema);
            RemoveDroppedPhysicalKeys(intent.Table, intent.Schema);
            if (knownColumns is not null)
            {
                // WHY: A matching strict declaration can prove these members
                // without proving table creation, emptiness or unknown external
                // dependencies. Retain only their certified renamed contracts.
                foreach (var column in knownColumns.Columns)
                {
                    SetProjectedColumnDefinition(intent.Table, intent.Schema, column);
                }

                SetProjectedColumnMissing(intent.Table, intent.Schema, intent.Name);
                if (_prerequisites.TryGetValue(key, out var knownPrerequisites))
                {
                    if (knownPrerequisites.Columns.Remove(intent.Name, out var prerequisite))
                    {
                        knownPrerequisites.Columns[intent.NewName] = prerequisite;
                    }

                    var renamedChecks = knownColumns.CheckConstraints.ToDictionary(
                        static value => value.Name,
                        StringComparer.Ordinal);

                    knownPrerequisites.CheckConstraints.Rebind(value =>
                        renamedChecks.TryGetValue(value.Name, out var renamed) ? renamed : value);
                    knownPrerequisites.CheckDefinitionsFollowKnownRename = true;
                }
            }
            else
            {
                RenameProjectedColumnDefinition(intent.Table, intent.Schema, intent.Name, intent.NewName);
            }

            return;
        }

        SetOpaqueProviderPostcondition(mayMutateData: false);
    }

    /// <summary>Transforms only explicitly accepted member facts, never a target model or live-table guess.</summary>
    private ExpectedTableDefinition? CaptureAcceptedColumnRename(
        RenameColumnIntent intent
    )
    {
        var key = new TableKey(intent.Table, intent.Schema);
        if (IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
            && (!_prerequisites.TryGetValue(key, out var knownPrerequisites)
                || !knownPrerequisites.CheckDefinitionsFollowKnownRename))
        {
            // WHY: A later rename does not repair earlier unknown dependency
            // effects. Only an uninterrupted certified rename lineage can
            // carry these accepted source contracts through another rename.
            return null;
        }

        if (!_projectedColumnStates.TryGetValue(key, out var states)
            || !states.TryGetValue(intent.Name, out var source)
            || source.Definition is null)
        {
            return null;
        }

        var columns = states
            .Values
            .Where(static state => state.Definition is not null)
            .Select(static state => state.Definition!)
            .ToArray();

        var checks = _prerequisites.TryGetValue(key, out var prerequisites)
            ? prerequisites.CheckConstraints.Definitions.ToArray()
            : [];

        var acceptedMembers = new ExpectedTableDefinition(
            intent.Table,
            columns,
            intent.Schema,
            checkConstraints: checks);

        return TryProjectKnownColumnRename(acceptedMembers, intent, _objectIdentityNormalizer, out var renamed)
            ? renamed
            : null;
    }

    private SafeMigrationProviderAnalysis AnalyzeAlterColumn(
        ExpectedColumnDefinition actual,
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        ArgumentNullException.ThrowIfNull(actual);

        if (AreProjectedColumnTargetsEquivalent(actual, intent.Definition))
        {
            return Analysis(SafeMigrationObservedState.Matching);
        }

        if (intent.OldDefinition is null
            || !AreProjectedColumnTargetsEquivalent(actual, intent.OldDefinition))
        {
            return Analysis(SafeMigrationObservedState.Different);
        }

        var key = new TableKey(intent.Table, intent.Schema);
        if (IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
            || _projectedUnknownPhysicalKeys.Contains(key))
        {
            return StructureStateUnknown();
        }

        var provesEmpty = _prerequisites.TryGetValue(key, out var prerequisites)
            && ProvesTableEmpty(intent.Table, intent.Schema, prerequisites);
        var dataChanged = HasUnanalyzedDataChanges(intent.Table, intent.Schema);
        var tightensNullability = actual.IsNullable && !intent.Definition.IsNullable;

        // WHY: An exact old shape proves no facts about existing rows. A
        // projected definition must never erase a provider's blocking NULL or
        // length evidence, nor reuse that evidence after accepted DML.
        if (!provesEmpty
            && (liveAnalysis.RequiresLiveDataProof || tightensNullability))
        {
            if (dataChanged)
            {
                return DataStateUnknown();
            }

            if (liveAnalysis.ObservedState == SafeMigrationObservedState.DataBlocked)
            {
                return liveAnalysis;
            }
        }

        var repair =
            SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(
                actual,
                intent.Definition,
                _objectIdentityNormalizer?.ColumnIdentifierComparer)
                ? SafeMigrationRepairCapability.Safe
                : SafeMigrationRepairCapability.None;

        if (tightensNullability
            && !provesEmpty
            && liveAnalysis.RepairCapability != SafeMigrationRepairCapability.Safe)
        {
            repair = SafeMigrationRepairCapability.None;
        }

        var analysis = Analysis(SafeMigrationObservedState.Different, repair);
        // WHY: Matching authored metadata on a recreated owner is new structural
        // evidence, not the original owner's captured physical or row certificate.
        var canReuseLiveProof = prerequisites?.NewlyCreated != true
            && !_projectedStructurallyModifiedTables.Contains(key)
            && !IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
            && (!liveAnalysis.RequiresLiveDataProof || !dataChanged);

        _tables.TryGetValue(key, out var table);

        if (canReuseLiveProof
            && liveAnalysis is
            {
                ObservedState: SafeMigrationObservedState.Different,
                RepairCapability: SafeMigrationRepairCapability.Safe
            })
        {
            analysis = liveAnalysis;
        }

        return _projectedColumnAnalyzer?.ValidateProjectedAlterColumn(
                intent,
                actual,
                new SafeMigrationProjectedAlterColumnContext(
                    (ISafeMigrationProjectedAlterTable?)table ?? new ProjectedAlterTableView(this, key),
                    HasCompleteTable: table is not null,
                    ProvesEmpty: provesEmpty,
                    HasForeignKeyDependency: HasProjectedAlterForeignKeyDependency(intent),
                    CanReuseLiveProof: canReuseLiveProof,
                    HasDataMutation: dataChanged,
                    PreservesLiveValueDomain: !dataChanged
                    && TryGetProjectedColumnState(intent.Table, intent.Schema, actual.Name, out var state)
                    && state.PreservesLiveValueDomain,
                    CanReuseCreationCharacterSet: table?.CanReuseCreationCharacterSet == true),
                liveAnalysis,
                analysis)
            ?? analysis;
    }

    private void ObserveAcceptedColumnDefinition(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision,
        bool preservesOriginalValues = false
    )
    {
        var hasPrevious = TryGetProjectedColumnState(table, schema, definition.Name, out var previous);
        var hadUnchangedSource = !_projectedChangedColumns.TryGetValue(new TableKey(table, schema), out var changes)
            || !changes.Contains(definition.Name);

        // WHY: Owned staging completion changes nullability without adding or
        // backfilling values; only its exact native certificate preserves them.
        var preservesDomain = decision.Action switch
        {
            SafeMigrationAction.NoOp => hasPrevious && previous.PreservesLiveValueDomain,
            SafeMigrationAction.Apply =>
                analysis is { RequiredColumnAddResumesOwnedStaging: true, RepairMutatesData: false }
                && (hadUnchangedSource || (hasPrevious && previous.PreservesLiveValueDomain)),
            SafeMigrationAction.Repair => !analysis.RepairMutatesData
                && (analysis.RepairPreservesLiveValueDomain || preservesOriginalValues)
                && (!hasPrevious
                    || previous.PreservesLiveValueDomain
                    || (preservesOriginalValues && hadUnchangedSource)),
            _ => false,
        };

        // WHY: A later certificate cannot erase an earlier uncertified conversion.
        // Row epochs are checked separately because triggers can mutate another table.
        var mutationOrigin = decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair
            && !preservesDomain
                ? CurrentOperationOrdinal
                : hasPrevious
                    ? previous.ValueMutationOriginOrdinal
                    : -1;

        SetProjectedColumnDefinition(table, schema, definition, preservesDomain, mutationOrigin);
    }

    /// <summary>Reads accepted facts for an incomplete table without inventing missing live definitions.</summary>
    private sealed class ProjectedAlterTableView(
        SafeMigrationPreflightProjection projection,
        TableKey key
    ) : ISafeMigrationProjectedAlterTable
    {
        /// <inheritdoc />
        public IEnumerable<ExpectedColumnDefinition> Columns =>
            projection._projectedColumnStates.TryGetValue(key, out var columns)
                ? columns
                    .Values
                    .Where(static value => value.Definition is not null)
                    .Select(static value => value.Definition!)
                : [];

        /// <inheritdoc />
        public ExpectedPrimaryKeyDefinition? PrimaryKey =>
            projection._prerequisites.TryGetValue(key, out var prerequisites) ? prerequisites.PrimaryKey : null;

        /// <inheritdoc />
        public IEnumerable<ExpectedUniqueConstraintDefinition> UniqueConstraints =>
            projection._prerequisites.TryGetValue(key, out var prerequisites)
                ? prerequisites.UniqueConstraints.Definitions
                : [];

        /// <inheritdoc />
        public IEnumerable<ExpectedIndexDefinition> Indexes =>
            projection._prerequisites.TryGetValue(key, out var prerequisites) ? prerequisites.Indexes.Definitions : [];

        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        ) => projection.TryGetProjectedColumnDefinition(key.Table, key.Schema, column, out definition);
    }

    private sealed partial class ProjectedTable
    {
        public void AddColumn(
            ExpectedColumnDefinition definition
        )
        {
            if (!Columns.ContainsKey(definition.Name))
            {
                _columnOrder.Add(definition.Name);
            }

            Columns[definition.Name] = definition;
        }

        public void RemoveColumn(
            string name
        )
        {
            if (Columns.Remove(name))
            {
                var ordinal = FindColumnOrder(name);
                if (ordinal >= 0)
                {
                    _columnOrder.RemoveAt(ordinal);
                }
            }
        }

        public void RenameColumn(
            string source,
            string target
        )
        {
            if (!Columns.Remove(source, out var column))
            {
                return;
            }

            Columns[target] = Copy(column, name: target);
            var ordinal = FindColumnOrder(source);
            if (ordinal >= 0)
            {
                _columnOrder[ordinal] = target;
            }

            ReplaceValues(Columns, value => RenameComputedDefinition(value, source, target));

            PrimaryKey = PrimaryKey is null
                ? null
                : Copy(PrimaryKey, columns: Rename(PrimaryKey.Columns, source, target));

            ReplaceValues(UniqueConstraints, value => Copy(value, columns: Rename(value.Columns, source, target)));
            ReplaceValues(CheckConstraints, value => RenameCheckDefinition(value, source, target));
            ReplaceValues(
                ForeignKeys,
                value => Copy(
                    value,
                    columns: Rename(value.Columns, source, target),
                    principalColumns: SameIdentity(value.PrincipalTable, value.PrincipalSchema, _table, _schema)
                        ? Rename(value.PrincipalColumns, source, target)
                        : value.PrincipalColumns));

            ReplaceValues(
                Indexes,
                value => Copy(
                    value,
                    keys: value.Keys.Select(key => Copy(key, source, target)),
                    includedColumns: Rename(value.IncludedColumns, source, target),
                    structuredFilter: value.Filter is not null
                        ? SafeMigrationSql.OpaqueAfterRename(value.Filter)
                        : value.StructuredFilter is null
                            ? null
                            : RenameColumnExpression(value.StructuredFilter, source, target),
                    replaceFilter: value.Filter is not null || value.StructuredFilter is not null));
        }

        /// <summary>Renames parsed identifier roles while preserving native literals and conversion spelling.</summary>
        private ExpectedColumnDefinition RenameComputedDefinition(
            ExpectedColumnDefinition value,
            string source,
            string target
        )
        {
            if (value.CapturedComputedSql is not null)
            {
                if (!SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
                        value.ComputedExpression!,
                        source,
                        Columns.Comparer))
                {
                    // WHY: Unrelated renames retain the existing immutable
                    // contract without reparsing or allocating another tree.
                    return value;
                }

                // WHY: Re-rendering the renamed AST can change the native
                // numeric domain. Token-role rewriting keeps every other
                // source byte; an uncertifiable rewrite loses proof instead.
                return SafeMigrationSqlExpressionParser.TryRenameColumnIdentifier(
                    value.CapturedComputedSql,
                    source,
                    target,
                    Columns.Comparer,
                    out var renamedSql,
                    out var renamedExpression,
                    _objectIdentityNormalizer?.CapturedSqlIdentifierDelimiter,
                    _objectIdentityNormalizer?.FoldUnquotedCapturedSqlIdentifiers == true)
                    ? Copy(
                        value,
                        computedExpression: renamedExpression,
                        capturedComputedSql: renamedSql,
                        replaceComputed: true)
                    : Copy(
                        value,
                        computedExpression: SafeMigrationSql.OpaqueAfterRename(value.CapturedComputedSql),
                        replaceComputed: true);
            }

            return value.ComputedColumnSql is not null
                ? Copy(
                    value,
                    computedExpression: SafeMigrationSql.OpaqueAfterRename(value.ComputedColumnSql),
                    replaceComputed: true)
                : value.ComputedExpression is not null
                    ? Copy(
                        value,
                        computedExpression: RenameColumnExpression(value.ComputedExpression, source, target),
                        replaceComputed: true)
                    : value;
        }

        /// <summary>Retains captured CHECK semantics across a certified member rename.</summary>
        private ExpectedCheckConstraintDefinition RenameCheckDefinition(
            ExpectedCheckConstraintDefinition value,
            string source,
            string target
        )
        {
            if (value.CapturedSql is not null)
            {
                if (!SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
                        value.Expression!,
                        source,
                        Columns.Comparer))
                {
                    return value;
                }

                return SafeMigrationSqlExpressionParser.TryRenameColumnIdentifier(
                    value.CapturedSql,
                    source,
                    target,
                    Columns.Comparer,
                    out var renamedSql,
                    out var renamedExpression,
                    _objectIdentityNormalizer?.CapturedSqlIdentifierDelimiter,
                    _objectIdentityNormalizer?.FoldUnquotedCapturedSqlIdentifiers == true)
                    ? Copy(value, expression: renamedExpression, capturedSql: renamedSql, replaceExpression: true)
                    : Copy(
                        value,
                        expression: SafeMigrationSql.OpaqueAfterRename(value.CapturedSql),
                        replaceExpression: true);
            }

            return value.Sql is not null
                ? Copy(value, expression: SafeMigrationSql.OpaqueAfterRename(value.Sql), replaceExpression: true)
                : Copy(
                    value,
                    expression: RenameColumnExpression(value.Expression!, source, target),
                    replaceExpression: true);
        }

        /// <summary>Resolves ordered column members with the same physical identity as the column dictionary.</summary>
        private int FindColumnOrder(
            string name
        )
        {
            for (var ordinal = 0; ordinal < _columnOrder.Count; ordinal++)
            {
                if (Columns.Comparer.Equals(_columnOrder[ordinal], name))
                {
                    return ordinal;
                }
            }

            return -1;
        }

        /// <summary>Renames structured column members without changing their owner qualifiers.</summary>
        private SafeMigrationSqlExpression RenameColumnExpression(
            SafeMigrationSqlExpression expression,
            string source,
            string target
        ) => SafeMigrationSqlExpressionInspector.RenameColumnIdentifier(expression, source, target, Columns.Comparer);

        public void RenamePrincipalColumn(
            string principalTable,
            string? principalSchema,
            string source,
            string target
        )
        {
            ReplaceValues(
                ForeignKeys,
                value => SameIdentity(value.PrincipalTable, value.PrincipalSchema, principalTable, principalSchema)
                    ? Copy(value, principalColumns: Rename(value.PrincipalColumns, source, target))
                    : value);
        }
    }
}
