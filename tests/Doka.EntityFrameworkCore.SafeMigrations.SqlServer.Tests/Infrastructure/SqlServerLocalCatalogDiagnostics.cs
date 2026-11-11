namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Replays a small complete capture on an explicitly supplied local diagnostic server.</summary>
internal static partial class SqlServerLocalCatalogDiagnostics
{
    /// <summary>Compares all nine result columns, native dispatch and logical connection reset behavior.</summary>
    /// <param name="rootConnectionString">The explicit local server on which isolated databases are owned.</param>
    /// <param name="shared">The ordered classifier statements using the shared dispatch shape.</param>
    /// <param name="standalone">The equivalent ordered classifier statements using standalone literal dispatch.</param>
    /// <param name="parameters">The parameter sets aligned by statement index for both replay shapes.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    internal static async Task RunAsync(
        string rootConnectionString,
        IReadOnlyList<string> shared,
        IReadOnlyList<string> standalone,
        IReadOnlyList<SqlParameter[]> parameters,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rootSettings = new SqlConnectionStringBuilder(rootConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };

        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_DIAGNOSTIC_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(),
                "sm-sqlserver-diagnostics-"
                + Guid
                    .NewGuid()
                    .ToString("N"));

        Directory.CreateDirectory(output);
        var rootString = rootSettings.ConnectionString;
        await using var root = new SqlConnection(rootString);
        await root.OpenAsync(cancellationToken: cancellationToken);

        // WHY: Fresh uniquely owned databases avoid resetting another application's procedure cache.
        // Both shapes use identical catalogs, source parameters, ordinals and fixture data.
        var literalRows = await ReplayAsync(
            root,
            rootString,
            standalone,
            parameters,
            "literal",
            output,
            cancellationToken: cancellationToken);

        var sharedRows = await ReplayAsync(
            root,
            rootString,
            shared,
            parameters,
            "shared",
            output,
            cancellationToken: cancellationToken);

        if (!literalRows.SequenceEqual(sharedRows, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Local classifier replay changed a nine-column result.");
        }

        await VerifyConnectionResetAsync(root, rootString, output, cancellationToken: cancellationToken);
        await VerifyManagedTypesAsync(root, rootString, output, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Executes complete statements repeatedly and records raw statistics without summing nested scopes.
    /// </summary>
    /// <param name="root">The caller-owned admin connection used to create and drop the replay database.</param>
    /// <param name="rootString">The server connection settings used to connect to the isolated replay database.</param>
    /// <param name="statements">The ordered complete classifier statements to execute on each replay attempt.</param>
    /// <param name="parameters">The parameter sets aligned by statement index and cloned for each execution.</param>
    /// <param name="shape">The replay-shape label used to distinguish diagnostic output filenames.</param>
    /// <param name="output">The existing output directory receiving SQL, timing, statistics and cache evidence.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task<string[]> ReplayAsync(
        SqlConnection root,
        string rootString,
        IReadOnlyList<string> statements,
        IReadOnlyList<SqlParameter[]> parameters,
        string shape,
        string output,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = await CreateDatabaseAsync(root, cancellationToken: cancellationToken);
        try
        {
            await using var connection = new SqlConnection(
                SqlServerContainerFixture.BuildTestConnectionString(rootString, database));

            await connection.OpenAsync(cancellationToken: cancellationToken);
            await ExecuteAsync(connection, BuildFixtureSql(), cancellationToken: cancellationToken);
            await ExecuteAsync(
                connection,
                "SET STATISTICS IO ON; SET STATISTICS TIME ON;",
                cancellationToken: cancellationToken);

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

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
                    rows.AddRange(await ReadRowsAsync(reader, cancellationToken: cancellationToken));
                    var elapsed = Stopwatch.GetElapsedTime(statementStarted)
                        .TotalMilliseconds;

                    timings.Add(
                        FormattableString.Invariant(
                            $"attempt={attempt}; statement={index}; elapsed-ms={elapsed:F3}; chars={statements[index].Length}"));

                    await File.WriteAllLinesAsync(
                        Path.Combine(output, $"{shape}-statistics-{attempt}-{index}.log"),
                        statistics,
                        cancellationToken: cancellationToken);

                    await File.WriteAllTextAsync(
                        Path.Combine(output, $"{shape}-statement-{index}.sql"),
                        statements[index],
                        cancellationToken: cancellationToken);
                }

                timings.Add(
                    FormattableString.Invariant(
                        $"attempt={attempt}; elapsed-ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}; rows={rows.Count}"));

                rows.Sort(StringComparer.Ordinal);
                expectedRows ??= rows.ToArray();
                if (!expectedRows.SequenceEqual(rows, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("A repeated local classifier replay changed its evidence.");
                }

                await File.WriteAllLinesAsync(
                    Path.Combine(output, $"{shape}-rows-{attempt}.jsonl"),
                    rows,
                    cancellationToken: cancellationToken);
            }

            await ExportCachedPlansAsync(
                connection,
                Path.Combine(output, shape + "-cached-plans.jsonl"),
                cancellationToken: cancellationToken);

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
            await using var nativeReader = await batch.ExecuteReaderAsync(cancellationToken: cancellationToken);
            var nativeRows = await ReadRowsAsync(nativeReader, cancellationToken: cancellationToken);
            nativeRows.Sort(StringComparer.Ordinal);
            if (!expectedRows!.SequenceEqual(nativeRows, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Native local classifier dispatch changed its evidence.");
            }

            timings.Add(
                FormattableString.Invariant(
                    $"native; elapsed-ms={Stopwatch.GetElapsedTime(nativeStarted).TotalMilliseconds:F3}; rows={nativeRows.Count}"));

            await File.WriteAllLinesAsync(
                Path.Combine(output, shape + "-timing.log"),
                timings,
                cancellationToken: cancellationToken);

            return expectedRows ?? throw new InvalidOperationException("Local classifier replay returned no capture.");
        }
        finally
        {
            await DropDatabaseAsync(root, database);
        }
    }

    /// <summary>
    /// Exports bounded cache evidence for this helper's database, without aggregating overlapping scopes.
    /// </summary>
    /// <param name="connection">The caller-owned open session selecting the database for cache inspection.</param>
    /// <param name="path">The output file receiving serialized rows for the bounded cached-plan sample.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task ExportCachedPlansAsync(
        SqlConnection connection,
        string path,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT TOP (128) cp.usecounts, cp.objtype, qs.execution_count, qs.total_worker_time,
        qs.total_logical_reads, qs.statement_start_offset, qs.statement_end_offset,
        st.text FROM sys.dm_exec_cached_plans cp CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle)
        st CROSS APPLY sys.dm_exec_plan_attributes(cp.plan_handle) pa
        LEFT JOIN sys.dm_exec_query_stats qs ON qs.plan_handle = cp.plan_handle
        WHERE pa.attribute = N'dbid' AND CONVERT(int, pa.value) = DB_ID()
        AND st.text LIKE N'%@doka_ordinal%' ORDER BY cp.usecounts DESC;
        """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken: cancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(cancellationToken: cancellationToken))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(JsonSerializer.Serialize(values));
        }

        await File.WriteAllLinesAsync(path, rows, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Reads only ordinary nine-column classifier results; malformed shapes or duplicate ordinals fail.
    /// </summary>
    /// <param name="reader">The caller-owned reader whose current and subsequent result sets are consumed.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task<List<string>> ReadRowsAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rows = new List<string>();
        var ordinals = new HashSet<int>();
        do
        {
            if (reader.FieldCount != 9)
            {
                throw new InvalidOperationException("Local classifier replay returned an unexpected result shape.");
            }

            while (await reader.ReadAsync(cancellationToken: cancellationToken))
            {
                if (!ordinals.Add(reader.GetInt32(0)))
                {
                    throw new InvalidOperationException("Local classifier replay returned a duplicate ordinal.");
                }

                var values = new object[9];
                reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values));
            }
        } while (await reader.NextResultAsync(cancellationToken: cancellationToken));

        return rows;
    }

    /// <summary>Checks the pooling regression before any database cleanup, with an unchanged pooled control.</summary>
    /// <param name="root">The caller-owned administrative session used to own each isolated probe database.</param>
    /// <param name="rootString">The local server connection settings used for pooled and unpooled probes.</param>
    /// <param name="output">The diagnostic directory receiving the connection-reset comparison log.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task VerifyConnectionResetAsync(
        SqlConnection root,
        string rootString,
        string output,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var control = await ProbeConnectionResetAsync(
            root,
            rootString,
            pooling: true,
            changeCollation: false,
            cancellationToken: cancellationToken);

        var regression = await ProbeConnectionResetAsync(
            root,
            rootString,
            pooling: true,
            changeCollation: true,
            cancellationToken: cancellationToken);

        var corrected = await ProbeConnectionResetAsync(
            root,
            rootString,
            pooling: false,
            changeCollation: true,
            cancellationToken: cancellationToken);

        await File.WriteAllLinesAsync(
            Path.Combine(output, "connection-reset.log"),
            [
                "pooled_unchanged=" + control,
                "pooled_collation_changed=" + regression,
                "fixture_unpooled_collation_changed=" + corrected,
            ],
            cancellationToken: cancellationToken);

        if (control != "reopened_3"
            || regression != "login_reset_rejected"
            || corrected != "reopened_3")
        {
            throw new InvalidOperationException("The isolated local connection-reset reproduction was not confirmed.");
        }
    }

    /// <summary>
    /// Changes only a uniquely owned database and never interprets unrelated provider errors as evidence.
    /// </summary>
    /// <param name="root">The caller-owned admin connection used to create and drop the probe database.</param>
    /// <param name="rootString">The local server settings used to construct the probe session connection.</param>
    /// <param name="pooling">Whether the probe reuses pooled physical sessions when reopening its connection.</param>
    /// <param name="changeCollation">Whether to change the owned database's collation before reopening.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task<string> ProbeConnectionResetAsync(
        SqlConnection root,
        string rootString,
        bool pooling,
        bool changeCollation,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = await CreateDatabaseAsync(root, cancellationToken: cancellationToken);
        var settings = new SqlConnectionStringBuilder(
            SqlServerContainerFixture.BuildTestConnectionString(rootString, database)) { Pooling = pooling };

        await using var connection = new SqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken: cancellationToken);
            if (changeCollation)
            {
                await ExecuteAsync(
                    connection,
                    """
                    DECLARE @collation sysname = CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(),
                    'Collation')); IF @collation = N'Latin1_General_100_CS_AS' ALTER DATABASE
                    CURRENT COLLATE Latin1_General_100_CI_AS;
                    ELSE ALTER DATABASE CURRENT COLLATE Latin1_General_100_CS_AS;
                    """,
                    cancellationToken: cancellationToken);
            }

            await connection.CloseAsync();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await connection.OpenAsync(cancellationToken: cancellationToken);
                await ExecuteAsync(connection, "SELECT 1;", cancellationToken: cancellationToken);
                await connection.CloseAsync();
            }

            return "reopened_3";
        }
        catch (SqlException exception) when (pooling
            && changeCollation
            && exception.Message.Contains(
                "Resetting the connection results in a different state",
                StringComparison.Ordinal))
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
    private static string BuildFixtureSql() => $"""
        CREATE TABLE dbo.sqlserver_stress_target(id int NOT NULL);INSERT dbo.sqlserver_stress_target
        VALUES(1);CREATE TABLE dbo.sqlserver_stress_alter(caption varchar(10) NOT
        NULL);INSERT dbo.sqlserver_stress_alter VALUES('short');CREATE TABLE dbo.sqlserver_stress_managed(id
        int NOT NULL PRIMARY KEY,
        managed_value nvarchar(32) NOT NULL);{string.Join(
            "",
            LargeMigrationStressContract
                .ModelManagedUpdateOrdinals(LargeMigrationStressDialect.SqlServer)
                .TakeWhile(ordinal => ordinal < SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture)
                .Select(ordinal => "INSERT dbo.sqlserver_stress_managed VALUES("
                    + LargeMigrationStressContract
                        .ModelManagedUpdateKey(ordinal)
                        .ToString(CultureInfo.InvariantCulture)
                    + ",N'source');"))}
        """;

    /// <summary>Creates a fresh synthetic database and retains its exact name for recoverable cleanup.</summary>
    /// <param name="root">The caller-owned administrative connection on which admitted creation completes.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task<string> CreateDatabaseAsync(
        SqlConnection root,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = "sm_diagnostic_"
            + Guid
                .NewGuid()
                .ToString("N");

        // WHY: Complete admitted CREATE before transferring the exact name to the caller's finally-owned cleanup.
        // Canceling only the command wait could lose ownership of a database already committed by the server.
        await ExecuteAsync(root, $"CREATE DATABASE [{database}];", cancellationToken: CancellationToken.None);

        return database;
    }

    /// <summary>Drops only the exact synthetic database created by this diagnostic helper.</summary>
    private static Task DropDatabaseAsync(
        SqlConnection root,
        string database
    ) => ExecuteAsync(
        root,
        $"""
        ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK
        IMMEDIATE; DROP DATABASE [{database}];
        """,
        cancellationToken: CancellationToken.None);

    /// <summary>Executes fixture-only SQL without logging connection strings or source values.</summary>
    /// <param name="connection">The caller-owned open session on which the fixture command executes.</param>
    /// <param name="sql">The trusted fixture SQL executed with the diagnostic command timeout.</param>
    /// <param name="cancellationToken">The caller's cancellation for this helper operation.</param>
    private static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 180;

        await command.ExecuteNonQueryAsync(cancellationToken: cancellationToken);
    }
}
