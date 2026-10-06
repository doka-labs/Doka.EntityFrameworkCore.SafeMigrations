namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationRenamedTableAnalyzer
{
    private readonly Dictionary<RenamedSourceKey, SafeMigrationProviderAnalysis> _renamedSourceAnalyses = [];
    private readonly HashSet<RenameTableIntent> _renameTargetConflicts = [];

    bool ISafeMigrationRenamedTableAnalyzer.TryGetRenamedTableAnalysis(
        SafeMigrationOperation operation,
        string sourceTable,
        string? sourceSchema,
        [NotNullWhen(true)] out SafeMigrationProviderAnalysis? analysis
    )
    {
        return _renamedSourceAnalyses.TryGetValue(
            new RenamedSourceKey(
                operation, sourceTable, MySqlTableIdentity.NormalizeDatabase(sourceSchema, _currentDatabase)),
            out analysis);
    }

    private async Task CaptureRenameSourceAnalysesAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] results,
        MySqlBackfillConstraintCatalog backfillConstraints,
        CancellationToken cancellationToken
    )
    {
        var currentDatabase = _currentDatabase;
        var sources = new Dictionary<(string? Schema, string Table), (string? Schema, string Table)>();
        var originals = new List<SafeMigrationOperation>();
        var rebound = new List<SafeMigrationOperation>();
        var identities = new List<(string? Schema, string Table)>();
        var requestedSources = new HashSet<RenamedSourceKey>();

        var renames = operations.Select(static operation => operation.Intent).OfType<RenameTableIntent>().ToArray();
        if (renames.Length == 0)
        {
            return;
        }

        await CaptureRenameTargetPresenceAsync(context, renames, cancellationToken);
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index].Intent is RenameTableIntent rename)
            {
                results[index] = results[index].WithRenameTargetExists(_renameTargetConflicts.Contains(rename));
            }
        }

        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (operation.Intent is RenameTableIntent rename)
            {
                var source = (MySqlTableIdentity.NormalizeDatabase(rename.Schema, currentDatabase), rename.Name);
                var target = (MySqlTableIdentity.NormalizeDatabase(rename.NewSchema ?? rename.Schema, currentDatabase),
                    rename.NewName ?? rename.Name);

                sources[target] = sources.TryGetValue(source, out var original) ? original : source;
                sources.Remove(source);

                continue;
            }

            var owner = operation.Intent switch
            {
                AlterColumnIntent value => (value.Schema, value.Table),
                EnsureIndexIntent value => (value.Definition.Schema, value.Definition.Table),
                _ => ((string? Schema, string Table)?)null,
            };

            if (owner is not { } table
                || !sources.TryGetValue(
                    (MySqlTableIdentity.NormalizeDatabase(table.Schema, currentDatabase), table.Table),
                    out var physical))
            {
                continue;
            }

            var intent = RebindRenameIntent(operation.Intent, physical.Table, physical.Schema);
            if (!requestedSources.Add(new RenamedSourceKey(operation, physical.Table, physical.Schema)))
            {
                continue;
            }

            originals.Add(operation);
            rebound.Add(new SafeMigrationOperation(intent, operation.Policy));
            identities.Add(physical);
        }

        if (rebound.Count == 0)
        {
            return;
        }

        // WHY: These are candidate source observations, not accepted aliases.
        // Core activates one only after its rename succeeds. The normal
        // classifier retains exact-old checks, row probes and source limits.
        try
        {
            // Source-bound windows omit declarations, so their constraint
            // hazards must travel with the original operation identities.
            var analyses = await AnalyzeCoreAsync(context, rebound, captureRenameSources: false, cancellationToken,
                backfillConstraints.Rebind(originals, rebound));

            for (var index = 0; index < originals.Count; index++)
            {
                _renamedSourceAnalyses.Add(
                    new RenamedSourceKey(originals[index], identities[index].Table, identities[index].Schema),
                    analyses[index]);
            }
        }
        finally
        {
            _currentDatabase = currentDatabase;
        }
    }

    private static SafeMigrationIntent RebindRenameIntent(
        SafeMigrationIntent intent,
        string table,
        string? schema
    ) => intent switch
    {
        AlterColumnIntent value => new AlterColumnIntent(table, value.Definition, value.OldDefinition, schema),
        EnsureIndexIntent value => new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                value.Definition.Name, table, value.Definition.Keys, schema,
                value.Definition.Unique, value.Definition.Filter, value.Definition.IncludedColumns,
                value.Definition.Method, value.Definition.NullsDistinct, value.Definition.StructuredFilter)),
        _ => throw new UnreachableException(),
    };

    private readonly record struct RenamedSourceKey(
        SafeMigrationOperation Operation,
        string Table,
        string? Schema
    );

    private async Task CaptureRenameTargetPresenceAsync(
        DbContext context,
        IReadOnlyList<RenameTableIntent> renames,
        CancellationToken cancellationToken
    )
    {
        var requested = renames.Where(intent => TargetsCurrentDatabase(intent, _currentDatabase))
            .GroupBy(static intent => intent.NewName ?? intent.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var targets = requested.Keys.ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        var connection = context.Database.GetDbConnection();
        var commandTimeout = context.Database.GetCommandTimeout();
        var maximumPayloadBytes = await GetMaximumPayloadBytesAsync(connection, commandTimeout, cancellationToken);

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            (targets.Length + SafeMigrationCatalogQueryLimits.MaximumInventoryValues - 1)
                / SafeMigrationCatalogQueryLimits.MaximumInventoryValues,
            maximumPayloadBytes,
            commandTimeout,
            (command, slot) =>
            {
                var offset = slot * SafeMigrationCatalogQueryLimits.MaximumInventoryValues;
                var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumInventoryValues, targets.Length - offset);
                var parameters = new string[count];
                var valueBytes = 0;
                for (var index = 0; index < count; index++)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"@rename_target_{index.ToString(CultureInfo.InvariantCulture)}";
                    parameter.Value = targets[offset + index];
                    command.Parameters.Add(parameter);
                    parameters[index] = parameter.ParameterName;
                    valueBytes += Encoding.UTF8.GetByteCount(targets[offset + index]) + 32;
                }

                command.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES "
                    + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ("
                    + string.Join(", ", parameters) + ");";

                return new SafeMigrationCatalogProbeStatement(1, valueBytes, offset);
            },
            async (reader, _, _, token) =>
            {
                while (await reader.ReadAsync(token))
                {
                    if (!requested.TryGetValue(reader.GetString(0), out var matching))
                    {
                        throw new InvalidOperationException("MySQL returned an unowned rename destination.");
                    }

                    foreach (var rename in matching)
                    {
                        _renameTargetConflicts.Add(rename);
                    }
                }
            },
            cancellationToken);
    }
}
