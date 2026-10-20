namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Retains a complete expected display together with exact literal locations after native encoding.</summary>
internal sealed record MySqlExpressionCatalogCandidate(
    string Text,
    IReadOnlyList<MySqlExpressionCatalogLiteral> Literals
);

/// <summary>Identifies one complete literal token in the display's explicitly UTF-8-encoded byte sequence.</summary>
internal readonly record struct MySqlExpressionCatalogLiteral(
    int Start,
    int Length,
    string Text
);

/// <summary>Compares native spelling while separating case-insensitive identifiers from literal bytes.</summary>
internal static class MySqlExpressionCatalogComparison
{
    /// <summary>Requires one exact display shape and byte-exact quoted value tokens inside that shape.</summary>
    /// <param name="catalog">The provider-owned native catalog expression.</param>
    /// <param name="candidates">Equivalent displays generated from the expected expression only.</param>
    /// <param name="literal">The catalog parameter renderer.</param>
    /// <returns>A bounded native predicate, never a catalog SQL rewrite or data scan.</returns>
    internal static string Build(
        string catalog,
        IEnumerable<string> candidates,
        Func<string, string> literal
    ) => Build(
        catalog,
        candidates.Select(static candidate => MySqlExpressionCanonicalizer.CreateComparisonCandidate(candidate)),
        literal);

    /// <summary>Uses retained literal roles rather than reparsing MySQL's escaped catalog delimiters.</summary>
    internal static string Build(
        string catalog,
        IEnumerable<MySqlExpressionCatalogCandidate> candidates,
        Func<string, string> literal
    )
    {
        var builder = new StringBuilder();
        var utf8Catalog = $"CONVERT({catalog} USING utf8mb4)";
        var binaryCatalog = $"CAST({utf8Catalog} AS BINARY)";
        foreach (var candidate in candidates)
        {
            if (builder.Length > 0)
            {
                builder.Append(" OR ");
            }

            // WHY: Both engines bind column members without case sensitivity,
            // but INFORMATION_SCHEMA's ordinary collation also folds literals.
            // Binary LOWER compares structural tokens without accent folding;
            // every quoted value is independently compared before folding.
            builder
                .Append("(CAST(LOWER(")
                .Append(utf8Catalog)
                .Append(") AS BINARY) = CAST(LOWER(CONVERT(")
                .Append(literal(candidate.Text))
                .Append(" USING utf8mb4)) AS BINARY)");

            foreach (var token in candidate.Literals)
            {
                // WHY: Byte positions were captured before native encoding;
                // supplementary values and escaped delimiters cannot shift a
                // following guard or be mistaken for new quoted SQL tokens.
                builder
                    .Append(" AND SUBSTRING(")
                    .Append(binaryCatalog)
                    .Append(", ")
                    .Append(token.Start.ToString(CultureInfo.InvariantCulture))
                    .Append(", ")
                    .Append(token.Length.ToString(CultureInfo.InvariantCulture))
                    .Append(") = CAST(")
                    .Append("CONVERT(")
                    .Append(literal(token.Text))
                    .Append(" USING utf8mb4) AS BINARY)");
            }

            builder.Append(')');
        }

        return builder.Length == 0 ? "FALSE" : $"({builder})";
    }
}
