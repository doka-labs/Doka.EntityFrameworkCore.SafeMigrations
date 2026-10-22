namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Owns one exact native binary-default check without reading application rows.</summary>
/// <param name="Marker">The structural token consumed by this operation's catalog predicates.</param>
/// <param name="EligibilityExpression">The catalog-only guard that permits binding DEFAULT(column).</param>
/// <param name="QueryExpression">The parameter-free, identifier-delimited exact-value SELECT.</param>
/// <param name="Binding">The independently completed proof result, or null before materialization.</param>
internal sealed record MySqlSafeMigrationBinaryDefaultProof(
    string Marker,
    string EligibilityExpression,
    string QueryExpression,
    string? Binding = null
)
{
    internal const string MarkerPrefix = "__DOKA_SM_BINARY_DEFAULT_";

    /// <summary>Gets the closed operation-local session variable for one proof.</summary>
    /// <param name="ordinal">The zero-based proof position in this operation.</param>
    /// <returns>A provider-owned variable that never includes an authored identifier.</returns>
    internal static string Variable(int ordinal) =>
        "@doka_sm_binary_default_" + ordinal.ToString(CultureInfo.InvariantCulture);

    /// <summary>Builds one guarded prepared SELECT for all native literal defaults of an operation.</summary>
    /// <param name="proofs">The operation's exact immutable default queries.</param>
    /// <param name="eligibility">Fresh catalog barriers in the same order as the queries.</param>
    /// <param name="executionGuard">The preceding identity/prerequisite or accepted-action barrier.</param>
    /// <param name="statementName">The provider-owned prepared-statement name.</param>
    /// <param name="sqlVariable">The provider-owned variable holding the selected SQL.</param>
    /// <param name="variableOffset">The first owned result-variable ordinal in a bounded analysis batch.</param>
    /// <returns>A reset, qualified preparation, execution and immediate deallocation sequence.</returns>
    internal static string BuildEvaluationSql(
        IReadOnlyList<MySqlSafeMigrationBinaryDefaultProof> proofs,
        IReadOnlyList<string> eligibility,
        string executionGuard,
        string statementName,
        string sqlVariable,
        int variableOffset = 0
    )
    {
        if (proofs.Count == 0)
        {
            return string.Empty;
        }

        if (proofs.Count != eligibility.Count)
        {
            throw new ArgumentException(
                "Every binary-default query requires its own catalog barrier.", nameof(eligibility));
        }

        var builder = new StringBuilder();
        builder.Append(BuildResetSql(proofs.Count, variableOffset, clear: false));
        builder.Append("SET ").Append(sqlVariable).Append(" = CASE WHEN ").Append(executionGuard)
            .Append(" THEN CONCAT('SELECT '");
        // WHY: Preserve binary-proof variable slots and their ordered bounded selection ranges.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < proofs.Count; ordinal++)
        {
            var query = proofs[ordinal].QueryExpression;
            if (!query.StartsWith("SELECT ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The MySQL binary-default proof is not an owned SELECT.");
            }

            // WHY: PREPARE resolves DEFAULT(column) even in an untaken SQL
            // CASE branch. Select complete expression text using metadata
            // first, and prepare only qualified identifiers. The seed's
            // impossible LEFT JOIN keeps NOT NULL defaults observable without
            // reading rows, unlike a direct null-extended HEX projection.
            builder.Append(ordinal == 0 ? ", " : ", ', ', ")
                .Append("CASE WHEN COALESCE((").Append(eligibility[ordinal])
                .Append("), FALSE) THEN CONVERT(0x")
                .Append(Convert.ToHexString(Encoding.UTF8.GetBytes(query[7..])))
                .Append(" USING utf8mb4) ELSE 'FALSE' END");
        }

        builder.Append(", ' INTO ");
        // WHY: Preserve binary-proof variable slots and their ordered bounded selection ranges.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < proofs.Count; ordinal++)
        {
            if (ordinal > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Variable(variableOffset + ordinal));
        }

        builder.Append("') ELSE 'DO 0' END;PREPARE ").Append(statementName)
            .Append(" FROM ").Append(sqlVariable).Append(";EXECUTE ").Append(statementName)
            .Append(";DEALLOCATE PREPARE ").Append(statementName).Append(';');

        return builder.ToString();
    }

    /// <summary>Resets only the result variables owned by the selected proof scope.</summary>
    /// <param name="count">The number of owned result variables.</param>
    /// <param name="offset">The scope's first result-variable ordinal.</param>
    /// <param name="clear">Whether cleanup must release values instead of initializing FALSE.</param>
    /// <returns>The owned SET statement, or the shared empty string when no proof exists.</returns>
    internal static string BuildResetSql(int count, int offset, bool clear)
    {
        if (count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("SET ");
        // WHY: Preserve binary-proof variable slots and their ordered bounded selection ranges.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            if (ordinal > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Variable(offset + ordinal)).Append(clear ? " = NULL" : " = FALSE");
        }

        return builder.Append(';').ToString();
    }

    /// <summary>Expands only structural proof tokens, never marker-shaped names or literal payloads.</summary>
    /// <param name="sql">The rendered catalog expression.</param>
    /// <param name="proofs">The owning operation's immutable proof contracts.</param>
    /// <returns>The expression with materialized results, or FALSE for an unmaterialized proof.</returns>
    internal static string Bind(string sql, IReadOnlyList<MySqlSafeMigrationBinaryDefaultProof> proofs)
    {
        if (!sql.Contains(MarkerPrefix, StringComparison.Ordinal))
        {
            return sql;
        }

        StringBuilder? builder = null;
        var copied = 0;
        var position = 0;
        while (MySqlSafeMigrationRuntimePlan.FindNextProofMarker(sql.AsSpan(), position) is var marker
            && marker.Index >= 0)
        {
            position = marker.Index + marker.Length;
            if (marker.Kind != 5)
            {
                continue;
            }

            var digits = marker.Index + MarkerPrefix.Length;
            if (!int.TryParse(sql.AsSpan(digits, marker.Length - MarkerPrefix.Length - 2),
                    CultureInfo.InvariantCulture, out var ordinal)
                || (uint)ordinal >= (uint)proofs.Count
                || !sql.AsSpan(marker.Index, marker.Length).SequenceEqual(proofs[ordinal].Marker))
            {
                throw new InvalidOperationException("The MySQL binary-default proof token has no owning contract.");
            }

            // WHY: Absent evidence cannot authorize a default match. Capture
            // and independently rendered plans remain fail-closed until the
            // analyzer or scoped runtime binds a freshly evaluated result.
            builder ??= new StringBuilder(sql.Length);
            builder.Append(sql.AsSpan(copied, marker.Index - copied));
            builder.Append(proofs[ordinal].Binding ?? "FALSE");
            copied = position;
        }

        if (builder is null)
        {
            return sql;
        }

        builder.Append(sql.AsSpan(copied));

        return builder.ToString();
    }
}
