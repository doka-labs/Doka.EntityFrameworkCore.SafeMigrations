namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    private IReadOnlyList<ExpectedIndexDefinition> GetExpectedUniqueIndexes(
        EnsureTableIntent intent
    )
    {
        if (_planCapture.IsActive)
        {
            return _planCapture.GetExpectedUniqueIndexes(intent.Definition.Table, intent.Definition.Schema);
        }

        if (_planCapture.HasGenerationContract)
        {
            return _planCapture.GetGenerationUniqueIndexes(intent.Definition.Table, intent.Definition.Schema);
        }

        // Direct handler use has no ordered provider-generation scope. EF's
        // target relational model is the only authoritative fallback there.
        if (!ReferenceEquals(_designTimeModel, _modelUniqueIndexSource))
        {
            _modelUniqueIndexes = BuildModelUniqueIndexes(_designTimeModel);
            _modelUniqueIndexSource = _designTimeModel;
        }

        return _modelUniqueIndexes!.GetValueOrDefault(
                new ModelTableKey(intent.Definition.Table, intent.Definition.Schema))
            ?? s_emptyUniqueIndexes;
    }

    private SafeMigrationExpectedTableConstraints? GetExpectedTableConstraints(
        EnsureTableIntent intent,
        IModel? operationModel
    )
    {
        if (_planCapture.IsActive)
        {
            return _planCapture.GetExpectedTableConstraints(intent);
        }

        if (_planCapture.HasGenerationContract)
        {
            return _planCapture.GetGenerationTableConstraints(intent);
        }

        // Direct handler use has no ordered provider-generation scope. EF's
        // target relational model is the only authoritative fallback there.
        return SafeMigrationExpectedTableConstraints.FromModel(
            operationModel ?? _designTimeModel,
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.CheckConstraints);
    }

    private static Dictionary<ModelTableKey, IReadOnlyList<ExpectedIndexDefinition>> BuildModelUniqueIndexes(
        IModel model
    )
    {
        var result = new Dictionary<ModelTableKey, IReadOnlyList<ExpectedIndexDefinition>>();
        foreach (var table in model.GetRelationalModel()
                     .Tables)
        {
            var indexes = table
                .Indexes
                .Where(static index => index.IsUnique)
                .Select(index => CreateModelIndexDefinition(table, index))
                .OrderBy(static index => index.Name, StringComparer.Ordinal)
                .ToArray();

            if (indexes.Length > 0)
            {
                result.Add(new ModelTableKey(table.Name, table.Schema), indexes);
            }
        }

        return result;
    }

    private static ExpectedIndexDefinition CreateModelIndexDefinition(
        ITable table,
        ITableIndex index
    )
    {
        IReadOnlyList<int>? prefixLengths = null;
        foreach (var mappedIndex in index.MappedIndexes)
        {
            // Doka's migration contract exposes prefix lengths through this
            // stable provider annotation. Reading the relational model avoids
            // coupling SafeMigrations to Doka's internal metadata extensions.
            if (mappedIndex.FindAnnotation(IndexPrefixLengthAnnotation)
                    ?.Value is not IReadOnlyList<int> mappedPrefixLengths)
            {
                continue;
            }

            if (prefixLengths is not null
                && !prefixLengths.SequenceEqual(mappedPrefixLengths))
            {
                throw new InvalidOperationException(
                    $"Mapped indexes for '{table.Name}.{index.Name}' have conflicting prefix lengths.");
            }

            prefixLengths = mappedPrefixLengths;
        }

        if (prefixLengths is not null
            && prefixLengths.Count != index.Columns.Count)
        {
            throw new InvalidOperationException(
                $"Index '{table.Name}.{index.Name}' has an invalid prefix-length vector.");
        }

        if (index.IsDescending is { Count: > 0 } sortOrders
            && sortOrders.Count != index.Columns.Count)
        {
            throw new InvalidOperationException($"Index '{table.Name}.{index.Name}' has an invalid sort-order vector.");
        }

        var keys = index
            .Columns
            .Select((
                column,
                ordinal
            ) => new ExpectedIndexKeyDefinition(
                column: column.Name,
                sortOrder: ModelSortOrder(index.IsDescending, ordinal),
                prefixLength: prefixLengths?[ordinal] is > 0 ? prefixLengths[ordinal] : null))
            .ToArray();

        return new ExpectedIndexDefinition(index.Name, table.Name, keys, table.Schema, index.IsUnique, index.Filter);
    }

    private static SafeMigrationIndexSortOrder ModelSortOrder(
        IReadOnlyList<bool>? sortOrders,
        int ordinal
    ) => sortOrders switch
    {
        null => SafeMigrationIndexSortOrder.ProviderDefault,
        // EF represents IsDescending() without arguments as an empty vector,
        // meaning every key is descending rather than no key being descending.
        { Count: 0 } => SafeMigrationIndexSortOrder.Descending,
        _ when sortOrders[ordinal] => SafeMigrationIndexSortOrder.Descending,
        _ => SafeMigrationIndexSortOrder.Ascending,
    };

    private readonly record struct ModelTableKey(
        string Table,
        string? Schema
    );
}
