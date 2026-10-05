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
        if (!commandText.StartsWith(ScopePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The guarded command has no isolated dynamic scope.");
        }

        var body = new StringBuilder(commandText.Length);
        for (var offset = ScopePrefix.Length; offset < commandText.Length; offset++)
        {
            var character = commandText[offset];
            if (character != '\'')
            {
                body.Append(character);

                continue;
            }

            if (offset + 1 < commandText.Length && commandText[offset + 1] == '\'')
            {
                body.Append('\'');
                offset++;

                continue;
            }

            if (!commandText.AsSpan(offset + 1).Trim().SequenceEqual(";"))
            {
                throw new InvalidOperationException("The guarded command has content outside its dynamic scope.");
            }

            return body.ToString();
        }

        throw new InvalidOperationException("The guarded command has an unterminated dynamic scope.");
    }

    /// <summary>Quotes a test batch using the generator's parameter-free dynamic-scope shape.</summary>
    /// <param name="body">The batch to quote without modifying its nested statements.</param>
    /// <returns>A complete dynamic batch with one additional SQL literal quoting level.</returns>
    public static string EncodeScope(
        string body
    ) => ScopePrefix + body.Replace("'", "''", StringComparison.Ordinal) + "';\n";

    /// <summary>Selects and decodes an operation guard without confusing the shared identifier preamble.</summary>
    /// <param name="context">The configured SQL Server context.</param>
    /// <param name="operation">The safe operation whose runtime guard is inspected.</param>
    /// <returns>The complete operation guard with its nested binding scopes intact.</returns>
    public static string GenerateBody(
        DbContext context,
        SafeMigrationOperation operation
    )
    {
        var command = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model)
            .Single(static candidate => candidate.CommandText.Contains("DECLARE @doka_state", StringComparison.Ordinal));

        return DecodeScope(command.CommandText);
    }
}
