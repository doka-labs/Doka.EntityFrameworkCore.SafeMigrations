namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Qualifies captured source roles separately from executable structured-literal serialization.</summary>
internal static class MySqlCapturedExpressionContract
{
    private const int StableText = 1;
    private const int SupplementaryText = 2;

    /// <summary>Rejects native source spellings whose generic parse does not prove MySQL token roles.</summary>
    internal static string? GetUnsupportedFeature(
        string? source,
        SafeMigrationSqlExpression? expression,
        bool isMariaDb
    )
    {
        if (source is null)
        {
            return null;
        }

        if (!SafeMigrationSqlExpressionParser.IsCapturedSourceDialectCompatible(source, '`'))
        {
            // WHY: Brackets are not native identifiers; double quotes change
            // between values and members with ANSI_QUOTES. Neither can borrow
            // a backtick-rendered AST certificate by accident.
            return "captured_expression_dialect_unverified";
        }

        if (expression is not null
            && !HasNativeCastTargets(expression))
        {
            // WHY: Portable typed CAST maps column types such as BIGINT to
            // SIGNED. Captured SQL executes verbatim and cannot borrow that
            // mapping to certify a source spelling rejected by both servers.
            return "captured_expression_cast_target_unverified";
        }

        // WHY: Native I_S loses supplementary quoted source bytes on MariaDB.
        // Retaining author SQL must not turn Ready into a later postcondition
        // failure or accept question-mark metadata as a proof of exact values.
        return isMariaDb && expression is not null && (StringFlags(expression) & SupplementaryText) != 0
            ? "captured_expression_literal_catalog_unverifiable"
            : null;
    }

    /// <summary>Requires captured CAST spellings to belong to the certified common native grammar.</summary>
    private static bool HasNativeCastTargets(
        SafeMigrationSqlExpression expression
    )
    {
        var traversal = new MySqlExpressionTraversal(expression);
        try
        {
            while (traversal.TryMoveNext(out var current))
            {
                if (current is SafeMigrationSqlCastExpression cast
                    && !IsNativeCastTarget(cast.StoreType))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            traversal.Dispose();
        }
    }

    /// <summary>Retains native SIGNED INTEGER aliases without accepting portable column-type aliases.</summary>
    private static bool IsNativeCastTarget(
        string storeType
    )
    {
        if (!MySqlSafeMigrationCastTypeRenderer.TryRender(storeType, out var nativeType))
        {
            return false;
        }

        Span<char> compact = stackalloc char[storeType.Length];
        var length = 0;
        foreach (var character in storeType)
        {
            if (!char.IsWhiteSpace(character))
            {
                compact[length++] = char.ToUpperInvariant(character);
            }
        }

        var spelling = compact[..length];

        return spelling.Equals(nativeType.AsSpan(), StringComparison.Ordinal)
            || spelling is "SIGNEDINTEGER"
            || spelling is "UNSIGNEDINTEGER";
    }

    /// <summary>Uses the existing SQL-mode-independent serializer only when ordinary quoted text is unsafe.</summary>
    internal static string RenderExecutable(
        SafeMigrationSqlExpression expression,
        MySqlSafeMigrationSqlExpressionRenderer renderer
    ) => RequiresStableStrings(expression) ? renderer.RenderDefault(expression) : renderer.Render(expression);

    /// <summary>Identifies whether every string leaf must use the same stable executable spelling.</summary>
    internal static bool RequiresStableStrings(
        SafeMigrationSqlExpression expression
    ) => (StringFlags(expression) & StableText) != 0;

    /// <summary>Collects text properties without allocating a visitor, delegates or intermediate SQL.</summary>
    private static int StringFlags(
        SafeMigrationSqlExpression expression
    )
    {
        var flags = 0;
        Span<char> characterBuffer = stackalloc char[1];
        var traversal = new MySqlExpressionTraversal(expression, caseElseFirst: true);
        try
        {
            // WHY: Aggregate scans preserve every occurrence and the historic
            // ELSE-first order, but pending lists occupy one cursor per parent.
            while (traversal.TryMoveNext(out var current))
            {
                switch (current)
                {
                    case SafeMigrationSqlLiteralExpression { Value: string text }:
                        flags |= TextFlags(text);
                        break;
                    case SafeMigrationSqlLiteralExpression { Value: char character }:
                        characterBuffer[0] = character;
                        flags |= TextFlags(characterBuffer);
                        break;
                }
            }

            return flags;
        }
        finally
        {
            traversal.Dispose();
        }
    }

    /// <summary>Finds mode-dependent or lossy literal bytes while retaining a no-allocation ASCII fast path.</summary>
    private static int TextFlags(
        ReadOnlySpan<char> text
    )
    {
        var flags = 0;
        foreach (var character in text)
        {
            if (character is '\'' or '\\'
                || character < ' '
                || character > '~')
            {
                flags |= StableText;
            }

            if (char.IsSurrogate(character))
            {
                flags |= SupplementaryText;
            }
        }

        return flags;
    }
}
