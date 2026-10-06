namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    SafeMigrationProviderAnalysis ISafeMigrationProjectedKeyAnalyzer.ValidateProjectedIndex(
        EnsureIndexIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(liveAnalysis);
        ArgumentNullException.ThrowIfNull(projectedAnalysis);

        var hasStalePhysicalFailure = liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported
            && IsPhysicalIndexFailure(liveAnalysis.Code);

        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported
            && !hasStalePhysicalFailure)
        {
            // WHY: Projection may supply missing columns, but it cannot make an
            // unsupported access method, expression, or provider feature valid.
            return liveAnalysis;
        }

        if (projectedAnalysis.ObservedState is SafeMigrationObservedState.Different
            or SafeMigrationObservedState.DataBlocked)
        {
            // WHY: Structural conflicts and duplicate-row evidence precede
            // physical feasibility in the provider contract. Revalidation may
            // refine an actionable target, but it must not hide an earlier,
            // more specific blocking cause.
            return projectedAnalysis;
        }

        var result = MySqlProjectedIndexPhysicalShape.Validate(
            intent.Definition,
            columns,
            liveAnalysis.IndexPhysicalEnvironment,
            _typeMappingSource);

        // Physical feasibility is evaluated after duplicate-row evidence in the
        // immutable live plan. An Unsupported result therefore also proves that
        // a projected unique replacement is not already data-blocked.
        var feasibleAnalysis = hasStalePhysicalFailure
            ? new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None,
                postconditionSatisfied: false,
                "projected_missing")
            : projectedAnalysis;

        return result switch
        {
            // A physical rejection computed from the immutable live catalog is
            // stale after an earlier projected column change. The provider-neutral
            // projection has already proved every nonphysical prerequisite.
            MySqlProjectedIndexPhysicalShapeResult.Feasible => feasibleAnalysis,
            MySqlProjectedIndexPhysicalShapeResult.PrefixExceedsTargetColumn => Unsupported(
                "index_prefix_exceeds_target_column"),
            MySqlProjectedIndexPhysicalShapeResult.MissingRequiredPrefix => Unsupported(
                "index_prefix_required_for_key_limit"),
            MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit => Unsupported(
                "index_key_exceeds_physical_limit"),
            MySqlProjectedIndexPhysicalShapeResult.TooManyKeyParts => Unsupported(
                "index_too_many_key_parts"),
            MySqlProjectedIndexPhysicalShapeResult.UnsupportedStorageEngine => Unsupported(
                "index_storage_engine_unsupported"),
            MySqlProjectedIndexPhysicalShapeResult.Unverifiable => Unsupported(
                "index_key_length_unverifiable"),
            _ => throw new UnreachableException(),
        };
    }

    SafeMigrationProviderAnalysis ISafeMigrationProjectedKeyAnalyzer.ValidateProjectedPrimaryKey(
        EnsurePrimaryKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(liveAnalysis);
        ArgumentNullException.ThrowIfNull(projectedAnalysis);

        var result = MySqlProjectedIndexPhysicalShape.Validate(
            intent.Definition,
            columns,
            liveAnalysis.IndexPhysicalEnvironment,
            _typeMappingSource);

        return QualifyProjectedConstraint(
            result,
            liveAnalysis,
            projectedAnalysis,
            "primary_key");
    }

    SafeMigrationProviderAnalysis ISafeMigrationProjectedKeyAnalyzer.ValidateProjectedUniqueConstraint(
        EnsureUniqueConstraintIntent intent,
        ISafeMigrationProjectedColumnSource columns,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(liveAnalysis);
        ArgumentNullException.ThrowIfNull(projectedAnalysis);

        var result = MySqlProjectedIndexPhysicalShape.Validate(
            intent.Definition,
            columns,
            liveAnalysis.IndexPhysicalEnvironment,
            _typeMappingSource);

        return QualifyProjectedConstraint(
            result,
            liveAnalysis,
            projectedAnalysis,
            "unique_constraint");
    }

    private static bool IsPhysicalIndexFailure(
        string code
    ) => StringComparer.Ordinal.Equals(code, "index_prefix_required_for_key_limit")
        || StringComparer.Ordinal.Equals(code, "index_key_exceeds_physical_limit")
        || StringComparer.Ordinal.Equals(code, "index_storage_engine_unsupported")
        || StringComparer.Ordinal.Equals(code, "index_key_length_unverifiable");

    private static SafeMigrationProviderAnalysis QualifyProjectedConstraint(
        MySqlProjectedIndexPhysicalShapeResult result,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis,
        string codePrefix
    )
    {
        var hasStalePhysicalFailure = liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported
            && StringComparer.Ordinal.Equals(
                liveAnalysis.Code,
                $"{codePrefix}_exceeds_physical_limit");

        if (projectedAnalysis.ObservedState != SafeMigrationObservedState.Missing
            && !hasStalePhysicalFailure)
        {
            return projectedAnalysis;
        }

        var feasibleAnalysis = hasStalePhysicalFailure
            ? new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Missing,
                SafeMigrationRepairCapability.None,
                postconditionSatisfied: false,
                "projected_missing")
            : projectedAnalysis;

        return result switch
        {
            MySqlProjectedIndexPhysicalShapeResult.Feasible => feasibleAnalysis,
            MySqlProjectedIndexPhysicalShapeResult.KeyExceedsPhysicalLimit
                or MySqlProjectedIndexPhysicalShapeResult.MissingRequiredPrefix => Unsupported(
                    $"{codePrefix}_exceeds_physical_limit"),
            MySqlProjectedIndexPhysicalShapeResult.TooManyKeyParts => Unsupported(
                $"{codePrefix}_too_many_columns"),
            MySqlProjectedIndexPhysicalShapeResult.UnsupportedStorageEngine => Unsupported(
                $"{codePrefix}_storage_engine_unsupported"),
            MySqlProjectedIndexPhysicalShapeResult.Unverifiable
                or MySqlProjectedIndexPhysicalShapeResult.PrefixExceedsTargetColumn => Unsupported(
                    $"{codePrefix}_length_unverifiable"),
            _ => throw new UnreachableException(),
        };
    }

    private static SafeMigrationProviderAnalysis Unsupported(
        string code
    ) => new(
        SafeMigrationObservedState.Unsupported,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        code);

    private static void AttachIndexPhysicalEnvironments(
        SafeMigrationOperation[] operations,
        MySqlSafeMigrationRuntimePlan[] plans,
        IReadOnlyDictionary<string, SafeMigrationIndexPhysicalEnvironment> environments
    )
    {
        for (var ordinal = 0; ordinal < operations.Length; ordinal++)
        {
            var table = GetPhysicalKeyTable(operations[ordinal].Intent);
            if (table is not null
                && environments.TryGetValue(table, out var environment))
            {
                plans[ordinal] = plans[ordinal] with { IndexPhysicalEnvironment = environment };
            }
        }
    }

    /// <summary>Captures requested physical-key environments under packet-qualified transport bounds.</summary>
    /// <param name="connection">The open analysis connection.</param>
    /// <param name="operations">The immutable current-database operation stream.</param>
    /// <param name="maximumPayloadBytes">The qualified packet and shared payload bound.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The token that cancels construction and reading.</param>
    /// <returns>The fully captured requested environments.</returns>
    internal static async Task<IReadOnlyDictionary<string, SafeMigrationIndexPhysicalEnvironment>>
        ReadIndexPhysicalEnvironmentsAsync(
            DbConnection connection,
            IReadOnlyList<SafeMigrationOperation> operations,
            int maximumPayloadBytes,
            int? commandTimeout,
            CancellationToken cancellationToken
        )
    {
        var requestedColumns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var intent in operations.Select(static operation => operation.Intent))
        {
            var table = GetPhysicalKeyTable(intent);
            if (table is null)
            {
                continue;
            }

            if (!requestedColumns.TryGetValue(table, out var columns))
            {
                columns = new HashSet<string>(StringComparer.Ordinal);
                requestedColumns.Add(table, columns);
            }

            foreach (var column in GetPhysicalKeyColumns(intent))
            {
                columns.Add(column);
            }
        }

        var tables = requestedColumns.Keys
            .OrderBy(static table => table, StringComparer.Ordinal)
            .ToArray();

        if (tables.Length == 0)
        {
            return new Dictionary<string, SafeMigrationIndexPhysicalEnvironment>(StringComparer.Ordinal);
        }

        var environmentDefaults = await ReadDefaultIndexPhysicalEnvironmentAsync(
            connection,
            commandTimeout,
            cancellationToken);

        var collationCharacterSets = await ReadProjectedAlterCollationCharacterSetsAsync(
            connection, operations, maximumPayloadBytes, commandTimeout, cancellationToken);

        environmentDefaults = environmentDefaults with
        {
            DefaultEnvironment = environmentDefaults.DefaultEnvironment with
            {
                CollationCharacterSets = collationCharacterSets,
            },
        };

        var result = tables.ToDictionary(
            static table => table,
            _ => environmentDefaults.DefaultEnvironment,
            StringComparer.Ordinal);

        var columnShapes = tables.ToDictionary(
            static table => table,
            static _ => new Dictionary<string, SafeMigrationIndexColumnPhysicalShape>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var observedTables = new HashSet<string>(StringComparer.Ordinal);

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, (tables.Length + SafeMigrationCatalogQueryLimits.MaximumInventoryValues - 1)
                / SafeMigrationCatalogQueryLimits.MaximumInventoryValues,
            maximumPayloadBytes, commandTimeout,
            (command, slot) =>
            {
                var offset = slot * SafeMigrationCatalogQueryLimits.MaximumInventoryValues;
                var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumInventoryValues, tables.Length - offset);
                var parameterNames = new string[count];
                var valueBytes = 0;
                for (var index = 0; index < count; index++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"@table_{index.ToString(CultureInfo.InvariantCulture)}";
                    parameter.Value = tables[offset + index];
                    _ = command.Parameters.Add(parameter);
                    parameterNames[index] = parameter.ParameterName;
                    valueBytes += Encoding.UTF8.GetByteCount(tables[offset + index]) + 32;
                }

                command.CommandText = "SELECT t.TABLE_NAME, t.ENGINE, t.ROW_FORMAT, "
                    + "c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, "
                    + "c.CHARACTER_OCTET_LENGTH, c.NUMERIC_PRECISION, "
                    + "c.NUMERIC_SCALE, c.DATETIME_PRECISION "
                    + "FROM INFORMATION_SCHEMA.TABLES t "
                    + "JOIN INFORMATION_SCHEMA.COLUMNS c "
                    + "ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME "
                    + "WHERE t.TABLE_SCHEMA = DATABASE() "
                    + $"AND t.TABLE_NAME IN ({string.Join(", ", parameterNames)});";

                return new SafeMigrationCatalogProbeStatement(1, valueBytes, offset);
            },
            async (reader, slot, _, token) =>
            {
                var permitted = tables.Skip(slot * SafeMigrationCatalogQueryLimits.MaximumInventoryValues)
                    .Take(SafeMigrationCatalogQueryLimits.MaximumInventoryValues).ToHashSet(StringComparer.Ordinal);

                while (await reader.ReadAsync(token))
                {
                    var table = reader.GetString(0);
                    if (!permitted.Contains(table))
                    {
                        throw new InvalidOperationException("MySQL returned an unowned physical key table.");
                    }

                    var engine = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var rowFormat = reader.IsDBNull(2) ? null : reader.GetString(2);

                    if (observedTables.Add(table))
                    {
                        result[table] = CreateIndexPhysicalEnvironment(
                            engine,
                            rowFormat,
                            environmentDefaults.DynamicMaximumKeyBytes,
                            environmentDefaults.PageSize) with
                        {
                            DefaultCharacterSet = environmentDefaults.DefaultEnvironment.DefaultCharacterSet,
                            StrictSqlMode = environmentDefaults.DefaultEnvironment.StrictSqlMode,
                            SupportsQuotedExpressionDefaults =
                                environmentDefaults.DefaultEnvironment.SupportsQuotedExpressionDefaults,
                            SupportsTextExpressionControlCharacters =
                                environmentDefaults.DefaultEnvironment.SupportsTextExpressionControlCharacters,
                            CollationCharacterSets = environmentDefaults.DefaultEnvironment.CollationCharacterSets,
                            NewTableEnvironment = environmentDefaults.DefaultEnvironment,
                        };
                    }

                    var column = reader.GetString(3);
                    if (!requestedColumns[table].Contains(column))
                    {
                        continue;
                    }

                    if (!columnShapes[table].TryAdd(column, MySqlProjectedIndexPhysicalShape.CreateCatalogColumnShape(
                        reader.GetString(4),
                        ReadNullableInt64(reader, 5),
                        ReadNullableInt64(reader, 6),
                        ReadNullableInt32(reader, 7),
                        ReadNullableInt32(reader, 8),
                        ReadNullableInt32(reader, 9))))
                    {
                        throw new InvalidOperationException("MySQL returned a duplicate physical key column.");
                    }
                }
            },
            cancellationToken);

        foreach (var table in tables)
        {
            if (columnShapes[table].Count > 0)
            {
                result[table] = result[table] with { Columns = columnShapes[table], };
            }
        }

        return result;
    }

    private static string? GetPhysicalKeyTable(
        SafeMigrationIntent intent
    ) => intent switch
    {
        AlterColumnIntent value => value.Table,
        EnsureIndexIntent value => value.Definition.Table,
        EnsurePrimaryKeyIntent value => value.Definition.Table,
        EnsureUniqueConstraintIntent value => value.Definition.Table,
        _ => null,
    };

    private static IEnumerable<string> GetPhysicalKeyColumns(
        SafeMigrationIntent intent
    ) => intent switch
    {
        AlterColumnIntent value => [value.Definition.Name],
        EnsureIndexIntent value => value.Definition.Keys
            .Where(static key => key.Column is not null)
            .Select(static key => key.Column!),
        EnsurePrimaryKeyIntent value => value.Definition.Columns,
        EnsureUniqueConstraintIntent value => value.Definition.Columns,
        _ => [],
    };

    private static long? ReadNullableInt64(
        DbDataReader reader,
        int ordinal
    ) => reader.IsDBNull(ordinal)
        ? null
        : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int? ReadNullableInt32(
        DbDataReader reader,
        int ordinal
    ) => reader.IsDBNull(ordinal)
        ? null
        : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static async Task<IndexEnvironmentDefaults> ReadDefaultIndexPhysicalEnvironmentAsync(
        DbConnection connection,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = "SELECT @@default_storage_engine, @@innodb_default_row_format, @@innodb_page_size, "
            + "@@character_set_database, @@SESSION.sql_mode, VERSION();";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("MySQL did not return its default index environment.");
        }

        var engine = reader.IsDBNull(0) ? null : reader.GetString(0);
        var rowFormat = reader.IsDBNull(1) ? null : reader.GetString(1);
        var pageSize = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
        var dynamicMaximumKeyBytes = MaximumIndexKeyBytes(pageSize);
        var characterSet = reader.IsDBNull(3) ? null : reader.GetString(3);
        var sqlMode = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
        var strictMode = sqlMode.Split(',').Any(static mode =>
            mode.AsSpan().Trim().Equals("STRICT_TRANS_TABLES", StringComparison.OrdinalIgnoreCase)
            || mode.AsSpan().Trim().Equals("STRICT_ALL_TABLES", StringComparison.OrdinalIgnoreCase));

        var isMariaDb = reader.GetString(5).Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        var supportsQuotedExpressionDefaults = isMariaDb
            || !sqlMode.Split(',').Any(static mode =>
                mode.AsSpan().Trim().Equals("NO_BACKSLASH_ESCAPES", StringComparison.OrdinalIgnoreCase));

        return new IndexEnvironmentDefaults(
            CreateIndexPhysicalEnvironment(engine, rowFormat, dynamicMaximumKeyBytes, pageSize) with
            {
                DefaultCharacterSet = characterSet,
                StrictSqlMode = strictMode,
                SupportsQuotedExpressionDefaults = supportsQuotedExpressionDefaults,
                SupportsTextExpressionControlCharacters = !isMariaDb,
            },
            dynamicMaximumKeyBytes,
            pageSize);
    }

    private static SafeMigrationIndexPhysicalEnvironment CreateIndexPhysicalEnvironment(
        string? engine,
        string? rowFormat,
        int dynamicMaximumKeyBytes,
        int pageSize
    )
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(engine, "InnoDB"))
        {
            return new SafeMigrationIndexPhysicalEnvironment(MaximumKeyBytes: 0);
        }

        return new SafeMigrationIndexPhysicalEnvironment(
            StringComparer.OrdinalIgnoreCase.Equals(rowFormat, "Compact")
            || StringComparer.OrdinalIgnoreCase.Equals(rowFormat, "Redundant")
                ? 767
                : dynamicMaximumKeyBytes,
            StorageRowFormat: rowFormat,
            StoragePageSize: pageSize);
    }

    private static int MaximumIndexKeyBytes(
        int pageSize
    ) => pageSize switch
    {
        4096 => 768,
        8192 => 1536,
        _ => 3072,
    };

    private readonly record struct IndexEnvironmentDefaults(
        SafeMigrationIndexPhysicalEnvironment DefaultEnvironment,
        int DynamicMaximumKeyBytes,
        int PageSize);
}
