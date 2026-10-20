namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Captures member destination occupancy independently of immutable missing-source replay state.</summary>
    /// <param name="connection">The already-open and database-qualified analysis connection.</param>
    /// <param name="operations">The immutable ordered safe operation stream.</param>
    /// <param name="results">The complete live classifications, enriched only after complete capture.</param>
    /// <param name="currentDatabase">The verified selected physical database.</param>
    /// <param name="maximumPayloadBytes">The server-qualified bounded transport payload.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="cancellationToken">The token cancelling construction, transport and reading.</param>
    /// <returns>A task completed after every submitted destination has been validated.</returns>
    internal static async Task ReadMemberRenameTargetEvidenceAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] results,
        string? currentDatabase,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<int>? candidates = null;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal].Intent is RenameColumnIntent or RenameIndexIntent
                && (results[ordinal].ObservedState is not (SafeMigrationObservedState.Unsupported
                        or SafeMigrationObservedState.PrerequisiteMissing)
                    || (!results[ordinal].IsInvariantUnsupported
                        && IsGeneratedDependencyRefusal(results[ordinal].Code)))
                && TargetsCurrentDatabase(operations[ordinal].Intent, currentDatabase))
            {
                (candidates ??= []).Add(ordinal);
            }
        }

        if (candidates is null)
        {
            return;
        }

        var targets = new (bool Target, bool? SourceSupported)[candidates.Count];
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);

        // WHY: Source Missing can coexist with an occupied destination. Earlier
        // accepted creation changes only the source, not this independent fact.
        // Native owner casing excludes same-collation table shadows, while
        // column/index member names retain their native case-insensitive rules.
        await SafeMigrationCatalogProbeBatch.ReadAsync(connection, candidates.Count, maximumPayloadBytes,
            commandTimeout,
            (command, offset) =>
            {
                var parameters = new MySqlCatalogQueryParameterizer(command);
                var selections = new List<string>(Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, candidates.Count - offset));

                var sqlBytes = trailerBytes;
                while (offset + selections.Count < candidates.Count
                    && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var ordinal = candidates[offset + selections.Count];
                    var checkpoint = parameters.Capture();
                    var expression = BuildMemberRenameTargetExpression(
                        operations[ordinal].Intent, parameters.AddString);

                    var sourceSupported = operations[ordinal].Intent is RenameIndexIntent index
                        ? BuildIndexRenameSourceSupportedExpression(index, parameters.AddString)
                        : "NULL";

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                        + $"({expression}), ({sourceSupported})";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var payloadBytes = checked(sqlBytes + selectionBytes + parameters.Utf8PayloadBytes);
                    if (SafeMigrationCatalogQueryLimits.Exceeded(parameters.Count, payloadBytes, maximumPayloadBytes))
                    {
                        var parameterCount = parameters.Count;
                        parameters.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                ordinal, parameterCount, payloadBytes);
                        }

                        break;
                    }

                    selections.Add(selection);
                    sqlBytes += selectionBytes;
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;

                return new SafeMigrationCatalogProbeStatement(
                    selections.Count, parameters.Utf8PayloadBytes, candidates[offset]);
            },
            async (reader, offset, count, token) =>
            {
                if (reader.FieldCount != 3)
                {
                    throw InvalidMemberRenameTargetResult();
                }

                var row = 0;
                while (await reader.ReadAsync(token))
                {
                    if (row >= count || reader.GetInt32(0) != candidates[offset + row])
                    {
                        throw InvalidMemberRenameTargetResult();
                    }

                    var hasSourceSupported = operations[candidates[offset + row]].Intent is RenameIndexIntent;
                    if (reader.IsDBNull(2) == hasSourceSupported)
                    {
                        throw InvalidMemberRenameTargetResult();
                    }

                    targets[offset + row] = (ReadColumnDependencyBoolean(reader, 1),
                        hasSourceSupported ? ReadColumnDependencyBoolean(reader, 2) : null);
                    row++;
                }

                if (row != count)
                {
                    throw InvalidMemberRenameTargetResult();
                }
            },
            cancellationToken);

        // WHY: Cancellation or a malformed later result must not expose a
        // partially certified destination set to ordered projection.
        for (var index = 0; index < candidates.Count; index++)
        {
            var ordinal = candidates[index];
            results[ordinal] = results[ordinal].WithRenameTargetExists(targets[index].Target);
            if (targets[index].SourceSupported is { } supported)
            {
                results[ordinal] = results[ordinal].WithRenameSourceSupported(supported);
            }

            if (operations[ordinal].Intent is RenameIndexIntent rename)
            {
                // WHY: PRIMARY is a reserved target even when no primary key
                // exists. Source creation or a target tombstone cannot change
                // that authored destination role; no live query is needed.
                results[ordinal] = results[ordinal].WithRenameTargetSupported(
                    !StringComparer.OrdinalIgnoreCase.Equals(rename.NewName, "PRIMARY"));
            }
        }
    }

    /// <summary>Uses native owner rules without widening the member's physical namespace.</summary>
    /// <param name="intent">The source-independent column or index rename destination.</param>
    /// <param name="literal">The bounded parameter renderer for catalog identities.</param>
    /// <returns>A catalog-only destination occupancy predicate.</returns>
    private static string BuildMemberRenameTargetExpression(SafeMigrationIntent intent, Func<string, string> literal)
    {
        var (table, target, catalog, alias, member) = intent switch
        {
            RenameColumnIntent column => (column.Table, column.NewName, "COLUMNS", "c", "COLUMN_NAME"),
            RenameIndexIntent index => (index.Table, index.NewName, "STATISTICS", "s", "INDEX_NAME"),
            _ => throw new UnreachableException(),
        };

        return BuildMemberRenamePresenceExpression(table, target, catalog, alias, member, literal);
    }

    /// <summary>Proves the native source owner and independent kind without assuming destination freedom.</summary>
    /// <param name="intent">The immutable index rename source.</param>
    /// <param name="literal">The bounded parameter renderer.</param>
    /// <returns>Whether the independently renameable source exists on the exact native owner.</returns>
    private static string BuildIndexRenameSourceSupportedExpression(
        RenameIndexIntent intent,
        Func<string, string> literal
    ) => BuildMemberRenamePresenceExpression(intent.Table, intent.Name, "STATISTICS", "s", "INDEX_NAME", literal,
        "s.INDEX_NAME <> 'PRIMARY'");

    /// <summary>Shares owner qualification for source and destination without interpreting authored SQL.</summary>
    /// <param name="table">The native physical owner.</param>
    /// <param name="name">The native member name.</param>
    /// <param name="catalog">The trusted provider-owned catalog suffix.</param>
    /// <param name="alias">The trusted catalog alias.</param>
    /// <param name="member">The trusted column containing the member identity.</param>
    /// <param name="literal">The bounded parameter renderer.</param>
    /// <param name="invariant">The optional provider-owned source-kind predicate.</param>
    /// <returns>The complete catalog-only presence predicate.</returns>
    private static string BuildMemberRenamePresenceExpression(
        string table,
        string name,
        string catalog,
        string alias,
        string member,
        Func<string, string> literal,
        string? invariant = null
    )
    {
        var owner = literal(table);
        var ownerMatch = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
            alias + ".TABLE_NAME", owner);

        var databaseMatch = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
            alias + ".TABLE_SCHEMA", "DATABASE()");

        return $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.{catalog} {alias} "
            + $"WHERE {alias}.TABLE_SCHEMA = DATABASE() AND {alias}.TABLE_NAME = {owner} "
            + $"AND {alias}.{member} = {literal(name)} AND {ownerMatch} AND {databaseMatch}"
            + (invariant is null ? string.Empty : $" AND {invariant}") + ")";
    }

    /// <summary>Rejects malformed destination facts without exposing physical names or data.</summary>
    /// <returns>The low-cardinality ownership protocol error.</returns>
    private static InvalidOperationException InvalidMemberRenameTargetResult() =>
        new("The MySQL member rename destination query returned malformed or incomplete evidence.");
}
