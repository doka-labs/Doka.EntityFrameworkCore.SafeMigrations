namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerStandaloneForeignKeyProjectionTests
{
    /// <summary>Supplies real graph result shapes without pretending to prove runtime orphan absence.</summary>
    private sealed class GraphConnection(
        bool hasKey,
        string? occupiedKind = null,
        bool existingChild = false,
        string keyKind = "PK-default",
        bool? captureReadyOverride = null,
        IReadOnlyList<string>? principalColumns = null
    ) : System.Data.Common.DbConnection
    {
        /// <summary>Gets the complete ordered capture statements.</summary>
        public List<string> Statements { get; } = [];

        /// <inheritdoc />
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        /// <inheritdoc />
        public override string Database => "graph_fixture";

        /// <inheritdoc />
        public override string DataSource => "graph_fixture";

        /// <inheritdoc />
        public override string ServerVersion => "16.0";

        /// <inheritdoc />
        public override ConnectionState State => ConnectionState.Open;

        /// <inheritdoc />
        public override void Open() { }

        /// <inheritdoc />
        public override void Close() { }

        /// <inheritdoc />
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override System.Data.Common.DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => throw new NotSupportedException();

        /// <inheritdoc />
        protected override System.Data.Common.DbCommand CreateDbCommand() => new GraphCommand(this);

        /// <summary>Returns only the requested compact metadata and Boolean storage certificates.</summary>
        public DataTableReader Read(
            string sql
        )
        {
            Statements.Add(sql);
            var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            if (sql.StartsWith("SELECT object_id, parent_object_id", StringComparison.Ordinal))
            {
                AddColumns(table, typeof(int), typeof(int), typeof(int), typeof(string), typeof(byte),
                    typeof(byte), typeof(int));
            }
            else if (sql.StartsWith("SELECT requested.schema_name", StringComparison.Ordinal))
            {
                AddColumns(table, typeof(string), typeof(string), typeof(string), typeof(string), typeof(int),
                    typeof(int), typeof(byte), typeof(bool), typeof(int), typeof(int), typeof(bool), typeof(bool),
                    typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string));

                var matches = System.Text.RegularExpressions.Regex.Matches(sql,
                    @"\(N'(?<schema>[^']*)', N'(?<table>[^']*)', "
                    + @"(?<column>N'[^']*'|CONVERT\(nvarchar\(128\), NULL\)), "
                    + @"(?<name>N'[^']*'|CONVERT\(nvarchar\(128\), NULL\))\)");

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var parent = match.Groups["table"].Value == "parent";
                    var other = match.Groups["table"].Value == "other";
                    var column = Unquote(match.Groups["column"].Value);
                    var columnOrdinal = principalColumns is null ? 0
                        : principalColumns.Select((name, ordinal) => (name, ordinal))
                            .FirstOrDefault(pair => pair.name == column).ordinal;

                    var name = Unquote(match.Groups["name"].Value);
                    var occupied = name == "FK_child" && occupiedKind is not null;

                    table.Rows.Add(match.Groups["schema"].Value, match.Groups["table"].Value,
                        column, name, 1, parent ? 100 : other ? 200 : existingChild ? 300 : DBNull.Value,
                        parent && column is not null ? (byte)56 : DBNull.Value,
                        parent && column is not null ? false : DBNull.Value,
                        parent && column is not null ? 0 : DBNull.Value,
                        other && name == "FK_child" && occupiedKind == "F" ? 700 : DBNull.Value, false, false,
                        parent && hasKey && name == (keyKind == "PK-default" ? "PK_parent" : "IX_parent")
                            ? 1 : DBNull.Value,
                        parent && hasKey && name == (keyKind == "PK-default" ? "PK_parent" : keyKind + "_parent")
                            && keyKind != "IX" ? 1 : DBNull.Value,
                        parent && column is not null ? columnOrdinal + 7 : DBNull.Value,
                        parent ? 100 : other ? 200 : existingChild ? 300 : DBNull.Value,
                        occupied ? 700 : DBNull.Value,
                        occupied ? occupiedKind is "F" or "PK" or "UQ" or "C" ? 200 : 0 : DBNull.Value,
                        occupied ? occupiedKind : DBNull.Value);
                }
            }
            else if (sql.StartsWith("SELECT i.object_id", StringComparison.Ordinal))
            {
                AddColumns(table, typeof(int), typeof(int), typeof(string), typeof(string), typeof(string),
                    typeof(int), typeof(string));
                if (hasKey)
                {
                    var names = principalColumns ?? ["Id"];
                    for (var ordinal = 0; ordinal < names.Count; ordinal++)
                    {
                        table.Rows.Add(100, 1, keyKind == "PK-default" ? "PK_parent" : "IX_parent",
                            keyKind == "IX" ? DBNull.Value
                                : keyKind == "PK-default" ? "PK_parent" : keyKind + "_parent",
                            names[ordinal], ordinal + 7,
                            keyKind == "IX" ? DBNull.Value : keyKind == "PK-default" ? "PK" : keyKind);
                    }
                }
            }
            else if (sql.Contains("CONVERT(bit, CASE WHEN", StringComparison.Ordinal))
            {
                AddColumns(table, typeof(int), typeof(bool));
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                    sql, @"SELECT (?<ordinal>\d+), CONVERT\(bit, CASE WHEN (?<ready>[01]) = 1"))
                {
                    table.Rows.Add(int.Parse(match.Groups["ordinal"].Value, CultureInfo.InvariantCulture),
                        captureReadyOverride ?? match.Groups["ready"].Value == "1");
                }
            }
            else
            {
                throw new InvalidOperationException("Unowned graph capture statement.");
            }

            return table.CreateDataReader();
        }

        private static string? Unquote(string token) => token.StartsWith("N'", StringComparison.Ordinal)
            ? token[2..^1] : null;

        private static void AddColumns(DataTable table, params Type[] types)
        {
            for (var ordinal = 0; ordinal < types.Length; ordinal++)
            {
                table.Columns.Add("column_" + ordinal, types[ordinal]);
            }
        }
    }

    /// <summary>Keeps command/reader ownership identical to the direct compact graph capture.</summary>
    private sealed class GraphCommand(GraphConnection connection) : System.Data.Common.DbCommand
    {
        private readonly SqlCommand _parameterOwner = new();

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        /// <inheritdoc />
        public override int CommandTimeout { get; set; }

        /// <inheritdoc />
        public override CommandType CommandType { get; set; }

        /// <inheritdoc />
        public override bool DesignTimeVisible { get; set; }

        /// <inheritdoc />
        public override UpdateRowSource UpdatedRowSource { get; set; }

        /// <inheritdoc />
        protected override System.Data.Common.DbConnection? DbConnection { get; set; } = connection;

        /// <inheritdoc />
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }

        /// <inheritdoc />
        protected override System.Data.Common.DbParameterCollection DbParameterCollection
            => _parameterOwner.Parameters;

        /// <inheritdoc />
        public override void Cancel() { }

        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new NotSupportedException();

        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Prepare() { }

        /// <inheritdoc />
        protected override System.Data.Common.DbParameter CreateDbParameter() => new SqlParameter();

        /// <inheritdoc />
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => connection.Read(CommandText);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _parameterOwner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
