namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Binds native identifier certificates only through the shared structural-token scanner.</summary>
internal static class MySqlSafeMigrationIdentifierReferenceBinding
{
    /// <summary>Uses FALSE for absent evidence and rejects malformed or unowned structural proof references.</summary>
    internal static string Bind(
        string sql,
        IReadOnlyList<MySqlSafeMigrationIdentifierReferenceProof> proofs
    )
    {
        if (!sql.Contains(MySqlSafeMigrationIdentifierReferenceProof.MarkerPrefix, StringComparison.Ordinal))
        {
            return sql;
        }

        StringBuilder? builder = null;
        var copied = 0;
        var position = 0;
        while (MySqlSafeMigrationRuntimePlan.FindNextProofMarker(sql.AsSpan(), position) is { Index: >= 0 } marker)
        {
            position = marker.Index + marker.Length;
            if (marker.Kind != 6)
            {
                continue;
            }

            var prefixLength = MySqlSafeMigrationIdentifierReferenceProof.MarkerPrefix.Length;
            if (!int.TryParse(
                    sql.AsSpan(marker.Index + prefixLength, marker.Length - prefixLength - 2),
                    CultureInfo.InvariantCulture,
                    out var ordinal)
                || (uint)ordinal >= (uint)proofs.Count
                || !sql
                    .AsSpan(marker.Index, marker.Length)
                    .SequenceEqual(proofs[ordinal].Marker))
            {
                throw new InvalidOperationException(
                    "The MySQL identifier-reference proof token has no owning contract.");
            }

            // WHY: Native parsing is separate from the generic AST. Missing
            // acquisition must never borrow another operation's session result
            // or become a catalog certificate through a render-only path.
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
