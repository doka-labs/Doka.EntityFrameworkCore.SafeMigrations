namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies the bounded, native-cause-preserving runtime rejection contract.</summary>
public sealed class SafeMigrationExecutionExceptionTests
{
    /// <summary>Retains the original failure and the authored structural identity for every main guard.</summary>
    /// <param name="guardCode">The established native guard category.</param>
    [Theory]
    [InlineData("doka_sm_different")]
    [InlineData("doka_sm_unsupported")]
    [InlineData("doka_sm_data_blocked")]
    [InlineData("doka_sm_prerequisite_missing")]
    [InlineData("doka_sm_postcondition")]
    public void KnownGuardRetainsNativeCauseAndOperationContext(
        string guardCode
    )
    {
        // Arrange
        var operation = CreateOperation();
        var nativeFailure = new InvalidOperationException("private-native-detail");

        // Act
        var exception = new SafeMigrationExecutionException(operation, guardCode, 7, nativeFailure);

        // Assert
        Assert.Same(nativeFailure, exception.InnerException);
        Assert.Equal(guardCode, exception.GuardCode);
        Assert.Equal(7, exception.OperationOrdinal);
        Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, exception.Policy);
        Assert.Equal("ExampleAnswers", exception.Identity.Table);
        Assert.Equal("Id", exception.Identity.ObjectName);
        Assert.StartsWith(guardCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains("ExampleAnswers", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-native-detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-comment", exception.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(exception.ReasonCode));
    }

    /// <summary>Does not fabricate a stream position when a command has no known ordinal.</summary>
    [Fact]
    public void UnknownOrdinalRemainsUnknown()
    {
        // Arrange
        var operation = CreateOperation();
        var nativeFailure = new InvalidOperationException("native rejection");

        // Act
        var exception = new SafeMigrationExecutionException(operation, "doka_sm_data_blocked", null, nativeFailure);

        // Assert
        Assert.Null(exception.OperationOrdinal);
        Assert.DoesNotContain("operation -1", exception.Message, StringComparison.Ordinal);
        Assert.Same(nativeFailure, exception.InnerException);
    }

    /// <summary>Rejects arbitrary database failures rather than classifying them as owned guards.</summary>
    /// <param name="guardCode">A foreign, malformed or differently cased category.</param>
    [Theory]
    [InlineData("1062")]
    [InlineData("duplicate_application_key")]
    [InlineData("DOKA_SM_DATA_BLOCKED")]
    [InlineData("doka_sm_data_blocked_suffix")]
    public void ForeignGuardIdentityIsRejected(
        string guardCode
    )
    {
        // Arrange
        var operation = CreateOperation();
        var nativeFailure = new InvalidOperationException("native rejection");

        // Act
        var exception = Record.Exception(() =>
            new SafeMigrationExecutionException(operation, guardCode, 0, nativeFailure));

        // Assert
        Assert.Equal("guardCode",
            Assert.IsType<ArgumentException>(exception)
                .ParamName);
    }

    /// <summary>Rejects an impossible negative operation ordinal.</summary>
    [Fact]
    public void NegativeOrdinalIsRejected()
    {
        // Arrange
        var operation = CreateOperation();
        var nativeFailure = new InvalidOperationException("native rejection");

        // Act
        var exception = Record.Exception(() =>
            new SafeMigrationExecutionException(operation, "doka_sm_data_blocked", -1, nativeFailure));

        // Assert
        Assert.Equal("operationOrdinal",
            Assert.IsType<ArgumentOutOfRangeException>(exception)
                .ParamName);
    }

    /// <summary>Requires the native failure rather than manufacturing an original cause.</summary>
    [Fact]
    public void MissingNativeFailureIsRejected()
    {
        // Arrange
        var operation = CreateOperation();

        // Act
        var exception = Record.Exception(() =>
            new SafeMigrationExecutionException(operation, "doka_sm_data_blocked", 0, null!));

        // Assert
        Assert.Equal("innerException",
            Assert.IsType<ArgumentNullException>(exception)
                .ParamName);
    }

    /// <summary>The payload-free command path preserves the public constructor's diagnostic contract.</summary>
    /// <param name="guardCode">The unchanged owned runtime category.</param>
    [Theory]
    [InlineData("doka_sm_different")]
    [InlineData("doka_sm_unsupported")]
    [InlineData("doka_sm_data_blocked")]
    [InlineData("doka_sm_prerequisite_missing")]
    [InlineData("doka_sm_postcondition")]
    public void CapturedContextPreservesPublicDiagnosticContract(
        string guardCode
    )
    {
        // Arrange
        var operation = CreateOperation();
        var context = SafeMigrationRuntimeDiagnosticContext.Create(operation);
        var nativeFailure = new InvalidOperationException("private-native-detail");
        var expected = new SafeMigrationExecutionException(operation, guardCode, 9, nativeFailure);

        // Act
        var actual = new SafeMigrationExecutionException(context, guardCode, 9, nativeFailure);

        // Assert
        Assert.Same(context.Identity, actual.Identity);
        Assert.Same(nativeFailure, actual.InnerException);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.ReasonCode, actual.ReasonCode);
        Assert.Equal(expected.Policy, actual.Policy);
        Assert.Equal(expected.OperationOrdinal, actual.OperationOrdinal);
    }

    /// <summary>A default captured context cannot manufacture a valid operation identity.</summary>
    [Fact]
    public void DefaultCapturedContextIsRejected()
    {
        // Arrange
        var nativeFailure = new InvalidOperationException("native rejection");

        // Act
        var exception = Record.Exception(() =>
            new SafeMigrationExecutionException(
                default(SafeMigrationRuntimeDiagnosticContext),
                "doka_sm_data_blocked",
                0,
                nativeFailure));

        // Assert
        Assert.Equal("context",
            Assert.IsType<ArgumentException>(exception)
                .ParamName);
    }

    /// <summary>Creates a synthetic required column whose private comment must not enter diagnostics.</summary>
    private static SafeMigrationOperation CreateOperation()
    {
        var column = new ExpectedColumnDefinition(
            "Id",
            typeof(Guid),
            isNullable: false,
            storeType: "char(36)",
            maxLength: 36,
            isFixedLength: true,
            comment: "private-comment");

        return new SafeMigrationOperation(
            new EnsureColumnIntent("ExampleAnswers", column),
            SafeMigrationPolicy.ThrowIfDifferent);
    }
}
