namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationContractFingerprintTests
{
    /// <summary>
    /// Retains the captured binary literal when its caller-owned array changes between repeated contract hashes.
    /// </summary>
    [Fact]
    public void Fingerprint_IsDeterministicAndSnapshotsMutableLiteralInput()
    {
        // Arrange
        var bytes = new byte[] { 1, 2, 3 };

        var operations = Operations(
            new EnsureColumnIntent(
                "items",
                Column(
                    clrType: typeof(byte[]),
                    storeType: "varbinary(3)",
                    defaultValue: SafeMigrationDefaultValue.Literal(bytes))));

        // Act
        var first = SafeMigrationContractFingerprint.Create(operations);
        bytes[0] = 9;
        var second = SafeMigrationContractFingerprint.Create(operations);

        // Assert
        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, static value => Assert.True(value is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    /// <summary>Binds every independent column field into the contract fingerprint.</summary>
    /// <param name="variant">The independently changed facet ordinal.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Fingerprint_BindsEveryColumnField(
        int variant
    )
    {
        // Arrange
        var baselineIntent = new EnsureColumnIntent("items", Column());
        var variants = new SafeMigrationIntent[]
        {
            new EnsureColumnIntent("other", Column()),
            new EnsureColumnIntent("items", Column(name: "other")),
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

        // Act
        var baseline = Fingerprint(baselineIntent);
        var actual = Fingerprint(variants[variant]);

        // Assert
        Assert.NotEqual(baseline, actual);
    }
}
