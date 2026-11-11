namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exposes exactly one generated dynamic scope without unquoting its nested commands.</summary>
internal static class SqlServerGuardedSqlTestContract
{
    private const string ScopePrefix = "EXEC sys.sp_executesql N'";

    /// <summary>Decodes one complete, parameter-free generated dynamic batch.</summary>
    /// <param name="commandText">The generated command containing one outer dynamic scope.</param>
    /// <returns>The unchanged inner batch with only the outer literal escaping removed.</returns>
    public static string DecodeScope(
        string commandText
    )
    {
        var reader = new SqlServerTestSqlReader(commandText);

        if (!reader.TryReadSequence("EXEC sys.sp_executesql N")
            || !reader.ReadSignificant() || reader.Token[0] != '\'')
        {
            throw new InvalidOperationException("The guarded command has no isolated dynamic scope.");
        }

        // WHY: Read only one literal and require the original closed terminator;
        // nested SQL and trailing executable text must never be silently flattened.
        if (commandText.AsSpan(reader.Position).Trim() is not ";")
        {
            throw new InvalidOperationException("The guarded command has content outside its dynamic scope.");
        }

        return reader.Token[1..^1].ToString().Replace("''", "'", StringComparison.Ordinal);
    }

    /// <summary>Quotes a test batch using the generator's parameter-free dynamic-scope shape.</summary>
    /// <param name="body">The batch to quote without modifying its nested statements.</param>
    /// <returns>A complete dynamic batch with one additional SQL literal quoting level.</returns>
    public static string EncodeScope(
        string body
    ) => ScopePrefix + body.Replace("'", "''", StringComparison.Ordinal) + "';\n";

    /// <summary>Recognizes the native state declaration, not a marker-shaped caller value.</summary>
    /// <param name="commandText">One complete generated command at its outer transport level.</param>
    /// <returns>Whether the command owns a native operation guard behind its optional protected transport.</returns>
    public static bool IsOperationGuard(
        string commandText
    ) => DecodeOperationBody(commandText) is not null;

    /// <summary>Counts state declarations without interpreting quoted data, identifiers or comments as code.</summary>
    /// <param name="body">One native decoded scope, with nested execution literals left quoted.</param>
    /// <returns>The number of exact private state declarations at this scope level.</returns>
    public static int CountStateDeclarations(
        string body
    )
    {
        var reader = new SqlServerTestSqlReader(body);
        var count = 0;

        while (reader.Read())
        {
            if (!reader.IsComment && reader.Token.Equals("DECLARE", StringComparison.OrdinalIgnoreCase))
            {
                var declaration = reader;

                if (declaration.TryRead("@doka_state", ignoreCase: false))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Decodes only the generator's closed MAX-first literal/NCHAR transport expression.</summary>
    /// <param name="body">One native scope whose quoted VALUES have not been interpreted as code.</param>
    /// <returns>The exact protected payload, or null when this is not the owned transport.</returns>
    /// <exception cref="InvalidOperationException">
    /// A recognized transport has an unexpected expression or suffix.
    /// </exception>
    internal static string? DecodeProtectedPayload(
        string body
    )
    {
        var reader = new SqlServerTestSqlReader(body);

        if (!reader.TryReadSequence("DECLARE @doka_script_payload"))
        {
            return null;
        }

        if (!reader.TryReadSequence("nvarchar(max) = CAST(N'' AS nvarchar(max))"))
        {
            throw new InvalidOperationException("The protected test payload has an unexpected MAX-first prefix.");
        }

        var payload = new StringBuilder();

        while (reader.TryRead("+"))
        {
            if (reader.TryRead("N") && reader.ReadSignificant() && reader.Token[0] == '\'')
            {
                payload.Append(reader.Token[1..^1].ToString().Replace("''", "'", StringComparison.Ordinal));
            }
            else if (reader.TryReadSequence("NCHAR(10)"))
            {
                payload.Append('\n');
            }
            else if (reader.TryReadSequence("NCHAR(13)"))
            {
                payload.Append('\r');
            }
            else
            {
                throw new InvalidOperationException("The protected test payload has an unexpected expression.");
            }
        }

        if (!reader.TryReadSequence("; EXEC sys.sp_executesql @doka_script_payload;") || reader.ReadSignificant())
        {
            throw new InvalidOperationException("The protected test payload has an unexpected execution suffix.");
        }

        return payload.ToString();
    }

    /// <summary>Unwraps owned lossless transports without mistaking a nested execution for the outer guard.</summary>
    /// <param name="commandText">The generated command to classify at its native guard level.</param>
    /// <returns>The exact guard body, or null for another command role.</returns>
    private static string? DecodeOperationBody(
        string commandText
    )
    {
        // WHY: A CR/GO-bearing authored VALUE can add a lossless carrier outside
        // the guard. Follow only that closed protocol, never arbitrary quoted SQL.
        while (HasScopePrefix(commandText))
        {
            var body = DecodeScope(commandText);

            if (CountStateDeclarations(body) > 0)
            {
                return body;
            }

            if (DecodeProtectedPayload(body) is not { } payload)
            {
                return null;
            }

            commandText = payload;
        }

        return null;
    }

    /// <summary>Identifies the owned outer transport by tokens, independently of source layout.</summary>
    /// <param name="commandText">The complete candidate command, never an arbitrary quoted value.</param>
    /// <returns>Whether the native prefix belongs to the isolated transport.</returns>
    private static bool HasScopePrefix(
        string commandText
    )
    {
        var reader = new SqlServerTestSqlReader(commandText);

        return reader.TryReadSequence("EXEC sys.sp_executesql N");
    }

    /// <summary>Selects and decodes an operation guard without confusing the shared identifier preamble.</summary>
    /// <param name="context">The configured SQL Server context.</param>
    /// <param name="operation">The safe operation whose runtime guard is inspected.</param>
    /// <returns>The complete operation guard with its nested binding scopes intact.</returns>
    public static string GenerateBody(
        DbContext context,
        SafeMigrationOperation operation
    )
    {
        var command = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.Model)
            .Single(static candidate => IsOperationGuard(candidate.CommandText));

        return DecodeOperationBody(command.CommandText)
            ?? throw new InvalidOperationException("The selected generated command has no operation guard.");
    }
}
