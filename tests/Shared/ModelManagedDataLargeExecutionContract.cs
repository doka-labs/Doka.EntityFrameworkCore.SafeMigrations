namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

internal static class ModelManagedDataLargeExecutionContract
{
    public const int TotalRowCount = 50_000;

    private const int BatchSize = 128;
    private const int EnsureRowCount = 16_640;
    private const int UpdateRowCount = 16_640;
    private const int DeleteRowCount = TotalRowCount - EnsureRowCount - UpdateRowCount;
    private const int EnsureKeyOffset = 1_000_000;
    private const int UpdateKeyOffset = 2_000_000;
    private const int DeleteKeyOffset = 3_000_000;
    private const string SourceValue = "source";
    private const string TargetValue = "target";

    public static ModelManagedDataLargeExecutionExpectation Populate(
        MigrationBuilder builder,
        string integerStoreType,
        string textStoreType
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddEnsureOperations(builder, integerStoreType, textStoreType);
        AddUpdateOperations(builder, integerStoreType, textStoreType);
        AddDeleteOperations(builder, integerStoreType, textStoreType);

        AssertBoundedOperations(builder.Operations);

        return new ModelManagedDataLargeExecutionExpectation(
            builder.Operations.Count,
            EnsureRowCount + UpdateRowCount);
    }

    public static IEnumerable<ModelManagedDataInitialRow> InitialRows()
    {
        for (var ordinal = 0; ordinal < EnsureRowCount; ordinal += 2)
        {
            yield return new ModelManagedDataInitialRow(EnsureKeyOffset + ordinal, TargetValue);
        }

        for (var ordinal = 0; ordinal < UpdateRowCount; ordinal++)
        {
            yield return new ModelManagedDataInitialRow(
                UpdateKeyOffset + ordinal,
                ordinal % 2 == 0 ? SourceValue : TargetValue);
        }

        for (var ordinal = 0; ordinal < DeleteRowCount; ordinal += 2)
        {
            yield return new ModelManagedDataInitialRow(DeleteKeyOffset + ordinal, SourceValue);
        }
    }

    private static void AddEnsureOperations(
        MigrationBuilder builder,
        string integerStoreType,
        string textStoreType
    )
    {
        foreach (var batch in Batches(EnsureRowCount))
        {
            var values = new object?[batch.Count, 2];
            for (var row = 0; row < batch.Count; row++)
            {
                values[row, 0] = EnsureKeyOffset + batch.Offset + row;
                values[row, 1] = TargetValue;
            }

            _ = builder.EnsureModelManagedDataFromModel(
                "large_model_managed_rows",
                ["id"],
                [integerStoreType],
                ["id", "managed_value"],
                [integerStoreType, textStoreType],
                values);
        }
    }

    private static void AddUpdateOperations(
        MigrationBuilder builder,
        string integerStoreType,
        string textStoreType
    )
    {
        foreach (var batch in Batches(UpdateRowCount))
        {
            var keyValues = new object?[batch.Count, 1];
            var oldValues = new object?[batch.Count, 1];
            var newValues = new object?[batch.Count, 1];

            for (var row = 0; row < batch.Count; row++)
            {
                keyValues[row, 0] = UpdateKeyOffset + batch.Offset + row;
                oldValues[row, 0] = SourceValue;
                newValues[row, 0] = TargetValue;
            }

            _ = builder.UpdateModelManagedDataFromModel(
                "large_model_managed_rows",
                ["id"],
                [integerStoreType],
                keyValues,
                ["managed_value"],
                [textStoreType],
                oldValues,
                newValues);
        }
    }

    private static void AddDeleteOperations(
        MigrationBuilder builder,
        string integerStoreType,
        string textStoreType
    )
    {
        foreach (var batch in Batches(DeleteRowCount))
        {
            var keyValues = new object?[batch.Count, 1];
            var oldValues = new object?[batch.Count, 2];

            for (var row = 0; row < batch.Count; row++)
            {
                var key = DeleteKeyOffset + batch.Offset + row;

                keyValues[row, 0] = key;
                oldValues[row, 0] = key;
                oldValues[row, 1] = SourceValue;
            }

            _ = builder.DeleteModelManagedDataFromModel(
                "large_model_managed_rows",
                ["id"],
                [integerStoreType],
                keyValues,
                ["id", "managed_value"],
                [integerStoreType, textStoreType],
                oldValues);
        }
    }

    private static IEnumerable<(int Offset, int Count)> Batches(
        int count
    )
    {
        for (var offset = 0; offset < count; offset += BatchSize)
        {
            yield return (offset, Math.Min(BatchSize, count - offset));
        }
    }

    private static void AssertBoundedOperations(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        foreach (var operation in operations.Cast<SafeMigrationOperation>())
        {
            var intent = Assert.IsAssignableFrom<ModelManagedDataIntent>(operation.Intent);
            var cellCount = intent switch
            {
                EnsureModelManagedDataIntent ensure =>
                    Cells(ensure.KeyValues) + Cells(ensure.Values),
                UpdateModelManagedDataIntent update =>
                    Cells(update.KeyValues) + Cells(update.OldValues) + Cells(update.NewValues),
                DeleteModelManagedDataIntent deletion =>
                    Cells(deletion.KeyValues) + Cells(deletion.OldValues),
                _ => throw new ArgumentOutOfRangeException(nameof(operations)),
            };

            Assert.InRange(intent.RowCount, 1, SafeMigrationModelManagedDataLimits.MaximumRowsPerOperation);
            Assert.InRange(cellCount, 1, SafeMigrationModelManagedDataLimits.MaximumCellsPerOperation);
        }
    }

    private static int Cells(
        ModelManagedDataMatrix matrix
    ) => checked(matrix.RowCount * matrix.ColumnCount);
}

internal sealed class ModelManagedDataLargeExecutionExpectation
{
    public ModelManagedDataLargeExecutionExpectation(
        int operationCount,
        int finalRowCount
    )
    {
        OperationCount = operationCount;
        FinalRowCount = finalRowCount;
    }

    public int OperationCount { get; }

    public int FinalRowCount { get; }

    public void AssertInitialReport(
        SafeMigrationRunReport report
    )
    {
        ArgumentNullException.ThrowIfNull(report);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(OperationCount, report.Assessments.Count);
        Assert.All(report.Assessments, static assessment =>
            Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Contains(
            report.Assessments,
            static assessment => assessment.ObservedState == SafeMigrationObservedState.Missing);
        Assert.Contains(
            report.Assessments,
            static assessment => assessment.ObservedState == SafeMigrationObservedState.TransitionReady);
    }

    public void AssertReplayReport(
        SafeMigrationRunReport report
    )
    {
        ArgumentNullException.ThrowIfNull(report);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(OperationCount, report.Assessments.Count);
        Assert.All(report.Assessments, static assessment =>
            Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Contains(
            report.Assessments,
            static assessment => assessment.ObservedState == SafeMigrationObservedState.Matching);
        Assert.Contains(
            report.Assessments,
            static assessment => assessment.ObservedState == SafeMigrationObservedState.Missing);
    }
}

internal readonly record struct ModelManagedDataInitialRow(
    int Id,
    string Value
);

internal readonly record struct ModelManagedDataPhaseMeasurement(
    double ElapsedMilliseconds,
    long AllocatedBytes
);

internal readonly record struct ModelManagedDataMeasuredResult<T>(
    T Result,
    ModelManagedDataPhaseMeasurement Measurement
);

internal static class ModelManagedDataLargeExecutionEvidence
{
    private static readonly System.Text.Json.JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<ModelManagedDataMeasuredResult<T>> MeasureAsync<T>(
        Func<Task<T>> action
    )
    {
        ArgumentNullException.ThrowIfNull(action);

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var result = await action();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        return new ModelManagedDataMeasuredResult<T>(
            result,
            new ModelManagedDataPhaseMeasurement(elapsed, allocated));
    }

    public static async Task<ModelManagedDataPhaseMeasurement> MeasureAsync(
        Func<Task> action
    )
    {
        ArgumentNullException.ThrowIfNull(action);

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        await action();

        return new ModelManagedDataPhaseMeasurement(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
    }

    public static void Write(
        string provider,
        string version,
        IReadOnlyList<MigrationCommand> commands,
        ModelManagedDataPhaseMeasurement initialAnalysis,
        ModelManagedDataPhaseMeasurement initialExecution,
        ModelManagedDataPhaseMeasurement replayExecution,
        ModelManagedDataPhaseMeasurement replayAnalysis
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(commands);

        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "artifacts", "performance", "live");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{provider}-{version}-model-managed-data.json");
        var payload = new
        {
            schemaVersion = 1,
            runtime = Environment.Version.ToString(),
            provider,
            version,
            totalRowTransitions = ModelManagedDataLargeExecutionContract.TotalRowCount,
            commandCount = commands.Count,
            maximumBatchRows = SafeMigrationModelManagedDataLimits.MaximumRowsPerOperation,
            maximumBatchCells = SafeMigrationModelManagedDataLimits.MaximumCellsPerOperation,
            maximumGeneratedCommandUtf8Bytes = commands.Count == 0
                ? 0
                : commands.Max(static command => Encoding.UTF8.GetByteCount(command.CommandText)),
            initialAnalysis,
            initialExecution,
            replayExecution,
            replayAnalysis,
        };

        File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, s_serializerOptions));
    }

    private static string FindRepositoryRoot(
        string start
    )
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }
}
