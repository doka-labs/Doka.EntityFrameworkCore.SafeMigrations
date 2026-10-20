namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Validates the exact private generated-DROP certificate without adding ordinary-row payload.</summary>
public sealed class PostgreSqlGeneratedDropEvidenceTests
{
    /// <summary>Only a physically Matching DROP consumes the private code and restores its public reason.</summary>
    [Fact]
    public void MatchingDropConsumesOnlyItsExactCompletenessCode()
    {
        // Arrange
        var intent = new DropColumnIntent("source", "items");
        var code = PostgreSqlSafeMigrationCatalogSqlBuilder.EmptyGeneratedDropSetCode;

        // Act
        var result = PostgreSqlSafeMigrationProviderAnalyzer.DecodeGeneratedDropCompleteness(
            intent, SafeMigrationObservedState.Matching, ref code);

        // Assert
        Assert.True(result);
        Assert.Equal("classified_matching", code);
    }

    /// <summary>Missing, rejected and wrong-intent rows cannot borrow a complete generated-set certificate.</summary>
    /// <param name="state">The observed native state.</param>
    /// <param name="drop">Whether this row belongs to the exact DROP intent.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing, true)]
    [InlineData(SafeMigrationObservedState.Different, true)]
    [InlineData(SafeMigrationObservedState.Unsupported, true)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing, true)]
    [InlineData(SafeMigrationObservedState.Matching, false)]
    public void WrongStateOrIntentRejectsPrivateCompletenessCode(SafeMigrationObservedState state, bool drop)
    {
        // Arrange
        SafeMigrationIntent intent = drop ? new DropColumnIntent("source", "items") : new EnsureSchemaIntent("items");
        var code = PostgreSqlSafeMigrationCatalogSqlBuilder.EmptyGeneratedDropSetCode;

        // Act
        var failure = Record.Exception(() => PostgreSqlSafeMigrationProviderAnalyzer.DecodeGeneratedDropCompleteness(
            intent, state, ref code));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(PostgreSqlSafeMigrationCatalogSqlBuilder.EmptyGeneratedDropSetCode, code);
    }

    /// <summary>No complete-set proof is inferred from an ordinary state code or an almost matching token.</summary>
    /// <param name="code">The unrelated or malformed transport code.</param>
    [Theory]
    [InlineData("classified_matching")]
    [InlineData("classified_missing")]
    [InlineData("generated_column_drop_empty_set_proof_suffix")]
    [InlineData("GENERATED_COLUMN_DROP_EMPTY_SET_PROOF")]
    public void OtherCodesRemainUnchangedWithoutCompleteness(string code)
    {
        // Arrange
        var original = code;
        var intent = new DropColumnIntent("source", "items");

        // Act
        var result = PostgreSqlSafeMigrationProviderAnalyzer.DecodeGeneratedDropCompleteness(
            intent, SafeMigrationObservedState.Matching, ref code);

        // Assert
        Assert.False(result);
        Assert.Equal(original, code);
    }
}
