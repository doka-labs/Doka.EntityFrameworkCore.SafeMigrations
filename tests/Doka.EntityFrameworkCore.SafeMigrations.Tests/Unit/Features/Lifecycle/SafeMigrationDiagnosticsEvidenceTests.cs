namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationDiagnosticsEvidenceTests
{
    [Fact]
    public void FacetDifference_AcceptsBoundedPrintableMetadata()
    {
        var difference = new SafeMigrationFacetDifference(
            "column_max_length",
            new string('e', 256),
            new string('a', 256));

        Assert.Equal("column_max_length", difference.Facet);
        Assert.Equal(256, difference.Expected.Length);
        Assert.Equal(256, difference.Actual.Length);
    }

    [Theory]
    [InlineData("Column_Type")]
    [InlineData("column-type")]
    [InlineData("")]
    [InlineData(" ")]
    public void FacetDifference_RejectsInvalidFacetCodes(
        string facet
    )
    {
        Assert.Throws<ArgumentException>(() => new SafeMigrationFacetDifference(
            facet,
            "expected",
            "actual"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("line\nbreak")]
    public void FacetDifference_RejectsEmptyOrNonPrintableValues(
        string value
    )
    {
        Assert.Throws<ArgumentException>(() => new SafeMigrationFacetDifference(
            "column_store_type",
            value,
            "actual"));
        Assert.Throws<ArgumentException>(() => new SafeMigrationFacetDifference(
            "column_store_type",
            "expected",
            value));
    }

    [Fact]
    public void FacetDifference_RejectsOversizedCodesAndValues()
    {
        Assert.Throws<ArgumentException>(() => new SafeMigrationFacetDifference(
            new string('f', 65),
            "expected",
            "actual"));
        Assert.Throws<ArgumentException>(() => new SafeMigrationFacetDifference(
            "column_store_type",
            new string('e', 257),
            "actual"));
        Assert.Throws<ArgumentException>(() => new SafeMigrationFacetDifference(
            "column_store_type",
            "expected",
            new string('a', 257)));
    }

    [Fact]
    public void ProviderAnalysis_SnapshotsAndBoundsDifferences()
    {
        var mutable = new List<SafeMigrationFacetDifference>
        {
            new("column_max_length", "200", "10"),
        };

        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            postconditionSatisfied: false,
            "varchar_length_transition",
            SafeMigrationOperationalImpact.TableRewritePossible,
            mutable);

        mutable.Clear();

        Assert.Single(analysis.Differences);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, analysis.OperationalImpact);
        Assert.Throws<ArgumentException>(() => new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "too_many_differences",
            SafeMigrationOperationalImpact.Unknown,
            Enumerable
                .Range(0, 17)
                .Select(index => new SafeMigrationFacetDifference(
                    "column_store_type",
                    index.ToString(CultureInfo.InvariantCulture),
                    "actual"))));
    }

    [Fact]
    public void DifferenceParser_RejectsMalformedPayloadWithoutEchoingIt()
    {
        const string sensitivePayload = "column_store_type\u001fexpected\u001fsecret\nvalue";
        var oversizedPayload = new string('s', 16_000);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationFacetDifferenceParser.Parse(sensitivePayload, "TestProvider"));

        var oversizedException = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationFacetDifferenceParser.Parse(oversizedPayload, "TestProvider"));

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitivePayload, exception.Message, StringComparison.Ordinal);
        Assert.Contains("malformed", oversizedException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oversizedPayload, oversizedException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferenceParser_PreservesOrderedBoundedRecords()
    {
        const string payload = "column_store_type\u001fvarchar(200)\u001fvarchar(10)"
            + "\u001ecolumn_nullability\u001fnot_nullable\u001fnullable";

        var differences = SafeMigrationFacetDifferenceParser.Parse(payload, "TestProvider");

        Assert.Equal(2, differences.Count);
        Assert.Equal("column_store_type", differences[0].Facet);
        Assert.Equal("column_nullability", differences[1].Facet);
    }

    [Fact]
    public void BlockedPreflight_ThrowsTypedExceptionWithCompleteReportAndBoundedSummary()
    {
        var assessments = Enumerable
            .Range(0, 20)
            .Select(index => new SafeMigrationAssessment(
                index,
                typeof(SafeMigrationOperation).FullName!,
                isSafeOperation: true,
                SafeMigrationOperationKind.EnsureColumn,
                $"column_{index}",
                SafeMigrationObservedState.Different,
                SafeMigrationAction.RejectDifferent,
                postconditionSatisfied: false,
                "different_no_safe_repair",
                "column_definition_different",
                "different_no_safe_repair",
                SafeMigrationOperationalImpact.NotApplicable,
                index == 0
                    ? [new SafeMigrationFacetDifference("column_max_length", "200", "10")]
                    : null))
            .ToArray();

        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Blocked,
            assessments);

        var exception = Assert.Throws<SafeMigrationPreflightException>(report.ThrowIfBlocked);

        Assert.Same(report, exception.Report);
        Assert.Contains("blocked 20 operations", exception.Message, StringComparison.Ordinal);
        Assert.Contains("operation 0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("column_max_length: expected 200, actual 10", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("column_19", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedPreflight_BoundsCallerSuppliedSummaryFieldsWithoutMutatingTheReport()
    {
        var objectName = new string('o', 512);
        var analysisCode = new string('a', 512);
        var decisionCode = new string('d', 512);
        var expected = new string('e', 256);
        var actual = new string('a', 256);
        var assessment = new SafeMigrationAssessment(
            0,
            typeof(SafeMigrationOperation).FullName!,
            isSafeOperation: true,
            SafeMigrationOperationKind.EnsureColumn,
            objectName,
            SafeMigrationObservedState.Different,
            SafeMigrationAction.RejectDifferent,
            postconditionSatisfied: false,
            "different_no_safe_repair",
            analysisCode,
            decisionCode,
            SafeMigrationOperationalImpact.NotApplicable,
            [new SafeMigrationFacetDifference("column_store_type", expected, actual)]);

        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Blocked,
            [assessment]);

        var exception = Assert.Throws<SafeMigrationPreflightException>(report.ThrowIfBlocked);

        Assert.InRange(exception.Message.Length, 1, 900);
        Assert.DoesNotContain(objectName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(analysisCode, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(decisionCode, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(expected, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(actual, exception.Message, StringComparison.Ordinal);
        Assert.Same(assessment, Assert.Single(exception.Report.Assessments));
        Assert.Equal(objectName, assessment.ObjectName);
        Assert.Equal(analysisCode, assessment.AnalysisCode);
        Assert.Equal(decisionCode, assessment.DecisionCode);
        Assert.Equal(expected, Assert.Single(assessment.Differences).Expected);
        Assert.Equal(actual, Assert.Single(assessment.Differences).Actual);
    }

    [Fact]
    public void BlockedPreflight_SanitizesCallerSuppliedControlCharactersInSummary()
    {
        const string objectName = "column\nforged";
        const string analysisCode = "analysis\u001bcode";
        const string decisionCode = "decision\r\ncode";

        var assessment = new SafeMigrationAssessment(
            0,
            typeof(SafeMigrationOperation).FullName!,
            isSafeOperation: true,
            SafeMigrationOperationKind.EnsureColumn,
            objectName,
            SafeMigrationObservedState.Different,
            SafeMigrationAction.RejectDifferent,
            postconditionSatisfied: false,
            "different_no_safe_repair",
            analysisCode,
            decisionCode,
            SafeMigrationOperationalImpact.NotApplicable,
            differences: null);

        var report = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Blocked,
            [assessment]);

        var exception = Assert.Throws<SafeMigrationPreflightException>(report.ThrowIfBlocked);

        Assert.DoesNotContain('\n', exception.Message);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\u001b', exception.Message);
        Assert.Contains("column?forged", exception.Message, StringComparison.Ordinal);
        Assert.Contains("analysis?code", exception.Message, StringComparison.Ordinal);
        Assert.Contains("decision??code", exception.Message, StringComparison.Ordinal);
        Assert.Equal(objectName, assessment.ObjectName);
        Assert.Equal(analysisCode, assessment.AnalysisCode);
        Assert.Equal(decisionCode, assessment.DecisionCode);
    }

    [Fact]
    public void ThrowIfBlocked_RejectsPostflightAndAcceptsReadyPreflight()
    {
        var ready = CreateReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Ready,
            []);

        var postflight = CreateReport(
            SafeMigrationReportMode.Postflight,
            SafeMigrationReportStatus.Blocked,
            []);

        ready.ThrowIfBlocked();

        Assert.Throws<InvalidOperationException>(postflight.ThrowIfBlocked);
    }

    private static SafeMigrationRunReport CreateReport(
        SafeMigrationReportMode mode,
        SafeMigrationReportStatus status,
        IReadOnlyList<SafeMigrationAssessment> assessments
    ) => new(
        mode,
        status,
        DateTimeOffset.UnixEpoch,
        "instance-test",
        new SafeMigrationProviderEnvironment("npgsql_postgresql", "postgresql", "18.6"),
        "202609050001_Diagnostics",
        $"safe-relational-model:v1:npgsql_postgresql:sha256:{new string('a', 64)}",
        new string('b', 64),
        assessments);
}
