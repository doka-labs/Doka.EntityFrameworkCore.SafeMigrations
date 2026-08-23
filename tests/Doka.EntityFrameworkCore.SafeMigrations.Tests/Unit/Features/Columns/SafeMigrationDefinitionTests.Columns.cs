namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationDefinitionTests
{
    [Fact]
    public void DefaultValueSupportsEveryDocumentedLiteralFamily()
    {
        foreach (var value in SafeMigrationLiteralContract.CreateRepresentativeValues())
        {
            Assert.Equal(
                SafeMigrationDefaultValueKind.Literal,
                SafeMigrationDefaultValue.Literal(value)
                    .Kind);
        }
    }

    [Fact]
    public void ColumnRepairSafetyBindsEveryLosslessTransitionField()
    {
        Assert.Throws<ArgumentNullException>(() => SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(null!));
        Assert.True(SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(RepairColumn(isNullable: true)));
        Assert.True(
            SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(
                RepairColumn(defaultValue: SafeMigrationDefaultValue.Literal(1m))));
        Assert.True(
            SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(
                RepairColumn(computedColumnSql: "1 + 1", isStored: true)));
        Assert.False(SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(RepairColumn()));

        var baseline = RepairColumn(isNullable: true, comment: "legacy");
        var losslessTarget = RepairColumn(
            isNullable: false,
            comment: "canonical",
            defaultValue: SafeMigrationDefaultValue.Literal(1m));

        Assert.Throws<ArgumentNullException>(() =>
            SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(null!, losslessTarget));
        Assert.Throws<ArgumentNullException>(() =>
            SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(baseline, null!));
        Assert.True(SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(baseline, losslessTarget));
        Assert.All(
            new[]
            {
                RepairColumn(name: "other", isNullable: true, comment: "legacy"),
                RepairColumn(clrType: typeof(long), isNullable: true, comment: "legacy"),
                RepairColumn(storeType: "bigint", isNullable: true, comment: "legacy"),
                RepairColumn(isUnicode: true, isNullable: true, comment: "legacy"),
                RepairColumn(maxLength: 40, isNullable: true, comment: "legacy"),
                RepairColumn(isFixedLength: true, isNullable: true, comment: "legacy"),
                RepairColumn(isRowVersion: true, isNullable: true, comment: "legacy"),
                RepairColumn(precision: 11, isNullable: true, comment: "legacy"),
                RepairColumn(scale: 3, isNullable: true, comment: "legacy"),
                RepairColumn(collation: "other", isNullable: true, comment: "legacy"),
                RepairColumn(computedColumnSql: "1 + 1", isNullable: true, comment: "legacy", isStored: true),
            },
            target => Assert.False(SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(baseline, target)));

        var computedBaseline = RepairColumn(computedColumnSql: "1 + 1", isStored: true);
        var virtualTarget = RepairColumn(computedColumnSql: "1 + 1", isStored: false);

        Assert.False(SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(computedBaseline, virtualTarget));
    }

    private static ExpectedColumnDefinition RepairColumn(
        string name = "value",
        Type? clrType = null,
        bool isNullable = false,
        string storeType = "decimal(10,2)",
        bool? isUnicode = false,
        int? maxLength = null,
        bool? isFixedLength = false,
        bool isRowVersion = false,
        int? precision = 10,
        int? scale = 2,
        string? collation = "canonical",
        string? comment = null,
        SafeMigrationDefaultValue? defaultValue = null,
        string? computedColumnSql = null,
        bool? isStored = null
    ) => new(
        name,
        clrType ?? typeof(decimal),
        isNullable,
        storeType,
        isUnicode,
        maxLength,
        isFixedLength,
        isRowVersion,
        precision,
        scale,
        collation is null ? null : new SafeMigrationCollationIdentifier(collation),
        comment,
        defaultValue ?? SafeMigrationDefaultValue.None,
        computedColumnSql,
        isStored);
}
