namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private bool SharesUniqueConstraintAndIndexIdentity =>
        _projectedKeyAnalyzer?.SharesUniqueConstraintAndIndexIdentity == true;

    private void CaptureSharedUniqueKeys(
        ProjectedPrerequisites prerequisites,
        ExpectedTableDefinition definition
    )
    {
        if (!SharesUniqueConstraintAndIndexIdentity)
        {
            return;
        }

        foreach (var uniqueConstraint in definition.UniqueConstraints)
        {
            prerequisites.Indexes.AcceptPhysical(
                uniqueConstraint.Name,
                AsUniqueIndex(uniqueConstraint));
        }
    }

    private void CaptureSharedUniqueKeys(
        ProjectedTable table,
        ExpectedTableDefinition definition
    )
    {
        if (!SharesUniqueConstraintAndIndexIdentity)
        {
            return;
        }

        foreach (var uniqueConstraint in definition.UniqueConstraints)
        {
            table.Indexes[uniqueConstraint.Name] = AsUniqueIndex(uniqueConstraint);
        }
    }

    private void ObserveSharedUniqueIndex(
        ProjectedPrerequisites prerequisites,
        EnsureIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
    {
        if (!SharesUniqueConstraintAndIndexIdentity
            || !TryAsUniqueConstraint(intent.Definition, out var uniqueConstraint)
            || StringComparer.OrdinalIgnoreCase.Equals(liveAnalysis.MatchedObjectName, "PRIMARY"))
        {
            return;
        }

        ObserveAcceptedDefinition(
            prerequisites.UniqueConstraints,
            uniqueConstraint.Name,
            uniqueConstraint,
            liveAnalysis,
            decision);
    }

    private void ObserveSharedUniqueConstraint(
        ProjectedPrerequisites prerequisites,
        EnsureUniqueConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
    {
        if (!SharesUniqueConstraintAndIndexIdentity)
        {
            return;
        }

        ObserveAcceptedDefinition(
            prerequisites.Indexes,
            intent.Definition.Name,
            AsUniqueIndex(intent.Definition),
            liveAnalysis,
            decision);
    }

    private void ObserveSharedUniqueIndex(
        ProjectedTable table,
        EnsureIndexIntent intent
    )
    {
        if (SharesUniqueConstraintAndIndexIdentity
            && TryAsUniqueConstraint(intent.Definition, out var uniqueConstraint))
        {
            table.UniqueConstraints[uniqueConstraint.Name] = uniqueConstraint;
        }
    }

    private void ObserveSharedUniqueConstraint(
        ProjectedTable table,
        EnsureUniqueConstraintIntent intent
    )
    {
        if (SharesUniqueConstraintAndIndexIdentity)
        {
            table.Indexes[intent.Definition.Name] = AsUniqueIndex(intent.Definition);
        }
    }

    private bool DropSharedUniqueIndex(
        ProjectedPrerequisites prerequisites,
        string physicalName
    )
    {
        if (!SharesUniqueConstraintAndIndexIdentity)
        {
            return false;
        }

        var wasKnownCandidateKey = prerequisites.UniqueConstraints.TryGetValue(physicalName, out _);

        // WHY: MySQL exposes UNIQUE constraints as indexes. A physical index
        // drop invalidates that identity even when no earlier Ensure operation
        // populated the cross-kind projection cache.
        prerequisites.UniqueConstraints.MarkPhysicalMissing(physicalName);

        return wasKnownCandidateKey;
    }

    private bool DropSharedUniqueIndex(
        ProjectedTable table,
        string physicalName
    )
    {
        if (!SharesUniqueConstraintAndIndexIdentity)
        {
            return false;
        }

        return table.UniqueConstraints.Remove(physicalName);
    }

    private void DropSharedUniqueConstraint(
        ProjectedPrerequisites prerequisites,
        string physicalName
    )
    {
        if (SharesUniqueConstraintAndIndexIdentity)
        {
            prerequisites.Indexes.MarkPhysicalMissing(physicalName);
        }
    }

    private void DropSharedUniqueConstraint(
        ProjectedTable table,
        string physicalName
    )
    {
        if (SharesUniqueConstraintAndIndexIdentity)
        {
            table.Indexes.Remove(physicalName);
        }
    }

    private void RenameSharedUniqueIndex(
        ProjectedPrerequisites prerequisites,
        string source,
        string target,
        ExpectedIndexDefinition renamedIndex
    )
    {
        if (!SharesUniqueConstraintAndIndexIdentity
            || !TryAsUniqueConstraint(renamedIndex, out var renamedConstraint))
        {
            return;
        }

        prerequisites.UniqueConstraints.MarkPhysicalMissing(source);
        prerequisites.UniqueConstraints.AcceptPhysical(target, renamedConstraint);
    }

    private void RenameSharedUniqueIndex(
        ProjectedTable table,
        string source,
        string target,
        ExpectedIndexDefinition renamedIndex
    )
    {
        if (!SharesUniqueConstraintAndIndexIdentity
            || !TryAsUniqueConstraint(renamedIndex, out var renamedConstraint))
        {
            return;
        }

        table.UniqueConstraints.Remove(source);
        table.UniqueConstraints[target] = renamedConstraint;
    }

    private static ExpectedIndexDefinition AsUniqueIndex(
        ExpectedUniqueConstraintDefinition definition
    ) => new(
        definition.Name,
        definition.Table,
        definition.Columns.Select(static column => new ExpectedIndexKeyDefinition(column: column)),
        definition.Schema,
        unique: true);

    private static bool TryAsUniqueConstraint(
        ExpectedIndexDefinition definition,
        [NotNullWhen(true)] out ExpectedUniqueConstraintDefinition? uniqueConstraint
    )
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(definition.Name, "PRIMARY")
            || !definition.Unique
            || definition.Filter is not null
            || definition.StructuredFilter is not null
            || definition.IncludedColumns.Count > 0
            || definition.NullsDistinct == false
            || definition.Method is not null
                && !StringComparer.OrdinalIgnoreCase.Equals(definition.Method, "BTREE")
            || definition.Keys.Any(static key => key.Column is null
                || key.PrefixLength is not null
                || key.Collation is not null
                || key.OperatorClass is not null
                || key.NullOrder != SafeMigrationIndexNullOrder.ProviderDefault
                || key.SortOrder == SafeMigrationIndexSortOrder.Descending))
        {
            uniqueConstraint = null;
            return false;
        }

        uniqueConstraint = new ExpectedUniqueConstraintDefinition(
            definition.Name,
            definition.Table,
            definition.Keys.Select(static key => key.Column!),
            definition.Schema);

        return true;
    }

    private bool HasProjectedKeyColumnChange(
        string table,
        string? schema,
        IReadOnlyList<string> columns
    )
    {
        var key = new TableKey(table, schema);
        if (_prerequisites.TryGetValue(key, out var prerequisites)
            && prerequisites.NewlyCreated)
        {
            return true;
        }

        if (!_projectedChangedColumns.TryGetValue(key, out var changedColumns))
        {
            return false;
        }

        return columns.Any(changedColumns.Contains);
    }
}
