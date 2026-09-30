namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Tracks only candidate keys on the live principals referenced by inline FKs.</summary>
internal sealed class SqlServerProjectedCandidateKeys
{
    private readonly Dictionary<int, Dictionary<int, List<KeyColumn>>> _keys = [];
    private readonly Dictionary<NameIdentity, KeyIdentity> _names = [];
    private int _nextKeyId = -1;

    /// <summary>Binds a requested catalog name to its physical backing index.</summary>
    /// <param name="table">The physical table id.</param>
    /// <param name="name">The request or catalog name.</param>
    /// <param name="index">The physical backing index id.</param>
    public void BindName(
        int table,
        string name,
        int index
    )
        => _names[new NameIdentity(table, name)] = new KeyIdentity(table, index);

    /// <summary>Reads bounded principal key metadata using the analysis command's transaction and timeout.</summary>
    /// <param name="command">The reusable analysis command.</param>
    /// <param name="principalTables">Physical principal ids requiring candidate-key proof.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task completed after all bounded metadata batches have been read.</returns>
    public async Task ReadAsync(
        DbCommand command,
        IEnumerable<int> principalTables,
        CancellationToken cancellationToken
    )
    {
        foreach (var chunk in principalTables.Where(static id => id > 0).Distinct().Chunk(256))
        {
            var tableIds = string.Join(", ", chunk.Select(static id => id.ToString(CultureInfo.InvariantCulture)));

            // WHY: Constraint and index names refer to the same backing index.
            // Keeping that identity makes either accepted DROP remove all aliases,
            // while an independent equivalent key remains valid FK evidence.
            command.CommandText = "SELECT i.object_id, i.index_id, i.name, kc.name, c.name, c.column_id "
                + "FROM sys.indexes i JOIN sys.index_columns ic "
                + "ON ic.object_id = i.object_id AND ic.index_id = i.index_id "
                + "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
                + "LEFT JOIN sys.key_constraints kc "
                + "ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id "
                + $"WHERE i.object_id IN ({tableIds}) AND i.is_unique = 1 AND i.is_disabled = 0 "
                + "AND i.is_hypothetical = 0 AND i.has_filter = 0 AND ic.key_ordinal > 0 "
                + "ORDER BY i.object_id, i.index_id, ic.key_ordinal;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetInt32(0);
                var index = reader.GetInt32(1);
                var tableKeys = GetTableKeys(table);

                if (!tableKeys.TryGetValue(index, out var columns))
                {
                    columns = [];
                    tableKeys.Add(index, columns);
                }

                columns.Add(new KeyColumn(reader.GetString(4), reader.GetInt32(5)));
                BindName(table, reader.GetString(2), index);
                if (!reader.IsDBNull(3))
                {
                    BindName(table, reader.GetString(3), index);
                }
            }
        }
    }

    /// <summary>Matches live columns by their resolved physical ids and new columns by exact authored names.</summary>
    /// <param name="table">The physical or projected table id.</param>
    /// <param name="columns">The requested ordered column names.</param>
    /// <param name="columnIds">Resolved live ids in the same order, or an empty span for authored-only keys.</param>
    /// <returns>Whether one surviving key represents exactly the requested columns.</returns>
    public bool Contains(
        int table,
        IReadOnlyList<string> columns,
        ReadOnlySpan<int> columnIds = default
    )
    {
        if (!_keys.TryGetValue(table, out var keys))
        {
            return false;
        }

        foreach (var key in keys.Values)
        {
            if (key.Count != columns.Count)
            {
                continue;
            }

            var matching = true;

            for (var ordinal = 0; ordinal < key.Count; ordinal++)
            {
                if (key[ordinal].Id > 0
                    ? columnIds.Length != columns.Count || columnIds[ordinal] != key[ordinal].Id
                    : key[ordinal].Name != columns[ordinal])
                {
                    matching = false;
                    break;
                }
            }

            if (matching)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records an accepted key with physical ids where current live metadata provides them.</summary>
    /// <param name="table">The physical or projected table id.</param>
    /// <param name="name">The current key name.</param>
    /// <param name="columns">The ordered key column names.</param>
    /// <param name="columnIds">Resolved live ids in the same order, or an empty span for authored columns.</param>
    public void Add(
        int table,
        string name,
        IReadOnlyList<string> columns,
        ReadOnlySpan<int> columnIds = default
    )
    {
        var alias = new NameIdentity(table, name);
        var tableKeys = GetTableKeys(table);

        if (!_names.TryGetValue(alias, out var identity) || !tableKeys.ContainsKey(identity.Index))
        {
            identity = new KeyIdentity(table, _nextKeyId--);
            _names[alias] = identity;
        }

        var key = new List<KeyColumn>(columns.Count);

        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            key.Add(new KeyColumn(columns[ordinal], columnIds.Length == columns.Count ? columnIds[ordinal] : 0));
        }

        tableKeys[identity.Index] = key;
    }

    /// <summary>Removes the physical key bound to an accepted drop's name.</summary>
    /// <param name="table">The physical or projected table id.</param>
    /// <param name="name">The accepted drop's request name.</param>
    public void Remove(
        int table,
        string name
    )
    {
        if (_names.TryGetValue(new NameIdentity(table, name), out var identity)
            && _keys.TryGetValue(table, out var keys))
        {
            keys.Remove(identity.Index);
        }
    }

    /// <summary>Preserves the backing key while moving its accepted index name.</summary>
    /// <param name="table">The physical or projected table id.</param>
    /// <param name="name">The old index name.</param>
    /// <param name="newName">The accepted new index name.</param>
    public void Rename(
        int table,
        string name,
        string newName
    )
    {
        if (_names.Remove(new NameIdentity(table, name), out var identity))
        {
            _names[new NameIdentity(table, newName)] = identity;
        }
    }

    /// <summary>Moves key column identities with an accepted column rename.</summary>
    /// <param name="table">The physical or projected table id.</param>
    /// <param name="name">The old column name.</param>
    /// <param name="newName">The accepted new column name.</param>
    /// <param name="columnId">The resolved physical column id, or zero for authored columns.</param>
    public void RenameColumn(
        int table,
        string name,
        string newName,
        int columnId = 0
    )
    {
        if (!_keys.TryGetValue(table, out var keys))
        {
            return;
        }

        foreach (var columns in keys.Values)
        {
            for (var ordinal = 0; ordinal < columns.Count; ordinal++)
            {
                if (columnId > 0 && columns[ordinal].Id == columnId || columns[ordinal].Name == name)
                {
                    columns[ordinal] = columns[ordinal] with { Name = newName };
                }
            }
        }
    }

    /// <summary>Removes every key that depends on an accepted dropped column.</summary>
    /// <param name="table">The physical or projected table id.</param>
    /// <param name="name">The accepted dropped column name.</param>
    /// <param name="columnId">The resolved physical column id, or zero for authored columns.</param>
    public void RemoveColumn(
        int table,
        string name,
        int columnId = 0
    )
    {
        if (!_keys.TryGetValue(table, out var keys))
        {
            return;
        }

        foreach (var index in keys.Where(pair => pair.Value.Any(column => column.Name == name
                || columnId > 0 && column.Id == columnId)).Select(static pair => pair.Key).ToArray())
        {
            keys.Remove(index);
        }
    }

    /// <summary>Removes a dropped table's complete bounded candidate-key state.</summary>
    /// <param name="table">The physical or projected table id.</param>
    public void RemoveTable(
        int table
    )
        => _keys.Remove(table);

    private Dictionary<int, List<KeyColumn>> GetTableKeys(
        int table
    )
    {
        if (!_keys.TryGetValue(table, out var keys))
        {
            keys = [];
            _keys.Add(table, keys);
        }

        return keys;
    }

    private readonly record struct KeyIdentity(
        int Table,
        int Index
    );

    private readonly record struct NameIdentity(
        int Table,
        string Name
    );

    private readonly record struct KeyColumn(
        string Name,
        int Id
    );
}
