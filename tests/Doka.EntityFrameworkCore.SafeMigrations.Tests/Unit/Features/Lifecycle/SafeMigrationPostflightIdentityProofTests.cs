namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies stream-scoped physical identity before final-writer reduction.</summary>
public sealed class SafeMigrationPostflightIdentityProofTests
{
    /// <summary>Unknown historical identities neither trust final lookup nor supersede another named writer.</summary>
    /// <param name="unprovenOrdinal">The complete-stream ordinal whose physical identity is unproven.</param>
    /// <param name="superseded">Whether both identities are proven and final-writer reduction is valid.</param>
    [Theory]
    [InlineData(null, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    public void ScopedIdentityFactoryControlsBothSidesOfFinalWriterReduction(int? unprovenOrdinal, bool superseded)
    {
        // Arrange
        MigrationOperation[] operations =
        [
            new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("items",
                [new ExpectedColumnDefinition("Id", typeof(int), false, "int")]),
                SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent),
            new SqlOperation { Sql = "SELECT 1;", },
            new SafeMigrationOperation(new DropTableIntent("items"), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        var identity = new IdentityProofFactory(unprovenOrdinal);
        SafeMigrationProviderAnalysis[] analyses =
        [
            new(SafeMigrationObservedState.Missing, SafeMigrationRepairCapability.None, false, "final_missing"),
            new(SafeMigrationObservedState.Missing, SafeMigrationRepairCapability.None, true, "final_missing"),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, identity, liveAnalyses: analyses);

        // Assert
        Assert.Equal(1, identity.FactoryCalls);
        Assert.Equal(operations.Length, identity.StreamLength);
        Assert.Equal(superseded, projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(2));
        Assert.Equal(unprovenOrdinal == 0, projection.IsIdentityUnproven(0));
        Assert.False(projection.IsIdentityUnproven(1));
        Assert.Equal(unprovenOrdinal == 2, projection.IsIdentityUnproven(2));
    }

    /// <summary>Supplies a scoped proof view distinct from the final-catalog normalizer itself.</summary>
    private sealed class IdentityProofFactory(int? unprovenOrdinal) : ISafeMigrationProviderObjectIdentityNormalizer
    {
        /// <summary>Gets how often the complete stream was captured.</summary>
        public int FactoryCalls { get; private set; }

        /// <summary>Gets the captured complete-stream size, including ordinary operations.</summary>
        public int StreamLength { get; private set; }

        /// <inheritdoc />
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        /// <inheritdoc />
        public string NormalizeIdentifier(string identifier) => identifier;

        /// <inheritdoc />
        public string? NormalizeSchema(string? schema) => schema;

        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;

        /// <inheritdoc />
        public ISafeMigrationProviderObjectIdentityNormalizer GetPostflightIdentityNormalizer(
            IReadOnlyList<MigrationOperation> operations
        )
        {
            FactoryCalls++;
            StreamLength = operations.Count;

            // WHY: The outer final-catalog normalizer has no historical rejection.
            // Only consuming this returned snapshot can enforce the scoped proof.
            return new IdentityProofSnapshot(unprovenOrdinal);
        }
    }

    /// <summary>Certifies all stable identities except one explicit complete-stream boundary.</summary>
    private sealed class IdentityProofSnapshot(int? unprovenOrdinal) : ISafeMigrationProviderObjectIdentityNormalizer
    {
        /// <inheritdoc />
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        /// <inheritdoc />
        public string NormalizeIdentifier(string identifier) => identifier;

        /// <inheritdoc />
        public string? NormalizeSchema(string? schema) => schema;

        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;

        /// <inheritdoc />
        public bool IsPostflightIdentityProven(SafeMigrationIntent intent, int ordinal) => ordinal != unprovenOrdinal;
    }
}
