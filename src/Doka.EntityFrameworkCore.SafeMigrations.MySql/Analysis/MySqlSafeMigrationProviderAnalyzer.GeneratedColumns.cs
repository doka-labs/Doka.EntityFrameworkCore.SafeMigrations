namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    private Dictionary<SafeMigrationOperation, GeneratedColumnSourceProof>? _generatedColumnSourceProofs;
    private HashSet<(string? Schema, string Table, string Column)>? _changedGeneratedColumnSources;
    private HashSet<(string? Schema, string Table)>? _changedGeneratedColumnOwners;
    private bool _generatedColumnSourcesUnknown;

    /// <summary>Binds only actual native qualified results to their exact generated-column operations.</summary>
    private void CaptureGeneratedColumnSourceProofs(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses,
        bool isMariaDb
    )
    {
        _generatedColumnSourceProofs = null;
        _changedGeneratedColumnSources = null;
        _changedGeneratedColumnOwners = null;
        _generatedColumnSourcesUnknown = false;
        for (var index = 0; index < operations.Count; index++)
        {
            if (!TryGetColumnDependencyTarget(
                    operations[index].Intent,
                    out var table,
                    out var schema,
                    out var definition)
                || definition.ComputedExpression is null)
            {
                continue;
            }

            IReadOnlyList<string> valueSources = Array.Empty<string>();
            if (isMariaDb
                && !definition.IsNullable
                && !MariaDbGeneratedColumnNullability.TryGetRequiredIntegerSources(definition, out valueSources))
            {
                continue;
            }

            var sources = MySqlGeneratedColumnDependencies.Collect(definition);
            var analysis = analyses[index];
            var qualified = analysis.ObservedState == SafeMigrationObservedState.Missing
                || analysis is { ObservedState: SafeMigrationObservedState.Matching, PostconditionSatisfied: true };

            (_generatedColumnSourceProofs ??=
                    new Dictionary<SafeMigrationOperation, GeneratedColumnSourceProof>(
                        ReferenceEqualityComparer.Instance))
                [operations[index]] = new GeneratedColumnSourceProof(
                    table,
                    schema,
                    definition.Name,
                    sources,
                    valueSources,
                    qualified,
                    !isMariaDb,
                    analysis.ObservedState == SafeMigrationObservedState.Missing,
                    analysis is { ObservedState: SafeMigrationObservedState.Matching, PostconditionSatisfied: true }
                        ? analysis
                        : null);
        }
    }

    /// <summary>Refuses stale live dependency evidence before Core may reuse an unchanged computed target.</summary>
    private SafeMigrationProviderAnalysis? ValidateCapturedGeneratedColumn(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (liveAnalysis.ObservedState is not (SafeMigrationObservedState.Missing or SafeMigrationObservedState.Matching
                )
            || _generatedColumnSourceProofs is not { } proofs
            || !proofs.TryGetValue(operation, out var proof))
        {
            return null;
        }

        return GeneratedColumnSourcesAreCurrent(proof, columns) ? null : GeneratedColumnDependencyUnproven();
    }

    /// <summary>Qualifies generated targets and newly supplied dependencies after neutral projection.</summary>
    private SafeMigrationProviderAnalysis ValidateProjectedGeneratedColumn(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projected,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        if (_generatedColumnSourceProofs is not { } proofs
            || !proofs.TryGetValue(operation, out var proof))
        {
            return projected;
        }

        if (projected.ObservedState is SafeMigrationObservedState.Missing or SafeMigrationObservedState.Matching)
        {
            return GeneratedColumnSourcesAreCurrent(proof, columns) ? projected : GeneratedColumnDependencyUnproven();
        }

        if (projected is
            {
                ObservedState: SafeMigrationObservedState.PrerequisiteMissing,
                Code: "projected_structure_state_unknown"
            }
            && proof.LiveMatchingAnalysis is { } matching
            && !_generatedColumnSourcesUnknown
            && _changedGeneratedColumnOwners?.Contains((NormalizeProofSchema(proof.Schema), proof.Table)) != true
            && _changedGeneratedColumnSources?.Contains(
                (NormalizeProofSchema(proof.Schema), proof.Table, proof.TargetName))
            != true
            && GeneratedColumnSourcesAreCurrent(proof, columns))
        {
            // WHY: An unrelated member DROP invalidates the complete physical
            // layout, not this independently matched generated definition.
            // Reuse only its unchanged native identity and current operand
            // facts; missing targets and replaced members have no such proof.
            return matching;
        }

        if (projected is
        {
            ObservedState: SafeMigrationObservedState.Unsupported,
            IsInvariantUnsupported: false,
            Code: "generated_column_nullability",
        }
                or
        {
            ObservedState: SafeMigrationObservedState.PrerequisiteMissing,
            Code: "generated_column_dependency_missing",
        }
            && GeneratedColumnSourcesAreCurrent(proof, columns))
        {
            // WHY: This dynamic native result proves an absent generated target,
            // not an unsupported expression. Earlier accepted source tightening
            // can discharge it, but only complete current projected metadata may
            // replace the original, independently rejected source assumptions.
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None,
                false,
                "projected_generated_column_dependencies_proven");
        }

        return projected;
    }

    /// <summary>Uses accepted source definitions first and never resurrects dropped or renamed live members.</summary>
    private bool GeneratedColumnSourcesAreCurrent(
        GeneratedColumnSourceProof proof,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var normalizedSchema = NormalizeProofSchema(proof.Schema);
        var liveOwnerCurrent = !_generatedColumnSourcesUnknown
            && _changedGeneratedColumnOwners?.Contains((normalizedSchema, proof.Table)) != true;

        if (columns is ISafeMigrationProjectedTableSource tables
            && tables.TryGetProjectedTableState(proof.Table, proof.Schema, out var state)
            && state.IsNewlyCreated)
        {
            // WHY: Known member-only DDL can make Core's complete table shape
            // unknown while preserving untouched integral source columns. The
            // dedicated member/owner tombstones and opaque-operation flag below
            // retain the narrower physical lifetime without resurrecting members.
            liveOwnerCurrent = false;
        }

        foreach (var source in proof.Sources)
        {
            if (columns.TryGetProjectedColumn(proof.Table, proof.Schema, source, out var definition))
            {
                if ((proof.ForbidIdentitySources && MySqlGeneratedColumnDependencies.IsIdentitySource(definition))
                    || (IsRequiredValueSource(proof, source)
                        && !MariaDbGeneratedColumnNullability.IsRequiredOrdinaryInteger(definition)))
                {
                    return false;
                }

                continue;
            }

            if (!liveOwnerCurrent
                || _changedGeneratedColumnSources?.Contains((normalizedSchema, proof.Table, source)) == true)
            {
                return false;
            }

            if (!proof.LiveQualified)
            {
                if (_livePrerequisiteColumns is not { } facts
                    || !facts.TryGetValue((normalizedSchema, proof.Table, source), out var captured)
                    || !captured.OwnerPresent
                    || captured.StoreType is null
                    || (IsRequiredValueSource(proof, source)
                        && (captured.IsNullable
                            || captured.IsGenerated
                            || !MariaDbGeneratedColumnNullability.IsSignedIntegerStoreType(captured.StoreType))))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Requests sparse facts only when an earlier metadata change might discharge a dynamic refusal.</summary>
    private static bool RequiresGeneratedSourceCapture(
        SafeMigrationIntent intent,
        SafeMigrationProviderAnalysis analysis
    ) => intent is EnsureColumnIntent { Definition.ComputedExpression: not null }
        && analysis is
        {
            IsInvariantUnsupported: false,
            Code: "generated_column_dependency_missing" or "generated_column_nullability",
        };

    /// <summary>Separates ordinary binding from dependencies that must guarantee integral non-NULL.</summary>
    private static bool IsRequiredValueSource(
        GeneratedColumnSourceProof proof,
        string source
    )
    {
        foreach (var required in proof.ValueSources)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(required, source))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Expires exact sources on accepted structural changes, including nullable-only transitions.</summary>
    private void ObserveGeneratedColumnSources(
        SafeMigrationOperation operation,
        SafeMigrationDecision decision
    )
    {
        if (_generatedColumnSourceProofs is null
            || decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.Repair))
        {
            return;
        }

        switch (operation.Intent)
        {
            case EnsureColumnIntent value:
                Changed(value.Table, value.Schema, value.Definition.Name);
                break;
            case AlterColumnIntent value:
                Changed(value.Table, value.Schema, value.Definition.Name);
                break;
            case DropColumnIntent value:
                Changed(value.Table, value.Schema, value.Name);
                break;
            case RenameColumnIntent value:
                Changed(value.Table, value.Schema, value.Name);
                Changed(value.Table, value.Schema, value.NewName);
                break;
            case DropTableIntent value:
                OwnerChanged(value.Table, value.Schema);
                break;
            case RenameTableIntent value:
                OwnerChanged(value.Name, value.Schema);
                OwnerChanged(value.NewName ?? value.Name, value.NewSchema ?? value.Schema);
                break;
        }
    }

    /// <summary>Records a changed physical member using the native column and owner identity rules.</summary>
    private void Changed(
        string table,
        string? schema,
        string name
    ) => (_changedGeneratedColumnSources ??= new(PhysicalMemberComparer)).Add(
        (NormalizeProofSchema(schema), table, name));

    /// <summary>Invalidates source fallback across physical owner lifetimes.</summary>
    private void OwnerChanged(
        string table,
        string? schema
    ) => (_changedGeneratedColumnOwners ??= new(PhysicalOwnerComparer)).Add((NormalizeProofSchema(schema), table));

    /// <summary>Retains a precise rejection code instead of inventing a live generated-column certificate.</summary>
    private static SafeMigrationProviderAnalysis GeneratedColumnDependencyUnproven() => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        false,
        "projected_generated_column_dependency_unproven");

    /// <summary>Retains exact target matching and the minimal reference-bound native dependency facts.</summary>
    private sealed record GeneratedColumnSourceProof(
        string Table,
        string? Schema,
        string TargetName,
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> ValueSources,
        bool LiveQualified,
        bool ForbidIdentitySources,
        bool OriginallyMissing,
        SafeMigrationProviderAnalysis? LiveMatchingAnalysis
    );
}
