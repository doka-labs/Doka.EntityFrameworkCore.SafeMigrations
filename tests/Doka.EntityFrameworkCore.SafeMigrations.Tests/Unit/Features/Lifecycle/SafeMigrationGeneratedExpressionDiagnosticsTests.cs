namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Guards actionable, evidence-limited explanations for generated-expression refusals.</summary>
public sealed class SafeMigrationGeneratedExpressionDiagnosticsTests
{
    /// <summary>Names an occupied ordered CHECK target without inventing a data or type violation.</summary>
    /// <param name="policy">The explicit policy, neither of which authorizes replacing this occupied target.</param>
    [Theory]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe)]
    public void OccupiedGeneratedCaseTargetHasSpecificExplanationAndRecovery(SafeMigrationPolicy policy)
    {
        // Arrange
        const string code = "projected_generated_case_target_occupied";
        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            new ExpectedCheckConstraintDefinition("ck_case", "case_items", "private_expression_payload")), policy);

        var analysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, code);

        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind,
            analysis.ObservedState, policy, analysis.RepairCapability);

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            operation.Intent, analysis.ObservedState, code, decision.Code);

        var message = SafeMigrationDiagnosticMessages.FormatRuntimeFromAnalysis(operation, analysis, decision, 7);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, decision.Action);
        Assert.Equal(code, explanation.ReasonCode);
        Assert.Contains("accepted earlier operation", explanation.Reason, StringComparison.Ordinal);
        Assert.Contains("CHECK target name", explanation.Reason, StringComparison.Ordinal);
        Assert.Contains("constraint lifetime", explanation.NextStep, StringComparison.Ordinal);
        Assert.Contains("does not overwrite", explanation.NextStep, StringComparison.Ordinal);
        Assert.Contains("reasonCode=" + code, message, StringComparison.Ordinal);
        Assert.DoesNotContain("private_expression_payload", message, StringComparison.Ordinal);
        Assert.DoesNotContain("NULL", explanation.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("incompatible", explanation.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("CASCADE", explanation.NextStep, StringComparison.Ordinal);
    }

    /// <summary>Target occupancy is not inferred from an accepted operation's name or expression family.</summary>
    /// <param name="state">The actual nonconflicting classification.</param>
    /// <param name="decisionCode">The corresponding accepted planner result.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing, "missing_apply")]
    [InlineData(SafeMigrationObservedState.Matching, "matching_noop")]
    public void NonconflictingGeneratedCaseTargetDoesNotAcquireOccupancyExplanation(
        SafeMigrationObservedState state, string decisionCode)
    {
        // Arrange
        const string code = "classified_generated_case_binding";

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            SafeMigrationOperationKind.EnsureCheckConstraint, state, code, decisionCode);

        // Assert
        Assert.Equal(decisionCode, explanation.ReasonCode);
        Assert.DoesNotContain("occupied", explanation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overwrite", explanation.NextStep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unknown lookalikes do not establish target occupancy or disclose their private suffix.</summary>
    [Fact]
    public void UnknownOccupiedGeneratedCaseCodeKeepsGenericDifferentExplanation()
    {
        // Arrange
        const string code = "projected_generated_case_target_occupied_private_payload";

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            SafeMigrationOperationKind.EnsureCheckConstraint, SafeMigrationObservedState.Different,
            code, "different_no_safe_repair");

        // Assert
        Assert.Equal("definition_change_not_authorized", explanation.ReasonCode);
        Assert.DoesNotContain("occupied", explanation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private_payload", explanation.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("private_payload", explanation.NextStep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Preserves the actual capability identity and explains the missing proof without row-data claims.
    /// </summary>
    /// <param name="code">The provider-established generated-expression capability refusal.</param>
    /// <param name="reasonFragment">The expected specific proof boundary.</param>
    [Theory]
    [InlineData("opaque_sql_expression", "structural")]
    [InlineData("computed_expression_unproven", "deterministic")]
    [InlineData("generated_case_operand_type", "types")]
    [InlineData("computed_column_native_type_unproven", "types")]
    [InlineData("generated_column_nullability", "non-NULL")]
    [InlineData("computed_column_nullability_unproven", "non-NULL")]
    [InlineData("computed_column_operand_unproven", "source-column")]
    [InlineData("generated_column_dependency_missing", "source-column")]
    [InlineData("generated_column_dependency_qualification", "source-column")]
    [InlineData("projected_generated_column_dependency_unproven", "source-column")]
    [InlineData("projected_generated_case_binding_unproven", "source-column")]
    [InlineData("computed_column_storage_unproven", "storage")]
    [InlineData("structured_convert_dialect", "dialect")]
    [InlineData("captured_expression_dialect_unverified", "quoting")]
    [InlineData("captured_sql_identifier", "identifier")]
    [InlineData("captured_sql_type_binding", "cast")]
    [InlineData("captured_expression_identifier_keyword_unverified", "identifier")]
    [InlineData("captured_expression_cast_target_unverified", "cast")]
    [InlineData("captured_sql_function_binding", "function")]
    [InlineData("captured_sql_operator_binding", "operator")]
    [InlineData("generated_column_nondeterministic_expression", "deterministic")]
    [InlineData("generated_column_function_unverified", "function")]
    [InlineData("generated_column_identity_dependency", "AUTO_INCREMENT")]
    [InlineData("generated_column_rename_dependency", "dependency")]
    [InlineData("generated_column_rename_unproven", "dependency")]
    [InlineData("projected_check_source_dependency", "CHECK")]
    [InlineData("projected_check_source_dependency_unproven", "CHECK")]
    [InlineData("captured_expression_literal_catalog_unverifiable", "literal")]
    [InlineData("generated_expression_assignment", "literal")]
    [InlineData("computed_column_operand_identity_unproven", "identities")]
    public void EstablishedGeneratedCodeHasSpecificExplanation(string code, string reasonFragment)
    {
        // Arrange
        const SafeMigrationObservedState state = SafeMigrationObservedState.Unsupported;

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            SafeMigrationOperationKind.EnsureColumn, state, code, "unsupported");

        // Assert
        Assert.Equal(code, explanation.ReasonCode);
        Assert.Contains(reasonFragment, explanation.Reason, StringComparison.Ordinal);
        Assert.NotEmpty(explanation.NextStep);
        Assert.DoesNotContain("found NULL", explanation.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate", explanation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A similarly named unknown code must not acquire a fabricated specific diagnosis.</summary>
    [Fact]
    public void UnknownGeneratedCodeKeepsGenericUnsupportedExplanation()
    {
        // Arrange
        const string unknownCode = "computed_column_native_type_unproven_private_payload";

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            SafeMigrationOperationKind.EnsureColumn, SafeMigrationObservedState.Unsupported,
            unknownCode, "unsupported");

        // Assert
        Assert.Equal("provider_contract_unsupported", explanation.ReasonCode);
        Assert.DoesNotContain("private_payload", explanation.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("private_payload", explanation.NextStep, StringComparison.Ordinal);
    }

    /// <summary>Distinguishes a known retained dependent from an unproven generated dependency set.</summary>
    /// <param name="code">The established removal classification.</param>
    /// <param name="reasonFragment">The precise retained or unproven evidence.</param>
    [Theory]
    [InlineData("generated_column_drop_dependency", "surviving generated column depends")]
    [InlineData("generated_column_drop_unproven", "not certified")]
    public void GeneratedDropExplanationPreservesEstablishedEvidence(string code, string reasonFragment)
    {
        // Arrange
        const SafeMigrationObservedState state = SafeMigrationObservedState.Unsupported;

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            SafeMigrationOperationKind.DropColumn, state, code, "unsupported");

        // Assert
        Assert.Equal(code, explanation.ReasonCode);
        Assert.Contains(reasonFragment, explanation.Reason, StringComparison.Ordinal);
        Assert.NotEmpty(explanation.NextStep);
        Assert.DoesNotContain("rows", explanation.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("CASCADE", explanation.NextStep, StringComparison.Ordinal);
    }

    /// <summary>Names aggregate DROP and rebuild evidence without inventing a particular blocker or row.</summary>
    /// <param name="code">The public native or projected refusal code.</param>
    /// <param name="state">The actual refusal family.</param>
    /// <param name="reasonFragment">The specific evidence retained by the classification.</param>
    [Theory]
    [InlineData("column_drop_dependency", SafeMigrationObservedState.Different, "native dependency")]
    [InlineData("generated_column_drop_other_dependency", SafeMigrationObservedState.Different, "native dependency")]
    [InlineData("projected_generated_column_drop_dependency", SafeMigrationObservedState.Different,
        "surviving generated column depends")]
    [InlineData("table_rebuild_foreign_key_violation", SafeMigrationObservedState.DataBlocked, "foreign-key rows")]
    public void SourceRemovalReasonPreservesItsEstablishedEvidence(
        string code, SafeMigrationObservedState state, string reasonFragment)
    {
        // Arrange
        var decision = state == SafeMigrationObservedState.DataBlocked ? "data_blocked" : "different_no_safe_repair";

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            SafeMigrationOperationKind.DropColumn, state, code, decision);

        // Assert
        Assert.Equal(code, explanation.ReasonCode);
        Assert.Contains(reasonFragment, explanation.Reason, StringComparison.Ordinal);
        Assert.NotEmpty(explanation.NextStep);
        Assert.DoesNotContain("duplicate", explanation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CASCADE", explanation.NextStep, StringComparison.Ordinal);
    }

    /// <summary>A retained CHECK explanation applies to removal as well as rename without promising a bypass.</summary>
    /// <param name="code">The retained or unproven CHECK dependency reason.</param>
    [Theory]
    [InlineData("projected_check_source_dependency")]
    [InlineData("projected_check_source_dependency_unproven")]
    public void CheckSourceExplanationDoesNotAssumeRename(string code)
    {
        // Arrange
        const SafeMigrationObservedState state = SafeMigrationObservedState.Different;

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            SafeMigrationOperationKind.DropColumn, state, code, "different_no_safe_repair");

        // Assert
        Assert.Equal(code, explanation.ReasonCode);
        Assert.Contains("CHECK", explanation.Reason, StringComparison.Ordinal);
        Assert.Contains("source-column change", explanation.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("rename", explanation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("renaming", explanation.NextStep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Similarly named private or unknown codes cannot inherit an invented specific native diagnosis.
    /// </summary>
    /// <param name="code">The unknown exact code, deliberately resembling an established family.</param>
    [Theory]
    [InlineData("column_drop_dependency_private_payload")]
    [InlineData("generated_column_drop_other_dependency_private_payload")]
    [InlineData("table_rebuild_foreign_key_violation_private_payload")]
    public void UnknownSourceRemovalCodeKeepsGenericExplanation(string code)
    {
        // Arrange
        const SafeMigrationObservedState state = SafeMigrationObservedState.Unsupported;

        // Act
        var explanation = SafeMigrationDiagnosticMessages.GetExplanation(
            SafeMigrationOperationKind.DropColumn, state, code, "unsupported");

        // Assert
        Assert.Equal("provider_contract_unsupported", explanation.ReasonCode);
        Assert.DoesNotContain("private_payload", explanation.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("private_payload", explanation.NextStep, StringComparison.Ordinal);
    }
}
