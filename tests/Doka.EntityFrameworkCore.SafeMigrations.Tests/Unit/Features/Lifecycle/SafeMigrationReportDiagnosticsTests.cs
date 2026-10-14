namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies readable report identities without changing analysis or selection semantics.</summary>
public sealed class SafeMigrationReportDiagnosticsTests
{
    /// <summary>Unqualified columns remain unqualified while their different owners are explicit.</summary>
    [Fact]
    public void SameColumnNameOnDifferentOwnersRemainsUnambiguous()
    {
        // Arrange
        var first = Assessment(new EnsureColumnIntent("first_owner", Column("Id")), 0);
        var second = Assessment(new EnsureColumnIntent("second_owner", Column("Id")), 1);
        var report = Report([first, second]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var text = SafeMigrationReportText.Format(report);
        var assessments = document.RootElement.GetProperty("assessments");

        // Assert
        Assert.Equal("Id", assessments[0].GetProperty("objectName").GetString());
        Assert.Equal("Id", assessments[1].GetProperty("objectName").GetString());
        Assert.Equal("first_owner", assessments[0].GetProperty("table").GetString());
        Assert.Equal("second_owner", assessments[1].GetProperty("table").GetString());
        Assert.Contains("first_owner", text, StringComparison.Ordinal);
        Assert.Contains("second_owner", text, StringComparison.Ordinal);
        Assert.Contains(first.Explanation.Reason, text, StringComparison.Ordinal);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
    }

    /// <summary>Composite foreign-key endpoints preserve their direction and authored qualifier.</summary>
    [Fact]
    public void CompositeForeignKeyExposesDependentAndPrincipalEndpoints()
    {
        // Arrange
        var intent = new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "fk_child_parent", "children", ["Tenant", "OwnerId"], "parents", ["Tenant", "Id"],
            schema: "child_schema", principalSchema: "parent_schema"));

        var report = Report([Assessment(intent, 0, SafeMigrationObservedState.PrerequisiteMissing)]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var text = SafeMigrationReportText.Format(report);
        var assessment = document.RootElement.GetProperty("assessments")[0];

        // Assert
        Assert.Equal("child_schema", assessment.GetProperty("schema").GetString());
        Assert.Equal("children", assessment.GetProperty("table").GetString());
        Assert.Equal("parent_schema", assessment.GetProperty("principalSchema").GetString());
        Assert.Equal("parents", assessment.GetProperty("principalTable").GetString());
        Assert.Equal(["Tenant", "OwnerId"], ReadColumns(assessment, "columns"));
        Assert.Equal(["Tenant", "Id"], ReadColumns(assessment, "principalColumns"));
        Assert.Contains("children", text, StringComparison.Ordinal);
        Assert.Contains("parents", text, StringComparison.Ordinal);
        Assert.DoesNotContain("does not exist", assessment.GetProperty("reason").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>Rename details describe source and target without changing the original object name.</summary>
    [Fact]
    public void RenameExposesBothAuthoredEndpoints()
    {
        // Arrange
        var intent = new RenameTableIntent("old_owner", "new_owner", "old_schema", "new_schema");
        var report = Report([Assessment(intent, 0)]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var text = SafeMigrationReportText.Format(report);
        var assessment = document.RootElement.GetProperty("assessments")[0];

        // Assert
        Assert.Equal("old_owner", assessment.GetProperty("objectName").GetString());
        Assert.Equal("old_schema", assessment.GetProperty("schema").GetString());
        Assert.Equal("old_owner", assessment.GetProperty("table").GetString());
        Assert.Equal("new_schema", assessment.GetProperty("newSchema").GetString());
        Assert.Equal("new_owner", assessment.GetProperty("newTable").GetString());
        Assert.Contains("old_owner", text, StringComparison.Ordinal);
        Assert.Contains("new_owner", text, StringComparison.Ordinal);
    }

    /// <summary>The original constructors do not fabricate an owner from a dotted object name.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalConstructorsRetainUnknownIdentity(bool detailedConstructor)
    {
        // Arrange
        var assessment = detailedConstructor
            ? new SafeMigrationAssessment(0, "OriginalOperation", true, SafeMigrationOperationKind.EnsureColumn,
                "ambiguous.owner.Id", SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked,
                false, "data_blocked", "classified_data_blocked", "data_blocked",
                SafeMigrationOperationalImpact.NotApplicable, differences: null)
            : new SafeMigrationAssessment(0, "OriginalOperation", true, SafeMigrationOperationKind.EnsureColumn,
                "ambiguous.owner.Id", SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked,
                false, "data_blocked");

        var report = Report([assessment]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var value = document.RootElement.GetProperty("assessments")[0];

        // Assert
        Assert.Null(assessment.Identity);
        Assert.Null(assessment.MigrationId);
        Assert.Null(assessment.Policy);
        Assert.False(value.GetProperty("identityKnown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("table").ValueKind);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("schema").ValueKind);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("identityObjectName").ValueKind);
        Assert.Equal("ambiguous.owner.Id", value.GetProperty("objectName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(value.GetProperty("reason").GetString()));
    }

    /// <summary>A known default-schema identity is different from an uncaptured identity.</summary>
    [Fact]
    public void KnownDefaultSchemaIsNotUnknownIdentity()
    {
        // Arrange
        var report = Report([Assessment(new EnsureColumnIntent("items", Column("Id")), 0)]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var value = document.RootElement.GetProperty("assessments")[0];

        // Assert
        Assert.True(value.GetProperty("identityKnown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("schema").ValueKind);
        Assert.Equal("items", value.GetProperty("table").GetString());
        Assert.False(value.GetProperty("diagnosticContextTruncated").GetBoolean());
    }

    /// <summary>Complete and selected views share the same assessment payload and fingerprints.</summary>
    [Fact]
    public void CompleteAndBlockingViewsKeepIdenticalContext()
    {
        // Arrange
        var blocked = Assessment(new EnsureColumnIntent("blocked_owner", Column("Id")), 4);
        var matching = Assessment(new EnsureColumnIntent("matching_owner", Column("Id")), 7,
            SafeMigrationObservedState.Matching);

        var report = Report([blocked, matching]);

        // Act
        using var complete = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        using var view = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report,
            SafeMigrationReportSelection.BlockingOnly));

        // Assert
        Assert.Equal(4, complete.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(3, view.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(complete.RootElement.GetProperty("assessments")[0].GetRawText(),
            Assert.Single(view.RootElement.GetProperty("assessments").EnumerateArray()).GetRawText());
        Assert.Equal(report.ContractFingerprint,
            view.RootElement.GetProperty("sourceReport").GetProperty("contractFingerprint").GetString());
        Assert.Equal(2, report.Assessments.Count);
        Assert.Equal("Id", blocked.ObjectName);
    }

    /// <summary>Deferred validation identifies its origin but never claims execution already passed.</summary>
    [Fact]
    public void DeferredEntryHasAuthoredOwnerAndDistinctInvalidatingMigration()
    {
        // Arrange
        var intent = new EnsureIndexIntent(new ExpectedIndexDefinition("ix_items", "items",
            [new ExpectedIndexKeyDefinition(column: "Id")]));

        var assessment = new SafeMigrationAssessment(2, "SafeOperation", true, intent.Kind, intent.ObjectName,
            observedState: null, SafeMigrationAction.ValidateAtRuntime, postconditionSatisfied: null,
            "runtime_validation_required", "projected_structure_state_unknown", "runtime_validation_required",
            SafeMigrationOperationalImpact.Unknown, differences: null,
            deferredOrigin: new SafeMigrationDeferredOrigin(
                "202610140001_Preparation", 1, typeof(SqlOperation).FullName!),
            identity: SafeMigrationOperationIdentity.Create(intent), migrationId: "202610140002_Target",
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        var report = Report([assessment], SafeMigrationReportStatus.RuntimeValidationRequired);

        // Act
        var nonMatchingText = SafeMigrationReportText.Format(report, SafeMigrationReportSelection.NonMatching);
        var blockingText = SafeMigrationReportText.Format(report);
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var value = document.RootElement.GetProperty("assessments")[0];

        // Assert
        Assert.Equal("202610140002_Target", value.GetProperty("migrationId").GetString());
        Assert.Equal("202610140001_Preparation",
            value.GetProperty("deferredOrigin").GetProperty("migrationId").GetString());
        Assert.Contains("items", nonMatchingText, StringComparison.Ordinal);
        Assert.Contains("202610140001_Preparation", nonMatchingText, StringComparison.Ordinal);
        Assert.Contains("runtime-validation-required=1", blockingText, StringComparison.Ordinal);
        Assert.DoesNotContain("ix_items", blockingText, StringComparison.Ordinal);
        Assert.Null(assessment.PostconditionSatisfied);
    }

    /// <summary>Diagnostic identity fields are bounded, Unicode-preserving and explicitly truncated.</summary>
    [Fact]
    public void HostileLargeContextIsBoundedWithoutChangingOriginalMachineIdentity()
    {
        // Arrange
        var objectName = new string('x', 1500);
        var table = "owner\n\u001b" + new string('\u20ac', 1500);
        var identity = SafeMigrationOperationIdentity.CreateKnown(SafeMigrationOperationKind.EnsureIndex,
            "schema", table, objectName,
            Array.AsReadOnly(Enumerable.Range(0, 20).Select(index => $"c_{index}").ToArray()));

        var assessment = new SafeMigrationAssessment(0, "SafeOperation", true, SafeMigrationOperationKind.EnsureIndex,
            objectName, SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked, false,
            "data_blocked", "classified_data_blocked", "data_blocked", SafeMigrationOperationalImpact.NotApplicable,
            differences: null, deferredOrigin: null, identity: identity);

        var report = Report([assessment]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var text = SafeMigrationReportText.Format(report);
        var value = document.RootElement.GetProperty("assessments")[0];

        // Assert
        Assert.Equal(objectName, value.GetProperty("objectName").GetString());
        Assert.Equal(1024, value.GetProperty("identityObjectName").GetString()!.Length);
        Assert.True(value.GetProperty("diagnosticContextTruncated").GetBoolean());
        Assert.Equal(1024, value.GetProperty("table").GetString()!.Length);
        Assert.Contains('\u20ac', value.GetProperty("table").GetString()!);
        Assert.EndsWith("...", value.GetProperty("table").GetString()!, StringComparison.Ordinal);
        Assert.Equal(16, value.GetProperty("columns").GetArrayLength());
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain(table, text, StringComparison.Ordinal);
        Assert.Equal(table, assessment.Identity!.Table);
    }

    /// <summary>Definition payloads never appear merely because an identity is now captured.</summary>
    [Fact]
    public void DefaultAndCommentPayloadsAreNotDiagnosticContext()
    {
        // Arrange
        const string payload = "private-row-or-default-payload";
        var intent = new EnsureColumnIntent("items", new ExpectedColumnDefinition("Secret", typeof(string), true,
            "varchar(64)", comment: payload, defaultValue: SafeMigrationDefaultValue.Literal(payload)));

        var report = Report([Assessment(intent, 0)]);

        // Act
        var json = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var text = SafeMigrationReportText.Format(report);

        // Assert
        Assert.DoesNotContain(payload, json, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, text, StringComparison.Ordinal);
        Assert.Contains("items", json, StringComparison.Ordinal);
        Assert.Contains("Secret", json, StringComparison.Ordinal);
    }

    /// <summary>Large selections disclose their omissions rather than constructing an unbounded log.</summary>
    [Fact]
    public void LargeSummaryHasExplicitOmissionsAndBoundedOutput()
    {
        // Arrange
        var assessments = Enumerable.Range(0, 10_000)
            .Select(index => Assessment(new EnsureColumnIntent("items", Column($"column_{index}")), index)).ToArray();

        var report = Report(assessments);

        // Act
        var text = SafeMigrationReportText.Format(report);

        // Assert
        Assert.InRange(text.Length, 1, 50_000);
        Assert.Contains("blockers=10000", text, StringComparison.Ordinal);
        Assert.Contains("Omitted selected assessment details: 9980", text, StringComparison.Ordinal);
        Assert.Contains("column_19", text, StringComparison.Ordinal);
        Assert.DoesNotContain("column_20", text, StringComparison.Ordinal);
        Assert.Equal(10_000, report.Assessments.Count);
    }

    /// <summary>The streaming and convenience text surfaces emit exactly the same bounded content.</summary>
    [Fact]
    public void StreamingTextMatchesFormatAndLeavesWriterOpen()
    {
        // Arrange
        var report = Report([Assessment(new EnsureColumnIntent("items", Column("Id")), 0)]);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);

        // Act
        SafeMigrationReportText.Write(writer, report);
        var streamed = writer.ToString();
        var formatted = SafeMigrationReportText.Format(report);
        writer.Write("still-open");

        // Assert
        Assert.Equal(formatted, streamed);
        Assert.EndsWith("still-open", writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Unknown selections fail before partial output is written.</summary>
    [Fact]
    public void UndefinedSelectionDoesNotWritePartialOutput()
    {
        // Arrange
        var report = Report([Assessment(new EnsureColumnIntent("items", Column("Id")), 0)]);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SafeMigrationReportText.Write(writer, report, (SafeMigrationReportSelection)999));

        // Assert
        Assert.Equal("selection", exception.ParamName);
        Assert.Equal(string.Empty, writer.ToString());
    }

    /// <summary>Contradictory blocker selections fail closed on the text surface too.</summary>
    [Fact]
    public void BlockedReportWithoutBlockerDoesNotProduceMisleadingSummary()
    {
        // Arrange
        var report = Report([Assessment(new EnsureColumnIntent("items", Column("Id")), 0,
            SafeMigrationObservedState.Matching)]);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => SafeMigrationReportText.Write(writer, report));

        // Assert
        Assert.Contains("no assessment selected as blocking", exception.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, writer.ToString());
    }

    /// <summary>Creates a synthetic definition without row or SQL payloads.</summary>
    private static ExpectedColumnDefinition Column(string name) => new(name, typeof(int), false, "int");

    /// <summary>Captures a known safe identity using the same constructor used by the runner.</summary>
    private static SafeMigrationAssessment Assessment(
        SafeMigrationIntent intent,
        int ordinal,
        SafeMigrationObservedState state = SafeMigrationObservedState.DataBlocked
    )
    {
        var action = state switch
        {
            SafeMigrationObservedState.Matching => SafeMigrationAction.NoOp,
            SafeMigrationObservedState.PrerequisiteMissing => SafeMigrationAction.RejectPrerequisiteMissing,
            _ => SafeMigrationAction.RejectDataBlocked,
        };

        var code = state == SafeMigrationObservedState.Matching ? "classified_matching" : "classified_data_blocked";

        return new SafeMigrationAssessment(ordinal, typeof(SafeMigrationOperation).FullName!, true, intent.Kind,
            intent.ObjectName, state, action, state == SafeMigrationObservedState.Matching, code, code, code,
            SafeMigrationOperationalImpact.NotApplicable, differences: null, deferredOrigin: null,
            identity: SafeMigrationOperationIdentity.Create(intent), migrationId: "202610140001_Diagnostics",
            policy: SafeMigrationPolicy.ThrowIfDifferent,
            explanation: SafeMigrationDiagnosticMessages.GetExplanation(intent, state, code, code));
    }

    /// <summary>Builds a report whose target migration is distinct from its assessment migration.</summary>
    private static SafeMigrationRunReport Report(
        IReadOnlyList<SafeMigrationAssessment> assessments,
        SafeMigrationReportStatus status = SafeMigrationReportStatus.Blocked
    ) => new(SafeMigrationReportMode.Preflight, status, DateTimeOffset.UnixEpoch, "instance-test",
        new SafeMigrationProviderEnvironment("doka_mysql", "mariadb", "11.8.9"), "202610140009_Target",
        $"safe-relational-model:v1:doka_mysql:sha256:{new string('a', 64)}", new string('b', 64), assessments);

    /// <summary>Reads one endpoint column list while preserving order.</summary>
    private static IEnumerable<string?> ReadColumns(JsonElement assessment, string propertyName) =>
        assessment.GetProperty(propertyName).EnumerateArray().Select(static column => column.GetString());
}
