namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationContractFingerprintTests
{
    [Fact]
    public void Fingerprint_IsDeterministicAndSnapshotsMutableLiteralInput()
    {
        var bytes = new byte[] { 1, 2, 3, };

        var operations = Operations(
            new EnsureColumnIntent(
                "items",
                Column(
                    clrType: typeof(byte[]),
                    storeType: "varbinary(3)",
                    defaultValue: SafeMigrationDefaultValue.Literal(bytes))));

        var first = SafeMigrationContractFingerprint.Create(operations);
        bytes[0] = 9;
        var second = SafeMigrationContractFingerprint.Create(operations);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, static value => Assert.True(value is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
    }

    [Fact]
    public void Fingerprint_BindsEveryColumnField()
    {
        var baseline = Fingerprint(new EnsureColumnIntent("items", Column()));
        var variants = new SafeMigrationIntent[]
        {
            new EnsureColumnIntent("other", Column()), new EnsureColumnIntent("items", Column(name: "other")),
            new EnsureColumnIntent("items", Column(clrType: typeof(int))),
            new EnsureColumnIntent("items", Column(isNullable: false)),
            new EnsureColumnIntent("items", Column(storeType: "varchar(41)")),
            new EnsureColumnIntent("items", Column(isUnicode: false)),
            new EnsureColumnIntent("items", Column(maxLength: 41)),
            new EnsureColumnIntent("items", Column(isFixedLength: true)),
            new EnsureColumnIntent("items", Column(isRowVersion: true)),
            new EnsureColumnIntent("items", Column(precision: 12)),
            new EnsureColumnIntent("items", Column(scale: 3)),
            new EnsureColumnIntent("items", Column(collation: "binary")),
            new EnsureColumnIntent("items", Column(comment: "other")),
            new EnsureColumnIntent("items", Column(defaultValue: SafeMigrationDefaultValue.Literal("other"))),
            new EnsureColumnIntent(
                "items",
                Column(defaultValue: SafeMigrationDefaultValue.None, computedColumnSql: "1 + 1", isStored: true)),
        };

        Assert.All(variants, variant => Assert.NotEqual(baseline, Fingerprint(variant)));
    }
}
