namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    private async Task PopulateColumnDiagnosticsAsync(
        DbConnection connection,
        SafeMigrationOperation[] operations,
        SafeMigrationProviderAnalysis[] results,
        IModel model,
        MySqlExpectedUniqueIndexCatalog expectedUniqueIndexes,
        IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
            expectedTableConstraints,
        MySqlBackfillConstraintCatalog backfillConstraints,
        int maximumPayloadBytes,
        int? commandTimeout,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        var candidates = new List<MySqlDiagnosticCandidate>();
        // WHY: Keep bounded diagnostic selections aligned with their original operation ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var localOrdinal = 0; localOrdinal < operations.Length; localOrdinal++)
        {
            var analysis = results[operationOffset + localOrdinal];
            if (operations[localOrdinal].Intent is EnsureColumnIntent or AlterColumnIntent
                && analysis.Differences.Count == 0
                && analysis.ObservedState is SafeMigrationObservedState.Different
                    or SafeMigrationObservedState.DataBlocked)
            {
                candidates.Add(new MySqlDiagnosticCandidate(operationOffset + localOrdinal, operations[localOrdinal]));
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var plans = CapturePlans(
            candidates
                .Select(static candidate => candidate.Operation)
                .ToArray(),
            model,
            expectedUniqueIndexes,
            expectedTableConstraints,
            backfillConstraints,
            includeAnalysisEvidence: true,
            includeTransitionEvidence: false);

        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            candidates.Count,
            maximumPayloadBytes,
            commandTimeout,
            (command, offset) =>
            {
                var count = Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                    candidates.Count - offset);

                var parameterizer = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
                var selections = new List<string>(count);
                // WHY: Keep bounded diagnostic selections aligned with their original operation ordinals.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < count; index++)
                {
                    var plan = plans[offset + index];
                    var evidence = plan.DiagnosticEvidenceExpression is null
                        ? "NULL"
                        : plan.RenderDiagnosticEvidenceExpression(parameterizer.Add);

                    selections.Add(
                        $"SELECT {candidates[offset + index].Ordinal.ToString(CultureInfo.InvariantCulture)}, "
                        + $"({evidence})");
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;

                return new SafeMigrationCatalogProbeStatement(
                    count,
                    parameterizer.Utf8PayloadBytes,
                    candidates[offset].Ordinal);
            },
            async (reader, offset, count, token) =>
            {
                var row = 0;
                while (await reader.ReadAsync(token))
                {
                    if (row >= count
                        || reader.GetInt32(0) != candidates[offset + row].Ordinal)
                    {
                        throw new InvalidOperationException("The MySQL diagnostic query returned an invalid ordinal.");
                    }

                    var differences = reader.IsDBNull(1)
                        ? []
                        : SafeMigrationFacetDifferenceParser.Parse(reader.GetString(1), "MySQL");

                    ReplaceAnalysisWithDifferences(
                        results,
                        candidates[offset + row].Ordinal,
                        plans[offset + row],
                        differences);

                    row++;
                }

                if (row != count)
                {
                    throw new InvalidOperationException(
                        "The MySQL diagnostic query returned an inconsistent row count.");
                }
            },
            cancellationToken);
    }

    private static void ReplaceAnalysisWithDifferences(
        SafeMigrationProviderAnalysis[] results,
        int ordinal,
        MySqlSafeMigrationRuntimePlan plan,
        IReadOnlyList<SafeMigrationFacetDifference> differences
    )
    {
        var current = results[ordinal];

        results[ordinal] = current.WithDifferences(
            differences,
            plan.MayRequireNullabilityDataProof
            && (current.RepairCapability == SafeMigrationRepairCapability.Safe
                || current.ObservedState == SafeMigrationObservedState.DataBlocked)
            && differences.Any(static difference =>
                StringComparer.Ordinal.Equals(difference.Facet, "column_nullability")));
    }

    private readonly record struct MySqlDiagnosticCandidate(
        int Ordinal,
        SafeMigrationOperation Operation
    );
}
