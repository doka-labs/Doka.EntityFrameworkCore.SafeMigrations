namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Identifies dependency transport independently of data scans and other catalog phases.</summary>
    internal const string ColumnDependencyQueryMarker = "/* doka_sm_column_dependencies */";

    /// <summary>Deduplicates dependency requests without merging their operation-reference lifetimes.</summary>
    /// <param name="operations">The immutable source operations.</param>
    /// <param name="analyses">Their independently qualified source and row results.</param>
    /// <param name="currentDatabase">The verified selected database.</param>
    /// <returns>Unique requests and additional reference-distinct consumers of each captured fact.</returns>
    internal static (IReadOnlyList<MySqlColumnDependencyRequest> Requests,
        IReadOnlyDictionary<int, HashSet<SafeMigrationOperation>>? Aliases) CreateColumnDependencyRequests(
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses,
        string? currentDatabase
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(analyses);
        if (operations.Count != analyses.Count)
        {
            throw new ArgumentException("The column dependency source results do not match the operation count.");
        }

        Dictionary<(string? Schema, string Table, string Column, bool Nullable), int>? identities = null;
        List<MySqlColumnDependencyRequest>? requests = null;
        Dictionary<int, HashSet<SafeMigrationOperation>>? aliases = null;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var operation = operations[ordinal];
            if (analyses[ordinal].ObservedState != SafeMigrationObservedState.Different
                || analyses[ordinal].RepairCapability != SafeMigrationRepairCapability.Safe
                || !TryGetColumnDependencyTarget(operation.Intent, out var table, out var schema, out var column)
                || !TargetsCurrentDatabase(operation.Intent, currentDatabase))
            {
                continue;
            }

            var key = (MySqlTableIdentity.NormalizeDatabase(schema, currentDatabase), table, column.Name,
                column.IsNullable);

            identities ??= new(ColumnDependencyIdentityComparer.Instance);
            if (identities.TryGetValue(key, out var existing))
            {
                if (!ReferenceEquals(requests![existing].Operation, operation))
                {
                    aliases ??= [];
                    if (!aliases.TryGetValue(existing, out var consumers))
                    {
                        aliases.Add(existing, consumers = new(ReferenceEqualityComparer.Instance));
                    }

                    consumers.Add(operation);
                }

                continue;
            }

            requests ??= [];
            identities.Add(key, requests.Count);
            requests.Add(new(operation, table, column.Name, column.IsNullable, ordinal));
        }

        return (requests is null ? [] : requests, aliases);
    }

    /// <summary>Reads bounded dependency facts, including an explicit absence sentinel for every request.</summary>
    /// <param name="connection">The open provider or compatible wrapper connection.</param>
    /// <param name="requests">The deduplicated current-database requests.</param>
    /// <param name="maximumPayloadBytes">The qualified aggregate UTF-8 transport bound.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="observe">Consumes complete facts only after a statement has passed every shape check.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A task completed when every requested sentinel and dependency has been validated.</returns>
    internal static async Task ReadColumnDependenciesAsync(
        DbConnection connection,
        IReadOnlyList<MySqlColumnDependencyRequest> requests,
        int maximumPayloadBytes,
        int? commandTimeout,
        Action<int, bool, IReadOnlySet<string>?> observe,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(observe);

        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var fixedBytes = Encoding.UTF8.GetByteCount(BuildColumnDependencyQuery(string.Empty));
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, requests.Count, maximumPayloadBytes, commandTimeout,
            (command, offset) =>
            {
                var parameters = new MySqlCatalogQueryParameterizer(command);
                var sources = new List<string>(Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, requests.Count - offset));

                var sourceBytes = 0;
                while (offset + sources.Count < requests.Count
                    && sources.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var ordinal = offset + sources.Count;
                    var request = requests[ordinal];
                    var checkpoint = parameters.Capture();
                    var source = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)} AS request_ordinal, "
                        + $"{parameters.AddString(request.Table)} AS table_name, "
                        + $"{parameters.AddString(request.Column)} AS column_name, "
                        + $"{(request.Nullable ? "TRUE" : "FALSE")} AS target_nullable";

                    var addedBytes = Encoding.UTF8.GetByteCount(source)
                        + (sources.Count == 0 ? 0 : separatorBytes);

                    var payload = checked(fixedBytes + 3 * (sourceBytes + addedBytes) + parameters.Utf8PayloadBytes);
                    if (SafeMigrationCatalogQueryLimits.Exceeded(parameters.Count, payload, maximumPayloadBytes))
                    {
                        var parameterCount = parameters.Count;
                        parameters.Rollback(checkpoint);
                        if (sources.Count == 0)
                        {
                            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                request.Ordinal, parameterCount, payload);
                        }

                        break;
                    }

                    sources.Add(source);
                    sourceBytes += addedBytes;
                }

                command.CommandText = BuildColumnDependencyQuery(
                    string.Join(SafeMigrationCatalogQueryLimits.Separator, sources));

                return new SafeMigrationCatalogProbeStatement(
                    sources.Count, parameters.Utf8PayloadBytes, requests[offset].Ordinal);
            },
            (reader, offset, count, token) => ReadColumnDependencyStatementAsync(
                reader, requests, offset, count, observe, token),
            cancellationToken);
    }

    /// <summary>Builds one tagged catalog-only request set without truncating or encoding physical FK names.</summary>
    /// <param name="source">The trusted bounded request SELECT with bound owner and column parameters.</param>
    /// <returns>Sentinel, PRIMARY and SET NULL rows ordered by their exact request identity.</returns>
    internal static string BuildColumnDependencyQuery(
        string source
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        var primaryOwner = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
            "primary_key.TABLE_NAME", "request.table_name");
        var foreignOwner = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
            "rc.TABLE_NAME", "request.table_name");
        var primarySchema = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
            "primary_key.CONSTRAINT_SCHEMA", "DATABASE()");
        var foreignSchema = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
            "rc.CONSTRAINT_SCHEMA", "DATABASE()");

        // WHY: Sentinels prove absence rather than inferring it from a missing
        // result. Both dependency roles share one bounded source window, while
        // actual FK names remain complete separate rows, never concatenations.
        return ColumnDependencyQueryMarker + "\nSELECT request.request_ordinal, 0 AS proof_kind, "
            + "request.target_nullable, NULL AS primary_member, NULL AS foreign_key_name "
            + $"FROM ({source}) request\nUNION ALL\n"
            + "SELECT request.request_ordinal, 1, TRUE, COUNT(primary_column.COLUMN_NAME) > 0, NULL "
            + $"FROM ({source}) request LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS primary_key "
            + "ON primary_key.CONSTRAINT_SCHEMA = DATABASE() "
            + $"AND {primarySchema} AND primary_key.TABLE_NAME = request.table_name AND {primaryOwner} "
            + "AND primary_key.CONSTRAINT_TYPE = 'PRIMARY KEY' "
            + "LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE primary_column "
            + "ON CAST(primary_column.CONSTRAINT_SCHEMA AS BINARY) = CAST(primary_key.CONSTRAINT_SCHEMA AS BINARY) "
            + "AND CAST(primary_column.TABLE_NAME AS BINARY) = CAST(primary_key.TABLE_NAME AS BINARY) "
            + "AND primary_column.CONSTRAINT_NAME = primary_key.CONSTRAINT_NAME "
            + "AND primary_column.COLUMN_NAME = request.column_name "
            + "WHERE request.target_nullable = TRUE GROUP BY request.request_ordinal\nUNION ALL\n"
            + "SELECT DISTINCT request.request_ordinal, 2, FALSE, NULL, rc.CONSTRAINT_NAME "
            + $"FROM ({source}) request JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc "
            + $"ON rc.CONSTRAINT_SCHEMA = DATABASE() AND {foreignSchema} "
            + $"AND rc.TABLE_NAME = request.table_name AND {foreignOwner} "
            + "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE local_key "
            + "ON CAST(local_key.CONSTRAINT_SCHEMA AS BINARY) = CAST(rc.CONSTRAINT_SCHEMA AS BINARY) "
            + "AND CAST(local_key.TABLE_NAME AS BINARY) = CAST(rc.TABLE_NAME AS BINARY) "
            + "AND local_key.CONSTRAINT_NAME = rc.CONSTRAINT_NAME "
            + "AND local_key.COLUMN_NAME = request.column_name "
            + "WHERE request.target_nullable = FALSE "
            + "AND (rc.DELETE_RULE = 'SET NULL' OR rc.UPDATE_RULE = 'SET NULL')\nORDER BY 1, 2, 5;";
    }

    /// <summary>Validates a bounded statement before publishing its shared read-only dependency facts.</summary>
    /// <param name="reader">The current statement's native result reader.</param>
    /// <param name="requests">The immutable source request stream.</param>
    /// <param name="offset">The first request owned by this statement.</param>
    /// <param name="count">The statement's bounded request count.</param>
    /// <param name="observe">Receives only completed immutable facts.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A task completed after exact-width, identity, role and completeness validation.</returns>
    private static async Task ReadColumnDependencyStatementAsync(
        DbDataReader reader,
        IReadOnlyList<MySqlColumnDependencyRequest> requests,
        int offset,
        int count,
        Action<int, bool, IReadOnlySet<string>?> observe,
        CancellationToken cancellationToken
    )
    {
        if (reader.FieldCount != 5)
        {
            throw InvalidColumnDependencyResult();
        }

        var sentinels = new bool[count];
        var primarySeen = new bool[count];
        var primary = new bool[count];
        var names = new HashSet<string>?[count];
        var previous = offset;
        while (await reader.ReadAsync(cancellationToken))
        {
            var ordinal = reader.GetInt32(0);
            if (ordinal < offset || ordinal >= offset + count || ordinal < previous)
            {
                throw InvalidColumnDependencyResult();
            }

            previous = ordinal;
            var index = ordinal - offset;
            var nullable = ReadColumnDependencyBoolean(reader, 2);
            if (nullable != requests[ordinal].Nullable)
            {
                throw InvalidColumnDependencyResult();
            }

            switch (reader.GetInt32(1))
            {
                case 0:
                    if (sentinels[index] || !reader.IsDBNull(3) || !reader.IsDBNull(4))
                    {
                        throw InvalidColumnDependencyResult();
                    }

                    sentinels[index] = true;
                    break;
                case 1:
                    if (!sentinels[index] || !nullable || primarySeen[index] || !reader.IsDBNull(4))
                    {
                        throw InvalidColumnDependencyResult();
                    }

                    primary[index] = ReadColumnDependencyBoolean(reader, 3);
                    primarySeen[index] = true;
                    break;
                case 2:
                    if (!sentinels[index] || nullable || !reader.IsDBNull(3) || reader.IsDBNull(4))
                    {
                        throw InvalidColumnDependencyResult();
                    }

                    var name = reader.GetString(4);
                    if (string.IsNullOrWhiteSpace(name)
                        || !(names[index] ??= new(StringComparer.OrdinalIgnoreCase)).Add(name))
                    {
                        throw InvalidColumnDependencyResult();
                    }

                    break;
                default:
                    throw InvalidColumnDependencyResult();
            }
        }

        for (var index = 0; index < count; index++)
        {
            if (!sentinels[index] || primarySeen[index] != requests[offset + index].Nullable)
            {
                throw InvalidColumnDependencyResult();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        for (var index = 0; index < count; index++)
        {
            var completed = names[index] is { } captured
                ? System.Collections.Frozen.FrozenSet.ToFrozenSet(captured, StringComparer.OrdinalIgnoreCase)
                : null;

            observe(offset + index, primary[index], completed);
        }
    }

    /// <summary>Accepts only native integral zero/one or boolean metadata, never truthy arbitrary values.</summary>
    /// <param name="reader">The current statement reader.</param>
    /// <param name="ordinal">The boolean result field.</param>
    /// <returns>The exact native boolean.</returns>
    private static bool ReadColumnDependencyBoolean(
        DbDataReader reader,
        int ordinal
    )
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool boolean => boolean,
            sbyte number when number is 0 or 1 => number == 1,
            byte number when number is 0 or 1 => number == 1,
            short number when number is 0 or 1 => number == 1,
            ushort number when number is 0 or 1 => number == 1,
            int number when number is 0 or 1 => number == 1,
            uint number when number is 0 or 1 => number == 1,
            long number when number is 0 or 1 => number == 1,
            ulong number when number is 0 or 1 => number == 1,
            _ => throw InvalidColumnDependencyResult(),
        };
    }

    /// <summary>Rejects malformed transport evidence without exposing physical names or data.</summary>
    /// <returns>The privacy-safe protocol exception.</returns>
    private static InvalidOperationException InvalidColumnDependencyResult()
        => new("The MySQL column dependency query returned malformed or incomplete evidence.");

    /// <summary>Adds one complete dependency fact under an immutable operation-reference lifetime.</summary>
    /// <param name="operation">The original source-bound consumer.</param>
    /// <param name="hasPrimaryKey">Whether its nullable target is an original PRIMARY member.</param>
    /// <param name="names">The finalized immutable SET NULL names, or null when absent.</param>
    /// <param name="primary">The unpublished PRIMARY consumer set.</param>
    /// <param name="setNull">The unpublished SET NULL consumer map.</param>
    private static void AddColumnDependencyFact(
        SafeMigrationOperation operation,
        bool hasPrimaryKey,
        IReadOnlySet<string>? names,
        ref HashSet<SafeMigrationOperation>? primary,
        ref Dictionary<SafeMigrationOperation, IReadOnlySet<string>>? setNull
    )
    {
        if (hasPrimaryKey)
        {
            (primary ??= new(ReferenceEqualityComparer.Instance)).Add(operation);
        }

        if (names is { Count: > 0 })
        {
            (setNull ??= new(ReferenceEqualityComparer.Instance)).Add(operation, names);
        }
    }

    /// <summary>Preserves raw table identity and case-insensitive members during dependency deduplication.</summary>
    private sealed class ColumnDependencyIdentityComparer
        : IEqualityComparer<(string? Schema, string Table, string Column, bool Nullable)>
    {
        /// <summary>Gets the shared stateless comparer.</summary>
        public static ColumnDependencyIdentityComparer Instance { get; } = new();

        /// <inheritdoc />
        public bool Equals(
            (string? Schema, string Table, string Column, bool Nullable) x,
            (string? Schema, string Table, string Column, bool Nullable) y
        )
            => StringComparer.Ordinal.Equals(x.Schema, y.Schema)
                && StringComparer.Ordinal.Equals(x.Table, y.Table)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Column, y.Column)
                && x.Nullable == y.Nullable;

        /// <inheritdoc />
        public int GetHashCode(
            (string? Schema, string Table, string Column, bool Nullable) value
        )
            => HashCode.Combine(value.Schema is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Schema),
                StringComparer.Ordinal.GetHashCode(value.Table),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Column), value.Nullable);
    }
}

/// <summary>Retains one source-bound operation while requesting its shared physical column dependencies.</summary>
/// <param name="Operation">The original reference-bound source operation.</param>
/// <param name="Table">The untouched authored owner.</param>
/// <param name="Column">The source-proven target column.</param>
/// <param name="Nullable">Whether PRIMARY membership rather than SET NULL actions must be captured.</param>
/// <param name="Ordinal">The original operation ordinal used for bounded-query diagnostics.</param>
internal readonly record struct MySqlColumnDependencyRequest(
    SafeMigrationOperation Operation,
    string Table,
    string Column,
    bool Nullable,
    int Ordinal
);
