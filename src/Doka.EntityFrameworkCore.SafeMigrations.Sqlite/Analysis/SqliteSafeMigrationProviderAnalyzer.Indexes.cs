namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <summary>Classifies index creation or convergence with physical facets and row uniqueness.</summary>
    private SafeMigrationProviderAnalysis AnalyzeEnsureIndex(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsureIndexIntent intent
    )
    {
        var unsupported = IndexUnsupportedFeature(intent.Definition);
        if (unsupported is not null)
        {
            // WHY: A missing table can be created by earlier raw SQL, but it
            // cannot make an unsupported index definition executable.
            return InvariantUnsupported(unsupported);
        }

        if (!snapshot.Tables.TryGetValue(intent.Definition.Table, out var table)
            || intent.Definition.Keys.Any(key => key.Column is not null && !table.Columns.ContainsKey(key.Column)))
        {
            return PrerequisiteMissing();
        }

        var named = table.Indexes.FirstOrDefault(index =>
            s_identifierComparer.Equals(index.Name, intent.Definition.Name));

        if (named is not null)
        {
            return IndexMatches(named, intent.Definition, table.Columns)
                ? Matching(postconditionSatisfied: true, matchedObjectName: named.Name)
                : Different("index_definition", "matching", "different", matchedObjectName: named.Name);
        }

        var semantic = table.Indexes.FirstOrDefault(index => index.Origin == "c"
            && IndexMatches(index, intent.Definition, table.Columns));

        if (semantic is not null)
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: semantic.Name);
        }

        if (intent.Definition.Unique
            && HasDuplicateIndexKey(connection, transaction, intent.Definition))
        {
            return DataBlocked("unique_index_duplicate_values");
        }

        return Missing(postconditionSatisfied: false);
    }

    /// <summary>Classifies index deletion without confusing another table's schema-scoped name.</summary>
    private static SafeMigrationProviderAnalysis AnalyzeDropIndex(
        SqliteCatalogSnapshot snapshot,
        DropIndexIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return Missing(postconditionSatisfied: true);
        }

        var index = table.Indexes.FirstOrDefault(value => s_identifierComparer.Equals(value.Name, intent.Name));

        return index is null
            ? Missing(postconditionSatisfied: true)
            : index.Origin == "c"
                ? Matching(postconditionSatisfied: false, matchedObjectName: index.Name)
                : Different("index_origin", "created", index.Origin, matchedObjectName: index.Name);
    }

    /// <summary>Classifies an index rename using both physical identities and target occupancy.</summary>
    private static SafeMigrationProviderAnalysis AnalyzeRenameIndex(
        SqliteCatalogSnapshot snapshot,
        RenameIndexIntent intent
    )
    {
        var targetOccupied = IsIndexRenameTargetOccupied(snapshot, intent.NewName);
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return Missing(postconditionSatisfied: true)
                .WithRenameTargetExists(targetOccupied);
        }

        var source = table.Indexes.FirstOrDefault(value => s_identifierComparer.Equals(value.Name, intent.Name));
        if (source is not null
            && !targetOccupied)
        {
            return source.Origin == "c"
                ? Matching(postconditionSatisfied: false, matchedObjectName: source.Name)
                    .WithRenameSourceSupported(true)
                    .WithRenameTargetExists(false)
                : Different("index_origin", "created", source.Origin, matchedObjectName: source.Name)
                    .WithRenameSourceSupported(false)
                    .WithRenameTargetExists(false);
        }

        return (source is null
                ? Missing(postconditionSatisfied: true)
                : Different("rename_target", "missing", "existing"))
            // WHY: A later accepted destination drop can remove a collision,
            // but cannot turn a constraint-backed source into a standalone index.
            .WithRenameSourceSupported(source?.Origin == "c")
            .WithRenameTargetExists(targetOccupied);
    }

    /// <summary>Checks SQLite's schema-wide table, view and index destination namespace.</summary>
    /// <param name="snapshot">The complete catalog in the current execution transaction.</param>
    /// <param name="name">The independently captured index destination.</param>
    /// <returns>Whether native CREATE INDEX cannot acquire the requested name.</returns>
    private static bool IsIndexRenameTargetOccupied(
        SqliteCatalogSnapshot snapshot,
        string name
    )
    {
        if (snapshot.Tables.ContainsKey(name))
        {
            return true;
        }

        // WHY: RenameIndex is implemented as CREATE followed by DROP. Unlike
        // column members, its target can collide with another table's index or
        // a view, so an owner-local lookup cannot certify destination absence.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var table in snapshot.Tables.Values)
        {
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var index in table.Indexes)
            {
                if (s_identifierComparer.Equals(index.Name, name))
                {
                    return true;
                }
            }
        }

        foreach (var schemaObject in snapshot.OtherObjects)
        {
            if (schemaObject.Type is "view" or "index"
                && s_identifierComparer.Equals(schemaObject.Name, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Compares ordered index keys, expression roles, collations and filtering.</summary>
    private bool IndexMatches(
        SqliteIndexSnapshot actual,
        ExpectedIndexDefinition expected,
        IReadOnlyDictionary<string, SqliteColumnSnapshot> columns
    )
    {
        var actualKeys = actual
            .Keys
            .Where(static value => value.IsKey)
            .OrderBy(static value => value.Ordinal)
            .ToArray();

        var expectedFilter = expected.StructuredFilter is null
            ? expected.Filter
            : _expressionRenderer.Render(expected.StructuredFilter);

        if (actual.Unique != expected.Unique
            || actual.Partial != (expectedFilter is not null)
            || actualKeys.Length != expected.Keys.Count
            || !SqlEquivalent(actual.Filter, expectedFilter))
        {
            return false;
        }

        // WHY: Compare actual and expected key facets at the same ordered key position.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < actualKeys.Length; index++)
        {
            var actualKey = actualKeys[index];
            var expectedKey = expected.Keys[index];
            var expectedExpression = expectedKey.StructuredExpression is null
                ? expectedKey.Expression
                : _expressionRenderer.Render(expectedKey.StructuredExpression);

            var identityMatches = expectedKey.Column is not null
                ? actualKey.Expression is null && s_identifierComparer.Equals(actualKey.Column, expectedKey.Column)
                : actualKey.Column is null && SqlEquivalent(actualKey.Expression, expectedExpression);

            var expectedCollation = expectedKey.Collation?.Name
                ?? (expectedKey.Column is not null && columns.TryGetValue(expectedKey.Column, out var column)
                    ? column.Collation ?? "BINARY"
                    : "BINARY");

            if (!identityMatches
                || actualKey.Descending != (expectedKey.SortOrder == SafeMigrationIndexSortOrder.Descending)
                || !s_identifierComparer.Equals(actualKey.Collation, expectedCollation))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks a retained physical index against the target-model rebuild shape.</summary>
    private static bool ModelIndexMatches(
        SqliteIndexSnapshot actual,
        ITableIndex expected
    )
    {
        var actualKeys = actual
            .Keys
            .Where(static value => value.IsKey)
            .OrderBy(static value => value.Ordinal)
            .ToArray();

        if (actual.Unique != expected.IsUnique
            || actual.Partial != (expected.Filter is not null)
            || actualKeys.Length != expected.Columns.Count
            || !SqlEquivalent(actual.Filter, expected.Filter))
        {
            return false;
        }

        var descending = expected.IsDescending;
        // WHY: Compare actual and expected key facets at the same ordered key position.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < actualKeys.Length; index++)
        {
            var actualKey = actualKeys[index];
            var expectedCollation = expected.Columns[index].Collation ?? "BINARY";
            if (actualKey.Expression is not null
                || !s_identifierComparer.Equals(actualKey.Column, expected.Columns[index].Name)
                || actualKey.Descending != (descending is not null && index < descending.Count && descending[index])
                || !s_identifierComparer.Equals(actualKey.Collation, expectedCollation))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Identifies index contracts outside SQLite's supported representation.</summary>
    private string? IndexUnsupportedFeature(
        ExpectedIndexDefinition definition
    )
    {
        if (definition.IncludedColumns.Count > 0
            || definition.Method is not null
            || definition.NullsDistinct is not null)
        {
            return "index_provider_option";
        }

        foreach (var key in definition.Keys)
        {
            if (key.PrefixLength is not null
                || key.OperatorClass is not null
                || key.NullOrder != SafeMigrationIndexNullOrder.ProviderDefault
                || key.Collation?.Schema is not null)
            {
                return "index_key_provider_option";
            }

            if (key.StructuredExpression is not null)
            {
                var unsupported = _expressionRenderer.GetUnsupportedFeature(key.StructuredExpression);
                if (unsupported is not null)
                {
                    return unsupported;
                }
            }
        }

        return definition.StructuredFilter is not null
            ? _expressionRenderer.GetUnsupportedFeature(definition.StructuredFilter)
            : null;
    }
}
