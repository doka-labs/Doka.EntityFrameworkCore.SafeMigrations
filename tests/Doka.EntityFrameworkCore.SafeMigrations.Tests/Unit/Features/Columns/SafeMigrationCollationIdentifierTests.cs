namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationCollationIdentifierTests
{
    [Fact]
    public void Identity_UsesOrdinalSchemaAndNameComponents()
    {
        var first = new SafeMigrationCollationIdentifier("name.with.dot", "schema_a");
        var same = new SafeMigrationCollationIdentifier("name.with.dot", "schema_a");
        var otherSchema = new SafeMigrationCollationIdentifier("name.with.dot", "schema_b");
        var encodedPath = new SafeMigrationCollationIdentifier("schema_a.name.with.dot");

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, otherSchema);
        Assert.NotEqual(first, encodedPath);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("valid", "")]
    [InlineData("valid", " ")]
    public void Constructor_RejectsInvalidIdentityParts(
        string? name,
        string? schema
    ) => Assert.ThrowsAny<ArgumentException>(() => new SafeMigrationCollationIdentifier(name!, schema));

    [Fact]
    public void ContractFingerprint_DistinguishesQualifiedAndDottedCollationIdentities()
    {
        var qualified = Fingerprint(new SafeMigrationCollationIdentifier("name.with.dot", "schema_a"));
        var otherSchema = Fingerprint(new SafeMigrationCollationIdentifier("name.with.dot", "schema_b"));
        var encodedPath = Fingerprint(new SafeMigrationCollationIdentifier("schema_a.name.with.dot"));

        Assert.NotEqual(qualified, otherSchema);
        Assert.NotEqual(qualified, encodedPath);
        Assert.NotEqual(otherSchema, encodedPath);
    }

    private static string Fingerprint(
        SafeMigrationCollationIdentifier collation
    ) => SafeMigrationContractFingerprint.Create(
    [
        new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition("value", typeof(string), true, "text", collation: collation)),
            SafeMigrationPolicy.ThrowIfDifferent),
    ]);
}
