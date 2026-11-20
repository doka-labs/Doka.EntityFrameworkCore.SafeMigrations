namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Protects speculative cursor commits without replacing the immutable SQL source.</summary>
public sealed class SqlServerTestSqlReaderTests
{
    /// <summary>A single-token match commits the cursor; a mismatch retains the original source position.</summary>
    /// <param name="matches">Whether the expected identifier exists after the leading comment.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpeculativeTokenReadPreservesSourceAndRollsBackOnlyOnMismatch(
        bool matches
    )
    {
        // Arrange
        var reader = new SqlServerTestSqlReader("/* source */ [Value] ;");
        var expected = matches
            ? "[Value]"
            : "[Other]";

        // Act
        var matched = reader.TryRead(expected);

        // Assert
        Assert.Equal(matches, matched);
        Assert.Equal(matches
                ? 20
                : 0,
            reader.Position);

        Assert.False(reader.IsComment);
        Assert.True(reader.ReadSignificant());
        Assert.Equal(matches
                ? ";"
                : "[Value]",
            reader.Token.ToString());
    }

    /// <summary>A complete pattern commits all tokens, while any mismatch preserves the original cursor.</summary>
    /// <param name="matches">Whether the entire identifier and keyword pattern matches.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpeculativePatternReadRetainsSourceAfterCommitOrRollback(
        bool matches
    )
    {
        // Arrange
        var reader = new SqlServerTestSqlReader("SELECT /* source */ [Value] FROM [items];");
        reader.ReadSignificant();
        var originalPosition = reader.Position;
        var expected = matches
            ? "[Value] FROM"
            : "[Value] WHERE";

        // Act
        var matched = reader.TryReadSequence(expected);

        // Assert
        Assert.Equal(matches, matched);
        Assert.Equal(6, originalPosition);
        Assert.Equal(matches
                ? 32
                : originalPosition,
            reader.Position);

        Assert.False(reader.IsComment);
        Assert.True(reader.ReadSignificant());
        Assert.Equal(matches
                ? "[items]"
                : "[Value]",
            reader.Token.ToString());
    }
}
