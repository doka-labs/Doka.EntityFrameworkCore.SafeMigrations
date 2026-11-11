namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks;

/// <summary>Reports table-definition composition independently of database latency.</summary>
public class PostgreSqlAllocationBenchmarks : IDisposable
{
    private PostgreSqlGenerationContext _context = null!;
    private ISqlGenerationHelper _helper = null!;
    private IRelationalTypeMappingSource _mappings = null!;
    private SafeMigrationOperation _operation = null!;

    /// <summary>Gets or sets the number of expected columns in the strict table definition.</summary>
    [Params(4, 64)]
    public int ColumnCount { get; set; }

    /// <summary>Releases offline provider service ownership.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates native mappings and immutable expected columns outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new PostgreSqlGenerationContext();
        _mappings = _context.GetService<IRelationalTypeMappingSource>();
        _helper = _context.GetService<ISqlGenerationHelper>();
        var columns = Enumerable
            .Range(0, ColumnCount)
            .Select(static index => new ExpectedColumnDefinition(
                "column_" + index.ToString(CultureInfo.InvariantCulture),
                typeof(int),
                false,
                "integer"))
            .ToArray();

        var builder = new MigrationBuilder(_context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition("allocation_table", columns),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        _operation = (SafeMigrationOperation)builder.Operations[0];
        var plan = new PostgreSqlSafeMigrationCatalogSqlBuilder(_mappings, _helper).Build(_operation);

        if (!ReferenceEquals(plan.Postcondition, plan.ExecutionPostcondition)
            || !plan.Postcondition.Contains("'column_0'", StringComparison.Ordinal)
            || !plan.Postcondition.Contains(
                "'column_" + (ColumnCount - 1).ToString(CultureInfo.InvariantCulture) + "'",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Prepared table composition did not preserve its column contract.");
        }
    }

    /// <summary>Composes a complete production catalog plan and consumes its classifier text.</summary>
    /// <returns>The exact generated state expression.</returns>
    [Benchmark]
    public string BuildTableDefinition() => new PostgreSqlSafeMigrationCatalogSqlBuilder(_mappings, _helper)
        .Build(_operation)
        .StateExpression;

    /// <summary>Releases the owned native provider context after measurement.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources owned by this fixture.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            // WHY: Disposal also supports incomplete setup; null! does not initialize the context.
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            _context?.Dispose();
        }
    }
}

/// <summary>Reports prepared five-marker append and materialization through the production plan.</summary>
public class PostgreSqlPlaceholderBenchmarks
{
    private readonly StringBuilder _destination = new(256);
    private PostgreSqlSafeMigrationRuntimePlan _plan = null!;

    /// <summary>Creates all five structural markers with the fresh NULL-proof path enabled.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var template = string.Join(" | ",
            PostgreSqlSafeMigrationRuntimePlan.DataProbePlaceholder,
            PostgreSqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
            PostgreSqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
            PostgreSqlSafeMigrationRuntimePlan.NullabilityRepairInvariantPlaceholder,
            PostgreSqlSafeMigrationRuntimePlan.MatchingPlaceholder);

        _plan = new PostgreSqlSafeMigrationRuntimePlan(
            template,
            "verified_matching",
            SafeMigrationRepairCapability.Safe,
            template)
        {
            NullabilityDataProbe = new PostgreSqlSafeMigrationNullabilityDataProbe(
                "complete_contract",
                "repair_eligible",
                "row_probe",
                "proof_rows"),
        };

        var appended = Append();
        var materialized = Materialize();

        if (appended != materialized.Length
            || !_destination
                .ToString()
                .Equals(materialized, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Prepared placeholder renderers did not preserve exact output.");
        }
    }

    /// <summary>Appends into the fixture-owned reusable destination without materializing a string.</summary>
    /// <returns>The appended character count.</returns>
    [Benchmark]
    public int Append()
    {
        _destination.Clear();
        _plan.AppendStateExpression(_destination, "cached_data_blocked", "fresh_transition", "verified_matching");

        return _destination.Length;
    }

    /// <summary>Materializes the identical five-marker expression using the production renderer.</summary>
    /// <returns>The rendered expression.</returns>
    [Benchmark]
    public string Materialize() => _plan.RenderStateExpression("cached_data_blocked", "fresh_transition");
}

/// <summary>Reports complete runtime guards with combined narrowing and NULL proofs.</summary>
public class PostgreSqlGuardBenchmarks : IDisposable
{
    private PostgreSqlGenerationContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private IReadOnlyList<MigrationOperation> _operations = null!;

    /// <summary>Gets or sets whether generation uses the escaped idempotent transport.</summary>
    [Params(false, true)]
    public bool Idempotent { get; set; }

    /// <summary>Releases offline provider service ownership.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates the native provider services and the combined row-proof intent.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new PostgreSqlGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _ = _context.Model;
        var builder = new MigrationBuilder(_context.Database.ProviderName!);
        builder.EnsureColumn(
            "proof_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, "character varying(800)", maxLength: 800),
            SafeMigrationPolicy.RepairIfSafe);

        _operations = builder.Operations;

        if (GenerateGuard() == 0)
        {
            throw new InvalidOperationException("Prepared guard generation did not emit its operation.");
        }
    }

    /// <summary>Generates the actual native guard instead of a historical private-renderer simulation.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int GenerateGuard() => _generator.Generate(
            _operations,
            _context.Model,
            Idempotent
                ? MigrationsSqlGenerationOptions.Idempotent
                : MigrationsSqlGenerationOptions.Default)
        .Count;

    /// <summary>Releases the owned provider context after measurement.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources owned by this fixture.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            // WHY: Disposal also supports incomplete setup; null! does not initialize the context.
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            _context?.Dispose();
        }
    }
}
