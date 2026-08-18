namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlCatalogQueryParameterizer
{
    private const string Utf8HexPrefix = "_utf8mb4 X'";

    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly DbCommand _command;

    public MySqlCatalogQueryParameterizer(
        DbCommand command
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        _command = command;
    }

    public string Parameterize(
        string sql
    )
    {
        ArgumentNullException.ThrowIfNull(sql);

        var result = new StringBuilder(sql.Length);
        for (var index = 0; index < sql.Length;)
        {
            if (sql
                .AsSpan(index)
                .StartsWith(Utf8HexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = ReadHexLiteral(sql, ref index);
                result.Append(AddString(value));
                continue;
            }

            if (sql[index] == '`')
            {
                CopyDelimitedIdentifier(sql, result, ref index);
                continue;
            }

            if (sql[index] == '\'')
            {
                var value = ReadQuotedLiteral(sql, ref index);
                result.Append(AddString(value));
                continue;
            }

            result.Append(sql[index]);
            index++;
        }

        return result.ToString();
    }

    private string AddString(
        string value
    )
    {
        var name = $"@doka_sm_p{_command.Parameters.Count.ToString(CultureInfo.InvariantCulture)}";
        var parameter = _command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _command.Parameters.Add(parameter);

        return name;
    }

    private static string ReadHexLiteral(
        string sql,
        ref int index
    )
    {
        var contentStart = index + Utf8HexPrefix.Length;
        var closingQuote = sql.IndexOf('\'', contentStart);
        if (closingQuote < 0)
        {
            throw new InvalidOperationException("The generated MySQL hexadecimal literal is not terminated.");
        }

        var hex = sql.AsSpan(contentStart, closingQuote - contentStart);
        if (hex.Length % 2 != 0)
        {
            throw new InvalidOperationException("The generated MySQL hexadecimal literal has an odd length.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The generated MySQL hexadecimal literal is invalid.", exception);
        }

        index = closingQuote + 1;

        return s_strictUtf8.GetString(bytes);
    }

    private static string ReadQuotedLiteral(
        string sql,
        ref int index
    )
    {
        var result = new StringBuilder();
        index++;
        while (index < sql.Length)
        {
            var value = sql[index++];
            if (value == '\\')
            {
                throw new InvalidOperationException(
                    "A generated MySQL catalog literal used a SQL-mode-dependent backslash escape.");
            }

            if (value != '\'')
            {
                result.Append(value);
                continue;
            }

            if (index < sql.Length
                && sql[index] == '\'')
            {
                result.Append('\'');
                index++;
                continue;
            }

            return result.ToString();
        }

        throw new InvalidOperationException("The generated MySQL quoted literal is not terminated.");
    }

    private static void CopyDelimitedIdentifier(
        string sql,
        StringBuilder result,
        ref int index
    )
    {
        result.Append(sql[index++]);
        while (index < sql.Length)
        {
            var value = sql[index++];
            result.Append(value);
            if (value != '`')
            {
                continue;
            }

            if (index < sql.Length
                && sql[index] == '`')
            {
                result.Append(sql[index++]);
                continue;
            }

            return;
        }

        throw new InvalidOperationException("The generated MySQL delimited identifier is not terminated.");
    }
}
