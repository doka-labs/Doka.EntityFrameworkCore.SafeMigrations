namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Retains generated SQL while canonicalizing only validated, operation-owned correlation metadata.</summary>
internal static class MySqlRuntimeSqlCanonicalizer
{
    private const string CanonicalCorrelation = "00000000000000000000000000000000";

    /// <summary>Canonicalizes metadata and platform newlines outside allocation measurement windows.</summary>
    /// <param name="sql">One command whose authored identifiers, literals and SQL body remain exact.</param>
    /// <returns>The complete SQL with only known non-semantic metadata and line endings normalized.</returns>
    internal static string NormalizeOwnedCorrelation(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (!MySqlSafeMigrationRuntimeDiagnostic.TryReadMarker(sql, out _, out var correlation))
        {
            return sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        var text = sql.AsSpan().TrimStart();
        var markerStart = sql.Length - text.Length;
        var ordinalStart = markerStart + MySqlSafeMigrationRuntimeDiagnostic.MarkerPrefix.Length;
        var tokenStart = ordinalStart + sql.AsSpan(ordinalStart).IndexOf(':') + 1;
        var token = correlation.ToString();
        var canonical = string.Concat(sql.AsSpan(0, tokenStart), CanonicalCorrelation,
            sql.AsSpan(tokenStart + correlation.Length));

        // WHY: Global GUID replacement could hide authored SQL drift. Only
        // the exact correlated value assignment is metadata rather than a user literal or identifier.
        return canonical.Replace(", @doka_sm_diag = CONCAT('" + token + "-'",
            ", @doka_sm_diag = CONCAT('" + CanonicalCorrelation + "-'", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
