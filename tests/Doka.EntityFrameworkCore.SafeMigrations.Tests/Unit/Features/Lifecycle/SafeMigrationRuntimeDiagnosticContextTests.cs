namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Guards payload-free capture, required-shape facts and rendering equivalence.</summary>
public sealed class SafeMigrationRuntimeDiagnosticContextTests
{
    private static readonly string[] s_guardCodes =
    [
        "doka_sm_different", "doka_sm_unsupported", "doka_sm_data_blocked", "doka_sm_prerequisite_missing",
        "doka_sm_postcondition", "doka_sm_postcondition_failed", "doka_sm_missing", "doka_sm_script_unsupported",
        "doka_sm_identity_scope_in_use", "doka_sm_identity_acquisition_unproven",
    ];

    /// <summary>Captured metadata produces the same bounded text for every closed family and guard state.</summary>
    /// <param name="intent">The immutable synthetic operation intent.</param>
    /// <param name="expectedTable">The expected owner supplied by the identity coverage fixture.</param>
    [Theory]
    [MemberData(nameof(SafeMigrationOperationIdentityTests.AllIntents),
        MemberType = typeof(SafeMigrationOperationIdentityTests))]
    public void CapturedAndOperationBasedRenderingAreEquivalent(SafeMigrationIntent intent, string? expectedTable)
    {
        // Arrange
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var context = SafeMigrationRuntimeDiagnosticContext.Create(operation);

        // Act
        var actual = s_guardCodes.Select(code =>
                SafeMigrationDiagnosticMessages.FormatCapturedRuntime(context, code, 5))
            .ToArray();

        var expected = s_guardCodes.Select(code => SafeMigrationDiagnosticMessages.FormatRuntime(operation, code, 5))
            .ToArray();

        // Assert
        Assert.Equal(expectedTable, context.Identity.Table);
        Assert.Equal(operation.Policy, context.Policy);
        Assert.Equal(expected, actual);
    }

    /// <summary>Capture distinguishes absent replacement from explicit default without retaining its value.</summary>
    /// <param name="hasDefault">Whether the definition declares an explicit replacement.</param>
    /// <param name="isNullable">Whether the requested column accepts NULL.</param>
    /// <param name="expectedUnproven">The required-shape fact expected after capture.</param>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void CaptureRetainsOnlyTheRequiredReplacementFact(bool hasDefault, bool isNullable, bool expectedUnproven)
    {
        // Arrange
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("Items",
            new ExpectedColumnDefinition("IsActive", typeof(bool), isNullable,
                defaultValue: hasDefault ? SafeMigrationDefaultValue.Literal(false) : null)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var context = SafeMigrationRuntimeDiagnosticContext.Create(operation);
        var explanation = SafeMigrationDiagnosticMessages.GetRuntimeExplanation(context, "doka_sm_data_blocked");

        // Assert
        Assert.Equal(expectedUnproven, context.RequiredColumnReplacementUnproven);
        Assert.Equal(expectedUnproven ? "required_column_replacement_unproven" : "data_precondition_failed",
            explanation.ReasonCode);
    }

    /// <summary>An uninitialized internal struct must not fabricate owner, policy or row facts.</summary>
    [Fact]
    public void DefaultContextIsRejected()
    {
        // Arrange
        var context = default(SafeMigrationRuntimeDiagnosticContext);

        // Act
        var exception = Assert.Throws<ArgumentException>(() =>
            SafeMigrationDiagnosticMessages.FormatCapturedRuntime(context, "doka_sm_data_blocked"));

        // Assert
        Assert.Equal("context", exception.ParamName);
        Assert.Contains("captured identity", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Unknown selected codes stay machine-readable without inventing a detailed interpretation.</summary>
    [Fact]
    public void UnknownCapturedClassifierRemainsEquivalentAndHonest()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropForeignKeyIntent("FK_Items_Parents", "Items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var context = SafeMigrationRuntimeDiagnosticContext.Create(operation);

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatCapturedRuntime(context, "doka_sm_unsupported",
            diagnosticReasonCode: "future_provider_capability");

        var expected = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_unsupported",
            diagnosticReasonCode: "future_provider_capability");

        // Assert
        Assert.Equal(expected, message);
        Assert.Contains("analysis=future_provider_capability", message, StringComparison.Ordinal);
        Assert.Contains("reasonCode=provider_contract_unsupported", message, StringComparison.Ordinal);
    }

}
