namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Writes bounded operator summaries without constructing a filtered report.</summary>
public static class SafeMigrationReportText
{
    private const int MaximumDisplayedAssessments = 20;

    private const int MaximumDisplayedUnexpectedObjects = 10;

    /// <summary>Formats a bounded report summary for console or log output.</summary>
    /// <param name="report">The immutable complete source report.</param>
    /// <param name="selection">The entries to display; blocking assessments are the default.</param>
    /// <returns>The summary, including explicit counts for omitted selected entries.</returns>
    /// <remarks>No query, filtered collection or report mutation is performed.</remarks>
    /// <exception cref="ArgumentNullException">The report is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The selection is undefined.</exception>
    /// <exception cref="InvalidOperationException">A blocked report has no selected blocker.</exception>
    public static string Format(
        SafeMigrationRunReport report,
        SafeMigrationReportSelection selection = SafeMigrationReportSelection.BlockingOnly
    )
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(writer, report, selection);

        return writer.ToString();
    }

    /// <summary>Streams a bounded report summary to a caller-owned text writer.</summary>
    /// <param name="writer">The writer, which remains open after the summary is written.</param>
    /// <param name="report">The immutable complete source report.</param>
    /// <param name="selection">The entries to display; blocking assessments are the default.</param>
    /// <remarks>
    /// At most 20 assessment details and 10 unexpected-object details are emitted.
    /// Counts always describe the complete selection, and omitted details are labeled explicitly.
    /// Identifiers are escaped and bounded for single-line log safety.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The writer or report is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The selection is undefined.</exception>
    /// <exception cref="InvalidOperationException">A blocked report has no selected blocker.</exception>
    public static void Write(
        TextWriter writer,
        SafeMigrationRunReport report,
        SafeMigrationReportSelection selection = SafeMigrationReportSelection.BlockingOnly
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);

        var selectionCode = SafeMigrationReportJson.SelectionCode(selection);
        var selectedCount = 0;
        var blockedCount = 0;
        var deferredCount = 0;
        foreach (var assessment in report.Assessments)
        {
            selectedCount += SafeMigrationReportJson.ShouldIncludeAssessment(report.Mode, assessment, selection)
                ? 1 : 0;
            blockedCount += SafeMigrationReportJson.IsBlockingAssessment(report.Mode, assessment) ? 1 : 0;
            deferredCount += assessment.Action == SafeMigrationAction.ValidateAtRuntime ? 1 : 0;
        }

        SafeMigrationReportJson.ValidateBlockingSelection(report, selection, selectedCount);

        writer.Write("SafeMigrations ");
        writer.Write(SafeMigrationReportJson.ModeCode(report.Mode));
        writer.Write(": ");
        writer.Write(SafeMigrationReportJson.StatusCode(report.Status));
        writer.Write("; blockers=");
        writer.Write(blockedCount.ToString(CultureInfo.InvariantCulture));
        writer.Write("; runtime-validation-required=");
        writer.Write(deferredCount.ToString(CultureInfo.InvariantCulture));
        writer.Write("; assessments=");
        writer.Write(selectedCount.ToString(CultureInfo.InvariantCulture));
        writer.Write('/');
        writer.Write(report.Assessments.Count.ToString(CultureInfo.InvariantCulture));
        writer.Write("; selection=");
        writer.WriteLine(selectionCode);

        writer.Write("Instance: ");
        writer.Write(SafeMigrationDiagnosticMessages.BoundForDisplay(report.InstanceId));
        writer.Write("; provider: ");
        writer.Write(SafeMigrationDiagnosticMessages.BoundForDisplay(report.Environment.ProviderId));
        writer.Write("; target migration: ");
        writer.WriteLine(report.TargetMigrationId is { } migrationId
            ? SafeMigrationDiagnosticMessages.BoundForDisplay(migrationId)
            : "unknown");

        // WHY: Bound detail emission independently of report cardinality.
        // Large migrations retain complete immutable JSON evidence, while
        // console diagnostics cannot allocate or flood an unbounded log entry.
        var displayedCount = 0;
        foreach (var assessment in report.Assessments)
        {
            if (!SafeMigrationReportJson.ShouldIncludeAssessment(report.Mode, assessment, selection))
            {
                continue;
            }

            if (displayedCount == MaximumDisplayedAssessments)
            {
                break;
            }

            writer.WriteLine(SafeMigrationDiagnosticMessages.FormatAssessment(assessment));
            displayedCount++;
        }

        if (selectedCount > displayedCount)
        {
            writer.Write("Omitted selected assessment details: ");
            writer.WriteLine((selectedCount - displayedCount).ToString(CultureInfo.InvariantCulture));
        }

        WriteUnexpectedObjects(writer, report, selection);
    }

    /// <summary>Writes bounded unexpected-object identities without implying an operation failure.</summary>
    private static void WriteUnexpectedObjects(
        TextWriter writer,
        SafeMigrationRunReport report,
        SafeMigrationReportSelection selection
    )
    {
        if (selection == SafeMigrationReportSelection.BlockingOnly)
        {
            return;
        }

        writer.Write("Unexpected objects (informational): ");
        writer.WriteLine(report.UnexpectedObjects.Count.ToString(CultureInfo.InvariantCulture));
        var displayedCount = Math.Min(report.UnexpectedObjects.Count, MaximumDisplayedUnexpectedObjects);
        for (var index = 0; index < displayedCount; index++)
        {
            var value = report.UnexpectedObjects[index];
            writer.Write("Unexpected ");
            writer.Write(value.ObjectKind.ToString());
            writer.Write(": schema=");
            writer.Write(value.Schema is { } schema
                ? SafeMigrationDiagnosticMessages.BoundForDisplay(schema)
                : "<provider-default>");
            writer.Write("; table=");
            writer.Write(value.Table is { } table
                ? SafeMigrationDiagnosticMessages.BoundForDisplay(table)
                : "<not-applicable>");
            writer.Write("; name=");
            writer.Write(SafeMigrationDiagnosticMessages.BoundForDisplay(value.Name));
            writer.Write("; code=");
            writer.WriteLine(SafeMigrationDiagnosticMessages.BoundForDisplay(value.Code));
        }

        if (report.UnexpectedObjects.Count > displayedCount)
        {
            writer.Write("Omitted unexpected-object details: ");
            writer.WriteLine((report.UnexpectedObjects.Count - displayedCount).ToString(CultureInfo.InvariantCulture));
        }
    }
}
