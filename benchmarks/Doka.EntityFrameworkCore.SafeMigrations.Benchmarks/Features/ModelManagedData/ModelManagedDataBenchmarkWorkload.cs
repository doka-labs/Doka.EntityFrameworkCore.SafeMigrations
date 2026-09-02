namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

internal static class ModelManagedDataBenchmarkWorkload
{
    public const int RowsPerOperation = 128;
    public const int TransitionCount = RowsPerOperation * 3;

    public static IReadOnlyList<MigrationOperation> CreateOperations(
        string providerName,
        string integerStoreType,
        string textStoreType
    )
    {
        var builder = new MigrationBuilder(providerName);
        var ensureValues = new object?[RowsPerOperation, 2];
        var updateKeys = new object?[RowsPerOperation, 1];
        var updateSource = new object?[RowsPerOperation, 1];
        var updateTarget = new object?[RowsPerOperation, 1];
        var deleteKeys = new object?[RowsPerOperation, 1];
        var deleteSource = new object?[RowsPerOperation, 2];

        for (var row = 0; row < RowsPerOperation; row++)
        {
            ensureValues[row, 0] = 1_000_000 + row;
            ensureValues[row, 1] = "target";
            updateKeys[row, 0] = 2_000_000 + row;
            updateSource[row, 0] = "source";
            updateTarget[row, 0] = "target";
            deleteKeys[row, 0] = 3_000_000 + row;
            deleteSource[row, 0] = 3_000_000 + row;
            deleteSource[row, 1] = "source";
        }

        _ = builder.EnsureModelManagedDataFromModel(
            "benchmark_model_data",
            ["id"],
            [integerStoreType],
            ["id", "managed_value"],
            [integerStoreType, textStoreType],
            ensureValues);
        _ = builder.UpdateModelManagedDataFromModel(
            "benchmark_model_data",
            ["id"],
            [integerStoreType],
            updateKeys,
            ["managed_value"],
            [textStoreType],
            updateSource,
            updateTarget);
        _ = builder.DeleteModelManagedDataFromModel(
            "benchmark_model_data",
            ["id"],
            [integerStoreType],
            deleteKeys,
            ["id", "managed_value"],
            [integerStoreType, textStoreType],
            deleteSource);

        return builder.Operations;
    }
}
