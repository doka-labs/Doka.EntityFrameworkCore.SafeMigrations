namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerGuardRenderingAllocationTests
{
    /// <summary>
    /// Creates a quoted fixture intent without catalog access or input allocations during measurement.
    /// </summary>
    private static SafeMigrationOperation CreateOperation(
        SafeMigrationPolicy policy,
        bool managedData
    )
    {
        if (managedData)
        {
            return new SafeMigrationOperation(new EnsureModelManagedDataIntent("O'Brien", ["Id"], ["int"],
                ["Id", "Caption"], ["int", "nvarchar(80)"],
                new object?[,] { { 7, "O'Brien \uD83D\uDE80" } }, null, null), policy);
        }

        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        builder.EnsureColumn("O'Brien", new ExpectedColumnDefinition("Caption", typeof(string), true,
            "nvarchar(80)"), policy);

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>Exercises all optional support fragments and fixed scalar templates with quoted expressions.</summary>
    private static SqlServerSafeMigrationRuntimePlan CreatePlan(
        bool delayed,
        string? preamble
    )
        => new("N'matching'", "1", SafeMigrationRepairCapability.Safe, "1")
        {
            PhysicalTableSupportExpression = "CASE WHEN N'O''Brien' = N'O''Brien' THEN 1 ELSE 0 END",
            ColumnLayoutFailureExpression = "CAST(NULL AS nvarchar(32))",
            ColumnCollationSupportExpression = "1",
            DefaultValueSupportExpression = "CASE WHEN N'\uD83D\uDE80' = N'\uD83D\uDE80' THEN 1 ELSE 0 END",
            DefaultValueSupportRequiresDelayedBinding = delayed,
            IndexFilterSupportExpression = "1",
            PrerequisiteExpression = "1",
            StateEvaluationGuardExpression = "1",
            StateEvaluationGuardFailureExpression = "N'unsupported'",
            RequiresDelayedBinding = delayed,
            CatalogPreambleSql = preamble,
            PostApplySql = "SELECT N'post O''Brien';\n",
        };

    /// <summary>Prepares a non-transaction-suppressed provider command before the measured rendering path.</summary>
    private static MigrationCommand CreateCommand(
        DbContext context,
        string sql
    )
    {
        var dependencies = context.GetService<MigrationsSqlGeneratorDependencies>();
        var command = dependencies.CommandBuilderFactory.Create().Append(sql).Build();

        return new MigrationCommand(command, context, dependencies.Logger);
    }

    /// <summary>Counts only prepared guard rendering on the executing thread, excluding delegate creation.</summary>
    private static (long Bytes, string Sql) MeasureAllocations(
        Func<string> render,
        int count
    )
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sql = string.Empty;
        for (var index = 0; index < count; index++)
        {
            sql = render();
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        return (bytes, sql);
    }

    /// <summary>Counts prepared command wrapping while excluding fixture and destination allocations.</summary>
    private static long MeasureWrappingAllocations(
        Action<int> wrap,
        int count
    )
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            wrap(index);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Exposes a completed batch whose enumeration would reveal an allocation-path regression.</summary>
    private sealed class IndexedOnlyCommands : IReadOnlyList<MigrationCommand>
    {
        private readonly IReadOnlyList<MigrationCommand> _commands;

        public IndexedOnlyCommands(
            IReadOnlyList<MigrationCommand> commands
        )
        {
            _commands = commands;
        }

        public int Count => _commands.Count;
        public MigrationCommand this[int index] => _commands[index];

        public IEnumerator<MigrationCommand> GetEnumerator()
            => throw new InvalidOperationException("The prepared batch must be indexed without enumeration.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Records delegated provider execution without opening the fixture connection.</summary>
    private sealed class RecordingCommand : MigrationCommand
    {
        private readonly int _result;

        public RecordingCommand(
            IRelationalCommand command,
            DbContext context,
            MigrationsSqlGeneratorDependencies dependencies,
            int result,
            bool transactionSuppressed
        ) : base(command, context, dependencies.Logger, transactionSuppressed)
        {
            _result = result;
        }

        public int Executions { get; private set; }
        public IReadOnlyDictionary<string, object?>? ReceivedParameters { get; private set; }

        public override int ExecuteNonQuery(
            IRelationalConnection connection,
            IReadOnlyDictionary<string, object?>? parameterValues = null
        )
        {
            Executions++;
            ReceivedParameters = parameterValues;

            return _result;
        }
    }
}
