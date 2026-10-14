namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Protects whole display units through identity, message and provider-budget truncation.</summary>
public sealed class SafeMigrationDiagnosticTruncationTests
{
    /// <summary>Every proposed prefix ends on a complete escaped character, including literal backslash runs.</summary>
    /// <param name="display">The already escaped ASCII text.</param>
    /// <param name="proposedEnd">The proposed exclusive prefix boundary.</param>
    /// <param name="expectedEnd">The complete display-unit boundary.</param>
    [Theory]
    [InlineData("\\u20ACtail", 0, 0)]
    [InlineData("\\u20ACtail", 1, 0)]
    [InlineData("\\u20ACtail", 2, 0)]
    [InlineData("\\u20ACtail", 3, 0)]
    [InlineData("\\u20ACtail", 4, 0)]
    [InlineData("\\u20ACtail", 5, 0)]
    [InlineData("\\u20ACtail", 6, 6)]
    [InlineData("\\u20ACtail", 7, 7)]
    [InlineData("\\\\u20ACtail", 1, 0)]
    [InlineData("\\\\u20ACtail", 2, 2)]
    [InlineData("\\\\u20ACtail", 3, 3)]
    [InlineData("\\\\u20ACtail", 7, 7)]
    [InlineData("\\\\\\u20ACtail", 3, 2)]
    [InlineData("\\\\\\u20ACtail", 4, 2)]
    [InlineData("\\\\\\u20ACtail", 7, 2)]
    [InlineData("\\\\\\u20ACtail", 8, 8)]
    [InlineData("plain text", 5, 5)]
    public void DisplayPrefixPreservesCompleteUnits(string display, int proposedEnd, int expectedEnd)
    {
        // Arrange
        const string outside = "retained\\";
        var builder = new StringBuilder(outside + display);

        // Act
        var end = SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(
            builder, outside.Length, outside.Length + proposedEnd);

        // Assert
        Assert.Equal(outside.Length + expectedEnd, end);
        Assert.Equal(outside + display, builder.ToString());
        Assert.InRange(end, outside.Length, outside.Length + proposedEnd);
    }

    /// <summary>The native budget can cross a builder chunk without splitting a display escape.</summary>
    [Fact]
    public void DisplayBoundaryDoesNotDependOnBuilderChunks()
    {
        // Arrange
        var builder = new StringBuilder(1);
        builder.Append(new string('x', 1020));
        builder.Append('\\');
        builder.Append("u20AC");
        var chunkCount = 0;
        foreach (var chunk in builder.GetChunks())
        {
            chunkCount += chunk.Length == 0 ? 0 : 1;
        }

        Assert.True(chunkCount > 1, "The fixture must cross a real builder chunk.");

        // Act
        var end = SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(builder, 0, 1024);

        // Assert
        Assert.Equal(1020, end);
        Assert.Equal(new string('x', 1020) + "\\u20AC", builder.ToString());
    }

    /// <summary>Invalid buffer ranges fail before inspecting characters or mutating the caller buffer.</summary>
    /// <param name="start">The invalid or incompatible first offset.</param>
    /// <param name="end">The invalid exclusive end.</param>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    [InlineData(0, 5)]
    public void InvalidDisplayRangeIsRejected(int start, int end)
    {
        // Arrange
        var builder = new StringBuilder("text");

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(builder, start, end));

        // Assert
        Assert.NotNull(exception.ParamName);
        Assert.Equal("text", builder.ToString());
    }

    /// <summary>Composite identities that cross the total cap preserve whole Unicode and backslash escapes.</summary>
    /// <param name="padding">The offset adjustment exercising each position within a Unicode escape.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void IdentityCapDoesNotSplitDisplayEscapes(int padding)
    {
        // Arrange
        var member = new string('\u20AC', 18) + "\\u20AC";
        var identity = SafeMigrationOperationIdentity.CreateKnown(SafeMigrationOperationKind.EnsureForeignKey,
            member, member, new string('n', padding) + member,
            Array.AsReadOnly(new[] { member, member, member, member }),
            member, member, Array.AsReadOnly(new[] { member, member, member, member }));

        // Act
        var display = SafeMigrationDiagnosticMessages.FormatIdentity(identity);

        // Assert
        Assert.EndsWith("...", display, StringComparison.Ordinal);
        Assert.InRange(display.Length, 1016, 1024);
        AssertCompleteDisplayEscapes(display);
    }

    /// <summary>Report-only metadata crossing the final message cap cannot leave a partial printable escape.</summary>
    /// <param name="padding">The offset adjustment at the message boundary.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AssessmentCapDoesNotSplitDisplayEscapes(int padding)
    {
        // Arrange
        var member = new string('\u20AC', 18);
        var identity = SafeMigrationOperationIdentity.CreateKnown(SafeMigrationOperationKind.EnsureForeignKey,
            member, member, member, Array.AsReadOnly(new[] { member, member, member, member }),
            member, member, Array.AsReadOnly(new[] { member, member, member, member }));

        var assessment = new SafeMigrationAssessment(1, "SafeOperation", true,
            SafeMigrationOperationKind.EnsureForeignKey, "FK", observedState: null,
            SafeMigrationAction.ValidateAtRuntime, postconditionSatisfied: null, "runtime_validation_required",
            new string('a', padding) + new string('\u20AC', 30), new string('\u20AC', 30),
            SafeMigrationOperationalImpact.NotApplicable,
            [new SafeMigrationFacetDifference("column_store_type", new string('x', 96), new string('y', 96)),
                new SafeMigrationFacetDifference("column_collation", new string('\\', 96), new string('\\', 96))],
            deferredOrigin: new SafeMigrationDeferredOrigin(member, 123, member), identity: identity,
            migrationId: member, policy: SafeMigrationPolicy.ThrowIfDifferent,
            explanation: SafeMigrationDiagnosticMessages.GetExplanation(SafeMigrationOperationKind.EnsureForeignKey,
                state: null, "projected_structure_state_unknown", "runtime_validation_required"));

        // Act
        var display = SafeMigrationDiagnosticMessages.FormatAssessment(assessment);

        // Assert
        Assert.EndsWith("...", display, StringComparison.Ordinal);
        Assert.InRange(display.Length, 2040, 2048);
        AssertCompleteDisplayEscapes(display);
    }

    /// <summary>The postflight and native exception paths identify the same closed owned guard category.</summary>
    [Fact]
    public void PostconditionUsesCanonicalOwnedGuardIdentity()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("Items", null),
            SafeMigrationPolicy.ThrowIfDifferent);

        var analysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, false, "classified_matching");

        var native = new InvalidOperationException("native cause");

        // Act
        var postflight = SafeMigrationDiagnosticMessages.FormatPostcondition(operation, analysis, 3);
        var execution = new SafeMigrationExecutionException(operation, "doka_sm_postcondition", 3, native);

        // Assert
        Assert.StartsWith(execution.GuardCode + ":", postflight, StringComparison.Ordinal);
        Assert.StartsWith(execution.GuardCode + ":", execution.Message, StringComparison.Ordinal);
        Assert.Contains("reasonCode=postcondition_failed", postflight, StringComparison.Ordinal);
        Assert.Contains("Earlier DDL may already have applied", postflight, StringComparison.Ordinal);
        Assert.Contains("no full rollback is implied", postflight, StringComparison.Ordinal);
        Assert.Same(native, execution.InnerException);
    }

    /// <summary>A satisfied postcondition must not be rendered as a failure in the unified category.</summary>
    [Fact]
    public void SatisfiedPostconditionCannotProduceFailureText()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("Items", null),
            SafeMigrationPolicy.ThrowIfDifferent);

        var analysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, true, "classified_missing");

        // Act
        var rejection = Assert.Throws<ArgumentException>(() =>
            SafeMigrationDiagnosticMessages.FormatPostcondition(operation, analysis, null));

        // Assert
        Assert.Equal("analysis", rejection.ParamName);
        Assert.Contains("unsatisfied condition", rejection.Message, StringComparison.Ordinal);
    }

    /// <summary>A human-readable alias does not broaden the closed native exception-ownership contract.</summary>
    [Fact]
    public void PostconditionDisplayAliasIsNotAnOwnedNativeGuard()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("Items", null),
            SafeMigrationPolicy.ThrowIfDifferent);

        var native = new InvalidOperationException("native cause");

        // Act
        var rejection = Assert.Throws<ArgumentException>(() =>
            new SafeMigrationExecutionException(operation, "doka_sm_postcondition_failed", null, native));

        // Assert
        Assert.Equal("guardCode", rejection.ParamName);
        Assert.Contains("recognized SafeMigrations runtime guard", rejection.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The former presentation spelling renders the canonical category without losing phase warnings.
    /// </summary>
    [Fact]
    public void PostconditionDisplayAliasRendersCanonicalCategory()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("Items", null),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_postcondition_failed");

        // Assert
        Assert.StartsWith("doka_sm_postcondition:", message, StringComparison.Ordinal);
        Assert.Contains("reasonCode=postcondition_failed", message, StringComparison.Ordinal);
        Assert.Contains("Earlier DDL may already have applied", message, StringComparison.Ordinal);
        Assert.Contains("no full rollback is implied", message, StringComparison.Ordinal);
    }

    /// <summary>Boundary selection performs no allocations, including after a garbage collection.</summary>
    [Fact]
    public void DisplayBoundarySelectionDoesNotAllocate()
    {
        // Arrange
        var builder = new StringBuilder(new string('x', 1020) + "\\u20AC");
        var checksum = SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(builder, 0, 1024);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        for (var index = 0; index < 10_000; index++)
        {
            checksum += SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(builder, 0, 1024);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(10_001 * 1020, checksum);
        Assert.Equal(0, allocated);
    }

    /// <summary>The independent test oracle rejects the exact malformed units that motivated this fix.</summary>
    /// <param name="display">The deliberately incomplete or invalid display escape.</param>
    [Theory]
    [InlineData("\\")]
    [InlineData("\\u20")]
    [InlineData("\\u20A...")]
    [InlineData("\\x")]
    public void DisplayEscapeOracleRejectsIncompleteUnits(string display)
    {
        // Arrange
        Action check = () => AssertCompleteDisplayEscapes(display);

        // Act
        var rejection = Assert.ThrowsAny<Xunit.Sdk.XunitException>(check);

        // Assert
        Assert.NotNull(rejection);
    }

    /// <summary>Asserts whole display units independently of the production truncation algorithm.</summary>
    /// <param name="display">The printable Core output under test.</param>
    private static void AssertCompleteDisplayEscapes(string display)
    {
        for (var offset = 0; offset < display.Length; offset++)
        {
            Assert.InRange(display[offset], ' ', '~');
            if (display[offset] != '\\')
            {
                continue;
            }

            Assert.True(++offset < display.Length, "A display escape cannot end after its backslash.");
            if (display[offset] == '\\')
            {
                continue;
            }

            Assert.Equal('u', display[offset]);
            for (var digit = 0; digit < 4; digit++)
            {
                Assert.True(++offset < display.Length, "A Unicode display escape needs all four hex digits.");
                Assert.True(char.IsAsciiHexDigit(display[offset]), "A display escape contains invalid hex.");
            }

        }

    }
}
