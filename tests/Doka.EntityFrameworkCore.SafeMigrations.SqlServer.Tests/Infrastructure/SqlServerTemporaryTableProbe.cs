namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Observes one exact local temporary table in the supplied session without enumerating tempdb.</summary>
internal static class SqlServerTemporaryTableProbe
{
    /// <summary>Creates an unopened, parameterized object lookup on the existing native session.</summary>
    /// <param name="connection">The session whose local name resolution is required.</param>
    /// <param name="localName">The complete single-hash local temporary table name, at most 116 characters.</param>
    /// <param name="transaction">The caller's existing transaction, when present.</param>
    /// <returns>A command owned by the caller; no connection or transaction is opened.</returns>
    internal static SqlCommand CreateObjectIdCommand(
        SqlConnection connection,
        string localName,
        SqlTransaction? transaction = null
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(localName);
        if (localName.Length is < 2 or > 116 || localName[0] != '#' || localName[1] == '#')
        {
            throw new ArgumentException("An exact local temporary table name is required.", nameof(localName));
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;

        // WHY: A prefix scan sees other sessions' metadata and can wait for
        // their DDL transactions. OBJECT_ID resolves only this session's local
        // name; quoting and parameters also preserve literal punctuation.
        command.CommandText = "SELECT OBJECT_ID(@qualified_name, N'U');";
        command.Parameters.Add("@qualified_name", SqlDbType.NVarChar, 258).Value =
            "tempdb..[" + localName.Replace("]", "]]", StringComparison.Ordinal) + "]";

        return command;
    }

    /// <summary>Returns the exact session-local object identity, or null when that object is absent.</summary>
    /// <param name="connection">An already-open native session retained through observation.</param>
    /// <param name="localName">The exact local temporary table name.</param>
    /// <param name="transaction">The caller's existing transaction, when present.</param>
    /// <returns>The native identity without substituting objects from another session.</returns>
    internal static async Task<int?> ReadObjectIdAsync(
        SqlConnection connection,
        string localName,
        SqlTransaction? transaction = null
    )
    {
        await using var command = CreateObjectIdCommand(connection, localName, transaction);
        var value = await command.ExecuteScalarAsync(CancellationToken.None);

        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
