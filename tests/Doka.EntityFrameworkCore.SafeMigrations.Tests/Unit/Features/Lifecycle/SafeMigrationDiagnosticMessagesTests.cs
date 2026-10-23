namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Guards actionable reasons, bounded formatting, privacy and evidence-preserving fallbacks.</summary>
public sealed class SafeMigrationDiagnosticMessagesTests
{
    /// <summary>Explains the missing atomic widening contract without guessing that data violates a target.</summary>
    [Fact]
    public void IntegerWideningRefusalExplainsLockAndSnapshotAuthority()
    {
        // Arrange
        const string code = "integer_widening_unproven";

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(SafeMigrationOperationKind.AlterColumn,
            SafeMigrationObservedState.Unsupported, code, "unsupported");

        // Assert
        Assert.Equal(code, explanation.ReasonCode);
        Assert.Contains("lock-authority", explanation.Reason, StringComparison.Ordinal);
        Assert.Contains("READ COMMITTED", explanation.NextStep, StringComparison.Ordinal);
        Assert.Contains("no privileges or isolation settings are changed automatically", explanation.NextStep,
            StringComparison.Ordinal);
        Assert.DoesNotContain("offending", explanation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Does not accept a similar caller payload as a proven native widening diagnostic.</summary>
    [Fact]
    public void IntegerWideningLookalikeRetainsPayloadFreeFallback()
    {
        // Arrange
        const string unknown = "integer_widening_unproven_private_payload";

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(SafeMigrationOperationKind.AlterColumn,
            SafeMigrationObservedState.Unsupported, unknown, "unsupported");

        // Assert
        Assert.NotEqual(unknown, explanation.ReasonCode);
        Assert.DoesNotContain("private_payload", explanation.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("private_payload", explanation.NextStep, StringComparison.Ordinal);
    }

    /// <summary>Guard identities survive formatting without substituting a different native error category.</summary>
    /// <param name="guardCode">The supported native guard token.</param>
    [Theory]
    [InlineData("doka_sm_different")]
    [InlineData("doka_sm_unsupported")]
    [InlineData("doka_sm_data_blocked")]
    [InlineData("doka_sm_prerequisite_missing")]
    [InlineData("doka_sm_postcondition")]
    [InlineData("doka_sm_missing")]
    [InlineData("doka_sm_script_unsupported")]
    [InlineData("doka_sm_identity_scope_in_use")]
    [InlineData("doka_sm_identity_acquisition_unproven")]
    public void RuntimeFormattingPreservesGuardAndOperationContext(string guardCode)
    {
        // Arrange
        var operation = RequiredColumnOperation();

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, guardCode, operationOrdinal: 7);

        // Assert
        Assert.StartsWith(guardCode + ":", message, StringComparison.Ordinal);
        Assert.Contains("EnsureColumn", message, StringComparison.Ordinal);
        Assert.Contains("[application].[Items]", message, StringComparison.Ordinal);
        Assert.Contains("object=[IsActive]", message, StringComparison.Ordinal);
        Assert.Contains("operation 7", message, StringComparison.Ordinal);
        Assert.Contains("policy=ThrowIfDifferent", message, StringComparison.Ordinal);
        Assert.Contains("reason=", message, StringComparison.Ordinal);
        Assert.Contains("next=", message, StringComparison.Ordinal);
        Assert.InRange(message.Length, 1, 2048);
    }

    /// <summary>Describes a branch-established missing replacement without guessing from all data failures.</summary>
    [Fact]
    public void RequiredAddBranchExplainsExplicitBackfill()
    {
        // Arrange
        var operation = RequiredColumnOperation();

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_data_blocked",
            diagnosticReasonCode: "required_column_backfill_missing");

        // Assert
        Assert.Contains("reasonCode=required_column_backfill_missing", message, StringComparison.Ordinal);
        Assert.Contains("replacement value", message, StringComparison.Ordinal);
        Assert.Contains("Add nullable", message, StringComparison.Ordinal);
        Assert.Contains("explicitly backfill", message, StringComparison.Ordinal);
        Assert.DoesNotContain("NULL values", message, StringComparison.Ordinal);
    }

    /// <summary>Generic data failures must not be mislabeled as a specific orphan or missing-column defect.</summary>
    [Fact]
    public void GenericDataBlockRetainsUnavailableSpecificCause()
    {
        // Arrange
        var foreignKey = new SafeMigrationOperation(new EnsureForeignKeyIntent(
            new ExpectedForeignKeyDefinition("FK_Children_Parents", "Children", ["ParentId"], "Parents", ["Id"])),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(foreignKey, "doka_sm_data_blocked");

        // Assert
        Assert.Contains("reasonCode=data_precondition_failed", message, StringComparison.Ordinal);
        Assert.Contains("classification alone does not identify", message, StringComparison.Ordinal);
        Assert.DoesNotContain("found orphaned", message, StringComparison.Ordinal);
        Assert.Contains("principal=<unqualified>.[Parents]", message, StringComparison.Ordinal);
        Assert.Contains("principalColumns=([Id])", message, StringComparison.Ordinal);
    }

    /// <summary>Only the actual NULL-data branch can report that a NULL check found offending values.</summary>
    [Fact]
    public void ActualAnalysisAndDecisionPreserveSpecificReason()
    {
        // Arrange
        var operation = RequiredColumnOperation();
        var analysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.DataBlocked,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, "column_nullability_data_blocked");

        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind, analysis.ObservedState, operation.Policy);

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntimeFromAnalysis(operation, analysis, decision, 3);

        // Assert
        Assert.Contains("reasonCode=column_nullability_data_blocked", message, StringComparison.Ordinal);
        Assert.Contains("found NULL values", message, StringComparison.Ordinal);
        Assert.Contains("analysis=column_nullability_data_blocked", message, StringComparison.Ordinal);
        Assert.Contains("decision=data_blocked", message, StringComparison.Ordinal);
        Assert.DoesNotContain("required_column_replacement_unproven", message, StringComparison.Ordinal);
    }

    /// <summary>An accepted planner decision must not be rendered as a runtime rejection.</summary>
    [Fact]
    public void AcceptedDecisionIsNotMislabeledAsRuntimeFailure()
    {
        // Arrange
        var operation = RequiredColumnOperation();
        var analysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, postconditionSatisfied: true, "classified_matching");

        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind, analysis.ObservedState, operation.Policy);

        // Act
        var exception = Assert.Throws<ArgumentException>(() =>
            SafeMigrationDiagnosticMessages.FormatRuntimeFromAnalysis(operation, analysis, decision));

        // Assert
        Assert.Equal("decision", exception.ParamName);
        Assert.Contains("rejecting decision", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Unknown codes never echo native SQL or arbitrary caller payloads into their explanations.</summary>
    [Fact]
    public void UnknownReasonAndGuardHaveHonestPayloadFreeFallback()
    {
        // Arrange
        const string sentinel = "private-sentinel-SQL\npassword";
        var operation = RequiredColumnOperation();

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            (SafeMigrationOperationKind?)null, state: null, sentinel, sentinel);

        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, sentinel,
            diagnosticReasonCode: sentinel);

        // Assert
        Assert.Equal("diagnostic_reason_unavailable", explanation.ReasonCode);
        Assert.Contains("not established", explanation.Reason, StringComparison.Ordinal);
        Assert.StartsWith("doka_sm_unknown:", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sentinel", message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Unrecognized caller codes use the documented display fallback without claiming native ownership.
    /// </summary>
    /// <param name="guardCode">An unknown, malformed or differently cased caller-supplied guard code.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("DOKA_SM_DATA_BLOCKED")]
    [InlineData("doka_sm_postcondition_failed_suffix")]
    [InlineData("unrecognized_guard")]
    [InlineData("doka_sm_unknown")]
    public void UnknownGuardUsesDisplayFallbackWithoutGrantingOwnership(string guardCode)
    {
        // Arrange
        var operation = RequiredColumnOperation();
        var native = new InvalidOperationException("native cause");

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, guardCode);
        var ownershipRejection = Assert.Throws<ArgumentException>(() =>
            new SafeMigrationExecutionException(operation, guardCode, null, native));

        // Assert
        Assert.StartsWith("doka_sm_unknown:", message, StringComparison.Ordinal);
        Assert.Contains("reasonCode=diagnostic_reason_unavailable", message, StringComparison.Ordinal);
        Assert.Contains("object=[IsActive]", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Earlier DDL may already have applied", message, StringComparison.Ordinal);
        Assert.Equal("guardCode", ownershipRejection.ParamName);
    }

    /// <summary>
    /// The actual postcondition analysis code remains available independently of guard normalization.
    /// </summary>
    [Fact]
    public void PostconditionAnalysisCodeRetainsItsExecutionWarning()
    {
        // Arrange
        const string analysisCode = "postcondition_failed";

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationObservedState.Different, analysisCode, "different_no_safe_repair");

        // Assert
        Assert.Equal(analysisCode, explanation.ReasonCode);
        Assert.Contains("Earlier DDL may already have applied", explanation.Reason, StringComparison.Ordinal);
        Assert.Contains("no full rollback is implied", explanation.NextStep, StringComparison.Ordinal);
    }

    /// <summary>A trusted stable classifier identity survives an explicitly generic human explanation.</summary>
    [Fact]
    public void UnknownStableClassifierCodeIsRetainedWithoutInventingItsMeaning()
    {
        // Arrange
        var operation = RequiredColumnOperation();
        const string providerCode = "unsupported_fixture";

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_unsupported",
            diagnosticReasonCode: providerCode);

        // Assert
        Assert.Contains("analysis=unsupported_fixture", message, StringComparison.Ordinal);
        Assert.Contains("reasonCode=provider_contract_unsupported", message, StringComparison.Ordinal);
        Assert.DoesNotContain("The table is absent", message, StringComparison.Ordinal);
    }

    /// <summary>A default value explicitly describes unavailable evidence instead of returning null strings.</summary>
    [Fact]
    public void DefaultExplanationIsExplicitlyUnavailable()
    {
        // Arrange
        var explanation = default(SafeMigrationDiagnosticExplanation);

        // Act
        var fields = new[] { explanation.ReasonCode, explanation.Reason, explanation.NextStep };

        // Assert
        Assert.All(fields, field => Assert.False(string.IsNullOrWhiteSpace(field)));
        Assert.Equal("diagnostic_reason_unavailable", fields[0]);
    }

    /// <summary>Postcondition failures do not promise that execution was prevented or fully rolled back.</summary>
    [Fact]
    public void PostconditionMessageRetainsPartialFailureWarning()
    {
        // Arrange
        var operation = RequiredColumnOperation();

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_postcondition");

        // Assert
        Assert.Contains("Earlier DDL may already have applied", message, StringComparison.Ordinal);
        Assert.Contains("no full rollback is implied", message, StringComparison.Ordinal);
    }

    /// <summary>Postcondition phase wording cannot be overridden by a secondary classifier reason.</summary>
    /// <param name="guardCode">The native category or accepted presentation alias.</param>
    [Theory]
    [InlineData("doka_sm_postcondition")]
    [InlineData("doka_sm_postcondition_failed")]
    public void PostconditionReasonCannotEraseExecutionWarning(string guardCode)
    {
        // Arrange
        var operation = RequiredColumnOperation();
        var analysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, "classified_different");

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, guardCode,
            diagnosticReasonCode: "required_column_backfill_missing");

        var postcondition = SafeMigrationDiagnosticMessages.FormatPostcondition(operation, analysis, 2);

        // Assert
        Assert.StartsWith("doka_sm_postcondition:", message, StringComparison.Ordinal);
        Assert.Contains("reasonCode=postcondition_failed", message, StringComparison.Ordinal);
        Assert.Contains("Earlier DDL may already have applied", message, StringComparison.Ordinal);
        Assert.Contains("analysis=classified_different", postcondition, StringComparison.Ordinal);
        Assert.DoesNotContain("decision=", postcondition, StringComparison.Ordinal);
    }

    /// <summary>A deferred state explains that no final classification or execution result exists yet.</summary>
    [Fact]
    public void DeferredExplanationDoesNotClaimLiveSuccessOrFailure()
    {
        // Arrange
        const string invalidatedCode = "projected_structure_state_unknown";

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            SafeMigrationOperationKind.EnsureForeignKey, state: null, invalidatedCode, "runtime_validation_required");

        // Assert
        Assert.Equal("runtime_validation_required", explanation.ReasonCode);
        Assert.Contains("final decision has not yet been made", explanation.Reason, StringComparison.Ordinal);
        Assert.Contains("not a successful execution", explanation.NextStep, StringComparison.Ordinal);
        Assert.DoesNotContain("absent", explanation.Reason, StringComparison.Ordinal);
    }

    /// <summary>Ordinary report constructors never invent a table owner from a lone column name.</summary>
    [Fact]
    public void LegacyAssessmentHasHonestUnknownOwnerAndSafeFacetValues()
    {
        // Arrange
        var assessment = new SafeMigrationAssessment(1, typeof(SafeMigrationOperation).FullName!,
            isSafeOperation: true, SafeMigrationOperationKind.EnsureColumn, "Id", SafeMigrationObservedState.Different,
            SafeMigrationAction.RejectDifferent, postconditionSatisfied: false, "different_no_safe_repair",
            "classified_different", "different_no_safe_repair", SafeMigrationOperationalImpact.NotApplicable,
            [new SafeMigrationFacetDifference("column_max_length", "200", "10")]);

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatAssessment(assessment);

        // Assert
        Assert.Contains("operation 1", message, StringComparison.Ordinal);
        Assert.Contains("owner=<unavailable>", message, StringComparison.Ordinal);
        Assert.Contains("object=[Id]", message, StringComparison.Ordinal);
        Assert.Contains("column_max_length: expected 200, actual 10", message, StringComparison.Ordinal);
    }

    /// <summary>Printable comments and SQL defaults stay out of ordinary error text.</summary>
    [Fact]
    public void UnapprovedFacetPayloadsAreNotDisplayed()
    {
        // Arrange
        const string sentinel = "private-password-sentinel";
        var assessment = new SafeMigrationAssessment(1, typeof(SafeMigrationOperation).FullName!,
            isSafeOperation: true, SafeMigrationOperationKind.EnsureColumn, "Id", SafeMigrationObservedState.Different,
            SafeMigrationAction.RejectDifferent, postconditionSatisfied: false, "different_no_safe_repair",
            "classified_different", "different_no_safe_repair", SafeMigrationOperationalImpact.NotApplicable,
            [new SafeMigrationFacetDifference("column_default", sentinel, sentinel),
                new SafeMigrationFacetDifference("column_comment", sentinel, sentinel)]);

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatAssessment(assessment);

        // Assert
        Assert.DoesNotContain(sentinel, message, StringComparison.Ordinal);
        Assert.Contains("column_default: values omitted", message, StringComparison.Ordinal);
        Assert.Contains("column_comment: values omitted", message, StringComparison.Ordinal);
    }

    /// <summary>Known qualifiers are not replaced with a guessed live database name.</summary>
    [Fact]
    public void IdentityFormattingDisplaysCompositeRelationshipAndRenameDirection()
    {
        // Arrange
        var foreignKey = SafeMigrationOperationIdentity.Create(new EnsureForeignKeyIntent(
            new ExpectedForeignKeyDefinition("FK_Children_Parents", "Children", ["TenantId", "ParentId"],
                "Parents", ["TenantId", "Id"], "child", "parent")));

        var rename = SafeMigrationOperationIdentity.Create(new RenameColumnIntent("OldId", "Children", "NewId"));

        // Act
        var foreignLabel = SafeMigrationDiagnosticMessages.FormatIdentity(foreignKey);
        var renameLabel = SafeMigrationDiagnosticMessages.FormatIdentity(rename);

        // Assert
        Assert.Contains("owner=[child].[Children]", foreignLabel, StringComparison.Ordinal);
        Assert.Contains("columns=([TenantId],[ParentId])", foreignLabel, StringComparison.Ordinal);
        Assert.Contains("principal=[parent].[Parents]", foreignLabel, StringComparison.Ordinal);
        Assert.Contains("principalColumns=([TenantId],[Id])", foreignLabel, StringComparison.Ordinal);
        Assert.Contains("owner=<unqualified>.[Children]", renameLabel, StringComparison.Ordinal);
        Assert.Contains("object=[OldId]", renameLabel, StringComparison.Ordinal);
        Assert.Contains("targetObject=[NewId]", renameLabel, StringComparison.Ordinal);
    }

    /// <summary>Hostile identifiers cannot inject new log lines, terminal controls or unbounded text.</summary>
    [Fact]
    public void RuntimeFormattingEscapesControlsAndBoundsOversizedUnicodeIdentifiers()
    {
        // Arrange
        var identifier = "Items\r\n\u001B]\\\u20AC" + new string('x', 10_000);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent(identifier,
            new ExpectedColumnDefinition(identifier, typeof(bool), isNullable: false), schema: identifier),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_data_blocked", 7);

        // Assert
        Assert.InRange(message.Length, 1, 2048);
        Assert.All(message, character => Assert.InRange(character, ' ', '~'));
        Assert.Contains("\\u000D\\u000A\\u001B", message, StringComparison.Ordinal);
        Assert.Contains("\\u20AC", message, StringComparison.Ordinal);
        Assert.Contains("...", message, StringComparison.Ordinal);
        Assert.Contains("reason=", message, StringComparison.Ordinal);
        Assert.Equal(identifier, operation.Intent.ObjectName);
    }

    /// <summary>Literal defaults, comments and opaque expressions are never rendered from an intent.</summary>
    [Fact]
    public void RuntimeFormattingDoesNotRenderDefinitionPayloads()
    {
        // Arrange
        const string sentinel = "private-password-sentinel";
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("Items",
            new ExpectedColumnDefinition("Value", typeof(string), isNullable: false,
                comment: sentinel, defaultValue: SafeMigrationDefaultValue.Literal(sentinel))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_different");

        // Assert
        Assert.DoesNotContain(sentinel, message, StringComparison.Ordinal);
        Assert.Contains("object=[Value]", message, StringComparison.Ordinal);
    }

    /// <summary>Formatting can append to a reusable generation buffer without changing its existing content.</summary>
    [Fact]
    public void CachedIdentityAppendMatchesStandaloneFormatting()
    {
        // Arrange
        var operation = RequiredColumnOperation();
        var identity = SafeMigrationOperationIdentity.Create(operation.Intent);
        var destination = new StringBuilder("preserved-prefix");
        var expected = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_data_blocked", 2,
            "required_column_backfill_missing");

        // Act
        SafeMigrationDiagnosticMessages.AppendRuntime(destination, operation, identity, "doka_sm_data_blocked", 2,
            "required_column_backfill_missing");

        // Assert
        Assert.Equal("preserved-prefix" + expected, destination.ToString());
    }

    /// <summary>Plain ASCII labels reuse their original reference; escaped labels remain bounded.</summary>
    /// <param name="source">The synthetic identifier.</param>
    /// <param name="expected">The bounded escaped representation.</param>
    [Theory]
    [InlineData("Items", "Items")]
    [InlineData("Items\n", "Items\\u000A")]
    [InlineData("\u20AC", "\\u20AC")]
    [InlineData("\\", "\\\\")]
    public void IdentifierDisplayEscapesWithoutGuessingOrPayloadInterpretation(string source, string expected)
    {
        // Arrange
        const int maximumLength = 128;

        // Act
        var display = SafeMigrationDiagnosticMessages.BoundForDisplay(source, maximumLength);

        // Assert
        Assert.Equal(expected, display);
        Assert.InRange(display.Length, 1, maximumLength);
    }

    /// <summary>Too-small display bounds fail as an argument contract instead of emitting a partial escape.</summary>
    [Fact]
    public void InvalidDisplayBoundIsRejected()
    {
        // Arrange
        const int invalidBound = 3;

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SafeMigrationDiagnosticMessages.BoundForDisplay("Items", invalidBound));

        // Assert
        Assert.Equal("maximumLength", exception.ParamName);
    }

    /// <summary>Unavailable operation attribution is null, not an invented negative stream ordinal.</summary>
    [Fact]
    public void NegativeOperationOrdinalIsRejected()
    {
        // Arrange
        var operation = RequiredColumnOperation();

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_different", -1));

        // Assert
        Assert.Equal("operationOrdinal", exception.ParamName);
        Assert.Contains("use null", exception.Message, StringComparison.Ordinal);
    }

    private static SafeMigrationOperation RequiredColumnOperation() => new(
        new EnsureColumnIntent("Items",
            new ExpectedColumnDefinition("IsActive", typeof(bool), isNullable: false, storeType: "tinyint(1)"),
            schema: "application"),
        SafeMigrationPolicy.ThrowIfDifferent);
}
