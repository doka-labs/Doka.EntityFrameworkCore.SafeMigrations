namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Resolves only candidate-relevant authored owners using the server's native table-name rules.</summary>
    /// <param name="connection">The open connection that produced the unexpected-object inventory.</param>
    /// <param name="operations">The complete pending stream, including repeated or ineligible column intents.</param>
    /// <param name="findings">The unchanged physical CHECK inventory.</param>
    /// <param name="maximumPayloadBytes">The current qualified transport bound.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="cancellationToken">The token that cancels construction, transport and reading.</param>
    /// <returns>The physical owner mapping, or null when no exact guard name could belong to this stream.</returns>
    private async Task<IReadOnlyDictionary<string, string?>?> ResolveRequiredColumnAddRecoveryOwnersAsync(
        DbConnection connection,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<SafeMigrationUnexpectedObject> findings,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (authored, physical) = FindRequiredColumnAddRecoveryOwnerCandidates(
            operations, findings, _currentDatabase);

        if (authored.Length == 0)
        {
            return null;
        }

        var result = await ReadNativeInventoryOwnersAsync(
            connection, authored, maximumPayloadBytes, commandTimeout, cancellationToken);

        var allowedPhysicalOwners = physical.ToHashSet(StringComparer.Ordinal);
        foreach (var (owner, _) in authored)
        {
            if (result[owner] is { } resolved && !allowedPhysicalOwners.Contains(resolved))
            {
                result[owner] = null;
            }
        }

        return result;
    }

    /// <summary>Reads exact native owner bindings for a bounded stream without changing expected definitions.</summary>
    /// <param name="connection">The already-open inventory connection.</param>
    /// <param name="authored">The exact source owner names and their diagnostic ordinals.</param>
    /// <param name="maximumPayloadBytes">The current qualified transport bound.</param>
    /// <param name="commandTimeout">The caller's configured command timeout.</param>
    /// <param name="cancellationToken">The token that cancels construction, transport and reading.</param>
    /// <returns>One binding per source owner, with null only for no native catalog match.</returns>
    private async Task<Dictionary<string, string?>> ReadNativeInventoryOwnersAsync(
        DbConnection connection,
        (string Owner, int Ordinal)[] authored,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<string, string?>(authored.Length, StringComparer.Ordinal);
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection, authored.Length, maximumPayloadBytes, commandTimeout,
            (command, offset) =>
            {
                var parameters = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
                var selections = new List<string>(Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, authored.Length - offset));

                var sqlBytes = Encoding.UTF8.GetByteCount(BuildRequiredColumnAddRecoveryOwnerQuery(string.Empty));
                while (offset + selections.Count < authored.Length
                    && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var ordinal = offset + selections.Count;
                    var checkpoint = parameters.Capture();
                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)} AS ordinal, "
                        + $"{parameters.AddString(authored[ordinal].Owner)} AS owner";

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
                                authored[ordinal].Ordinal, parameterCount, payloadBytes);
                        }

                        break;
                    }

                    selections.Add(selection);
                    sqlBytes += selectionBytes;
                }

                command.CommandText = BuildRequiredColumnAddRecoveryOwnerQuery(
                    string.Join(SafeMigrationCatalogQueryLimits.Separator, selections));

                return new SafeMigrationCatalogProbeStatement(
                    selections.Count, parameters.Utf8PayloadBytes, authored[offset].Ordinal);
            },
            async (reader, offset, count, token) =>
            {
                var row = 0;
                while (await reader.ReadAsync(token))
                {
                    if (row >= count || reader.GetInt32(0) != offset + row)
                    {
                        throw new InvalidOperationException(
                            "The MySQL recovery owner query returned an invalid ordinal.");
                    }

                    var matches = reader.GetInt64(2);
                    if (matches is < 0 or > 1 || (matches == 0) != reader.IsDBNull(1))
                    {
                        throw new InvalidOperationException(
                            "The MySQL native inventory owner query returned an ambiguous or inconsistent identity.");
                    }

                    result.Add(authored[offset + row].Owner, matches == 0 ? null : reader.GetString(1));
                    row++;
                }

                if (row != count)
                {
                    throw new InvalidOperationException(
                        "The MySQL recovery owner query returned an invalid row count.");
                }
            },
            cancellationToken);

        // WHY: The server folds table names under modes 1/2, not .NET's Unicode
        // comparer. Ambiguous matches fail closed rather than selecting an
        // arbitrary physical owner or merging independently authored contracts.
        return result;
    }

    /// <summary>Builds one indexed native-owner lookup without parameterizing the entire physical inventory.</summary>
    /// <param name="authoredOwners">The trusted bounded derived SELECT of ordinals and bound owner values.</param>
    /// <returns>The catalog-only statement, including its fixed grouping and ordering.</returns>
    internal static string BuildRequiredColumnAddRecoveryOwnerQuery(string authoredOwners)
    {
        ArgumentNullException.ThrowIfNull(authoredOwners);

        var nativeMatches = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
            "physical_owner.TABLE_NAME", "source_owner.owner");

        // WHY: Ordinary name equality keeps the catalog filter index-eligible;
        // the native binary cast then excludes collation aliases and
        // preserves mode 0/1/2 semantics. Inventory size cannot enlarge this SQL.
        return "SELECT source_owner.ordinal, CASE WHEN COUNT(physical_owner.TABLE_NAME) = 1 "
            + "THEN MIN(physical_owner.TABLE_NAME) ELSE NULL END, COUNT(physical_owner.TABLE_NAME) FROM ("
            + authoredOwners
            + ") source_owner LEFT JOIN INFORMATION_SCHEMA.TABLES physical_owner "
            + "ON physical_owner.TABLE_SCHEMA = DATABASE() AND physical_owner.TABLE_TYPE = 'BASE TABLE' "
            + "AND physical_owner.TABLE_NAME = source_owner.owner "
            + $"AND {nativeMatches} GROUP BY source_owner.ordinal ORDER BY 1;";
    }

    /// <summary>Finds exact guard-name potential and pending owners that could alias its physical columns.</summary>
    /// <param name="operations">The complete immutable pending migration stream.</param>
    /// <param name="findings">The unchanged inventory's physical CHECK identities.</param>
    /// <param name="currentDatabase">The verified selected database.</param>
    /// <returns>The sparse authored owner/ordinal pairs and physical candidate owners.</returns>
    internal static ((string Owner, int Ordinal)[] Authored, string[] Physical)
        FindRequiredColumnAddRecoveryOwnerCandidates(
            IReadOnlyList<MigrationOperation> operations,
            IReadOnlyList<SafeMigrationUnexpectedObject> findings,
            string? currentDatabase
        )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(findings);

        Dictionary<string, HashSet<string>>? checks = null;
        foreach (var finding in findings)
        {
            if (finding.ObjectKind != SafeMigrationDatabaseObjectKind.CheckConstraint
                || finding.Table is null
                || !MySqlSafeMigrationCatalogSqlBuilder.IsRequiredColumnAddGuardNameCandidate(finding.Name)
                || MySqlTableIdentity.NormalizeDatabase(finding.Schema, currentDatabase) is not null)
            {
                continue;
            }

            checks ??= new(StringComparer.Ordinal);
            if (!checks.TryGetValue(finding.Name, out var owners))
            {
                checks.Add(finding.Name, owners = new(StringComparer.Ordinal));
            }

            owners.Add(finding.Table);
        }

        if (checks is null)
        {
            return ([], []);
        }

        HashSet<string>? columns = null;
        HashSet<string>? physicalOwners = null;
        foreach (var operation in operations)
        {
            if (operation is not SafeMigrationOperation { Intent: EnsureColumnIntent ensure } safe
                || !MySqlSafeMigrationCatalogSqlBuilder.TargetsDatabase(ensure, currentDatabase)
                || safe.GetAnnotations().Any()
                || !MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(ensure.Definition))
            {
                continue;
            }

            var guardName = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(ensure);
            if (checks.TryGetValue(guardName, out var owners))
            {
                (columns ??= new(StringComparer.OrdinalIgnoreCase)).Add(ensure.Definition.Name);
                (physicalOwners ??= new(StringComparer.Ordinal)).UnionWith(owners);
            }
        }

        if (columns is null || physicalOwners is null)
        {
            return ([], []);
        }

        var authoredOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal] is SafeMigrationOperation { Intent: EnsureColumnIntent ensure }
                && MySqlSafeMigrationCatalogSqlBuilder.TargetsDatabase(ensure, currentDatabase)
                && columns.Contains(ensure.Definition.Name))
            {
                // WHY: Ineligible/annotated later intents still make a resolved
                // physical target ambiguous. They must participate in native
                // owner resolution, not disappear during eligibility filtering.
                authoredOwners.TryAdd(ensure.Table, ordinal);
            }
        }

        return (authoredOwners.Select(static owner => (Owner: owner.Key, Ordinal: owner.Value)).ToArray(),
            physicalOwners.ToArray());
    }
}
