namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    /// <summary>Adds bounded physical facet evidence only to unresolved column differences or data blockers.</summary>
    /// <param name="connection">The current analysis connection.</param>
    /// <param name="operations">The original ordinal-aligned operation stream.</param>
    /// <param name="results">The classifications to enrich without changing their owning ordinal.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The cancellation token for catalog reads.</param>
    private async Task PopulateColumnDiagnosticsAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] results,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var candidates = new List<int>();
        // WHY: Select diagnostics from matching operation and analysis slots.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var analysis = results[ordinal];
            if (operations[ordinal].Intent is EnsureColumnIntent or AlterColumnIntent
                && analysis.Differences.Count == 0
                && analysis.ObservedState is SafeMigrationObservedState.Different
                    or SafeMigrationObservedState.DataBlocked)
            {
                candidates.Add(ordinal);
            }
        }

        // WHY: Diagnostics need only this scalar proof flag after reading.
        // Retaining every generated SQL plan would defeat bounded transport.
        var nullabilityProofs = new bool[candidates.Count];
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            candidates.Count,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes,
            commandTimeout,
            (command, offset) =>
            {
                var count = Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                    candidates.Count - offset);

                var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                    _typeMappingSource,
                    _sqlGenerationHelper,
                    parameters.AddString,
                    parameters.Add);

                var selections = new List<string>(count);
                // WHY: Keep bounded probe slots aligned with their original candidate ordinals.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < count; index++)
                {
                    var ordinal = candidates[offset + index];
                    var plan = builder.Build(
                        operations[ordinal],
                        includeAnalysisEvidence: true,
                        includeTransitionEvidence: false);

                    // WHY: Alter eligibility includes the exact old definition.
                    // A diagnostic mismatch alone cannot establish that gate;
                    // retain its catalog-qualified proof flag from classification.
                    nullabilityProofs[offset + index] = plan.MayRequireNullabilityDataProof
                        && operations[ordinal].Intent is not AlterColumnIntent;

                    selections.Add(
                        $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                        + $"({plan.DiagnosticEvidenceExpression ?? "NULL"})");
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
                            "The PostgreSQL diagnostic query returned an invalid ordinal.");
                    }

                    var differences = reader.IsDBNull(1)
                        ? []
                        : SafeMigrationFacetDifferenceParser.Parse(reader.GetString(1), "PostgreSQL");

                    ReplaceAnalysisWithDifferences(
                        results,
                        candidates[offset + row],
                        nullabilityProofs[offset + row],
                        differences);

                    row++;
                }

                if (row != count)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL diagnostic query returned an inconsistent row count.");
                }
            },
            cancellationToken);
    }

    /// <summary>Preserves qualified nullability proof while attaching exact physical differences.</summary>
    private static void ReplaceAnalysisWithDifferences(
        SafeMigrationProviderAnalysis[] results,
        int ordinal,
        bool mayRequireNullabilityDataProof,
        IReadOnlyList<SafeMigrationFacetDifference> differences
    )
    {
        var current = results[ordinal];

        results[ordinal] = current.WithDifferences(
            differences,
            mayRequireNullabilityDataProof
            && differences.Any(static difference =>
                StringComparer.Ordinal.Equals(difference.Facet, "column_nullability")));
    }
}
