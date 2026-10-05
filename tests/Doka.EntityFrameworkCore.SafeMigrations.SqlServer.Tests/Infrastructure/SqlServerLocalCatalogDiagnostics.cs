namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Replays a small complete capture on an explicitly supplied local diagnostic server.</summary>
internal static partial class SqlServerLocalCatalogDiagnostics
{
    /// <summary>Compares all nine result columns, native dispatch and logical connection reset behavior.</summary>
    internal static async Task RunAsync(
        string rootConnectionString,
        IReadOnlyList<string> shared,
        IReadOnlyList<string> standalone,
        IReadOnlyList<SqlParameter[]> parameters
    )
    {
        var rootSettings = new SqlConnectionStringBuilder(rootConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };

        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_DIAGNOSTIC_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "sm-sqlserver-diagnostics-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(output);
        var rootString = rootSettings.ConnectionString;
        await using var root = new SqlConnection(rootString);
        await root.OpenAsync();

        // WHY: Fresh uniquely owned databases avoid resetting another application's procedure cache.
        // Both shapes use identical catalogs, source parameters, ordinals and fixture data.
        var literalRows = await ReplayAsync(root, rootString, standalone, parameters, "literal", output);
        var sharedRows = await ReplayAsync(root, rootString, shared, parameters, "shared", output);
        if (!literalRows.SequenceEqual(sharedRows, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Local classifier replay changed a nine-column result.");
        }

        await VerifyConnectionResetAsync(root, rootString, output);
        await VerifyManagedTypesAsync(root, rootString, output);
    }

    /// <summary>Executes complete statements repeatedly and records raw statistics without summing nested scopes.</summary>
    private static async Task<string[]> ReplayAsync(
        SqlConnection root,
        string rootString,
        IReadOnlyList<string> statements,
        IReadOnlyList<SqlParameter[]> parameters,
        string shape,
        string output
    )
    {
        var database = await CreateDatabaseAsync(root);
        try
        {
            await using var connection = new SqlConnection(
                SqlServerContainerFixture.BuildTestConnectionString(rootString, database));

            await connection.OpenAsync();
            await ExecuteAsync(connection, BuildFixtureSql());
            await ExecuteAsync(connection, "SET STATISTICS IO ON; SET STATISTICS TIME ON;");
            var statistics = new List<string>();
            var timings = new List<string>();
            connection.InfoMessage += (_, arguments) => statistics.Add(arguments.Message);
            string[]? expectedRows = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var rows = new List<string>();
                var started = Stopwatch.GetTimestamp();
                for (var index = 0; index < statements.Count; index++)
                {
                    statistics.Clear();
                    var statementStarted = Stopwatch.GetTimestamp();
                    await using var command = connection.CreateCommand();
                    command.CommandText = statements[index];
                    command.CommandTimeout = 180;
                    foreach (var parameter in parameters[index])
                    {
                        command.Parameters.Add((SqlParameter)((ICloneable)parameter).Clone());
                    }

                    await using var reader = await command.ExecuteReaderAsync();
                    rows.AddRange(await ReadRowsAsync(reader));
                    var elapsed = Stopwatch.GetElapsedTime(statementStarted).TotalMilliseconds;
                    timings.Add(FormattableString.Invariant(
                        $"attempt={attempt}; statement={index}; elapsed-ms={elapsed:F3}; chars={statements[index].Length}"));

                    await File.WriteAllLinesAsync(Path.Combine(output, $"{shape}-statistics-{attempt}-{index}.log"),
                        statistics);

                    await File.WriteAllTextAsync(Path.Combine(output, $"{shape}-statement-{index}.sql"), statements[index]);
                }

                timings.Add(FormattableString.Invariant(
                    $"attempt={attempt}; elapsed-ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}; rows={rows.Count}"));

                rows.Sort(StringComparer.Ordinal);
                expectedRows ??= rows.ToArray();
                if (!expectedRows.SequenceEqual(rows, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("A repeated local classifier replay changed its evidence.");
                }

                await File.WriteAllLinesAsync(Path.Combine(output, $"{shape}-rows-{attempt}.jsonl"), rows);
            }

            await ExportCachedPlansAsync(connection, Path.Combine(output, shape + "-cached-plans.jsonl"));

            // WHY: SqlBatch scopes are provider-owned. A sequential replay alone cannot prove that
            // repeated statement-local variable names and ordinal bindings also survive native dispatch.
            await using var batch = new SqlBatch(connection) { Timeout = 180 };
            for (var index = 0; index < statements.Count; index++)
            {
                var command = new SqlBatchCommand(statements[index]);
                foreach (var parameter in parameters[index])
                {
                    command.Parameters.Add((SqlParameter)((ICloneable)parameter).Clone());
                }

                batch.BatchCommands.Add(command);
            }

            var nativeStarted = Stopwatch.GetTimestamp();
            await using var nativeReader = await batch.ExecuteReaderAsync();
            var nativeRows = await ReadRowsAsync(nativeReader);
            nativeRows.Sort(StringComparer.Ordinal);
            if (!expectedRows!.SequenceEqual(nativeRows, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Native local classifier dispatch changed its evidence.");
            }

            timings.Add(FormattableString.Invariant(
                $"native; elapsed-ms={Stopwatch.GetElapsedTime(nativeStarted).TotalMilliseconds:F3}; rows={nativeRows.Count}"));

            await File.WriteAllLinesAsync(Path.Combine(output, shape + "-timing.log"), timings);

            return expectedRows ?? throw new InvalidOperationException("Local classifier replay returned no capture.");
        }
        finally
        {
            await DropDatabaseAsync(root, database);
        }
    }

    /// <summary>Exports bounded cache evidence for this helper's database, without aggregating overlapping scopes.</summary>
    private static async Task ExportCachedPlansAsync(SqlConnection connection, string path)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP (128) cp.usecounts, cp.objtype, qs.execution_count, "
            + "qs.total_worker_time, qs.total_logical_reads, qs.statement_start_offset, qs.statement_end_offset, st.text "
            + "FROM sys.dm_exec_cached_plans cp "
            + "CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st "
            + "CROSS APPLY sys.dm_exec_plan_attributes(cp.plan_handle) pa "
            + "LEFT JOIN sys.dm_exec_query_stats qs ON qs.plan_handle = cp.plan_handle "
            + "WHERE pa.attribute = N'dbid' AND CONVERT(int, pa.value) = DB_ID() "
            + "AND st.text LIKE N'%@doka_ordinal%' ORDER BY cp.usecounts DESC;";

        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(System.Text.Json.JsonSerializer.Serialize(values));
        }

        await File.WriteAllLinesAsync(path, rows);
    }

    /// <summary>Reads only ordinary nine-column classifier results; malformed shapes or duplicate ordinals fail.</summary>
    private static async Task<List<string>> ReadRowsAsync(SqlDataReader reader)
    {
        var rows = new List<string>();
        var ordinals = new HashSet<int>();
        do
        {
            if (reader.FieldCount != 9)
            {
                throw new InvalidOperationException("Local classifier replay returned an unexpected result shape.");
            }

            while (await reader.ReadAsync())
            {
                if (!ordinals.Add(reader.GetInt32(0)))
                {
                    throw new InvalidOperationException("Local classifier replay returned a duplicate ordinal.");
                }

                var values = new object[9];
                reader.GetValues(values);
                rows.Add(System.Text.Json.JsonSerializer.Serialize(values));
            }
        } while (await reader.NextResultAsync());

        return rows;
    }

    /// <summary>Checks the pooling regression before any database cleanup, with an unchanged pooled control.</summary>
    private static async Task VerifyConnectionResetAsync(SqlConnection root, string rootString, string output)
    {
        var control = await ProbeConnectionResetAsync(root, rootString, pooling: true, changeCollation: false);
        var regression = await ProbeConnectionResetAsync(root, rootString, pooling: true, changeCollation: true);
        var corrected = await ProbeConnectionResetAsync(root, rootString, pooling: false, changeCollation: true);
        await File.WriteAllLinesAsync(Path.Combine(output, "connection-reset.log"),
        [
            "pooled_unchanged=" + control,
            "pooled_collation_changed=" + regression,
            "fixture_unpooled_collation_changed=" + corrected,
        ]);

        if (control != "reopened_3" || regression != "login_reset_rejected" || corrected != "reopened_3")
        {
            throw new InvalidOperationException("The isolated local connection-reset reproduction was not confirmed.");
        }
    }

    /// <summary>Changes only a uniquely owned database and never interprets unrelated provider errors as evidence.</summary>
    private static async Task<string> ProbeConnectionResetAsync(
        SqlConnection root,
        string rootString,
        bool pooling,
        bool changeCollation
    )
    {
        var database = await CreateDatabaseAsync(root);
        var settings = new SqlConnectionStringBuilder(
            SqlServerContainerFixture.BuildTestConnectionString(rootString, database)) { Pooling = pooling };

        await using var connection = new SqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync();
            if (changeCollation)
            {
                await ExecuteAsync(connection, "DECLARE @collation sysname = "
                    + "CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')); "
                    + "IF @collation = N'Latin1_General_100_CS_AS' "
                    + "ALTER DATABASE CURRENT COLLATE Latin1_General_100_CI_AS; "
                    + "ELSE ALTER DATABASE CURRENT COLLATE Latin1_General_100_CS_AS;");
            }

            await connection.CloseAsync();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, "SELECT 1;");
                await connection.CloseAsync();
            }

            return "reopened_3";
        }
        catch (SqlException exception) when (pooling && changeCollation
            && exception.Message.Contains("Resetting the connection results in a different state", StringComparison.Ordinal))
        {
            return "login_reset_rejected";
        }
        finally
        {
            await connection.CloseAsync();
            SqlConnection.ClearPool(connection);
            await DropDatabaseAsync(root, database);
        }
    }

    /// <summary>Creates the same three populated tables used by the mixed stress contract.</summary>
    private static string BuildFixtureSql()
        => "CREATE TABLE dbo.sqlserver_stress_target(id int NOT NULL);"
            + "INSERT dbo.sqlserver_stress_target VALUES(1);"
            + "CREATE TABLE dbo.sqlserver_stress_alter(caption varchar(10) NOT NULL);"
            + "INSERT dbo.sqlserver_stress_alter VALUES('short');"
            + "CREATE TABLE dbo.sqlserver_stress_managed(id int NOT NULL PRIMARY KEY, managed_value nvarchar(32) NOT NULL);"
            + string.Join("", LargeMigrationStressContract.ModelManagedUpdateOrdinals(LargeMigrationStressDialect.SqlServer)
                .TakeWhile(ordinal => ordinal < SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture)
                .Select(ordinal => "INSERT dbo.sqlserver_stress_managed VALUES("
                    + LargeMigrationStressContract.ModelManagedUpdateKey(ordinal).ToString(CultureInfo.InvariantCulture)
                    + ",N'source');"));

    /// <summary>Creates a fresh synthetic database and retains its exact name for recoverable cleanup.</summary>
    private static async Task<string> CreateDatabaseAsync(SqlConnection root)
    {
        var database = "sm_diagnostic_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(root, $"CREATE DATABASE [{database}];");

        return database;
    }

    /// <summary>Drops only the exact synthetic database created by this diagnostic helper.</summary>
    private static Task DropDatabaseAsync(SqlConnection root, string database)
        => ExecuteAsync(root,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");

    /// <summary>Executes fixture-only SQL without logging connection strings or source values.</summary>
    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 180;

        await command.ExecuteNonQueryAsync();
    }
}
