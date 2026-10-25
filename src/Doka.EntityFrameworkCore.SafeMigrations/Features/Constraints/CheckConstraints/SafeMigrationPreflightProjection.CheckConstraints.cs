namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureCheckConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        intent = (EnsureCheckConstraintIntent)SafeMigrationCapturedExpressionNormalization.NormalizeIntent(intent,
            _objectIdentityNormalizer?.FoldUnquotedCapturedSqlIdentifiers == true);

        if (IsProjectedTableStructureUnknown(intent.Definition.Table, intent.Definition.Schema))
        {
            if (_prerequisites.TryGetValue(new TableKey(intent.Definition.Table, intent.Definition.Schema),
                    out var renamedPrerequisites)
                && renamedPrerequisites.CheckDefinitionsFollowKnownRename
                && AnalyzeAcceptedCheckDefinition(renamedPrerequisites.CheckConstraints, intent.Definition.Name,
                    intent.Definition, liveAnalysis) is { ObservedState: SafeMigrationObservedState.Matching } accepted)
            {
                // WHY: Only a certified rename can carry an exact previously
                // accepted CHECK through this structural boundary. This never
                // creates a new data-validating constraint or a row-level proof.
                return accepted;
            }

            return StructureStateUnknown();
        }

        if (!TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            if (_prerequisites.TryGetValue(
                    new TableKey(intent.Definition.Table, intent.Definition.Schema),
                    out var prerequisites))
            {
                var accepted = AnalyzeAcceptedCheckDefinition(
                    prerequisites.CheckConstraints,
                    intent.Definition.Name,
                    intent.Definition,
                    liveAnalysis);

                if (accepted is not null)
                {
                    if (accepted.ObservedState == SafeMigrationObservedState.Missing
                        && !TryGetConstraintPrerequisites(
                            intent.Definition.Table,
                            intent.Definition.Schema,
                            SafeMigrationPrerequisiteColumns.Local(intent),
                            out _)
                        && !CanReuseMatchingLiveColumnPrerequisites(
                            intent.Definition.Table,
                            intent.Definition.Schema,
                            SafeMigrationPrerequisiteColumns.Local(intent),
                            liveAnalysis))
                    {
                        return StructureStateUnknown();
                    }

                    return InvalidateDataDependentMissing(
                        intent.Definition.Table,
                        intent.Definition.Schema,
                        accepted);
                }
            }

            var projectedAnalysis = CanProjectMissingCheckConstraint(intent, liveAnalysis)
                ? Analysis(SafeMigrationObservedState.Missing)
                : liveAnalysis;

            return InvalidateDataDependentMissing(
                intent.Definition.Table,
                intent.Definition.Schema,
                projectedAnalysis);
        }

        var analysis = AnalyzeDefinition(
            table.CheckConstraints,
            intent.Definition.Name,
            intent.Definition,
            AreProjectedCheckTargetsEquivalent);

        return InvalidateDataDependentMissing(table.Table, table.Schema, analysis);
    }

    /// <summary>
    /// Retains physical occupancy and tombstones while recognizing a certified native CHECK token rewrite.
    /// </summary>
    private SafeMigrationProviderAnalysis? AnalyzeAcceptedCheckDefinition(
        ProjectedDefinitionSet<ExpectedCheckConstraintDefinition> definitions,
        string name,
        ExpectedCheckConstraintDefinition expected,
        SafeMigrationProviderAnalysis liveAnalysis)
    {
        if (expected.CapturedSql is null || _objectIdentityNormalizer?.CapturedSqlIdentifierDelimiter is null)
        {
            return AnalyzeAcceptedDefinition(definitions, name, expected, liveAnalysis);
        }

        if (definitions.TryGetValue(name, out var exact))
        {
            return Analysis(AreProjectedCheckSemanticsEquivalent(exact, expected)
                ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different);
        }

        if (definitions.ContainsSemantically(expected)
            || definitions.Any((Projection: this, Expected: expected),
                static (value, state) => state.Projection.AreProjectedCheckSemanticsEquivalent(value, state.Expected)))
        {
            return Analysis(SafeMigrationObservedState.Matching);
        }

        return AnalyzeAcceptedDefinition(definitions, name, expected, liveAnalysis);
    }

    /// <summary>
    /// Changes only projected source-token comparison, not global CHECK equality or contract hashing.
    /// </summary>
    private bool AreProjectedCheckTargetsEquivalent(
        ExpectedCheckConstraintDefinition accepted,
        ExpectedCheckConstraintDefinition target)
        => StringComparer.Ordinal.Equals(accepted.Name, target.Name)
            && AreProjectedCheckSemanticsEquivalent(accepted, target);

    /// <summary>
    /// Only native terminal-member quotes and verified member identity may differ in a captured CHECK.
    /// </summary>
    private bool AreProjectedCheckSemanticsEquivalent(
        ExpectedCheckConstraintDefinition accepted,
        ExpectedCheckConstraintDefinition target)
        => AreProjectedCheckSemanticsEquivalent(accepted, target, _objectIdentityNormalizer);

    /// <summary>Shares only the scoped native CHECK token certificate across known projection lineages.</summary>
    /// <param name="accepted">The previously accepted, possibly source-renamed CHECK contract.</param>
    /// <param name="target">The explicitly authored complete CHECK reassertion.</param>
    /// <param name="normalizer">The provider-certified owner, quote, and column-member identity.</param>
    /// <returns>Whether the reassertion preserves every non-column source role and its exact physical owner.</returns>
    internal static bool AreProjectedCheckSemanticsEquivalent(
        ExpectedCheckConstraintDefinition accepted,
        ExpectedCheckConstraintDefinition target,
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer)
    {
        if (SafeMigrationDefinitionEquivalence.CheckConstraintSemantics(accepted, target))
        {
            return true;
        }

        // WHY: A native rename may introduce identifier quotes while the next
        // scaffolded CHECK uses a bare member. The role-aware certificate keeps
        // every numeric, literal, function, type, qualifier, and operator byte
        // exact; it never parses an explicit opaque constructor into approval.
        return accepted.CapturedSql is { } source && target.CapturedSql is { } targetSource
            && accepted.Sql is null && target.Sql is null
            && accepted.Expression is not null && target.Expression is not null
            && IdentifierEquals(normalizer, accepted.Table, target.Table)
            && StringComparer.Ordinal.Equals(NormalizeSchema(normalizer, accepted.Schema),
                NormalizeSchema(normalizer, target.Schema))
            && normalizer?.CapturedSqlIdentifierDelimiter is { } delimiter
            && SafeMigrationSqlExpressionParser.SourcesDifferOnlyByNativeColumnQuotes(source, targetSource, delimiter,
                normalizer.FoldUnquotedCapturedSqlIdentifiers, normalizer.ColumnIdentifierComparer);
    }

    private SafeMigrationProviderAnalysis Project(
        DropCheckConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
        ? StructureStateUnknown()
        : TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                table.CheckConstraints.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Missing)
            : _prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites)
                && prerequisites.CheckConstraints.TryGetPhysicalState(intent.Name, out var present)
                    ? Analysis(present ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing)
                    : liveAnalysis;

    private void Observe(
        EnsureCheckConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
    {
        intent = (EnsureCheckConstraintIntent)SafeMigrationCapturedExpressionNormalization.NormalizeIntent(intent,
            _objectIdentityNormalizer?.FoldUnquotedCapturedSqlIdentifiers == true);

        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp)
        {
            // WHY: A standalone accepted CHECK proves its owner exists even
            // when no earlier table or column operation created a sparse view.
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Definition.Table, intent.Definition.Schema);

            ObserveAcceptedDefinition(
                prerequisites.CheckConstraints,
                intent.Definition.Name,
                intent.Definition,
                liveAnalysis,
                decision);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.CheckConstraints[intent.Definition.Name] = intent.Definition;
        }
    }

    private void Observe(
        DropCheckConstraintIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.CheckConstraints.MarkPhysicalMissing(intent.Name, CurrentOperationOrdinal);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.CheckConstraints.Remove(intent.Name);
        }
    }
}
