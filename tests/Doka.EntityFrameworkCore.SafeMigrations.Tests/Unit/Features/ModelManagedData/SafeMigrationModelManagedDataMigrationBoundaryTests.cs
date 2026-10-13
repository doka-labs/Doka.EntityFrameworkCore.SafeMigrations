namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Preserves one-migration repetition safety while accepting internally certified later migrations.</summary>
public sealed class SafeMigrationModelManagedDataMigrationBoundaryTests
{
    /// <summary>Different certified migration origins permit a typed seed's later transition.</summary>
    /// <param name="reordered">Whether later predicates reorder the same column/value pairs.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CertifiedDifferentMigrationOriginsAllowLaterTypedKeyTransition(bool reordered)
    {
        // Arrange
        MigrationOperation[] operations = [Seed(), Update(reordered)];
        string[] migrationIds = ["202610130001_Initial", "202610130002_Change"];

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationModelManagedDataContractValidator.Validate(operations, migrationIds));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Missing provenance or one shared authored ID preserves duplicate-key rejection.</summary>
    /// <param name="reordered">Whether repeated predicates reorder their pairs.</param>
    /// <param name="knownOrigin">Whether the shared authored ID is explicitly captured.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OneAuthoredMigrationRejectsRepeatedExactOrReorderedTypedKey(bool reordered, bool knownOrigin)
    {
        // Arrange
        MigrationOperation[] operations = [Seed(), Update(reordered)];
        string[]? migrationIds = knownOrigin ? ["same_authored", "same_authored"] : null;

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataContractValidator.Validate(operations, migrationIds));

        // Assert
        Assert.Contains("same typed model-managed key", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The existing one-argument API retains strict authored-batch repetition validation.</summary>
    [Fact]
    public void StandaloneValidationCannotInferMigrationBoundaries()
    {
        // Arrange
        MigrationOperation[] operations = [Seed(), Update(reordered: true)];

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataContractValidator.Validate(operations));

        // Assert
        Assert.Contains("same typed model-managed key", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Ordinary SQL or DDL inside one authored migration cannot reset its typed-key ledger.</summary>
    /// <param name="ordinarySql">Whether the interleaved operation is SQL rather than typed DDL.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryOperationsWithinOneOriginDoNotResetRepetitionProtection(bool ordinarySql)
    {
        // Arrange
        MigrationOperation ordinary = ordinarySql
            ? new SqlOperation { Sql = "SELECT 1;", }
            : new AlterTableOperation { Name = "other_items", Comment = "unrelated", };

        MigrationOperation[] operations = [Seed(), ordinary, Update(reordered: true)];
        string[] migrationIds = ["same_authored", "same_authored", "same_authored"];

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataContractValidator.Validate(operations, migrationIds));

        // Assert
        Assert.Contains("same typed model-managed key", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The complete-stream boundary includes ordinary operations before a later managed transition.</summary>
    [Fact]
    public void CertifiedMigrationBoundaryMayStartWithAnOrdinaryOperation()
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Seed(),
            new AlterTableOperation { Name = "other_items", Comment = "unrelated", },
            Update(reordered: true),
        ];

        string[] migrationIds = ["initial", "later", "later"];

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationModelManagedDataContractValidator.Validate(operations, migrationIds));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>Different typed rows remain valid inside one authored migration without boundary exemptions.</summary>
    [Fact]
    public void OneAuthoredMigrationAcceptsDistinctTypedKeys()
    {
        // Arrange
        MigrationOperation[] operations = [Seed(), Update(reordered: false, differentRow: true)];
        string[] migrationIds = ["same_authored", "same_authored"];

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationModelManagedDataContractValidator.Validate(operations, migrationIds));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>A malformed provenance count fails before any operation or migration-ID element is accessed.</summary>
    /// <param name="tooManyIds">Whether the provenance list is longer rather than shorter than the stream.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidCertifiedProvenanceCountFailsBeforeElementAccess(bool tooManyIds)
    {
        // Arrange
        var operations = new UnreadableOperations();
        string[] migrationIds = tooManyIds ? ["first", "second", "third"] : ["first"];

        // Act
        var exception = Assert.Throws<ArgumentException>(() =>
            SafeMigrationModelManagedDataContractValidator.Validate(operations, migrationIds));

        // Assert
        Assert.Equal("migrationIds", exception.ParamName);
        Assert.False(operations.ElementWasRead);
    }

    /// <summary>Creates one authored composite seed with distinct typed key values.</summary>
    private static SafeMigrationOperation Seed() => new(
        new EnsureModelManagedDataIntent("items", ["Id", "Code"], ["int", "int"],
            ["Id", "Code", "Value"], ["int", "int", "int"], new object?[,] { { 1, 2, 7 } },
            schema: null, uniqueKeys: null), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Reorders predicates with their values, or selects an independently different row.</summary>
    private static SafeMigrationOperation Update(bool reordered, bool differentRow = false) => new(
        new UpdateModelManagedDataIntent("items", reordered ? ["Code", "Id"] : ["Id", "Code"], ["int", "int"],
            reordered ? new object?[,] { { 2, differentRow ? 3 : 1 } }
                : new object?[,] { { differentRow ? 3 : 1, 2 } },
            ["Value"], ["int"], new object?[,] { { 7 } }, new object?[,] { { 8 } }, schema: null, uniqueKeys: null),
        SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Proves the count guard without allowing an accidental read to masquerade as validation.</summary>
    private sealed class UnreadableOperations : IReadOnlyList<MigrationOperation>
    {
        /// <summary>Gets whether validation accessed an operation before rejecting invalid provenance.</summary>
        public bool ElementWasRead { get; private set; }

        /// <inheritdoc />
        public int Count => 2;

        /// <inheritdoc />
        public MigrationOperation this[int index]
        {
            get
            {
                ElementWasRead = true;

                throw new InvalidOperationException("The provenance count must be checked before element access.");
            }
        }

        /// <inheritdoc />
        public IEnumerator<MigrationOperation> GetEnumerator() =>
            throw new InvalidOperationException("The provenance count must be checked before enumeration.");

        /// <inheritdoc />
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
