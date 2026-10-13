namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    private const string NativeSqlModeStatementPrefix = "/*! SET ";
    private const string NativeSqlModeStatementSuffix = " */;";
    private const string NativeSqlModeSnapshotAssignment = "@__doka_previous_sql_mode = @@SESSION.sql_mode";
    private const string NativeSqlModeRestoreAssignment = "SESSION sql_mode = ";
    private const string NativeSqlModeSavedValue = "@__doka_previous_sql_mode";
    private const string NativeSqlModeSnapshot = NativeSqlModeStatementPrefix + NativeSqlModeSnapshotAssignment
        + NativeSqlModeStatementSuffix;
    private const string NativeSqlModeMutation = "/*! SET SESSION sql_mode = IF("
        + "FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode), @@SESSION.sql_mode, "
        + "CONCAT_WS(',', NULLIF(@@SESSION.sql_mode, ''), 'NO_BACKSLASH_ESCAPES')) */;";
    private const string NativeSqlModeRestore = NativeSqlModeStatementPrefix + NativeSqlModeRestoreAssignment
        + NativeSqlModeSavedValue + NativeSqlModeStatementSuffix;
    private const string AcquiredNativeSqlModeSnapshot = NativeSqlModeStatementPrefix + NativeSqlModeSnapshotAssignment
        + ", @doka_sm_provider_setup_entered = 1" + NativeSqlModeStatementSuffix;
    private const string ConditionalNativeSqlModeRestore = NativeSqlModeStatementPrefix + NativeSqlModeRestoreAssignment
        + "IF(COALESCE(@doka_sm_provider_setup_entered = 1, FALSE), " + NativeSqlModeSavedValue
        + ", @@SESSION.sql_mode)" + NativeSqlModeStatementSuffix;
    private const string ProviderSetupEnteredReset = "SET @doka_sm_provider_setup_entered = 0;";
    private const string ProviderSetupEnteredCleanup = "SET @doka_sm_provider_setup_entered = NULL; ";
    private const string ProviderSqlWhitespace = " \t\r\n\v\f";

    /// <summary>Certifies the provider's bounded SQL-mode scope before admitting failure-path cleanup.</summary>
    /// <param name="baseline">The original provider scope whose command boundaries remain unchanged.</param>
    /// <returns>Whether the scope owns the exact native SQL-mode snapshot and restoration.</returns>
    private static bool HasCertifiedNativeSqlModeCleanup(BaselineBatch baseline)
    {
        for (var index = 0; index < baseline.Commands.Count; index++)
        {
            var command = baseline.Commands[index];

            if (command.CleanupCommands.Count == 0)
            {
                // WHY: Validated scoped specifications require both setup
                // and cleanup. Only body-only specifications reach this path.
                continue;
            }

            if (command.CleanupCommands.Count == 1
                && command.CleanupCommands[0].AsSpan().Trim(ProviderSqlWhitespace)
                    .SequenceEqual(NativeSqlModeRestore)
                && command.SetupCommands.Count == 2
                && command.SetupCommands[0].AsSpan().Trim(ProviderSqlWhitespace)
                    .SequenceEqual(NativeSqlModeSnapshot)
                && command.SetupCommands[1].AsSpan().Trim(ProviderSqlWhitespace)
                    .SequenceEqual(NativeSqlModeMutation))
            {
                return true;
            }

            // WHY: An arbitrary resource can be partially acquired before a
            // failing setup statement. Neither a pre-setup nor a post-setup
            // flag certifies its cleanup. Only the known snapshot scope or
            // entirely non-mutating literal DO fixtures have that proof.
            if (command.SetupCommands.Any(static sql => !IsNonMutatingProviderStatement(sql))
                || command.CleanupCommands.Any(static sql => !IsNonMutatingProviderStatement(sql)))
            {
                throw new InvalidOperationException(
                    "A provider cleanup acquisition cannot be certified for this native scope.");
            }
        }

        return false;
    }

    /// <summary>Recognizes one non-mutating integer DO statement without admitting executable suffixes.</summary>
    /// <param name="sql">The complete original provider fragment, including its terminator and comments.</param>
    /// <returns>Whether the fragment contains only a literal statement and ordinary trailing comments.</returns>
    private static bool IsNonMutatingProviderStatement(string sql)
    {
        var text = sql.AsSpan().Trim(ProviderSqlWhitespace);
        if (text.Length < 4 || !text.StartsWith("DO", StringComparison.OrdinalIgnoreCase)
            || !ProviderSqlWhitespace.Contains(text[2], StringComparison.Ordinal))
        {
            return false;
        }

        var offset = 2;
        while (offset < text.Length && ProviderSqlWhitespace.Contains(text[offset], StringComparison.Ordinal))
        {
            offset++;
        }

        var firstDigit = offset;
        while (offset < text.Length && text[offset] is >= '0' and <= '9')
        {
            offset++;
        }

        if (firstDigit == offset)
        {
            return false;
        }

        while (offset < text.Length && ProviderSqlWhitespace.Contains(text[offset], StringComparison.Ordinal))
        {
            offset++;
        }

        if (offset == text.Length || text[offset++] != ';')
        {
            return false;
        }

        while (offset < text.Length)
        {
            if (ProviderSqlWhitespace.Contains(text[offset], StringComparison.Ordinal))
            {
                offset++;

                continue;
            }

            var remaining = text[offset..];
            if (remaining.StartsWith("/*", StringComparison.Ordinal))
            {
                if (remaining.Length < 4 || remaining[2] is '!' or '+'
                    || (remaining[2] is 'M' or 'm' && remaining[3] == '!'))
                {
                    return false;
                }

                var end = remaining[2..].IndexOf("*/", StringComparison.Ordinal);
                if (end < 0 || remaining.Slice(2, end).Contains("/*", StringComparison.Ordinal))
                {
                    return false;
                }

                offset += end + 4;

                continue;
            }

            if (remaining[0] == '#' || (remaining.Length > 2
                    && remaining.StartsWith("--", StringComparison.Ordinal)
                    && ProviderSqlWhitespace.Contains(remaining[2], StringComparison.Ordinal)))
            {
                var end = remaining.IndexOfAny('\r', '\n');
                if (end < 0)
                {
                    return true;
                }

                offset += end + 1;

                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>Records acquisition only in the exact statement that obtains the fresh native snapshot.</summary>
    /// <param name="snapshot">The previously certified native snapshot fragment.</param>
    /// <returns>The original fragment with a statement-local acquisition assignment.</returns>
    internal static string BuildAcquiredNativeSqlModeSnapshot(string snapshot) =>
        BindCertifiedNativeSqlModeStatement(snapshot, NativeSqlModeSnapshot, AcquiredNativeSqlModeSnapshot);

    /// <summary>Leaves current mode unchanged when rejection or interruption prevented native acquisition.</summary>
    /// <param name="restore">The previously certified native restoration fragment.</param>
    /// <returns>The conditional restoration with its original executable-comment and transport boundaries.</returns>
    internal static string BuildConditionalNativeSqlModeRestore(string restore) =>
        BindCertifiedNativeSqlModeStatement(restore, NativeSqlModeRestore, ConditionalNativeSqlModeRestore);

    /// <summary>Binds one exact certified statement without rewriting arbitrary provider SQL.</summary>
    /// <param name="source">The original transport fragment, including surrounding whitespace.</param>
    /// <param name="expected">The shared complete native statement certificate.</param>
    /// <param name="bound">The shared structurally composed acquisition or conditional restoration.</param>
    /// <returns>The bound statement with unchanged outer whitespace and one string allocation.</returns>
    private static string BindCertifiedNativeSqlModeStatement(string source, string expected, string bound)
    {
        ArgumentNullException.ThrowIfNull(source);

        var text = source.AsSpan();
        var start = text.Length - text.TrimStart(ProviderSqlWhitespace).Length;
        var statement = text[start..].TrimEnd(ProviderSqlWhitespace);
        if (!statement.SequenceEqual(expected))
        {
            throw new InvalidOperationException("The certified native SQL-mode statement could not be bound.");
        }

        // WHY: Shared statement parts make acquisition and restoration follow
        // their native certificates. Exact binding rejects constant drift or
        // executable suffixes instead of silently accepting a no-op Replace.
        return string.Create(checked(source.Length - expected.Length + bound.Length),
            (Source: source, Start: start, ExpectedLength: expected.Length, Bound: bound),
            static (destination, state) =>
            {
                state.Source.AsSpan(0, state.Start).CopyTo(destination);
                state.Bound.AsSpan().CopyTo(destination[state.Start..]);
                state.Source.AsSpan(state.Start + state.ExpectedLength)
                    .CopyTo(destination[(state.Start + state.Bound.Length)..]);
            });
    }
}
