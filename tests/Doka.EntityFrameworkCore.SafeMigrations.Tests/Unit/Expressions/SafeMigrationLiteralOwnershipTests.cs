namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Protects captured binary expression contracts from caller-owned aliases.</summary>
public sealed class SafeMigrationLiteralOwnershipTests
{
    /// <summary>Neither input mutation nor a value returned to a caller can change a captured contract.</summary>
    /// <param name="mutateGetter">Whether to mutate the public getter result rather than the original input.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BinaryMutationPreservesFingerprintAndSemanticLookup(
        bool mutateGetter
    )
    {
        // Arrange
        byte[] input = [1, 2, 3];
        var literal = new SafeMigrationSqlLiteralExpression(input);
        var definition = ExpectedCheckConstraintDefinition.FromExpression("ck_payload", "items", literal);
        var equivalent = ExpectedCheckConstraintDefinition.FromExpression("ck_payload",
            "items",
            SafeMigrationSql.Literal(new byte[] { 1, 2, 3 }));

        var comparer = SafeMigrationSemanticDefinitionComparers.CheckConstraint;
        var definitions = new HashSet<ExpectedCheckConstraintDefinition>(comparer) { definition };
        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var before = SafeMigrationContractFingerprint.Create([operation]);
        var mutation = mutateGetter
            ? (byte[])literal.Value!
            : input;

        // Act
        mutation[0] = 9;
        var after = SafeMigrationContractFingerprint.Create([operation]);
        var remainsEqual = comparer.Equals(definition, equivalent);
        var remainsFindable = definitions.Contains(equivalent);

        // Assert
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(literal.Value));
        Assert.Equal(before, after);
        Assert.True(remainsEqual);
        Assert.True(remainsFindable);
    }

    /// <summary>Binary getter results have independent ownership even when callers only read them initially.</summary>
    [Fact]
    public void BinaryValueReturnsIndependentSnapshots()
    {
        // Arrange
        byte[] input = [1, 2];
        var literal = new SafeMigrationSqlLiteralExpression(input);

        // Act
        var first = literal.Value;
        var second = literal.Value;

        // Assert
        Assert.NotSame(input, first);
        Assert.NotSame(first, second);
        Assert.Equal(input, Assert.IsType<byte[]>(first));
        Assert.Equal(input, Assert.IsType<byte[]>(second));
    }

    /// <summary>Trusted internal readers reuse the owned snapshot rather than clone once per AST visit.</summary>
    [Fact]
    public void InternalBinaryBorrowingRetainsOneOwnedSnapshot()
    {
        // Arrange
        byte[] input = [1, 2];
        var literal = new SafeMigrationSqlLiteralExpression(input);

        // Act
        var first = literal.BorrowedValue;
        var second = literal.BorrowedValue;
        var publicValue = literal.Value;

        // Assert
        Assert.Same(first, second);
        Assert.NotSame(input, first);
        Assert.NotSame(first, publicValue);
        Assert.Equal(input, Assert.IsType<byte[]>(first));
    }

    /// <summary>Immutable values and SQL NULL retain their existing public representation.</summary>
    [Fact]
    public void NonBinaryValuesRetainTheirRepresentation()
    {
        // Arrange
        var text = new string('x', 3);
        var literal = new SafeMigrationSqlLiteralExpression(text);
        var nullLiteral = new SafeMigrationSqlLiteralExpression(null);

        // Act
        var value = literal.Value;
        var nullValue = nullLiteral.Value;

        // Assert
        Assert.Same(text, value);
        Assert.Null(nullValue);
    }
}
