namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    /// <summary>Captures destination occupancy without changing idempotent missing-source classification.</summary>
    private async Task PopulateRenameTargetEvidenceAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] results,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        List<int>? candidates = null;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal].Intent is RenameTableIntent
                && results[ordinal].ObservedState != SafeMigrationObservedState.Unsupported)
            {
                (candidates ??= []).Add(ordinal);
            }
        }

        if (candidates is null)
        {
            return;
        }

        // WHY: Missing source remains a valid replay result, but an earlier
        // accepted creation can establish that source during ordered projection.
        // Its destination therefore needs independent namespace-bound evidence.
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            candidates.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
            commandTimeout,
            (command, offset) =>
            {
                var count = Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, candidates.Count - offset);

                var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                    _typeMappingSource, _sqlGenerationHelper, parameters.AddString, parameters.Add);

                var selections = new List<string>(count);
                for (var index = 0; index < count; index++)
                {
                    var ordinal = candidates[offset + index];
                    var intent = (RenameTableIntent)operations[ordinal].Intent;
                    var intermediate = builder.BuildRenameIntermediateTargetExistsExpression(intent) ?? "NULL::boolean";
                    selections.Add(
                        $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                        + builder.BuildRenameTargetExistsExpression(intent)
                        + $", {intermediate}");
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;

                return new SafeMigrationCatalogProbeStatement(count, parameters.Utf8PayloadBytes, candidates[offset]);
            },
            async (reader, offset, count, token) =>
            {
                var row = 0;
                while (await reader.ReadAsync(token))
                {
                    if (row >= count
                        || reader.GetInt32(0) != candidates[offset + row])
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL rename destination query returned an invalid ordinal.");
                    }

                    var ordinal = candidates[offset + row];
                    results[ordinal] = results[ordinal].WithRenameTargetExists(
                        reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetBoolean(2));

                    row++;
                }

                if (row != count)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL rename destination query returned an inconsistent row count.");
                }
            },
            cancellationToken);
    }
}
