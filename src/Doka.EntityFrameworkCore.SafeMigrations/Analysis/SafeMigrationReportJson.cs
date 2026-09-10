namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Writes the versioned SafeMigrations report JSON contract.</summary>
public static class SafeMigrationReportJson
{
    private const int CurrentReportViewSchemaVersion = 1;

    private const int MaximumInitialBufferSize = 16 * 1024 * 1024;

    private const int EstimatedAssessmentSize = 512;

    private const int EstimatedUnexpectedObjectSize = 192;

    private const int EstimatedReportEnvelopeSize = 1024;

    private const int EstimatedReportViewEnvelopeSize = 1536;

    /// <summary>Serializes a report to a compact UTF-8 JSON document.</summary>
    /// <param name="report">The report to serialize.</param>
    /// <returns>A compact UTF-8 JSON document.</returns>
    public static byte[] SerializeToUtf8Bytes(
        SafeMigrationRunReport report
    )
    {
        ArgumentNullException.ThrowIfNull(report);

        var estimatedSize = EstimatedReportEnvelopeSize
            + ((long)report.Assessments.Count * EstimatedAssessmentSize)
            + ((long)report.UnexpectedObjects.Count * EstimatedUnexpectedObjectSize);

        // WHY: Report v2 adds bounded diagnostic fields to every assessment.
        // A proportional first buffer avoids repeated full-buffer copies while
        // the cap prevents a caller-controlled count from forcing one huge
        // speculative allocation before the first JSON token is written.
        var initialBufferSize = (int)Math.Min(estimatedSize, MaximumInitialBufferSize);
        var buffer = new ArrayBufferWriter<byte>(initialBufferSize);
        using var writer = new Utf8JsonWriter(buffer);
        Write(writer, report);
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Serializes an explicitly selected, self-describing report view to a
    /// compact UTF-8 JSON document.
    /// </summary>
    /// <param name="report">The complete source report to select from.</param>
    /// <param name="selection">The entries to include in the report view.</param>
    /// <returns>A compact UTF-8 report-view JSON document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="selection" /> is not a defined selection.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A blocked source report contains no assessment selected as blocking.
    /// </exception>
    public static byte[] SerializeToUtf8Bytes(
        SafeMigrationRunReport report,
        SafeMigrationReportSelection selection
    )
    {
        ArgumentNullException.ThrowIfNull(report);

        var selectionCode = SelectionCode(selection);
        var counts = CountSelectedContent(report, selection);
        ValidateBlockingSelection(report, selection, counts.AssessmentCount);

        var estimatedSize = EstimatedReportViewEnvelopeSize
            + ((long)counts.AssessmentCount * EstimatedAssessmentSize)
            + ((long)counts.UnexpectedObjectCount * EstimatedUnexpectedObjectSize);

        // WHY: Selection is evaluated without materializing a second report or
        // filtered collection. The bounded estimate therefore scales with the
        // emitted view rather than the complete source-report cardinality.
        var initialBufferSize = (int)Math.Min(estimatedSize, MaximumInitialBufferSize);
        var buffer = new ArrayBufferWriter<byte>(initialBufferSize);
        using var writer = new Utf8JsonWriter(buffer);
        WriteReportView(writer, report, selection, selectionCode, counts);
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Writes one report to a caller-owned writer without reflection or an
    /// intermediate object graph.
    /// </summary>
    /// <param name="writer">The caller-owned UTF-8 JSON writer.</param>
    /// <param name="report">The report to serialize.</param>
    public static void Write(
        Utf8JsonWriter writer,
        SafeMigrationRunReport report
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", report.SchemaVersion);
        writer.WriteString("mode", ModeCode(report.Mode));
        writer.WriteString("status", StatusCode(report.Status));
        writer.WriteString("generatedAtUtc", report.GeneratedAtUtc);
        writer.WriteString("instanceId", report.InstanceId);
        WriteEnvironment(writer, report.Environment);
        WriteNullableString(writer, "targetMigrationId", report.TargetMigrationId);
        writer.WriteString("modelFingerprint", report.ModelFingerprint);
        writer.WriteString("contractFingerprint", report.ContractFingerprint);
        writer.WriteStartArray("assessments");

        foreach (var assessment in report.Assessments)
        {
            WriteAssessment(writer, assessment);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("unexpectedObjects");

        foreach (var unexpectedObject in report.UnexpectedObjects)
        {
            WriteUnexpectedObject(writer, unexpectedObject);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an explicitly selected, self-describing report view to a
    /// caller-owned writer without an intermediate object graph.
    /// </summary>
    /// <param name="writer">The caller-owned UTF-8 JSON writer.</param>
    /// <param name="report">The complete source report to select from.</param>
    /// <param name="selection">The entries to include in the report view.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="selection" /> is not a defined selection.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A blocked source report contains no assessment selected as blocking.
    /// </exception>
    public static void Write(
        Utf8JsonWriter writer,
        SafeMigrationRunReport report,
        SafeMigrationReportSelection selection
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);

        var selectionCode = SelectionCode(selection);
        var counts = CountSelectedContent(report, selection);
        ValidateBlockingSelection(report, selection, counts.AssessmentCount);

        WriteReportView(writer, report, selection, selectionCode, counts);
    }

    private static void WriteReportView(
        Utf8JsonWriter writer,
        SafeMigrationRunReport report,
        SafeMigrationReportSelection selection,
        string selectionCode,
        ReportSelectionCounts counts
    )
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", CurrentReportViewSchemaVersion);
        writer.WriteString("documentKind", "safe_migration_report_view");
        writer.WriteString("selection", selectionCode);
        writer.WriteStartObject("sourceReport");
        writer.WriteNumber("schemaVersion", report.SchemaVersion);
        writer.WriteString("mode", ModeCode(report.Mode));
        writer.WriteString("status", StatusCode(report.Status));
        writer.WriteString("generatedAtUtc", report.GeneratedAtUtc);
        writer.WriteString("instanceId", report.InstanceId);
        WriteEnvironment(writer, report.Environment);
        WriteNullableString(writer, "targetMigrationId", report.TargetMigrationId);
        writer.WriteString("modelFingerprint", report.ModelFingerprint);
        writer.WriteString("contractFingerprint", report.ContractFingerprint);
        writer.WriteNumber("totalAssessmentCount", report.Assessments.Count);
        writer.WriteNumber("totalUnexpectedObjectCount", report.UnexpectedObjects.Count);
        writer.WriteEndObject();
        writer.WriteNumber("includedAssessmentCount", counts.AssessmentCount);
        writer.WriteNumber("includedUnexpectedObjectCount", counts.UnexpectedObjectCount);
        writer.WriteStartArray("assessments");

        foreach (var assessment in report.Assessments)
        {
            if (ShouldIncludeAssessment(report.Mode, assessment, selection))
            {
                WriteAssessment(writer, assessment);
            }
        }

        writer.WriteEndArray();
        writer.WriteStartArray("unexpectedObjects");

        if (selection is SafeMigrationReportSelection.Complete or SafeMigrationReportSelection.NonMatching)
        {
            foreach (var unexpectedObject in report.UnexpectedObjects)
            {
                WriteUnexpectedObject(writer, unexpectedObject);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static ReportSelectionCounts CountSelectedContent(
        SafeMigrationRunReport report,
        SafeMigrationReportSelection selection
    )
    {
        var assessmentCount = 0;
        foreach (var assessment in report.Assessments)
        {
            if (ShouldIncludeAssessment(report.Mode, assessment, selection))
            {
                assessmentCount++;
            }
        }

        var unexpectedObjectCount = selection is
            SafeMigrationReportSelection.Complete or
            SafeMigrationReportSelection.NonMatching
                ? report.UnexpectedObjects.Count
                : 0;

        return new ReportSelectionCounts(assessmentCount, unexpectedObjectCount);
    }

    private static bool ShouldIncludeAssessment(
        SafeMigrationReportMode mode,
        SafeMigrationAssessment assessment,
        SafeMigrationReportSelection selection
    ) => selection switch
    {
        SafeMigrationReportSelection.Complete => true,
        SafeMigrationReportSelection.NonMatching => !assessment.IsSafeOperation
            || assessment.ObservedState != SafeMigrationObservedState.Matching
            || assessment.Action != SafeMigrationAction.NoOp
            || assessment.PostconditionSatisfied != true,
        SafeMigrationReportSelection.BlockingOnly => IsBlockingAssessment(mode, assessment),
        _ => throw new ArgumentOutOfRangeException(nameof(selection)),
    };

    private static bool IsBlockingAssessment(
        SafeMigrationReportMode mode,
        SafeMigrationAssessment assessment
    ) => mode switch
    {
        SafeMigrationReportMode.Preflight => assessment.Action is { } action
            && action.RejectsExecution(),
        SafeMigrationReportMode.Postflight => assessment.IsSafeOperation
            && assessment.PostconditionSatisfied == false,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static void ValidateBlockingSelection(
        SafeMigrationRunReport report,
        SafeMigrationReportSelection selection,
        int includedAssessmentCount
    )
    {
        if (selection == SafeMigrationReportSelection.BlockingOnly
            && report.Status == SafeMigrationReportStatus.Blocked
            && includedAssessmentCount == 0)
        {
            // A blocker view must never make an internally inconsistent source
            // report appear safe. Future status/action additions therefore fail
            // closed until their selection semantics are defined explicitly.
            throw new InvalidOperationException(
                "A blocked SafeMigrations report contains no assessment selected as blocking.");
        }
    }

    private static string SelectionCode(
        SafeMigrationReportSelection selection
    ) => selection switch
    {
        SafeMigrationReportSelection.Complete => "complete",
        SafeMigrationReportSelection.NonMatching => "non_matching",
        SafeMigrationReportSelection.BlockingOnly => "blocking_only",
        _ => throw new ArgumentOutOfRangeException(nameof(selection)),
    };

    private static void WriteEnvironment(
        Utf8JsonWriter writer,
        SafeMigrationProviderEnvironment environment
    )
    {
        writer.WriteStartObject("environment");

        writer.WriteString("providerId", environment.ProviderId);
        writer.WriteString("engineFamily", environment.EngineFamily);
        writer.WriteString("serverVersion", environment.ServerVersion);

        writer.WriteEndObject();
    }

    private static void WriteAssessment(
        Utf8JsonWriter writer,
        SafeMigrationAssessment assessment
    )
    {
        writer.WriteStartObject();
        writer.WriteNumber("ordinal", assessment.Ordinal);
        writer.WriteString("operationType", assessment.OperationType);
        writer.WriteBoolean("isSafeOperation", assessment.IsSafeOperation);

        WriteNullableString(
            writer,
            "operationKind",
            assessment.OperationKind is null ? null : OperationKindCode(assessment.OperationKind.Value));
        WriteNullableString(writer, "objectName", assessment.ObjectName);
        WriteNullableString(
            writer,
            "observedState",
            assessment.ObservedState is null ? null : ObservedStateCode(assessment.ObservedState.Value));
        WriteNullableString(writer, "action", assessment.Action is null ? null : ActionCode(assessment.Action.Value));

        if (assessment.PostconditionSatisfied is null)
        {
            writer.WriteNull("postconditionSatisfied");
        }
        else
        {
            writer.WriteBoolean("postconditionSatisfied", assessment.PostconditionSatisfied.Value);
        }

        writer.WriteString("code", assessment.Code);
        writer.WriteString("analysisCode", assessment.AnalysisCode);
        writer.WriteString("decisionCode", assessment.DecisionCode);
        writer.WriteString("operationalImpact", OperationalImpactCode(assessment.OperationalImpact));
        writer.WriteStartArray("differences");

        foreach (var difference in assessment.Differences)
        {
            writer.WriteStartObject();
            writer.WriteString("facet", difference.Facet);
            writer.WriteString("expected", difference.Expected);
            writer.WriteString("actual", difference.Actual);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteUnexpectedObject(
        Utf8JsonWriter writer,
        SafeMigrationUnexpectedObject value
    )
    {
        writer.WriteStartObject();
        writer.WriteString("objectKind", ObjectKindCode(value.ObjectKind));

        WriteNullableString(writer, "schema", value.Schema);
        WriteNullableString(writer, "table", value.Table);

        writer.WriteString("name", value.Name);
        writer.WriteString("code", value.Code);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value
    )
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static string ModeCode(
        SafeMigrationReportMode value
    ) => value switch
    {
        SafeMigrationReportMode.Preflight => "preflight",
        SafeMigrationReportMode.Postflight => "postflight",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string StatusCode(
        SafeMigrationReportStatus value
    ) => value switch
    {
        SafeMigrationReportStatus.NoOperations => "no_operations",
        SafeMigrationReportStatus.Ready => "ready",
        SafeMigrationReportStatus.ReadyWithProviderOperations => "ready_with_provider_operations",
        SafeMigrationReportStatus.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string OperationKindCode(
        SafeMigrationOperationKind value
    ) => value switch
    {
        SafeMigrationOperationKind.EnsureSchema => "ensure_schema",
        SafeMigrationOperationKind.DropSchema => "drop_schema",
        SafeMigrationOperationKind.EnsureTable => "ensure_table",
        SafeMigrationOperationKind.DropTable => "drop_table",
        SafeMigrationOperationKind.RenameTable => "rename_table",
        SafeMigrationOperationKind.EnsureColumn => "ensure_column",
        SafeMigrationOperationKind.DropColumn => "drop_column",
        SafeMigrationOperationKind.RenameColumn => "rename_column",
        SafeMigrationOperationKind.AlterColumn => "alter_column",
        SafeMigrationOperationKind.EnsureIndex => "ensure_index",
        SafeMigrationOperationKind.DropIndex => "drop_index",
        SafeMigrationOperationKind.RenameIndex => "rename_index",
        SafeMigrationOperationKind.EnsurePrimaryKey => "ensure_primary_key",
        SafeMigrationOperationKind.DropPrimaryKey => "drop_primary_key",
        SafeMigrationOperationKind.EnsureUniqueConstraint => "ensure_unique_constraint",
        SafeMigrationOperationKind.DropUniqueConstraint => "drop_unique_constraint",
        SafeMigrationOperationKind.EnsureCheckConstraint => "ensure_check_constraint",
        SafeMigrationOperationKind.DropCheckConstraint => "drop_check_constraint",
        SafeMigrationOperationKind.EnsureForeignKey => "ensure_foreign_key",
        SafeMigrationOperationKind.DropForeignKey => "drop_foreign_key",
        SafeMigrationOperationKind.EnsureModelManagedData => "ensure_model_managed_data",
        SafeMigrationOperationKind.UpdateModelManagedData => "update_model_managed_data",
        SafeMigrationOperationKind.DeleteModelManagedData => "delete_model_managed_data",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ObservedStateCode(
        SafeMigrationObservedState value
    ) => value switch
    {
        SafeMigrationObservedState.Missing => "missing",
        SafeMigrationObservedState.Matching => "matching",
        SafeMigrationObservedState.Different => "different",
        SafeMigrationObservedState.Unsupported => "unsupported",
        SafeMigrationObservedState.DataBlocked => "data_blocked",
        SafeMigrationObservedState.PrerequisiteMissing => "prerequisite_missing",
        SafeMigrationObservedState.TransitionReady => "transition_ready",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ActionCode(
        SafeMigrationAction value
    ) => value switch
    {
        SafeMigrationAction.Apply => "apply",
        SafeMigrationAction.NoOp => "no_op",
        SafeMigrationAction.Repair => "repair",
        SafeMigrationAction.RejectDifferent => "reject_different",
        SafeMigrationAction.RejectUnsupported => "reject_unsupported",
        SafeMigrationAction.RejectDataBlocked => "reject_data_blocked",
        SafeMigrationAction.RejectPrerequisiteMissing => "reject_prerequisite_missing",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ObjectKindCode(
        SafeMigrationDatabaseObjectKind value
    ) => value switch
    {
        SafeMigrationDatabaseObjectKind.Table => "table",
        SafeMigrationDatabaseObjectKind.Column => "column",
        SafeMigrationDatabaseObjectKind.Index => "index",
        SafeMigrationDatabaseObjectKind.PrimaryKey => "primary_key",
        SafeMigrationDatabaseObjectKind.UniqueConstraint => "unique_constraint",
        SafeMigrationDatabaseObjectKind.CheckConstraint => "check_constraint",
        SafeMigrationDatabaseObjectKind.ForeignKey => "foreign_key",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string OperationalImpactCode(
        SafeMigrationOperationalImpact value
    ) => value switch
    {
        SafeMigrationOperationalImpact.NotApplicable => "not_applicable",
        SafeMigrationOperationalImpact.TableRewritePossible => "table_rewrite_possible",
        SafeMigrationOperationalImpact.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private readonly record struct ReportSelectionCounts(
        int AssessmentCount,
        int UnexpectedObjectCount);
}
