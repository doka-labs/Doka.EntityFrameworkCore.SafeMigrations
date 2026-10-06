namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Redirects the catalog relations a classification template reads.</summary>
/// <remarks>
/// WHY: MariaDB materialises every INFORMATION_SCHEMA subquery into an internal temporary table
/// and cannot keep one holding TEXT in its HEAP engine, so each lands on disk. Batched
/// classification can read a copy of those views instead, which needs the relation a predicate
/// names to be decided when the template is rendered rather than when it is built.
///
/// Templates keep naming the live views, and redirection happens only when a caller supplies a
/// binding. Without one no scan runs at all, so the runtime generation path pays nothing. Only
/// unquoted table sources in template text are scanned, never a rendered parameter value. Quoted
/// user expressions and identifiers therefore retain their original meaning. See D-014.
/// </remarks>
internal static class MySqlCatalogRelations
{
    /// <summary>The prefix every catalog relation name shares.</summary>
    public const string Prefix = "INFORMATION_SCHEMA.";

    private static readonly string[] s_views =
    [
        "INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS",
        "INFORMATION_SCHEMA.TABLE_CONSTRAINTS",
        "INFORMATION_SCHEMA.CHECK_CONSTRAINTS",
        "INFORMATION_SCHEMA.KEY_COLUMN_USAGE",
        "INFORMATION_SCHEMA.CHARACTER_SETS",
        "INFORMATION_SCHEMA.STATISTICS",
        "INFORMATION_SCHEMA.COLLATIONS",
        "INFORMATION_SCHEMA.COLUMNS",
        "INFORMATION_SCHEMA.TABLES",
    ];

    /// <summary>Gets the catalog relations a classification template may name.</summary>
    public static IReadOnlyList<string> All => s_views;

    /// <summary>Redirects unquoted catalog table sources in a complete statement.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="resolve">Maps a catalog relation to its replacement, or null to keep it.</param>
    /// <returns>The statement, unchanged when no binding is supplied.</returns>
    public static string Resolve(
        string sql,
        Func<string, string>? resolve
    )
    {
        ArgumentNullException.ThrowIfNull(sql);

        if (resolve is null
            || sql.IndexOf(Prefix, StringComparison.Ordinal) < 0)
        {
            return sql;
        }

        var builder = new StringBuilder(sql.Length);
        var reader = new Reader();
        reader.AppendResolved(builder, sql, 0, sql.Length, resolve);

        return builder.ToString();
    }

    private static string? MatchAt(
        string text,
        int start,
        int end
    )
    {
        // WHY: s_views is ordered longest first, so the first match is the longest one and a
        // shorter relation cannot consume the prefix of a longer one.
        foreach (var view in s_views)
        {
            var after = start + view.Length;
            if (after <= end
                && string.CompareOrdinal(text, start, view, 0, view.Length) == 0
                && (after == text.Length
                    || !IsIdentifierCharacter(text[after]) && text[after] is not ('.' or '\u001e')))
            {
                return view;
            }
        }

        return null;
    }

    private static bool IsIdentifierCharacter(
        char character
    ) => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '$'
        || character >= '\u0080';

    /// <summary>Retains lexical context while a template is rendered in spans separated by values.</summary>
    /// <remarks>
    /// WHY: Starting a new scan for each span can mistake the tail of a quoted expression or
    /// comment for SQL. One value-type reader per render keeps that context without allocating
    /// token lists or copying the whole template before parameter substitution.
    /// </remarks>
    internal struct Reader
    {
        private char _quote;
        private bool _lineComment;
        private bool _blockComment;
        private bool _verbatimRemainder;
        private bool _expectRelation;
        private ulong _queryScopes;
        private int _depth;

        /// <summary>Appends one template span, binding only supported unquoted FROM/JOIN sources.</summary>
        /// <param name="builder">The statement under construction.</param>
        /// <param name="text">The complete template.</param>
        /// <param name="start">The first character to append.</param>
        /// <param name="length">The number of characters to append.</param>
        /// <param name="resolve">The catalog binding, or null for the unchanged fast path.</param>
        public void AppendResolved(
            StringBuilder builder,
            string text,
            int start,
            int length,
            Func<string, string>? resolve
        )
        {
            if (resolve is null || _verbatimRemainder)
            {
                builder.Append(text, start, length);

                return;
            }

            var end = start + length;
            var copyStart = start;
            var position = start;
            while (position < end)
            {
                var character = text[position];
                var next = position + 1 < end ? text[position + 1] : '\0';
                if (_lineComment)
                {
                    _lineComment = character is not ('\r' or '\n');
                    position++;

                    continue;
                }

                if (_blockComment)
                {
                    if (character == '/' && next == '*')
                    {
                        // WHY: Nested block comments have engine-dependent acceptance. Keep
                        // their remainder verbatim instead of treating an inner close as SQL.
                        _verbatimRemainder = true;

                        break;
                    }

                    if (character == '*' && next == '/')
                    {
                        _blockComment = false;
                        position += 2;
                    }
                    else
                    {
                        position++;
                    }

                    continue;
                }

                if (_quote != '\0')
                {
                    if (character == '\\' && _quote != '`')
                    {
                        // WHY: Whether a backslash escapes the following quote depends on
                        // NO_BACKSLASH_ESCAPES. Without that session contract, keeping the rest
                        // on the live catalog is safer than rewriting a possible string body.
                        _verbatimRemainder = true;

                        break;
                    }

                    if (character == _quote)
                    {
                        if (next == _quote)
                        {
                            position += 2;

                            continue;
                        }

                        _quote = '\0';
                    }

                    position++;

                    continue;
                }

                if (character is '\'' or '"' or '`')
                {
                    _quote = character;
                    _expectRelation = false;
                    position++;

                    continue;
                }

                if (character == '#'
                    || character == '-' && next == '-'
                    && position + 2 < end && text[position + 2] <= ' ')
                {
                    _lineComment = true;
                    position++;

                    continue;
                }

                if (character == '/' && next == '*')
                {
                    if (IsExecutableComment(text, position, end))
                    {
                        // WHY: Executable comments can contribute SQL tokens depending on the
                        // engine and version. Treating their body as ordinary trivia could
                        // change the interpretation of everything after the comment.
                        _verbatimRemainder = true;

                        break;
                    }

                    _blockComment = true;
                    position += 2;

                    continue;
                }

                if (character is ' ' or '\t' or '\r' or '\n' or '\f' or '\v')
                {
                    position++;

                    continue;
                }

                if (IsIdentifierCharacter(character))
                {
                    if (_expectRelation && character == 'I')
                    {
                        var view = MatchAt(text, position, end);
                        if (view is not null)
                        {
                            builder.Append(text, copyStart, position - copyStart).Append(resolve(view));
                            position += view.Length;
                            copyStart = position;
                            _expectRelation = false;

                            continue;
                        }
                    }

                    var wordStart = position++;
                    while (position < end && (IsIdentifierCharacter(text[position]) || text[position] == '.'))
                    {
                        position++;
                    }

                    var word = text.AsSpan(wordStart, position - wordStart);
                    if (word.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                    {
                        _queryScopes |= 1UL << _depth;
                    }

                    _expectRelation = (wordStart == 0 || text[wordStart - 1] != '.')
                        && (_queryScopes & (1UL << _depth)) != 0
                        && (word.Equals("FROM", StringComparison.OrdinalIgnoreCase)
                            || word.Equals("JOIN", StringComparison.OrdinalIgnoreCase));

                    continue;
                }

                _expectRelation = false;
                if (character == '(')
                {
                    // WHY: FROM inside TRIM/EXTRACT is not a table source. Query scopes track
                    // SELECT-bearing parentheses without a heap-allocated parser stack. Shapes
                    // deeper than the fixed mask keep the live catalog rather than guessing.
                    if (++_depth == sizeof(ulong) * 8)
                    {
                        _verbatimRemainder = true;

                        break;
                    }
                }
                else if (character == ')' && _depth > 0)
                {
                    _queryScopes &= ~(1UL << _depth);
                    _depth--;
                }
                else if (character == ';')
                {
                    _queryScopes = 0;
                    _depth = 0;
                }

                position++;
            }

            builder.Append(text, copyStart, end - copyStart);
        }

        /// <summary>Consumes an opaque rendered value without interpreting its SQL text.</summary>
        public void ConsumeValue()
        {
            // WHY: Values are indivisible syntax supplied by the parameter renderer, not new
            // catalog sources. An embedded marker must not close an enclosing quote or comment.
            _expectRelation = false;
        }

        private static bool IsExecutableComment(
            string text,
            int start,
            int end
        ) => start + 2 < end && text[start + 2] == '!'
            || start + 3 < end && text[start + 2] == 'M' && text[start + 3] == '!';
    }
}
