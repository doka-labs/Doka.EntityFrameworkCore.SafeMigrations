namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>
    /// Captures exact native index presence and every bound FK through the bounded catalog transport.
    /// </summary>
    /// <param name="command">The owned native catalog command.</param>
    /// <param name="intent">The exact authored physical index removal.</param>
    /// <param name="isMariaDb">Whether the native MariaDB catalog has no MySQL array-index contract.</param>
    /// <returns>The aggregate UTF-8 parameter payload including bounded transport overhead.</returns>
    internal static int BuildMultiValuedIndexDropProbe(
        SafeMigrationCatalogCommand command,
        DropIndexIntent intent,
        bool isMariaDb
    )
    {
        var valueBytes = 0;
        var table = AddLivePrerequisiteParameter(command, intent.Table, ref valueBytes);
        var index = AddLivePrerequisiteParameter(command, intent.Name, ref valueBytes);
        var exists = IsSupportingIndexNameOccupied(table, index);
        var aliases = BuildMultiValuedIndexBoundForeignKeysQuery(table, index, isMariaDb);

        // WHY: The owned sentinel distinguishes a complete empty inventory
        // from a missing result set. Physical aliases stream independently,
        // never through GROUP_CONCAT or a guessed maximum number of FKs.
        command.CommandText = "SELECT certificate.index_exists, bound_fk.CONSTRAINT_NAME FROM (SELECT "
            + exists + " AS index_exists) certificate LEFT JOIN (" + aliases
            + ") bound_fk ON TRUE ORDER BY bound_fk.CONSTRAINT_NAME;";

        return valueBytes;
    }
}
