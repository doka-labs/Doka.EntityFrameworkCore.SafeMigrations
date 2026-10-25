namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Preserves Unicode strings across SQL Server's own literal continuation rules.</summary>
internal static class SqlServerSqlStringLiteral
{
    /// <summary>Renders exact string data without allowing native backslash-newline continuation.</summary>
    /// <param name="value">The original Unicode text, not executable SQL.</param>
    /// <returns>A Unicode literal, or a MAX-first concatenation when a native literal would change the value.</returns>
    internal static string Render(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!RequiresExpression(value.AsSpan()))
        {
            return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        // WHY: SQL Server itself consumes backslashes before LF or CR in N'...'.
        // Encoding only an outer script layer does not protect nested literals.
        // Keep MAX first so long input cannot truncate at 4000 UTF-16 units.
        var builder = new StringBuilder(value.Length);
        builder.Append("(CAST(N'' AS nvarchar(max)) + N'");
        var remaining = value.AsSpan();
        int newline;
        while ((newline = remaining.IndexOfAny('\r', '\n')) >= 0)
        {
            AppendEscaped(builder, remaining.Slice(0, newline));
            builder.Append(remaining[newline] == '\r' ? "' + NCHAR(13) + N'" : "' + NCHAR(10) + N'");
            remaining = remaining.Slice(newline + 1);
        }

        AppendEscaped(builder, remaining);
        builder.Append("')");

        return builder.ToString();
    }

    /// <summary>Identifies the only native quoted-string continuation forms without allocating.</summary>
    /// <param name="value">The original string content.</param>
    /// <returns>Whether the string requires an expression rather than one native quoted literal.</returns>
    internal static bool RequiresExpression(ReadOnlySpan<char> value)
        => value.IndexOf("\\\n") >= 0 || value.IndexOf("\\\r") >= 0;

    /// <summary>Binds a statement expression when sp_executesql cannot receive a quoted literal.</summary>
    /// <param name="sql">The exact dynamic statement text.</param>
    /// <param name="variable">A unique provider-owned variable in the existing private scope.</param>
    /// <param name="declaration">The required declaration, or empty text for an ordinary literal.</param>
    /// <returns>A legal Unicode constant or local variable accepted by sp_executesql.</returns>
    internal static string StatementArgument(
        string sql,
        string variable,
        out string declaration
    )
        => ProcedureArgument(sql, Render(sql), variable, out declaration);

    /// <summary>Binds an already rendered input argument without discarding analysis parameter capture.</summary>
    /// <param name="original">The original Unicode data used to identify native continuation hazards.</param>
    /// <param name="rendered">Its existing provider-owned literal or captured SQL parameter.</param>
    /// <param name="variable">A unique provider-owned local variable, never caller input.</param>
    /// <param name="declaration">The required declaration, or empty text for an ordinary input.</param>
    /// <returns>A legal constant or variable that can be passed directly to a stored procedure.</returns>
    internal static string ProcedureArgument(
        string original,
        string rendered,
        string variable,
        out string declaration
    )
    {
        if (!RequiresExpression(original.AsSpan()))
        {
            declaration = string.Empty;

            return rendered;
        }

        // WHY: sp_executesql accepts a Unicode constant or variable, not a
        // concatenation expression. Keep output-parameter bindings on the
        // original call rather than inserting a parameter-blind EXEC layer.
        declaration = "DECLARE " + variable + " nvarchar(max) = " + rendered + "; ";

        return variable;
    }

    /// <summary>Appends doubled apostrophes while preserving every other UTF-16 unit.</summary>
    /// <param name="builder">The destination buffer.</param>
    /// <param name="value">The original literal content.</param>
    internal static void AppendEscaped(
        StringBuilder builder,
        ReadOnlySpan<char> value
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        int apostrophe;
        while ((apostrophe = value.IndexOf('\'')) >= 0)
        {
            builder.Append(value.Slice(0, apostrophe)).Append("''");
            value = value.Slice(apostrophe + 1);
        }

        builder.Append(value);
    }
}
