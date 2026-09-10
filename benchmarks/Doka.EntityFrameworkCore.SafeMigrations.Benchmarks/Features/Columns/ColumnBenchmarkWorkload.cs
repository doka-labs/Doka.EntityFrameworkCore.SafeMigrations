namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

internal static class ColumnBenchmarkWorkload
{
    public static IReadOnlyList<MigrationOperation> CreateOperations(
        int count
    )
    {
        var operations = new List<MigrationOperation>(count);
        for (var index = 0; index < count; index++)
        {
            var definition = new ExpectedColumnDefinition(
                $"value_{index.ToString(CultureInfo.InvariantCulture)}",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80);

            operations.Add(
                new SafeMigrationOperation(
                    new EnsureColumnIntent("benchmark_items", definition),
                    SafeMigrationPolicy.ThrowIfDifferent));
        }

        return operations;
    }

    public static IReadOnlyList<MigrationOperation> CreateRepairOperations(
        int count
    )
    {
        var operations = new List<MigrationOperation>(count);
        for (var index = 0; index < count; index++)
        {
            var definition = new ExpectedColumnDefinition(
                $"value_{index.ToString(CultureInfo.InvariantCulture)}",
                typeof(string),
                isNullable: false,
                storeType: "varchar(80)",
                maxLength: 80,
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical"));

            operations.Add(
                new SafeMigrationOperation(
                    new EnsureColumnIntent("benchmark_items", definition),
                    SafeMigrationPolicy.RepairIfSafe));
        }

        return operations;
    }

    public static int RunPlanner(
        int count
    )
    {
        var checksum = 0;
        for (var index = 0; index < count; index++)
        {
            var state = (SafeMigrationObservedState)(index % 5);
            var decision = SafeMigrationDecisionPlanner.Plan(
                SafeMigrationOperationKind.EnsureColumn,
                state,
                SafeMigrationPolicy.ThrowIfDifferent);

            checksum += (int)decision.Action;
        }

        return checksum;
    }

    public static SafeMigrationRunReport CreateReport(
        int count
    )
    {
        var assessments = new SafeMigrationAssessment[count];
        for (var index = 0; index < count; index++)
        {
            assessments[index] = new SafeMigrationAssessment(
                index,
                typeof(SafeMigrationOperation).FullName!,
                isSafeOperation: true,
                SafeMigrationOperationKind.EnsureColumn,
                $"value_{index.ToString(CultureInfo.InvariantCulture)}",
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply,
                postconditionSatisfied: false,
                "apply_missing");
        }

        return new SafeMigrationRunReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Ready,
            new DateTimeOffset(
                2026,
                8,
                17,
                0,
                0,
                0,
                TimeSpan.Zero),
            "benchmark-instance",
            new SafeMigrationProviderEnvironment("benchmark", "mysql", "10.0.0"),
            "202608170001_Benchmark",
            $"safe-relational-model:v1:benchmark:sha256:{new string('a', 64)}",
            new string('b', 64),
            assessments);
    }

    public static SafeMigrationRunReport CreateBlockingReportViewWorkload(
        int count,
        int blockerCount
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockerCount, count);

        var assessments = new SafeMigrationAssessment[count];
        var firstBlockerIndex = count - blockerCount;
        for (var index = 0; index < count; index++)
        {
            var isBlocking = index >= firstBlockerIndex;
            var blockerOffset = index - firstBlockerIndex;
            var outcome = isBlocking
                ? (blockerOffset % 4) switch
                {
                    0 => (
                        ObservedState: SafeMigrationObservedState.Unsupported,
                        Action: SafeMigrationAction.RejectUnsupported),
                    1 => (
                        ObservedState: SafeMigrationObservedState.Different,
                        Action: SafeMigrationAction.RejectDifferent),
                    2 => (
                        ObservedState: SafeMigrationObservedState.DataBlocked,
                        Action: SafeMigrationAction.RejectDataBlocked),
                    _ => (
                        ObservedState: SafeMigrationObservedState.PrerequisiteMissing,
                        Action: SafeMigrationAction.RejectPrerequisiteMissing),
                }
                : (
                    ObservedState: SafeMigrationObservedState.Matching,
                    Action: SafeMigrationAction.NoOp);

            assessments[index] = new SafeMigrationAssessment(
                index,
                typeof(SafeMigrationOperation).FullName!,
                isSafeOperation: true,
                SafeMigrationOperationKind.EnsureColumn,
                $"value_{index.ToString(CultureInfo.InvariantCulture)}",
                outcome.ObservedState,
                outcome.Action,
                postconditionSatisfied: !isBlocking,
                isBlocking ? "different_reject" : "classified_matching");
        }

        return new SafeMigrationRunReport(
            SafeMigrationReportMode.Preflight,
            blockerCount == 0
                ? SafeMigrationReportStatus.Ready
                : SafeMigrationReportStatus.Blocked,
            new DateTimeOffset(
                2026,
                9,
                10,
                0,
                0,
                0,
                TimeSpan.Zero),
            "benchmark-instance",
            new SafeMigrationProviderEnvironment("benchmark", "mysql", "10.0.0"),
            "202609100001_Benchmark",
            $"safe-relational-model:v1:benchmark:sha256:{new string('a', 64)}",
            new string('b', 64),
            assessments);
    }
}
