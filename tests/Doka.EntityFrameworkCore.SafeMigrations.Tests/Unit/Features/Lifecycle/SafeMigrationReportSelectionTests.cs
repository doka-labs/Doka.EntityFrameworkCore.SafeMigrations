namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationReportSelectionTests
{
    [Fact]
    public void CompleteView_IsSelfDescribingAndPreservesTheCompleteSourceOrder()
    {
        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Blocked,
            [
                Assessment(9, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, true),
                Assessment(3, SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent, false),
                ProviderAssessment(14),
            ]);

        var bytes = SafeMigrationReportJson.SerializeToUtf8Bytes(
            report,
            SafeMigrationReportSelection.Complete);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var sourceReport = root.GetProperty("sourceReport");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("safe_migration_report_view", root.GetProperty("documentKind").GetString());
        Assert.Equal("complete", root.GetProperty("selection").GetString());
        Assert.Equal(report.SchemaVersion, sourceReport.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(report.InstanceId, sourceReport.GetProperty("instanceId").GetString());
        Assert.Equal(report.TargetMigrationId, sourceReport.GetProperty("targetMigrationId").GetString());
        Assert.Equal(report.ModelFingerprint, sourceReport.GetProperty("modelFingerprint").GetString());
        Assert.Equal(report.ContractFingerprint, sourceReport.GetProperty("contractFingerprint").GetString());
        Assert.Equal(
            report.Environment.ServerVersion,
            sourceReport.GetProperty("environment").GetProperty("serverVersion").GetString());
        Assert.Equal(report.Assessments.Count, sourceReport.GetProperty("totalAssessmentCount").GetInt32());
        Assert.Equal(
            report.UnexpectedObjects.Count,
            sourceReport.GetProperty("totalUnexpectedObjectCount").GetInt32());
        Assert.Equal(3, root.GetProperty("includedAssessmentCount").GetInt32());
        Assert.Equal(2, root.GetProperty("includedUnexpectedObjectCount").GetInt32());
        Assert.Equal(
            [9, 3, 14],
            root.GetProperty("assessments")
                .EnumerateArray()
                .Select(static assessment => assessment.GetProperty("ordinal").GetInt32()));
        Assert.Equal(2, root.GetProperty("unexpectedObjects").GetArrayLength());

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            SafeMigrationReportJson.Write(writer, report, SafeMigrationReportSelection.Complete);
        }

        Assert.Equal(bytes, stream.ToArray());

        using var canonicalDocument = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));

        Assert.False(canonicalDocument.RootElement.TryGetProperty("documentKind", out _));
        Assert.Equal(report.SchemaVersion, canonicalDocument.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void NonMatchingView_ExcludesOnlyFullyConvergedSafeAssessments()
    {
        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.ReadyWithProviderOperations,
            [
                Assessment(0, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, true),
                Assessment(1, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, false),
                Assessment(2, SafeMigrationObservedState.Missing, SafeMigrationAction.Apply, false),
                Assessment(3, SafeMigrationObservedState.TransitionReady, SafeMigrationAction.Repair, false),
                ProviderAssessment(4),
            ]);

        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(
            report,
            SafeMigrationReportSelection.NonMatching));
        var root = document.RootElement;

        Assert.Equal("non_matching", root.GetProperty("selection").GetString());
        Assert.Equal(4, root.GetProperty("includedAssessmentCount").GetInt32());
        Assert.Equal(2, root.GetProperty("includedUnexpectedObjectCount").GetInt32());
        Assert.Equal(
            [1, 2, 3, 4],
            root.GetProperty("assessments")
                .EnumerateArray()
                .Select(static assessment => assessment.GetProperty("ordinal").GetInt32()));
    }

    [Fact]
    public void BlockingOnlyPreflightView_IncludesExactlyTheRejectActions()
    {
        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Blocked,
            [
                Assessment(0, SafeMigrationObservedState.Missing, SafeMigrationAction.Apply, false),
                Assessment(1, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, true),
                Assessment(2, SafeMigrationObservedState.TransitionReady, SafeMigrationAction.Repair, false),
                Assessment(3, SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent, false),
                Assessment(4, SafeMigrationObservedState.Unsupported, SafeMigrationAction.RejectUnsupported, false),
                Assessment(5, SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked, false),
                Assessment(
                    6,
                    SafeMigrationObservedState.PrerequisiteMissing,
                    SafeMigrationAction.RejectPrerequisiteMissing,
                    false),
                ProviderAssessment(7),
            ]);

        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(
            report,
            SafeMigrationReportSelection.BlockingOnly));
        var root = document.RootElement;

        Assert.Equal("blocking_only", root.GetProperty("selection").GetString());
        Assert.Equal(4, root.GetProperty("includedAssessmentCount").GetInt32());
        Assert.Equal(0, root.GetProperty("includedUnexpectedObjectCount").GetInt32());
        Assert.Equal(
            [3, 4, 5, 6],
            root.GetProperty("assessments")
                .EnumerateArray()
                .Select(static assessment => assessment.GetProperty("ordinal").GetInt32()));
        Assert.Empty(root.GetProperty("unexpectedObjects").EnumerateArray());
    }

    [Fact]
    public void BlockingOnlyPostflightView_IncludesOnlyFailedSafePostconditions()
    {
        var report = CreateReport(
            SafeMigrationReportMode.Postflight,
            SafeMigrationReportStatus.Blocked,
            [
                Assessment(0, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, true),
                Assessment(1, SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent, false),
                Assessment(2, SafeMigrationObservedState.Missing, SafeMigrationAction.Apply, false),
                Assessment(3, SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent, null),
                ProviderAssessment(4, postconditionSatisfied: false),
            ]);

        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(
            report,
            SafeMigrationReportSelection.BlockingOnly));
        var root = document.RootElement;

        Assert.Equal(
            [1, 2],
            root.GetProperty("assessments")
                .EnumerateArray()
                .Select(static assessment => assessment.GetProperty("ordinal").GetInt32()));
    }

    [Fact]
    public void BlockingOnlyView_RepresentsANonBlockedReportAsAnEmptyView()
    {
        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Ready,
            [Assessment(0, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, true)]);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Indented = true,
            }))
        {
            SafeMigrationReportJson.Write(writer, report, SafeMigrationReportSelection.BlockingOnly);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("includedAssessmentCount").GetInt32());
        Assert.Empty(root.GetProperty("assessments").EnumerateArray());
        Assert.Contains((byte)'\n', stream.ToArray());
    }

    [Fact]
    public void BlockingOnlyView_SelectsTailBlockersFromFiftyThousandOrderedAssessments()
    {
        const int assessmentCount = 50_000;
        var assessments = new SafeMigrationAssessment[assessmentCount];
        for (var index = 0; index < assessmentCount; index++)
        {
            assessments[index] = Assessment(
                index,
                SafeMigrationObservedState.Matching,
                SafeMigrationAction.NoOp,
                postconditionSatisfied: true);
        }

        assessments[^4] = Assessment(
            assessmentCount - 4,
            SafeMigrationObservedState.Different,
            SafeMigrationAction.RejectDifferent,
            postconditionSatisfied: false);
        assessments[^3] = Assessment(
            assessmentCount - 3,
            SafeMigrationObservedState.Unsupported,
            SafeMigrationAction.RejectUnsupported,
            postconditionSatisfied: false);
        assessments[^2] = Assessment(
            assessmentCount - 2,
            SafeMigrationObservedState.DataBlocked,
            SafeMigrationAction.RejectDataBlocked,
            postconditionSatisfied: false);
        assessments[^1] = Assessment(
            assessmentCount - 1,
            SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationAction.RejectPrerequisiteMissing,
            postconditionSatisfied: false);

        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Blocked,
            assessments);

        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(
            report,
            SafeMigrationReportSelection.BlockingOnly));
        var root = document.RootElement;

        Assert.Equal(assessmentCount, root.GetProperty("sourceReport").GetProperty("totalAssessmentCount").GetInt32());
        Assert.Equal(4, root.GetProperty("includedAssessmentCount").GetInt32());
        Assert.Equal(
            [assessmentCount - 4, assessmentCount - 3, assessmentCount - 2, assessmentCount - 1],
            root.GetProperty("assessments")
                .EnumerateArray()
                .Select(static assessment => assessment.GetProperty("ordinal").GetInt32()));
    }

    [Fact]
    public void BlockingOnlyView_RejectsABlockedReportWithoutSelectedEvidenceBeforeWriting()
    {
        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Blocked,
            [Assessment(0, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, true)]);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var serializeException = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationReportJson.SerializeToUtf8Bytes(report, SafeMigrationReportSelection.BlockingOnly));
        var writeException = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationReportJson.Write(writer, report, SafeMigrationReportSelection.BlockingOnly));

        Assert.Contains("no assessment selected as blocking", serializeException.Message, StringComparison.Ordinal);
        Assert.Equal(serializeException.Message, writeException.Message);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, writer.BytesPending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void ReportView_RejectsUndefinedSelectionsBeforeWriting(
        int value
    )
    {
        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Ready,
            [Assessment(0, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, true)]);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        var selection = (SafeMigrationReportSelection)value;

        var serializeException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SafeMigrationReportJson.SerializeToUtf8Bytes(report, selection));
        var writeException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SafeMigrationReportJson.Write(writer, report, selection));

        Assert.Equal("selection", serializeException.ParamName);
        Assert.Equal("selection", writeException.ParamName);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, writer.BytesPending);
    }

    [Fact]
    public void ReportView_RejectsNullInputsBeforeWriting()
    {
        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Ready,
            [Assessment(0, SafeMigrationObservedState.Matching, SafeMigrationAction.NoOp, true)]);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        var serializeException = Assert.Throws<ArgumentNullException>(() =>
            SafeMigrationReportJson.SerializeToUtf8Bytes(null!, SafeMigrationReportSelection.Complete));
        var writerException = Assert.Throws<ArgumentNullException>(() =>
            SafeMigrationReportJson.Write(null!, report, SafeMigrationReportSelection.Complete));
        var reportException = Assert.Throws<ArgumentNullException>(() =>
            SafeMigrationReportJson.Write(writer, null!, SafeMigrationReportSelection.Complete));

        Assert.Equal("report", serializeException.ParamName);
        Assert.Equal("writer", writerException.ParamName);
        Assert.Equal("report", reportException.ParamName);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, writer.BytesPending);
    }

    private static SafeMigrationRunReport CreateReport(
        SafeMigrationReportMode mode,
        SafeMigrationReportStatus status,
        IReadOnlyList<SafeMigrationAssessment> assessments
    ) => new(
        mode,
        status,
        new DateTimeOffset(
            2026,
            9,
            10,
            8,
            30,
            0,
            TimeSpan.Zero),
        "instance-7f3a",
        new SafeMigrationProviderEnvironment("mysql_mariadb", "mariadb", "11.8.8"),
        "202609100830_Application",
        $"safe-relational-model:v1:mysql_mariadb:sha256:{new string('a', 64)}",
        new string('b', 64),
        assessments,
        [
            new SafeMigrationUnexpectedObject(
                SafeMigrationDatabaseObjectKind.Table,
                schema: null,
                table: null,
                "legacy_table",
                "unexpected_table"),
            new SafeMigrationUnexpectedObject(
                SafeMigrationDatabaseObjectKind.Index,
                schema: null,
                "items",
                "ix_items_legacy",
                "unexpected_index"),
        ]);

    private static SafeMigrationAssessment Assessment(
        int ordinal,
        SafeMigrationObservedState observedState,
        SafeMigrationAction action,
        bool? postconditionSatisfied
    ) => new(
        ordinal,
        typeof(SafeMigrationOperation).FullName!,
        isSafeOperation: true,
        SafeMigrationOperationKind.EnsureColumn,
        $"column_{ordinal.ToString(CultureInfo.InvariantCulture)}",
        observedState,
        action,
        postconditionSatisfied,
        "assessment");

    private static SafeMigrationAssessment ProviderAssessment(
        int ordinal,
        bool? postconditionSatisfied = null
    ) => new(
        ordinal,
        typeof(MigrationOperation).FullName!,
        isSafeOperation: false,
        operationKind: null,
        objectName: null,
        observedState: null,
        action: null,
        postconditionSatisfied,
        "provider_owned_not_analyzed");
}
