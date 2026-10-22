namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Attributes real bounded narrowing discovery and transport phases with deterministic ADO results.</summary>
internal static class PostgreSqlMaintenanceAttribution
{
    /// <summary>Measures exact native-batch and fallback windows without claiming native database timings.</summary>
    /// <param name="outputPath">The caller-owned JSON evidence destination.</param>
    /// <returns>Zero after all ordinal, SQL, transport and cancellation controls pass.</returns>
    internal static int Run(
        string outputPath
    )
    {
        using var context = new PostgreSqlGenerationContext();
        var catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var analyzer = new PostgreSqlSafeMigrationProviderAnalyzer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var resolve = Bind(analyzer);
        var results = new List<object>();
        foreach (var count in new[] { 31, 32, 33, 511, 512, 513, 4097 })
        {
            foreach (var native in new[] { false, true })
            {
                var operations = Operations(count);
                var states = new SafeMigrationObservedState?[count];
                using var connection = new CatalogStatementTestConnection(native, EligibilityRows);
                var resolved = resolve(connection, operations, states, CancellationToken.None);

                resolved
                    .GetAwaiter()
                    .GetResult();

                var output = ReadResult(resolved);
                var expected = operations
                    .Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                    .Select((
                            chunk,
                            window
                        ) => string.Join(
                            SafeMigrationCatalogQueryLimits.Separator,
                            chunk.Select((
                                operation,
                                ordinal
                            ) => EligibilitySql(
                                catalog,
                                operation,
                                (window * SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement) + ordinal)))
                        + SafeMigrationCatalogQueryLimits.Trailer)
                    .ToArray();

                MaintenanceAttribution.Require(
                    expected.SequenceEqual(connection.Submissions.Select(static submission => submission.Sql)),
                    "The measured PostgreSQL window lost exact SQL or ordinal ownership.");

                MaintenanceAttribution.Require(
                    output.Length == count
                    && connection.Submissions.All(static submission =>
                        submission is { Timeout: 71, Parameters.Count: 0 }
                        && System.Text.Encoding.UTF8.GetByteCount(submission.Sql)
                        <= SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes),
                    "The PostgreSQL attribution transport or result shape is invalid.");

                VerifyFacts(output);
                results.Add(
                    new
                    {
                        Id = "PERF-03",
                        Workload = "eligibility_windows",
                        Operations = count,
                        NativeBatch = native,
                        Statements = connection.Submissions.Count,
                        Batches = connection.BatchCounts.ToArray(),
                        SqlUtf8Bytes =
                            connection.Submissions.Sum(static submission =>
                                System.Text.Encoding.UTF8.GetByteCount(submission.Sql)),
                        ExactSqlHash =
                            MaintenanceAttribution.Hash(
                                string.Join("\n", connection.Submissions.Select(static submission => submission.Sql))),
                        Samples = MaintenanceAttribution.Measure(
                            () =>
                            {
                                connection.Submissions.Clear();
                                connection.BatchCounts.Clear();
                                resolve(connection, operations, states, CancellationToken.None)
                                    .GetAwaiter()
                                    .GetResult();

                                return connection.Submissions.Count;
                            },
                            count >= 512 ? 2 : 16),
                    });
            }
        }

        VerifyRepeatedSkippedAndCancelled(resolve);

        return MaintenanceAttribution.Write(outputPath, "PostgreSQL deterministic client ADO workload", results);
    }

    /// <summary>Binds the existing private phase once so reflection and argument arrays are never measured.</summary>
    /// <param name="analyzer">The real analyzer owning the measured phase.</param>
    /// <returns>The typed synchronous-completion task delegate.</returns>
    private static Func<DbConnection, IReadOnlyList<SafeMigrationOperation>, SafeMigrationObservedState?[],
        CancellationToken, Task> Bind(
        PostgreSqlSafeMigrationProviderAnalyzer analyzer
    )
    {
        var method =
            typeof(PostgreSqlSafeMigrationProviderAnalyzer).GetMethod(
                "ResolveDataProbeResultsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The bounded PostgreSQL probe phase is missing.");

        var connection = Expression.Parameter(typeof(DbConnection));
        var operations = Expression.Parameter(typeof(IReadOnlyList<SafeMigrationOperation>));
        var states = Expression.Parameter(typeof(SafeMigrationObservedState?[]));
        var cancellation = Expression.Parameter(typeof(CancellationToken));
        var call = Expression.Call(
            Expression.Constant(analyzer),
            method,
            connection,
            operations,
            states,
            Expression.Convert(Expression.Constant(71), typeof(int?)),
            cancellation);

        return Expression
            .Lambda<Func<DbConnection, IReadOnlyList<SafeMigrationOperation>, SafeMigrationObservedState?[],
                CancellationToken, Task>>(call, connection, operations, states, cancellation)
            .Compile();
    }

    /// <summary>Reads private results outside measurements without adding a production attribution API.</summary>
    /// <param name="task">The completed real analysis phase.</param>
    /// <returns>The boxed private-result array.</returns>
    private static Array ReadResult(
        Task task
    ) => (Array)(task
            .GetType()
            .GetProperty("Result")
            ?.GetValue(task)
        ?? throw new InvalidOperationException("The PostgreSQL probe result is absent."));

    /// <summary>Checks each eligibility result and its independently generated negative slots.</summary>
    /// <param name="output">The real private phase result array.</param>
    private static void VerifyFacts(
        Array output
    )
    {
        for (var ordinal = 0; ordinal < output.Length; ordinal++)
        {
            var result = output.GetValue(ordinal)
                ?? throw new InvalidOperationException("A required PostgreSQL result slot is absent.");

            var type = result.GetType();
            var eligible = type
                    .GetProperty("IsTransitionEligible")
                    ?.GetValue(result) as bool?
                ?? throw new InvalidOperationException("The transition-eligibility result field is absent.");

            var required = type
                    .GetProperty("IsRequired")
                    ?.GetValue(result) as bool?
                ?? throw new InvalidOperationException("The required-probe result field is absent.");

            var blocked = type
                    .GetProperty("IsBlocked")
                    ?.GetValue(result) as bool?
                ?? throw new InvalidOperationException("The blocked-data result field is absent.");

            MaintenanceAttribution.Require(
                eligible == (ordinal % 3 != 0) && !required && !blocked,
                "PostgreSQL data evidence was assigned to the wrong candidate.");
        }
    }

    /// <summary>Retains deduplication, skipped-ordinal and cancellation controls outside measurement.</summary>
    /// <param name="resolve">The one-time bound real phase delegate.</param>
    private static void VerifyRepeatedSkippedAndCancelled(
        Func<DbConnection, IReadOnlyList<SafeMigrationOperation>, SafeMigrationObservedState?[], CancellationToken,
            Task> resolve
    )
    {
        var operations = Operations(514);
        operations[512] = operations[1];
        operations[513] = operations[2];
        var states = new SafeMigrationObservedState?[operations.Length];
        states[0] = SafeMigrationObservedState.PrerequisiteMissing;
        states[511] = SafeMigrationObservedState.Unsupported;
        using var connection = new CatalogStatementTestConnection(true, EligibilityRows);
        var task = resolve(connection, operations, states, CancellationToken.None);

        task
            .GetAwaiter()
            .GetResult();

        var output = ReadResult(task);

        MaintenanceAttribution.Require(
            output.GetValue(0) is null
            && output.GetValue(511) is null
            && Equals(output.GetValue(1), output.GetValue(512))
            && Equals(output.GetValue(2), output.GetValue(513))
            && connection.Submissions.Count == 16,
            "The PostgreSQL repeated/skipped candidate fixture no longer owns its result slots.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            resolve(connection, operations, states, cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        MaintenanceAttribution.Require(cancelled, "PostgreSQL probe resolution ignored cancellation.");
    }

    /// <summary>Creates target/source lengths outside all timed phases.</summary>
    /// <param name="count">The operation count surrounding native batching boundaries.</param>
    /// <returns>The independently identified immutable column transitions.</returns>
    private static SafeMigrationOperation[] Operations(
        int count
    ) => Enumerable
        .Range(0, count)
        .Select(static index => new SafeMigrationOperation(
            new AlterColumnIntent(
                "maintenance_rows",
                new ExpectedColumnDefinition($"value_{index:D5}", typeof(string), true, "character varying(40)"),
                new ExpectedColumnDefinition($"value_{index:D5}", typeof(string), true, "character varying(80)")),
            SafeMigrationPolicy.RepairIfSafe))
        .ToArray();

    /// <summary>Builds the independent pre-materialized eligibility oracle before the measured private phase.</summary>
    /// <param name="catalog">The real catalog builder used only for the structural oracle.</param>
    /// <param name="operation">The exact immutable source/target transition.</param>
    /// <param name="ordinal">The authored operation slot.</param>
    /// <returns>The exact eligibility selection.</returns>
    private static string EligibilitySql(
        PostgreSqlSafeMigrationCatalogSqlBuilder catalog,
        SafeMigrationOperation operation,
        int ordinal
    )
    {
        var probe = catalog.Build(operation, includeTransitionEvidence: true)
                .DataProbe
            ?? throw new InvalidOperationException("The PostgreSQL attribution operation has no narrowing probe.");

        return $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
            + $"COALESCE(({probe.TransitionInvariantExpression}), FALSE), "
            + $"COALESCE(({probe.NarrowingExpression}), FALSE)";
    }

    /// <summary>Returns bounded deterministic catalog eligibility with no live row probes required.</summary>
    /// <param name="sql">The actually submitted statement.</param>
    /// <param name="parameters">The actually submitted bound parameters.</param>
    /// <returns>Statement-local ordered eligibility rows.</returns>
    private static DataTable EligibilityRows(
        string sql,
        IReadOnlyList<DbParameter> parameters
    )
    {
        MaintenanceAttribution.Require(parameters.Count == 0, "Eligibility unexpectedly bound query parameters.");
        var rows = new DataTable();
        rows.Columns.Add("ordinal", typeof(int));
        rows.Columns.Add("eligible", typeof(bool));
        rows.Columns.Add("narrowing", typeof(bool));
        foreach (var selection in sql.Split(SafeMigrationCatalogQueryLimits.Separator, StringSplitOptions.None))
        {
            var start = selection.IndexOf("SELECT ", StringComparison.Ordinal) + 7;
            var end = selection.IndexOf(',', start);
            var ordinal = int.Parse(selection.AsSpan(start, end - start), CultureInfo.InvariantCulture);
            rows.Rows.Add(ordinal, ordinal % 3 != 0, false);
        }

        return rows;
    }
}
