namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    private Dictionary<(string? Schema, string Table, string Name), MultiValuedIndexDropProof>?
        _multiValuedIndexDropProofs;

    private bool _multiValuedIndexDropBoundaryUnknown;

    /// <inheritdoc />
    bool ISafeMigrationLivePrerequisiteSource.CanRequalifyLiveDropIndexPrerequisites(
        DropIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var owner = (NormalizeProofSchema(intent.Schema), intent.Table);
        if (liveAnalysis.Code != "foreign_key_multi_valued_supporting_index"
            || _multiValuedIndexDropBoundaryUnknown
            || _invalidatedForeignKeyTables?.Contains(owner) == true
            || _multiValuedIndexDropProofs?.TryGetValue((owner.Item1, owner.Table, intent.Name), out var proof) != true
            || !proof.IndexExists || proof.ForeignKeyNames.Length == 0)
        {
            return false;
        }

        foreach (var name in proof.ForeignKeyNames)
        {
            var identity = (owner.Item1, owner.Table, name);
            if (_acceptedForeignKeyRemovals?.Contains(identity) != true
                || _acceptedForeignKeyCreations?.Contains(identity) == true)
            {
                return false;
            }
        }

        // WHY: This certificate proves only dependency-first removal of an
        // independently present index. It neither certifies historical rows
        // nor promotes a subsequent FK to Matching or bypasses native DDL.
        return true;
    }

    /// <summary>Captures only dynamically rejected MVI drops preceded by declared physical FK removals.</summary>
    private async Task CaptureMultiValuedIndexDropProofsAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        if (!analyses.Any(static analysis => analysis.ObservedState == SafeMigrationObservedState.Unsupported
                && !analysis.IsInvariantUnsupported
                && analysis.Code == "foreign_key_multi_valued_supporting_index"))
        {
            return;
        }

        HashSet<(string? Schema, string Table)>? removalOwners = null;
        Dictionary<(string? Schema, string Table, string Name), DropIndexIntent>? requests = null;

        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var intent = operations[ordinal].Intent;
            if (!TargetsCurrentDatabase(intent, _currentDatabase))
            {
                continue;
            }

            if (intent is DropForeignKeyIntent foreignKey)
            {
                (removalOwners ??= new(PhysicalOwnerComparer))
                    .Add((NormalizeProofSchema(foreignKey.Schema), foreignKey.Table));
            }
            else if (intent is DropIndexIntent index
                && analyses[ordinal].ObservedState == SafeMigrationObservedState.Unsupported
                && !analyses[ordinal].IsInvariantUnsupported
                && analyses[ordinal].Code == "foreign_key_multi_valued_supporting_index"
                && removalOwners?.Contains((NormalizeProofSchema(index.Schema), index.Table)) == true)
            {
                (requests ??= new(PhysicalMemberComparer))
                    .TryAdd((NormalizeProofSchema(index.Schema), index.Table, index.Name), index);
            }
        }

        if (requests is null)
        {
            return;
        }

        var targets = requests.ToArray();
        var captured = new Dictionary<(string? Schema, string Table, string Name), MultiValuedIndexDropProof>(
            PhysicalMemberComparer);

        var version = MySqlServerVersion.AutoDetect(context.Database.GetDbConnection(),
            MySqlServerVersionCompatibilityMode.AllowUnsupported);

        await SafeMigrationCatalogProbeBatch.ReadAsync(context.Database.GetDbConnection(), targets.Length,
            maximumPayloadBytes, commandTimeout,
            (command, ordinal) => new SafeMigrationCatalogProbeStatement(1,
                MySqlSafeMigrationCatalogSqlBuilder.BuildMultiValuedIndexDropProbe(command,
                    targets[ordinal].Value, version.IsMariaDb), ordinal),
            async (reader, ordinal, _, token) =>
            {
                captured.Add(targets[ordinal].Key, await ReadMultiValuedIndexDropProofAsync(reader, token));
            },
            cancellationToken, context.Database.CurrentTransaction?.GetDbTransaction());

        // Publish only complete independent aliases; canceled captures must
        // never expose partial dependency-removal authority to another run.
        _multiValuedIndexDropProofs = captured;
    }

    /// <summary>Rejects incomplete physical-index and FK-alias certificates before any requalification.</summary>
    /// <param name="reader">The exact two-field native index-existence and FK-alias result set.</param>
    /// <param name="cancellationToken">The caller's cancellation token for every streamed alias.</param>
    /// <returns>The complete snapshot with no alias-count or transport truncation.</returns>
    internal static async Task<MultiValuedIndexDropProof> ReadMultiValuedIndexDropProofAsync(
        DbDataReader reader,
        CancellationToken cancellationToken
    )
    {
        if (reader.FieldCount != 2)
        {
            throw new InvalidOperationException("MySQL returned an invalid multi-valued index-drop shape.");
        }

        bool? exists = null;
        List<string>? aliases = null;
        HashSet<string>? identities = null;
        var sentinel = false;

        while (await reader.ReadAsync(cancellationToken))
        {
            var current = ReadLivePrerequisiteBoolean(reader, 0);
            if (exists is not null && exists != current)
            {
                throw new InvalidOperationException("MySQL returned inconsistent native index-drop facts.");
            }

            exists = current;
            if (reader.IsDBNull(1))
            {
                if (sentinel || aliases is not null)
                {
                    throw new InvalidOperationException("MySQL returned an invalid native index-drop sentinel.");
                }

                sentinel = true;

                continue;
            }

            var name = reader.GetString(1);
            if (!current || sentinel || name.Length == 0
                || !(identities ??= new(StringComparer.OrdinalIgnoreCase)).Add(name))
            {
                throw new InvalidOperationException("MySQL returned an unowned or duplicate index-bound FK.");
            }

            (aliases ??= []).Add(name);
        }

        if (exists is null)
        {
            throw new InvalidOperationException("MySQL omitted an owned native index-drop certificate.");
        }

        return new MultiValuedIndexDropProof(exists.Value, aliases is null ? [] : aliases.ToArray());
    }

    /// <summary>Retains the independently present index and every originally bound physical FK.</summary>
    /// <param name="IndexExists">Whether the exact physical index exists in this snapshot.</param>
    /// <param name="ForeignKeyNames">The complete originally bound FK alias inventory.</param>
    internal readonly record struct MultiValuedIndexDropProof(bool IndexExists, string[] ForeignKeyNames);
}
