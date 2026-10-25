namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <summary>Classifies captured row transitions and independent uniqueness/dependency blockers.</summary>
    private static SafeMigrationProviderAnalysis AnalyzeModelManagedData(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table)
            || intent.KeyColumns.Any(column => !table.Columns.ContainsKey(column))
            || intent.Columns.Any(column => !table.Columns.ContainsKey(column)))
        {
            return PrerequisiteMissing();
        }

        if (intent is DeleteModelManagedDataIntent deletion)
        {
            if (deletion.ForeignKeys.Any(foreignKey =>
                    !snapshot.Tables.TryGetValue(foreignKey.Table, out var dependentTable)
                    || foreignKey.Columns.Any(column => !dependentTable.Columns.ContainsKey(column))))
            {
                return PrerequisiteMissing();
            }

            if (HasUnmodeledIncomingForeignKey(snapshot, deletion))
            {
                return Unsupported("model_managed_unmodeled_dependency");
            }
        }

        var states = ReadModelManagedRowStates(connection, transaction, intent);
        var dependencyCounts = intent is DeleteModelManagedDataIntent delete
            ? ReadModelManagedDependencyCounts(connection, transaction, delete)
            : [];

        var uniqueCollision = intent switch
        {
            EnsureModelManagedDataIntent ensure => HasModelManagedUniqueCollision(
                connection,
                transaction,
                ensure,
                ensure.Values,
                ensure.UniqueKeys),
            UpdateModelManagedDataIntent update => HasModelManagedUniqueCollision(
                connection,
                transaction,
                update,
                update.NewValues,
                update.UniqueKeys),
            DeleteModelManagedDataIntent => false,
            _ => throw new UnreachableException(),
        };

        var analysis = intent switch
        {
            EnsureModelManagedDataIntent => states.All(static state =>
                state == SafeMigrationModelManagedRowState.Target)
                ? Matching(postconditionSatisfied: true)
                : states.Any(static state => state == SafeMigrationModelManagedRowState.Different)
                    ? Different("model_managed_row", "target", "different")
                    : uniqueCollision
                        ? DataBlocked("model_managed_unique_collision")
                        : Missing(postconditionSatisfied: false),
            UpdateModelManagedDataIntent => states.Any(static state =>
                state == SafeMigrationModelManagedRowState.Missing)
                ? PrerequisiteMissing()
                : states.Any(static state => state == SafeMigrationModelManagedRowState.Different)
                    ? Different("model_managed_row", "source_or_target", "different")
                    : uniqueCollision
                        ? DataBlocked("model_managed_unique_collision")
                        : states.All(static state => state == SafeMigrationModelManagedRowState.Target)
                            ? Matching(postconditionSatisfied: true)
                            : TransitionReady(),
            DeleteModelManagedDataIntent => states.Any(static state =>
                state == SafeMigrationModelManagedRowState.Different)
                ? Different("model_managed_row", "source_or_missing", "different")
                : dependencyCounts.Any(static count => count > 0)
                    ? DataBlocked("model_managed_dependency")
                    : states.All(static state => state == SafeMigrationModelManagedRowState.Missing)
                        ? Missing(postconditionSatisfied: true)
                        : TransitionReady(),
            _ => throw new UnreachableException(),
        };

        return new SafeMigrationProviderAnalysis(
            analysis.ObservedState,
            analysis.RepairCapability,
            analysis.PostconditionSatisfied,
            analysis.Code,
            analysis.OperationalImpact,
            analysis.Differences)
        {
            ModelManagedDataEvidence = new SafeMigrationModelManagedDataEvidence(states, dependencyCounts),
        };
    }

    /// <summary>Reads model-managed row states in provider-parameter-bounded batches.</summary>
    private static SafeMigrationModelManagedRowState[] ReadModelManagedRowStates(
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent
    )
    {
        var result = new SafeMigrationModelManagedRowState[intent.RowCount];
        var parameterCountPerRow = intent.KeyColumns.Count + intent.Columns.Count;
        if (intent is UpdateModelManagedDataIntent)
        {
            parameterCountPerRow += intent.Columns.Count;
        }

        var batchSize = Math.Max(1, SqliteSafeMigrationLimits.MaximumParametersPerCommand / parameterCountPerRow);

        // WHY: Advance bounded row batches while retaining each row's original matrix offset.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var offset = 0; offset < intent.RowCount; offset += batchSize)
        {
            var count = Math.Min(batchSize, intent.RowCount - offset);
            ReadModelManagedRowStateBatch(connection, transaction, intent, offset, count, result);
        }

        return result;
    }

    /// <summary>Binds one bounded batch and preserves each captured row's result ordinal.</summary>
    private static void ReadModelManagedRowStateBatch(
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent,
        int offset,
        int count,
        SafeMigrationModelManagedRowState[] result
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var source = intent switch
        {
            UpdateModelManagedDataIntent update => update.OldValues,
            DeleteModelManagedDataIntent delete => delete.OldValues,
            EnsureModelManagedDataIntent ensure => ensure.Values,
            _ => throw new UnreachableException(),
        };

        var target = intent switch
        {
            UpdateModelManagedDataIntent update => update.NewValues,
            EnsureModelManagedDataIntent ensure => ensure.Values,
            DeleteModelManagedDataIntent => null,
            _ => throw new UnreachableException(),
        };

        var cteColumns = new List<string>(
            1
            + intent.KeyColumns.Count
            + source.ColumnCount
            + (target is null || ReferenceEquals(source, target) ? 0 : target.ColumnCount))
        {
            "ordinal",
        };

        cteColumns.AddRange(
            Enumerable
                .Range(0, intent.KeyColumns.Count)
                .Select(static index => $"key_{index}"));
        cteColumns.AddRange(
            Enumerable
                .Range(0, source.ColumnCount)
                .Select(static index => $"source_{index}"));
        if (target is not null
            && !ReferenceEquals(source, target))
        {
            cteColumns.AddRange(
                Enumerable
                    .Range(0, target.ColumnCount)
                    .Select(static index => $"target_{index}"));
        }

        var rows = new string[count];

        // WHY: Keep row selectors, declared column types and matrix values aligned by their original ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var localRow = 0; localRow < count; localRow++)
        {
            var row = offset + localRow;
            var parameters = new List<string>(cteColumns.Count)
            {
                row.ToString(CultureInfo.InvariantCulture),
            };

            // WHY: Keep row selectors, declared column types and matrix values aligned by their original ordinals.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                var name = $"$r{localRow}_k{column}";
                parameters.Add(name);
                AddParameter(command, name, intent.KeyValues.GetUnsafeValue(row, column));
            }

            // WHY: Keep row selectors, declared column types and matrix values aligned by their original ordinals.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var column = 0; column < source.ColumnCount; column++)
            {
                var name = $"$r{localRow}_s{column}";
                parameters.Add(name);
                AddParameter(command, name, source.GetUnsafeValue(row, column));
            }

            if (target is not null
                && !ReferenceEquals(source, target))
            {
                // WHY: Keep row selectors, declared column types and matrix values aligned by their original ordinals.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var column = 0; column < target.ColumnCount; column++)
                {
                    var name = $"$r{localRow}_t{column}";
                    parameters.Add(name);
                    AddParameter(command, name, target.GetUnsafeValue(row, column));
                }
            }

            rows[localRow] = "(" + string.Join(", ", parameters) + ")";
        }

        var keyPredicate = string.Join(
            " AND ",
            intent.KeyColumns.Select((
                    column,
                    index
                ) => $"stored.{Delimit(column)} IS expected.{Delimit($"key_{index}")}"));

        var sourcePredicate = string.Join(
            " AND ",
            intent.Columns.Select((
                    column,
                    index
                ) => $"stored.{Delimit(column)} IS expected.{Delimit($"source_{index}")}"));

        var targetPredicate = target is null
            ? "0"
            : ReferenceEquals(source, target)
                ? sourcePredicate
                : string.Join(
                    " AND ",
                    intent.Columns.Select((
                            column,
                            index
                        ) => $"stored.{Delimit(column)} IS expected.{Delimit($"target_{index}")}"));

        const string marker = "__doka_sm_present_8f29d0d2";
        command.CommandText = $"WITH expected ({string.Join(", ", cteColumns.Select(Delimit))}) AS ("
            + $"VALUES {string.Join(", ", rows)}) "
            + $"SELECT expected.{Delimit("ordinal")}, "
            + $"COUNT(stored.{Delimit(marker)}), "
            + $"COALESCE(SUM(CASE WHEN {sourcePredicate} THEN 1 ELSE 0 END), 0), "
            + $"COALESCE(SUM(CASE WHEN {targetPredicate} THEN 1 ELSE 0 END), 0) "
            + $"FROM expected LEFT JOIN (SELECT 1 AS {Delimit(marker)}, source.* "
            + $"FROM {Delimit(intent.Table)} AS source) AS stored ON {keyPredicate} "
            + $"GROUP BY expected.{Delimit("ordinal")} ORDER BY expected.{Delimit("ordinal")};";

        using var reader = command.ExecuteReader();
        var observed = 0;
        while (reader.Read())
        {
            var row = reader.GetInt32(0);
            var matches = reader.GetInt32(1);
            var sourceMatches = reader.GetInt32(2);
            var targetMatches = reader.GetInt32(3);
            result[row] = matches switch
            {
                0 => SafeMigrationModelManagedRowState.Missing,
                > 1 => SafeMigrationModelManagedRowState.Different,
                _ when targetMatches == 1 => SafeMigrationModelManagedRowState.Target,
                _ when sourceMatches == 1 => SafeMigrationModelManagedRowState.Source,
                _ => SafeMigrationModelManagedRowState.Different,
            };

            observed++;
        }

        if (observed != count)
        {
            throw new InvalidOperationException("SQLite returned an incomplete model-managed data analysis batch.");
        }
    }

    /// <summary>Checks each modeled unique key without exceeding the provider parameter bound.</summary>
    private static bool HasModelManagedUniqueCollision(
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent,
        ModelManagedDataMatrix targetValues,
        IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> uniqueKeys
    )
    {
        foreach (var uniqueKey in uniqueKeys)
        {
            var uniqueOrdinals = uniqueKey
                .Columns
                .Select(column => IndexOf(intent.Columns, column))
                .ToArray();

            var parameterCountPerRow = intent.KeyColumns.Count + uniqueOrdinals.Length;
            var batchSize = Math.Max(1, SqliteSafeMigrationLimits.MaximumParametersPerCommand / parameterCountPerRow);

            // WHY: Advance bounded row batches while retaining each row's original matrix offset.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var offset = 0; offset < intent.RowCount; offset += batchSize)
            {
                var count = Math.Min(batchSize, intent.RowCount - offset);
                if (HasModelManagedUniqueCollisionBatch(
                        connection,
                        transaction,
                        intent,
                        targetValues,
                        uniqueKey,
                        uniqueOrdinals,
                        offset,
                        count))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Checks one bounded batch for collisions outside the same captured primary key.</summary>
    private static bool HasModelManagedUniqueCollisionBatch(
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent,
        ModelManagedDataMatrix targetValues,
        ExpectedModelManagedDataUniqueKeyDefinition uniqueKey,
        int[] uniqueOrdinals,
        int offset,
        int count
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var cteColumns = Enumerable
            .Range(0, intent.KeyColumns.Count)
            .Select(static index => $"key_{index}")
            .Concat(
                Enumerable
                    .Range(0, uniqueKey.Columns.Count)
                    .Select(static index => $"unique_{index}"))
            .ToArray();

        var rows = new string[count];

        // WHY: Keep row selectors, declared column types and matrix values aligned by their original ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var localRow = 0; localRow < count; localRow++)
        {
            var row = offset + localRow;
            var parameters = new string[cteColumns.Length];
            var parameterIndex = 0;

            // WHY: Keep row selectors, declared column types and matrix values aligned by their original ordinals.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                var name = $"$r{localRow}_k{column}";
                parameters[parameterIndex++] = name;
                AddParameter(command, name, intent.KeyValues.GetUnsafeValue(row, column));
            }

            // WHY: Keep row selectors, declared column types and matrix values aligned by their original ordinals.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var column = 0; column < uniqueOrdinals.Length; column++)
            {
                var name = $"$r{localRow}_u{column}";
                parameters[parameterIndex++] = name;
                AddParameter(command, name, targetValues.GetUnsafeValue(row, uniqueOrdinals[column]));
            }

            rows[localRow] = "(" + string.Join(", ", parameters) + ")";
        }

        var uniqueMatch = string.Join(
            " AND ",
            uniqueKey.Columns.Select((
                    column,
                    index
                ) => $"stored.{Delimit(column)} IS incoming.{Delimit($"unique_{index}")}"));

        var nonNullTarget = string.Join(
            " AND ",
            uniqueKey.Columns.Select((
                    _,
                    index
                ) => $"incoming.{Delimit($"unique_{index}")} IS NOT NULL"));

        var samePrimaryKey = string.Join(
            " AND ",
            intent.KeyColumns.Select((
                    column,
                    index
                ) => $"stored.{Delimit(column)} IS incoming.{Delimit($"key_{index}")}"));

        command.CommandText = $"WITH incoming ({string.Join(", ", cteColumns.Select(Delimit))}) AS ("
            + $"VALUES {string.Join(", ", rows)}) SELECT 1 FROM incoming "
            + $"JOIN {Delimit(intent.Table)} AS stored ON {uniqueMatch} "
            + $"WHERE {nonNullTarget} AND NOT ({samePrimaryKey}) LIMIT 1;";

        return command.ExecuteScalar() is not null;
    }

    /// <summary>Reads each modeled incoming dependency count in bounded batches.</summary>
    private static long[] ReadModelManagedDependencyCounts(
        DbConnection connection,
        DbTransaction? transaction,
        DeleteModelManagedDataIntent intent
    )
    {
        var result = new long[intent.ForeignKeys.Count];

        // WHY: Keep FK positions and bounded row windows tied to the same authored dependency slots.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var foreignKeyIndex = 0; foreignKeyIndex < intent.ForeignKeys.Count; foreignKeyIndex++)
        {
            var foreignKey = intent.ForeignKeys[foreignKeyIndex];
            var principalOrdinals = foreignKey
                .PrincipalColumns
                .Select(column => IndexOf(intent.Columns, column))
                .ToArray();

            var parameterCountPerRow = principalOrdinals.Length;
            var batchSize = Math.Max(1, SqliteSafeMigrationLimits.MaximumParametersPerCommand / parameterCountPerRow);

            // WHY: Keep FK positions and bounded row windows tied to the same authored dependency slots.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var offset = 0; offset < intent.RowCount; offset += batchSize)
            {
                var count = Math.Min(batchSize, intent.RowCount - offset);
                result[foreignKeyIndex] += ReadModelManagedDependencyCountBatch(
                    connection,
                    transaction,
                    intent,
                    foreignKey,
                    principalOrdinals,
                    offset,
                    count);
            }
        }

        return result;
    }

    /// <summary>Counts retained dependent rows for one bounded principal-value batch.</summary>
    private static long ReadModelManagedDependencyCountBatch(
        DbConnection connection,
        DbTransaction? transaction,
        DeleteModelManagedDataIntent intent,
        ExpectedModelManagedDataForeignKeyDefinition foreignKey,
        int[] principalOrdinals,
        int offset,
        int count
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var cteColumns = Enumerable
            .Range(0, principalOrdinals.Length)
            .Select(static index => $"principal_{index}")
            .ToArray();

        var rows = new string[count];

        // WHY: Keep row selectors, declared column types and matrix values aligned by their original ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var localRow = 0; localRow < count; localRow++)
        {
            var row = offset + localRow;
            var parameters = new string[principalOrdinals.Length];

            // WHY: Keep row selectors, declared column types and matrix values aligned by their original ordinals.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var column = 0; column < principalOrdinals.Length; column++)
            {
                var name = $"$r{localRow}_p{column}";
                parameters[column] = name;
                AddParameter(command, name, intent.OldValues.GetUnsafeValue(row, principalOrdinals[column]));
            }

            rows[localRow] = "(" + string.Join(", ", parameters) + ")";
        }

        var dependencyMatch = string.Join(
            " AND ",
            foreignKey.Columns.Select((
                    column,
                    index
                ) => $"dependent.{Delimit(column)} IS incoming.{Delimit($"principal_{index}")}"));

        command.CommandText = $"WITH incoming ({string.Join(", ", cteColumns.Select(Delimit))}) AS ("
            + $"VALUES {string.Join(", ", rows)}) SELECT COUNT(*) FROM incoming "
            + $"JOIN {Delimit(foreignKey.Table)} AS dependent ON {dependencyMatch};";

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Detects incoming relationships absent from the captured deletion contract.</summary>
    private static bool HasUnmodeledIncomingForeignKey(
        SqliteCatalogSnapshot snapshot,
        DeleteModelManagedDataIntent intent
    )
    {
        foreach (var dependentTable in snapshot.Tables.Values)
        {
            foreach (var foreignKey in dependentTable.ForeignKeys)
            {
                if (!s_identifierComparer.Equals(foreignKey.PrincipalTable, intent.Table))
                {
                    continue;
                }

                var principalColumns = foreignKey.PrincipalColumns.Any(static column => column.Length > 0)
                    ? foreignKey.PrincipalColumns
                    : snapshot.Tables.TryGetValue(intent.Table, out var principalTable)
                        ? principalTable.PrimaryKeyColumns
                        : foreignKey.PrincipalColumns;

                var modeled = intent.ForeignKeys.Any(expected =>
                    s_identifierComparer.Equals(expected.Table, dependentTable.Name)
                    && IdentifiersEqual(expected.Columns, foreignKey.Columns)
                    && IdentifiersEqual(expected.PrincipalColumns, principalColumns));

                if (!modeled)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Resolves captured value ordinals without silently accepting missing metadata.</summary>
    private static int IndexOf(
        IReadOnlyList<string> columns,
        string column
    )
    {
        // WHY: Return the exact column ordinal needed to address the matching matrix value.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < columns.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(columns[index], column))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            $"Model-managed metadata references column '{column}' outside the captured value set.");
    }

    /// <summary>Binds the captured CLR value using the caller's command parameter ownership.</summary>
    private static void AddParameter(
        DbCommand command,
        string name,
        object? value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }
}
