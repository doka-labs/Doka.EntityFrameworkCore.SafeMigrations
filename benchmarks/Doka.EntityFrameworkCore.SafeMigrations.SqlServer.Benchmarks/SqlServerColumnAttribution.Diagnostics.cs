namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

public partial class SqlServerColumnAttribution
{
    /// <summary>Captures native catalog diagnostics outside every measured invocation.</summary>
    /// <param name="cancellationToken">The token controlling diagnostic database and file operations.</param>
    /// <returns>The complete asynchronous diagnostic capture.</returns>
    private async Task CaptureDiagnosticEvidenceAsync(
        CancellationToken cancellationToken
    )
    {
        await Context.Database.OpenConnectionAsync(cancellationToken);
        var catalogConnection = (SqlConnection)Context.Database.GetDbConnection();
        var statistics = new List<string>();

        Connection.InfoMessage += Capture;
        catalogConnection.InfoMessage += Capture;
        try
        {
            // WHY: Statistics and artifact I/O are diagnostic controls, never benchmark instrumentation.
            ValidateAnalyses(
                await Analyzer.AnalyzeAsync(Context, _operations, cancellationToken),
                ScenarioDefinition);

            await ExecuteAsync(Connection, "SET STATISTICS IO ON; SET STATISTICS TIME ON;", cancellationToken);
            await ExecuteAsync(catalogConnection, "SET STATISTICS IO ON; SET STATISTICS TIME ON;", cancellationToken);
            statistics.Clear();
            var analyses = await Analyzer.AnalyzeAsync(Context, _operations, cancellationToken);

            ValidateAnalyses(analyses, ScenarioDefinition);
            await File.WriteAllLinesAsync(
                Path.Combine(_output, "catalog-statistics.log"),
                statistics,
                cancellationToken);

            await WriteJsonAsync(Path.Combine(_output, "catalog-results.json"), analyses, cancellationToken);
            await ExportCatalogSqlAsync(
                catalogConnection,
                Path.Combine(_output, "catalog-cached-sql.json"),
                cancellationToken);

            statistics.Clear();

            // WHY: This control must exercise the exact synchronous API measured by ExecuteRuntime.
            var runtimeResult = ExecuteRuntime();
            await File.WriteAllLinesAsync(
                Path.Combine(_output, "runtime-statistics.log"),
                statistics,
                cancellationToken);

            await WriteJsonAsync(
                Path.Combine(_output, "scenario.json"),
                new
                {
                    Name = ScenarioName,
                    RowCount,
                    OperationCount = _operations.Count,
                    ExpectedState = ScenarioDefinition.ExpectedState.ToString(),
                    ScenarioDefinition.RejectRuntime,
                    RuntimeResult = runtimeResult,
                    FixtureFingerprint = _original,
                    GeneratedSqlCharacters = _sql.Length,
                    GeneratedSqlUtf8Bytes = Encoding.UTF8.GetByteCount(_sql),
                    SysColumnsSubqueryOccurrences = CountOccurrences(_sql, "FROM sys.columns"),
                    TimingEvidence = "BenchmarkDotNet artifacts; diagnostics contain no timing samples.",
                },
                cancellationToken);
        }
        finally
        {
            Connection.InfoMessage -= Capture;
            catalogConnection.InfoMessage -= Capture;

            // WHY: Session cleanup remains uncancellable after diagnostic work has acquired these settings.
            await ExecuteAsync(Connection, "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;", CancellationToken.None);
            await ExecuteAsync(
                catalogConnection,
                "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;",
                CancellationToken.None);
        }

        return;

        void Capture(
            object sender,
            SqlInfoMessageEventArgs message
        ) => statistics.Add(message.Message);
    }

    /// <summary>Requires exact state, postcondition and fail-closed repair capability for every classifier.</summary>
    private static void ValidateAnalyses(
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses,
        Scenario scenario
    )
    {
        if (analyses.Count != scenario.Columns
            || analyses.Any(analysis => analysis.ObservedState != scenario.ExpectedState
                || analysis.PostconditionSatisfied != (scenario.ExpectedState == SafeMigrationObservedState.Matching)
                || (scenario.RejectRuntime && analysis.RepairCapability == SafeMigrationRepairCapability.Safe)))
        {
            throw new InvalidOperationException(
                $"{scenario.Name}: unexpected analyzer state: " + JsonSerializer.Serialize(analyses, s_jsonOptions));
        }
    }

    /// <summary>Exports actual cached classifier batches without overlapping-statistics aggregation.</summary>
    /// <param name="connection">The acquired catalog connection.</param>
    /// <param name="output">The owned JSON destination.</param>
    /// <param name="cancellationToken">The token controlling query and evidence persistence.</param>
    /// <returns>The asynchronous catalog SQL export.</returns>
    private static async Task ExportCatalogSqlAsync(
        SqlConnection connection,
        string output,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
        SELECT TOP (128) cp.usecounts, cp.objtype, st.text
        FROM sys.dm_exec_cached_plans cp CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle)
        st CROSS APPLY sys.dm_exec_plan_attributes(cp.plan_handle) pa
        WHERE pa.attribute = N'dbid' AND CONVERT(int, pa.value) = DB_ID()
        AND st.text LIKE N'%@doka_ordinal%'
        AND st.text NOT LIKE N'%sys.dm_exec_cached_plans%' ORDER BY cp.usecounts
        DESC;
        """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<object[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            entries.Add(values);
        }

        await WriteJsonAsync(output, entries, cancellationToken);
    }

    /// <summary>Captures engine and host provenance without saving connection settings or credentials.</summary>
    /// <param name="connection">The owned diagnostic connection.</param>
    /// <param name="output">The owned evidence directory.</param>
    /// <param name="rows">The actual fixture row count.</param>
    /// <param name="cancellationToken">The token controlling query and file operations.</param>
    /// <returns>The complete asynchronous provenance capture.</returns>
    private static async Task WriteProvenanceAsync(
        SqlConnection connection,
        string output,
        int rows,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT @@VERSION, CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), cpu_count,
        physical_memory_kb FROM sys.dm_os_sys_info;
        """;

        command.CommandTimeout = CommandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The server returned no attribution provenance.");
        }

        var product = typeof(SafeMigrationOperation).Assembly.Location;
        var provider = typeof(SqlServerSafeMigrationRuntimePlan).Assembly.Location;
        var harness = typeof(SqlServerColumnAttribution).Assembly.Location;
        var metadata = new
        {
            SchemaVersion = 2,
            Status = "measurement_only_not_engine_qualification",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            TimingFramework = "BenchmarkDotNet",
            DiagnosticExecutionsPerPath = 1,
            RowCount = rows,
            Runtime = Environment.Version.ToString(),
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            HostProcessorCount = Environment.ProcessorCount,
            ServerVersion = reader.GetString(0),
            ServerEdition = reader.GetString(1),
            ServerCpuCount = reader.GetInt32(2),
            ServerPhysicalMemoryKilobytes = reader.GetInt64(3),
            ProductSha256 = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(product, cancellationToken))),
            ProviderSha256 = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(provider, cancellationToken))),
            HarnessSha256 = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(harness, cancellationToken))),
            Statistics = "Raw nested STATISTICS IO/TIME messages; do not sum overlapping scopes.",
            AllocationScope = "BenchmarkDotNet MemoryDiagnoser; not product-only async SqlClient attribution.",
        };

        await WriteJsonAsync(Path.Combine(output, "provenance.json"), metadata, cancellationToken);
    }

    /// <summary>Writes indented raw evidence through one shared serializer configuration.</summary>
    /// <typeparam name="T">The source-bound evidence shape.</typeparam>
    /// <param name="path">The owned evidence destination.</param>
    /// <param name="value">The complete functional evidence.</param>
    /// <param name="cancellationToken">The token controlling persistence.</param>
    /// <returns>The asynchronous JSON persistence.</returns>
    internal static Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken
    ) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, s_jsonOptions), cancellationToken);
}
