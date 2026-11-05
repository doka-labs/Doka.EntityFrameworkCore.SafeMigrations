namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Records paired column SQL shapes and prepared client generation costs without timing gates.</summary>
public class PostgreSqlColumnAttributionWorkload : IDisposable
{
    private static readonly string[] s_catalogRelations =
    [
        "pg_attribute",
        "pg_class",
        "pg_type",
        "pg_attrdef",
        "pg_collation",
        "pg_constraint",
    ];

    private PostgreSqlSafeMigrationCatalogSqlBuilder _catalog = null!;

    private PostgreSqlGenerationContext _context = null!;
    private bool _disposed;
    private IMigrationsSqlGenerator _generator = null!;
    private IReadOnlyList<MigrationOperation> _operations = null!;
    private SafeMigrationOperation[] _safeOperations = null!;

    /// <summary>Gets or sets the ordered target table width.</summary>
    [Params(1, 8, 16, 64)]
    public int ColumnCount { get; set; }

    /// <summary>Gets or sets whether visible, qualified and type-owned collations are mixed.</summary>
    [Params(false, true)]
    public bool MixedCollations { get; set; }

    /// <summary>Gets or sets complete table generation versus individual required-column repairs.</summary>
    [Params("table", "required_repairs")]
    public string Kind { get; set; } = "table";

    /// <summary>Releases the context owning the PostgreSQL generation services.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs paired plain and mixed-collation column generation benchmarks.</summary>
    /// <param name="args">The BenchmarkDotNet command-line arguments.</param>
    /// <returns>The shared benchmark runner exit code.</returns>
    internal static int Run(
        string[] args
    ) => BenchmarkProgram.Run(args, typeof(PostgreSqlColumnAttributionWorkload));

    /// <summary>Prepares ordered contracts and records exact SQL shapes outside timed generation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _disposed = false;
        _context = new PostgreSqlGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _catalog = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());

        var columns = Columns(ColumnCount, MixedCollations);
        _operations = Kind switch
        {
            "table" =>
            [
                new SafeMigrationOperation(
                    new EnsureTableIntent(
                        new ExpectedTableDefinition("attribution_rows", columns),
                        SafeMigrationTableMode.StrictDefinition),
                    SafeMigrationPolicy.ThrowIfDifferent),
            ],
            "required_repairs" => columns
                .Select(column => (MigrationOperation)new SafeMigrationOperation(
                    new EnsureColumnIntent("attribution_rows", column),
                    SafeMigrationPolicy.RepairIfSafe))
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
        };

        _safeOperations = _operations
            .Cast<SafeMigrationOperation>()
            .ToArray();

        var plans = _safeOperations
            .Select(operation => _catalog.Build(
                operation,
                includeAnalysisEvidence: true,
                includeTransitionEvidence: true))
            .ToArray();

        var commands = _generator.Generate(_operations, _context.Model);
        var classificationSql = string.Join(";", plans.Select(plan => plan.RenderStateExpression()));
        var runtimeSql = string.Join(";", commands.Select(command => command.CommandText));
        var replayClassification = string.Join(
            ";",
            _safeOperations.Select(operation => _catalog
                .Build(operation, includeAnalysisEvidence: true, includeTransitionEvidence: true)
                .RenderStateExpression()));

        var replayRuntime = string.Join(
            ";",
            _generator
                .Generate(_operations, _context.Model)
                .Select(command => command.CommandText));

        MaintenanceAttribution.Require(
            commands.Count > 0
            && classificationSql.Length > 0
            && runtimeSql.Length > 0
            && classificationSql == replayClassification
            && runtimeSql == replayRuntime,
            "PostgreSQL column attribution lost nonempty exact SQL or generation replay equivalence.");

        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            _ = MaintenanceAttribution.Write(
                Path.Combine(output, $"postgresql-columns-{Kind}-{ColumnCount}-{MixedCollations}.json"),
                "Functional/source evidence; PostgreSQL client SQL generation; not timing evidence",
                [
                    new
                    {
                        Kind,
                        Columns = ColumnCount,
                        MixedCollations,
                        RuntimeCommands = commands.Count,
                        Classification = Shape(classificationSql),
                        Runtime = Shape(runtimeSql),
                        ClassificationSqlHash = MaintenanceAttribution.Hash(classificationSql),
                        RuntimeSqlHash = MaintenanceAttribution.Hash(runtimeSql),
                    },
                ]);
        }
    }

    /// <summary>Generates one complete runtime command sequence from prepared immutable operations.</summary>
    /// <returns>The generated command count, consumed by BenchmarkDotNet.</returns>
    [Benchmark]
    public int GenerateCommands() => _generator.Generate(_operations, _context.Model)
        .Count;

    /// <summary>Builds catalog plans once for every operation in the prepared workload.</summary>
    /// <returns>The generated state SQL length checksum, consumed by BenchmarkDotNet.</returns>
    [Benchmark]
    public int BuildCatalogPlans()
    {
        var checksum = 0;

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var operation in _safeOperations)
        {
            checksum += _catalog.Build(operation, includeAnalysisEvidence: true, includeTransitionEvidence: true)
                .StateExpression.Length;
        }

        return checksum;
    }

    /// <summary>Disposes the context owning the real PostgreSQL generation services.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases owned managed resources once while supporting generated benchmark subclasses.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (!disposing || _disposed)
        {
            return;
        }

        _context?.Dispose();
        _disposed = true;
    }

    /// <summary>Builds non-null varchar contracts with default, comment and visible or qualified collations.</summary>
    /// <param name="count">The complete target table width.</param>
    /// <param name="mixed">Whether visible, qualified and type-owned collations are mixed.</param>
    /// <returns>The ordered expected column definitions.</returns>
    private static ExpectedColumnDefinition[] Columns(
        int count,
        bool mixed
    ) => Enumerable
        .Range(0, count)
        .Select(index => new ExpectedColumnDefinition(
            $"value_{index:D2}",
            typeof(string),
            false,
            "character varying(32)",
            maxLength: 32,
            collation: !mixed || index % 3 == 2
                ? null
                : new SafeMigrationCollationIdentifier(
                    index % 3 == 0
                        ? "C"
                        : "POSIX",
                    index % 3 == 0
                        ? null
                        : "pg_catalog"),
            defaultValue: SafeMigrationDefaultValue.Literal("canonical"),
            comment: "canonical"))
        .ToArray();

    /// <summary>Keeps exact SQL size and relation occurrences separate from execution-plan claims.</summary>
    /// <param name="sql">The complete emitted SQL text.</param>
    /// <returns>The exact payload size and textual catalog reference counts.</returns>
    private static object Shape(
        string sql
    ) => new
    {
        Characters = sql.Length,
        Utf8Bytes = Encoding.UTF8.GetByteCount(sql),
        Relations = s_catalogRelations.ToDictionary(name => name, name => Occurrences(sql, "pg_catalog." + name + " ")),
        InformationSchemaColumns = Occurrences(sql, "information_schema.columns"),
    };

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
