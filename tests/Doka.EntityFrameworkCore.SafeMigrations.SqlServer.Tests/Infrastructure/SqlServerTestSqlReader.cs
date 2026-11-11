namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Reads only the lexical boundaries needed by generated-command test instruments.</summary>
/// <remarks>Quoted data remains one token; this reader neither normalizes SQL nor proves SQL admissibility.</remarks>
internal ref struct SqlServerTestSqlReader
{
    private readonly ReadOnlySpan<char> _sql;

    /// <summary>Creates an allocation-free cursor over one immutable native SQL scope.</summary>
    /// <param name="sql">The unchanged text at exactly one execution/escaping level.</param>
    public SqlServerTestSqlReader(
        ReadOnlySpan<char> sql
    )
    {
        _sql = sql;
    }

    /// <summary>Gets the exclusive end of the current token.</summary>
    public int Position { get; private set; }

    /// <summary>Gets the inclusive start of the current token.</summary>
    public int TokenStart { get; private set; }

    /// <summary>Gets the original token, including its delimiters and escapes.</summary>
    public readonly ReadOnlySpan<char> Token => _sql[TokenStart..Position];

    /// <summary>Gets whether the current token is a complete line or block comment.</summary>
    public bool IsComment { get; private set; }

    /// <summary>Advances without splitting quoted strings, identifiers or nested block comments.</summary>
    /// <returns>Whether another complete token exists.</returns>
    /// <exception cref="InvalidOperationException">A quoted token or block comment is unterminated.</exception>
    public bool Read()
    {
        while (Position < _sql.Length && char.IsWhiteSpace(_sql[Position]))
        {
            Position++;
        }

        TokenStart = Position;
        IsComment = false;

        if (Position == _sql.Length)
        {
            return false;
        }

        var character = _sql[Position++];

        if (character == '-' && Position < _sql.Length && _sql[Position] == '-')
        {
            IsComment = true;

            while (Position < _sql.Length && _sql[Position] is not ('\r' or '\n'))
            {
                Position++;
            }
        }
        else if (character == '/' && Position < _sql.Length && _sql[Position] == '*')
        {
            IsComment = true;
            Position++;
            var depth = 1;

            while (Position < _sql.Length && depth > 0)
            {
                if (Position + 1 < _sql.Length && _sql[Position] == '/' && _sql[Position + 1] == '*')
                {
                    depth++;
                    Position += 2;
                }
                else if (Position + 1 < _sql.Length && _sql[Position] == '*' && _sql[Position + 1] == '/')
                {
                    depth--;
                    Position += 2;
                }
                else
                {
                    Position++;
                }
            }

            if (depth != 0)
            {
                throw new InvalidOperationException("The test SQL contains an unterminated block comment.");
            }
        }
        else if (character is '\'' or '"' or '[')
        {
            ReadDelimited(character == '['
                ? ']'
                : character);
        }
        else if (IsWordCharacter(character))
        {
            while (Position < _sql.Length && IsWordCharacter(_sql[Position]))
            {
                Position++;
            }
        }

        return true;
    }

    /// <summary>Consumes one expected significant token, preserving the cursor on a mismatch.</summary>
    /// <param name="expected">The keyword, punctuation or exactly delimited identifier.</param>
    /// <param name="ignoreCase">Whether keyword spelling, rather than physical identifier identity, is matched.</param>
    /// <returns>Whether the next non-comment token matches.</returns>
    public bool TryRead(
        scoped ReadOnlySpan<char> expected,
        bool ignoreCase = true
    )
    {
        var candidate = this;

        while (candidate.Read())
        {
            if (candidate.IsComment)
            {
                continue;
            }

            if (!candidate.Token.Equals(expected,
                    ignoreCase
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return false;
            }

            this = candidate;

            return true;
        }

        return false;
    }

    /// <summary>Consumes one closed token pattern while retaining exact variable and quoted-name identity.</summary>
    /// <param name="pattern">The test instrument's fixed native syntax, not caller SQL to normalize.</param>
    /// <returns>Whether every significant pattern token matches without changing the cursor on failure.</returns>
    public bool TryReadSequence(
        scoped ReadOnlySpan<char> pattern
    )
    {
        var expected = new SqlServerTestSqlReader(pattern);
        var candidate = this;

        while (expected.Read())
        {
            if (!candidate.TryRead(expected.Token,
                    ignoreCase: expected.Token[0] is not ('@' or '[' or '"' or '\'')))
            {
                return false;
            }
        }

        this = candidate;

        return true;
    }

    /// <summary>Reads the next significant token while leaving its original source range available.</summary>
    /// <returns>Whether a non-comment token exists.</returns>
    public bool ReadSignificant()
    {
        while (Read())
        {
            if (!IsComment)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Consumes one quoted token while keeping doubled delimiters inside that token.</summary>
    /// <param name="closing">The native closing delimiter.</param>
    private void ReadDelimited(
        char closing
    )
    {
        while (Position < _sql.Length)
        {
            if (_sql[Position++] != closing)
            {
                continue;
            }

            if (Position < _sql.Length && _sql[Position] == closing)
            {
                Position++;

                continue;
            }

            return;
        }

        throw new InvalidOperationException("The test SQL contains an unterminated quoted token.");
    }

    /// <summary>Keeps keywords distinct from longer identifiers and private variable names.</summary>
    /// <param name="character">The character at the current lexical boundary.</param>
    /// <returns>Whether the character belongs to an unquoted word token.</returns>
    private static bool IsWordCharacter(
        char character
    ) => char.IsLetterOrDigit(character) || character is '_' or '@' or '#' or '$';
}
