namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public abstract partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>Counts real version queries, executed once at the start of every catalog capture.</summary>
    protected sealed class CatalogReadCounter
    {
        /// <summary>Observes a real catalog command while returning the unchanged native engine version.</summary>
        public CatalogReadCounter(
            SqliteConnection connection,
            Action? onRead = null
        )
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();";
            var version = (string)(command.ExecuteScalar()
                ?? throw new InvalidOperationException("The SQLite engine version was not returned."));

            // WHY: Inventory uses direct ADO.NET commands, so EF interceptors
            // do not observe it. Replacing only this scalar function counts
            // captures without retaining SQL, changing catalog values or
            // adding a production observability seam.
            connection.CreateFunction(
                "sqlite_version",
                () =>
                {
                    Count++;
                    onRead?.Invoke();

                    return version;
                });
        }

        /// <summary>Gets the number of complete catalog captures started on this connection.</summary>
        public int Count { get; private set; }
    }
}
