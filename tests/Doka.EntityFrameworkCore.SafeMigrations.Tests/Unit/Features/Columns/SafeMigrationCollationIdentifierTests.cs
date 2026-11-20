namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Pins ordinal schema/name identity separately from dotted display names and their contract fingerprints.
/// </summary>
public sealed class SafeMigrationCollationIdentifierTests
{
    /// <summary>Compares schema and name as separate ordinal identity components.</summary>
    /// <param name="name">The compared collation name.</param>
    /// <param name="schema">The compared schema component.</param>
    /// <param name="expectedEqual">Whether the complete identity matches the baseline.</param>
    [Theory]
    [InlineData("name.with.dot", "schema_a", true)]
    [InlineData("name.with.dot", "schema_b", false)]
    [InlineData("schema_a.name.with.dot", null, false)]
    public void Identity_UsesOrdinalSchemaAndNameComponents(
        string name,
        string? schema,
        bool expectedEqual
    )
    {
        // Arrange
        var first = new SafeMigrationCollationIdentifier("name.with.dot", "schema_a");
        var compared = new SafeMigrationCollationIdentifier(name, schema);

        // Act
        var equal = first.Equals(compared);
        var firstHash = first.GetHashCode();
        var comparedHash = compared.GetHashCode();

        // Assert
        Assert.Equal(expectedEqual, equal);
        if (expectedEqual)
        {
            Assert.Equal(firstHash, comparedHash);
        }
    }

    /// <summary>Rejects missing or blank collation identity components.</summary>
    /// <param name="name">The invalid name or a valid name paired with an invalid schema.</param>
    /// <param name="schema">The optional schema component.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("valid", "")]
    [InlineData("valid", " ")]
    public void Constructor_RejectsInvalidIdentityParts(
        string? name,
        string? schema
    )
    {
        // Arrange: The theory supplies the invalid identity components.

        // Act
        var exception = Record.Exception(() => new SafeMigrationCollationIdentifier(name!, schema));

        // Assert
        Assert.IsType<ArgumentException>(exception, exactMatch: false);
    }

    /// <summary>
    /// Keeps schema/name component identity distinct from a dotted unqualified name in every compared fingerprint.
    /// </summary>
    [Fact]
    public void ContractFingerprint_DistinguishesQualifiedAndDottedCollationIdentities()
    {
        // Arrange
        var qualifiedIdentity = new SafeMigrationCollationIdentifier("name.with.dot", "schema_a");
        var otherSchemaIdentity = new SafeMigrationCollationIdentifier("name.with.dot", "schema_b");
        var encodedIdentity = new SafeMigrationCollationIdentifier("schema_a.name.with.dot");

        // Act
        var qualified = Fingerprint(qualifiedIdentity);
        var otherSchema = Fingerprint(otherSchemaIdentity);
        var encodedPath = Fingerprint(encodedIdentity);

        // Assert
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
