namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Records paired column SQL shapes and prepared client generation costs without timing gates.</summary>
internal static class PostgreSqlColumnAttributionWorkload
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private static readonly string[] s_catalogRelations =
    [
        "pg_attribute",
        "pg_class",
        "pg_type",
        "pg_attrdef",
        "pg_collation",
        "pg_constraint",
    ];

    /// <summary>Writes every raw sample for the same plain and mixed-collation column contracts.</summary>
    /// <param name="outputPath">The owned evidence JSON destination.</param>
    /// <returns>Zero after complete evidence has been written.</returns>
    internal static int Run(
        string outputPath
    )
    {
        using var context = new PostgreSqlGenerationContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var results = new List<object>();
        foreach (var count in new[] { 1, 8, 16, 64 })
        {
            foreach (var mixed in new[] { false, true })
            {
                var columns = Columns(count, mixed);
                var table = new SafeMigrationOperation(new EnsureTableIntent(
                    new ExpectedTableDefinition("attribution_rows", columns), SafeMigrationTableMode.StrictDefinition),
                    SafeMigrationPolicy.ThrowIfDifferent);

                var repairs = columns
                    .Select(column => (MigrationOperation)new SafeMigrationOperation(
                        new EnsureColumnIntent("attribution_rows", column), SafeMigrationPolicy.RepairIfSafe))
                    .ToArray();

                foreach (var (kind, operations) in new[]
                         {
                             ("table", (IReadOnlyList<MigrationOperation>)[table]),
                             ("required_repairs", (IReadOnlyList<MigrationOperation>)repairs),
                         })
                {
                    var plans = operations
                        .Cast<SafeMigrationOperation>()
                        .Select(operation => catalog.Build(
                            operation, includeAnalysisEvidence: true, includeTransitionEvidence: true))
                        .ToArray();

                    var commands = generator.Generate(operations, context.Model);
                    var classificationSql = string.Join(";", plans.Select(plan => plan.RenderStateExpression()));
                    var runtimeSql = string.Join(";", commands.Select(command => command.CommandText));
                    var generation = Measure(() => generator.Generate(operations, context.Model).Count);
                    var catalogBuild = Measure(() =>
                    {
                        var checksum = 0;
                        foreach (var operation in operations.Cast<SafeMigrationOperation>())
                        {
                            checksum += catalog.Build(operation, includeAnalysisEvidence: true,
                                includeTransitionEvidence: true).StateExpression.Length;
                        }

                        return checksum;
                    });

                    results.Add(new
                    {
                        Kind = kind,
                        Columns = count,
                        MixedCollations = mixed,
                        RuntimeCommands = commands.Count,
                        Classification = Shape(classificationSql),
                        Runtime = Shape(runtimeSql),
                        Generation = generation,
                        CatalogBuild = catalogBuild,
                    });
                }
            }
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(results, s_jsonOptions));
        Console.WriteLine($"Recorded {results.Count} PostgreSQL column generation workloads in {fullPath}.");

        return 0;
    }

    /// <summary>Builds non-null varchar contracts with default, comment and visible or qualified collations.</summary>
    /// <param name="count">The complete target table width.</param>
    /// <param name="mixed">Whether visible, qualified and type-owned collations are mixed.</param>
    /// <returns>The ordered expected column definitions.</returns>
    private static ExpectedColumnDefinition[] Columns(
        int count,
        bool mixed
    ) => Enumerable.Range(0, count)
        .Select(index => new ExpectedColumnDefinition(
            $"value_{index:D2}", typeof(string), false, "character varying(32)", maxLength: 32,
            collation: !mixed || index % 3 == 2
                ? null
                : new SafeMigrationCollationIdentifier(
                    index % 3 == 0 ? "C" : "POSIX", index % 3 == 0 ? null : "pg_catalog"),
            defaultValue: SafeMigrationDefaultValue.Literal("canonical"), comment: "canonical"))
        .ToArray();

    /// <summary>Keeps exact SQL size and relation occurrences separate from execution-plan claims.</summary>
    /// <param name="sql">The complete emitted SQL text.</param>
    /// <returns>The exact payload size and textual catalog reference counts.</returns>
    private static object Shape(
        string sql
    ) => new
    {
        Characters = sql.Length,
        Utf8Bytes = System.Text.Encoding.UTF8.GetByteCount(sql),
        Relations = s_catalogRelations
            .ToDictionary(name => name, name => Occurrences(sql, "pg_catalog." + name + " ")),
        InformationSchemaColumns = Occurrences(sql, "information_schema.columns"),
    };

    /// <summary>Preserves unsorted generation samples and consumes the result to prevent dead work.</summary>
    /// <param name="action">The prepared complete workload batch.</param>
    /// <returns>All warmed raw allocation and elapsed-time samples.</returns>
    private static object[] Measure(
        Func<int> action
    )
    {
        const int iterations = 16;
        for (var index = 0; index < 8; index++)
        {
            _ = action();
        }

        var samples = new object[7];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var checksum = 0L;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                checksum += action();
            }

            samples[sample] = new
            {
                Iterations = iterations,
                ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
                Checksum = checksum,
            };
        }

        return samples;
    }

    /// <summary>Counts exact catalog relation references, not inferred database reads.</summary>
    /// <param name="text">The complete emitted SQL text.</param>
    /// <param name="value">The exact catalog reference to count.</param>
    /// <returns>The ordinal occurrence count.</returns>
    private static int Occurrences(
        string text,
        string value
    )
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
