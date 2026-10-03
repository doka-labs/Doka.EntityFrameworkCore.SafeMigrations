namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

internal static class SqlServerColumnBenchmarkWorkload
{
    public static IReadOnlyList<MigrationOperation> CreateRepairOperations(
        int count
    )
    {
        var operations = new List<MigrationOperation>(count);

        for (var index = 0; index < count; index++)
        {
            var name = $"value_{index.ToString(CultureInfo.InvariantCulture)}";
            var oldDefinition = new ExpectedColumnDefinition(
                name,
                typeof(string),
                isNullable: true,
                storeType: "nvarchar(64)",
                maxLength: 64);

            var definition = new ExpectedColumnDefinition(
                name,
                typeof(string),
                isNullable: true,
                storeType: "nvarchar(128)",
                maxLength: 128);

            operations.Add(
                new SafeMigrationOperation(
                    new AlterColumnIntent("benchmark_items", definition, oldDefinition, "dbo"),
                    SafeMigrationPolicy.RepairIfSafe));
        }

        return operations;
    }
}
