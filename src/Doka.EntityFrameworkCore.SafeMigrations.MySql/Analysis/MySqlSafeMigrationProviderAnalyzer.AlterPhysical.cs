namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    private readonly Dictionary<SafeMigrationProviderAnalysis, MySqlProjectedAlterPhysicalSnapshot>
        _projectedAlterPhysicalSnapshots = new(ReferenceEqualityComparer.Instance);

    /// <summary>Checks cumulative physical capacity against the exact source-bound analysis.</summary>
    private bool HasSafeCumulativeAlterPhysicalShape(
        AlterColumnIntent intent,
        ISafeMigrationProjectedAlterTable table,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => _projectedAlterPhysicalSnapshots.TryGetValue(liveAnalysis, out var snapshot)
        && snapshot.IsSafe(intent, table, _typeMappingSource);

    /// <summary>
    /// Proves a later target from a certified unchanged original domain and cumulative physical shape.
    /// </summary>
    private bool HasSafeRetainedAlterValueDomain(
        AlterColumnIntent intent,
        ExpectedColumnDefinition source,
        ISafeMigrationProjectedAlterTable table,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => _projectedAlterPhysicalSnapshots.TryGetValue(liveAnalysis, out var snapshot)
        && snapshot.PreservesOriginalValueDomain(intent, source)
        && snapshot.IsSafe(intent, table, _typeMappingSource);

    /// <summary>Binds catalog shapes to analysis objects rather than inferred rename aliases.</summary>
    private void AttachProjectedAlterPhysicalSnapshots(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses,
        IReadOnlyDictionary<string, MySqlProjectedAlterPhysicalSnapshot> snapshots
    )
    {
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index].Intent is AlterColumnIntent intent
                && snapshots.TryGetValue(intent.Table, out var snapshot))
            {
                _projectedAlterPhysicalSnapshots[analyses[index]] = snapshot;
            }
        }
    }

    /// <summary>Captures row contributors and index parts once per table needing type-transition projection.</summary>
    /// <param name="connection">The open connection that owns this analysis snapshot.</param>
    /// <param name="operations">The ordered operations requiring source-bound physical evidence.</param>
    /// <param name="environments">The physical table environments captured on the same connection.</param>
    /// <param name="maximumPayloadBytes">The maximum encoded transport payload in bytes.</param>
    /// <param name="commandTimeout">The command timeout in seconds, or null for the connection default.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Only existing tables with complete owned column and index result sets.</returns>
    /// <remarks>No table data is queried; statement and parameter payloads use the shared bounded transport.</remarks>
    internal static async Task<IReadOnlyDictionary<string, MySqlProjectedAlterPhysicalSnapshot>>
        ReadProjectedAlterPhysicalSnapshotsAsync(
            DbConnection connection,
            IReadOnlyList<SafeMigrationOperation> operations,
            IReadOnlyDictionary<string, SafeMigrationIndexPhysicalEnvironment> environments,
            int maximumPayloadBytes,
            int? commandTimeout,
            CancellationToken cancellationToken
        )
    {
        var tables = operations
            .Where(static operation => operation.Intent is AlterColumnIntent alter
                && alter.OldDefinition is not null
                && (!StringComparer.OrdinalIgnoreCase.Equals(alter.OldDefinition.StoreType, alter.Definition.StoreType)
                    || alter.OldDefinition.MaxLength != alter.Definition.MaxLength))
            .Select(static operation => ((AlterColumnIntent)operation.Intent).Table)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static table => table, StringComparer.Ordinal)
            .ToArray();

        if (tables.Length == 0)
        {
            return new Dictionary<string, MySqlProjectedAlterPhysicalSnapshot>(StringComparer.Ordinal);
        }

        var columns = tables.ToDictionary(
            static table => table,
            static _ => new Dictionary<string, MySqlAlterColumnPhysicalShape>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var indexes = tables.ToDictionary(
            static table => table,
            static _ => new Dictionary<string, AlterPhysicalIndexBuilder>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var windows = (tables.Length + SafeMigrationCatalogQueryLimits.MaximumInventoryValues - 1)
            / SafeMigrationCatalogQueryLimits.MaximumInventoryValues;

        // WHY: Two catalog queries per bounded window avoid one row/index query
        // per Alter. Keeping the result sets separate avoids multiplying every
        // column by every index part on wide brownfield tables.
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, windows * 2, maximumPayloadBytes, commandTimeout,
            (command, slot) =>
            {
                var offset = (slot / 2) * SafeMigrationCatalogQueryLimits.MaximumInventoryValues;
                var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumInventoryValues, tables.Length - offset);
                var names = new string[count];
                var valueBytes = 0;
                for (var index = 0; index < count; index++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"@alter_table_{index.ToString(CultureInfo.InvariantCulture)}";
                    parameter.Value = tables[offset + index];
                    _ = command.Parameters.Add(parameter);
                    names[index] = parameter.ParameterName;
                    valueBytes += Encoding.UTF8.GetByteCount(tables[offset + index]) + 32;
                }

                var requested = string.Join(", ", names);
                command.CommandText = slot % 2 == 0
                    ? "SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, "
                        + "c.CHARACTER_MAXIMUM_LENGTH, c.CHARACTER_OCTET_LENGTH, "
                        + "c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.DATETIME_PRECISION, "
                        + "COALESCE(cs.MAXLEN, 1), c.COLLATION_NAME, c.CHARACTER_SET_NAME, "
                        + "(fk.COLUMN_NAME IS NOT NULL) FROM INFORMATION_SCHEMA.COLUMNS c "
                        + "LEFT JOIN INFORMATION_SCHEMA.CHARACTER_SETS cs "
                        + "ON cs.CHARACTER_SET_NAME = c.CHARACTER_SET_NAME "
                        + "LEFT JOIN (SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE "
                        + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ({requested}) "
                        + "AND REFERENCED_TABLE_NAME IS NOT NULL UNION "
                        + "SELECT REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME "
                        + "FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE "
                        + $"WHERE REFERENCED_TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IN ({requested})) fk "
                        + "ON fk.TABLE_NAME = c.TABLE_NAME AND fk.COLUMN_NAME = c.COLUMN_NAME "
                        + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME IN ({requested});"
                    : "SELECT s.TABLE_NAME, s.INDEX_NAME, s.INDEX_TYPE, s.SEQ_IN_INDEX, "
                        + "s.COLUMN_NAME, s.SUB_PART, s.NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS s "
                        + $"WHERE s.TABLE_SCHEMA = DATABASE() AND s.TABLE_NAME IN ({requested}) "
                        + "ORDER BY s.TABLE_NAME, s.INDEX_NAME, s.SEQ_IN_INDEX;";

                return new SafeMigrationCatalogProbeStatement(1, valueBytes, slot);
            },
            async (reader, slot, _, token) =>
            {
                var offset = (slot / 2) * SafeMigrationCatalogQueryLimits.MaximumInventoryValues;
                var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumInventoryValues, tables.Length - offset);
                while (await reader.ReadAsync(token))
                {
                    var table = reader.GetString(0);
                    if (Array.BinarySearch(tables, offset, count, table, StringComparer.Ordinal) < 0)
                    {
                        throw new InvalidOperationException("MySQL returned an unowned alter physical table.");
                    }

                    if (slot % 2 == 0)
                    {
                        var shape = MySqlProjectedAlterPhysicalSnapshot.CreateCatalogColumn(
                            reader.GetString(2), StringComparer.Ordinal.Equals(reader.GetString(3), "YES"),
                            ReadNullableInt64(reader, 4), ReadNullableInt64(reader, 5),
                            ReadNullableInt32(reader, 6), ReadNullableInt32(reader, 7),
                            ReadNullableInt32(reader, 8),
                            Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture),
                            reader.IsDBNull(10) ? null : reader.GetString(10),
                            reader.IsDBNull(11) ? null : reader.GetString(11),
                            Convert.ToBoolean(reader.GetValue(12), CultureInfo.InvariantCulture));

                        if (!columns[table].TryAdd(reader.GetString(1), shape))
                        {
                            throw new InvalidOperationException("MySQL returned a duplicate alter physical column.");
                        }

                        continue;
                    }

                    var name = reader.GetString(1);
                    var fullText = StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(2), "FULLTEXT");
                    var unique = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture) == 0;
                    if (!indexes[table].TryGetValue(name, out var index))
                    {
                        index = new AlterPhysicalIndexBuilder(fullText, unique);
                        indexes[table].Add(name, index);
                    }

                    if (index.IsFullText != fullText
                        || index.IsUnique != unique
                        || Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) != index.Parts.Count + 1)
                    {
                        throw new InvalidOperationException("MySQL returned inconsistent alter physical index parts.");
                    }

                    index.Parts.Add(new MySqlAlterIndexPart(
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        ReadNullableInt32(reader, 5)));
                }
            },
            cancellationToken);

        var result = new Dictionary<string, MySqlProjectedAlterPhysicalSnapshot>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            // A missing table is not a verified empty physical snapshot. Core
            // validates newly created tables through its complete-table path.
            if (columns[table].Count == 0
                || !environments.TryGetValue(table, out var environment))
            {
                continue;
            }

            result.Add(table, new MySqlProjectedAlterPhysicalSnapshot(
                columns[table],
                indexes[table].Select(static index => new MySqlAlterIndexPhysicalShape(
                    index.Value.IsFullText, index.Value.Parts,
                    StringComparer.Ordinal.Equals(index.Key, "PRIMARY"), index.Value.IsUnique)).ToArray(),
                environment));
        }

        return result;
    }

    private sealed class AlterPhysicalIndexBuilder(
        bool isFullText,
        bool isUnique
    )
    {
        internal bool IsFullText { get; } = isFullText;

        internal bool IsUnique { get; } = isUnique;

        internal List<MySqlAlterIndexPart> Parts { get; } = [];
    }
}
