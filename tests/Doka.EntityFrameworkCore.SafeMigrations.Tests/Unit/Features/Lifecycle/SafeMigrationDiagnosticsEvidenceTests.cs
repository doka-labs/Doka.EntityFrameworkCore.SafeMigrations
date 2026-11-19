namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Pins bounded structural evidence and payload-free rejection summaries without truncating the attached full report.
/// </summary>
public sealed class SafeMigrationDiagnosticsEvidenceTests
{
    /// <summary>
    /// Accepts complete 256-character printable expected and actual values without truncating their structural
    /// evidence.
    /// </summary>
    [Fact]
    public void FacetDifference_AcceptsBoundedPrintableMetadata()
    {
        // Arrange
        var expected = new string('e', 256);
        var actual = new string('a', 256);

        // Act
        var difference = new SafeMigrationFacetDifference(
            "column_max_length",
            expected,
            actual);

        // Assert
        Assert.Equal("column_max_length", difference.Facet);
        Assert.Equal(256, difference.Expected.Length);
        Assert.Equal(256, difference.Actual.Length);
    }

    /// <summary>
    /// Rejects empty, blank, mixed-case or punctuation-bearing diagnostic facets before storing evidence.
    /// </summary>
    /// <param name="facet">
    /// The independently invalid diagnostic facet code.
    /// </param>
    [Theory]
    [InlineData("Column_Type")]
    [InlineData("column-type")]
    [InlineData("")]
    [InlineData(" ")]
    public void FacetDifference_RejectsInvalidFacetCodes(
        string facet
    )
    {
        // Arrange: The theory supplies the invalid facet code.

        // Act
        var exception = Record.Exception(() => new SafeMigrationFacetDifference(
            facet,
            "expected",
            "actual"));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Rejects each invalid display value independently in both metadata positions.</summary>
    /// <param name="value">The invalid empty or nonprintable display value.</param>
    /// <param name="invalidActual">Whether the invalid value occupies the actual metadata position.</param>
    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData(" ", false)]
    [InlineData(" ", true)]
    [InlineData("line\nbreak", false)]
    [InlineData("line\nbreak", true)]
    public void FacetDifference_RejectsEmptyOrNonPrintableValues(
        string value,
        bool invalidActual
    )
    {
        // Arrange
        var expected = invalidActual
            ? "expected"
            : value;

        var actual = invalidActual
            ? value
            : "actual";

        // Act
        var exception = Record.Exception(() => new SafeMigrationFacetDifference(
            "column_store_type",
            expected,
            actual));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Rejects each independently oversized structural metadata field.</summary>
    /// <param name="field">The facet, expected-value or actual-value ordinal.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FacetDifference_RejectsOversizedCodesAndValues(
        int field
    )
    {
        // Arrange
        var facet = field == 0
            ? new string('f', 65)
            : "column_store_type";

        var expected = field == 1
            ? new string('e', 257)
            : "expected";

        var actual = field == 2
            ? new string('a', 257)
            : "actual";

        // Act
        var exception = Record.Exception(() => new SafeMigrationFacetDifference(facet, expected, actual));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>
    /// Retains diagnostic differences and operational impact after the caller clears the original mutable
    /// collection.
    /// </summary>
    [Fact]
    public void ProviderAnalysis_SnapshotsAndBoundsDifferences()
    {
        // Arrange
        var mutable = new List<SafeMigrationFacetDifference>
        {
            new("column_max_length", "200", "10"),
        };

        // Act
        var analysis = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            postconditionSatisfied: false,
            "varchar_length_transition",
            SafeMigrationOperationalImpact.TableRewritePossible,
            mutable);

        mutable.Clear();

        // Assert
        Assert.Single(analysis.Differences);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, analysis.OperationalImpact);
    }

    /// <summary>Rejects excessive difference evidence rather than truncating the structural proof.</summary>
    [Fact]
    public void ProviderAnalysis_RejectsExcessiveDifferences()
    {
        // Arrange
        var differences = Enumerable
            .Range(0, 17)
            .Select(index => new SafeMigrationFacetDifference(
                "column_store_type",
                index.ToString(CultureInfo.InvariantCulture),
                "actual"));

        // Act
        var exception = Record.Exception(() => new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "too_many_differences",
            SafeMigrationOperationalImpact.Unknown,
            differences));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Rejects malformed and oversized evidence independently without echoing private payload.</summary>
    /// <param name="oversized">Whether the supplied payload exceeds the parser's bounded size.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferenceParser_RejectsMalformedPayloadWithoutEchoingIt(
        bool oversized
    )
    {
        // Arrange
        var payload = oversized
            ? new string('s', 16_000)
            : "column_store_type\u001fexpected\u001fsecret\nvalue";

        // Act
        var exception = Record.Exception(() => SafeMigrationFacetDifferenceParser.Parse(payload, "TestProvider"));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("malformed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Preserves the authored store-type and nullability record order while parsing the bounded evidence protocol.
    /// </summary>
    [Fact]
    public void DifferenceParser_PreservesOrderedBoundedRecords()
    {
        // Arrange
        const string payload = "column_store_type\u001fvarchar(200)\u001fvarchar(10)"
            + "\u001ecolumn_nullability\u001fnot_nullable\u001fnullable";

        // Act
        var differences = SafeMigrationFacetDifferenceParser.Parse(payload, "TestProvider");

        // Assert
        Assert.Equal(2, differences.Count);
        Assert.Equal("column_store_type", differences[0].Facet);
        Assert.Equal("column_nullability", differences[1].Facet);
    }

    /// <summary>
    /// Retains the complete twenty-conflict report while emitting only the first bounded structural rejection
    /// summary.
    /// </summary>
    [Fact]
    public void BlockedPreflight_ThrowsTypedExceptionWithCompleteReportAndBoundedSummary()
    {
        // Arrange
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

        // Act
        var failure = Record.Exception(report.ThrowIfBlocked);

        // Assert
        var exception = Assert.IsType<SafeMigrationPreflightException>(failure);

        Assert.Same(report, exception.Report);
        Assert.Contains("blocked 20 operations", exception.Message, StringComparison.Ordinal);
        Assert.Contains("operation 0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("column_max_length: expected 200, actual 10", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("column_19", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bounds displayed identifiers and facets without changing their complete values in the attached immutable
    /// report.
    /// </summary>
    [Fact]
    public void BlockedPreflight_BoundsCallerSuppliedSummaryFieldsWithoutMutatingTheReport()
    {
        // Arrange
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

        // Act
        var failure = Record.Exception(report.ThrowIfBlocked);

        // Assert
        var exception = Assert.IsType<SafeMigrationPreflightException>(failure);

        Assert.InRange(exception.Message.Length, 1, 2300);
        Assert.DoesNotContain(objectName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(analysisCode, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(decisionCode, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(expected, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(actual, exception.Message, StringComparison.Ordinal);
        Assert.Same(assessment, Assert.Single(exception.Report.Assessments));
        Assert.Equal(objectName, assessment.ObjectName);
        Assert.Equal(analysisCode, assessment.AnalysisCode);
        Assert.Equal(decisionCode, assessment.DecisionCode);
        Assert.Equal(expected,
            Assert.Single(assessment.Differences)
                .Expected);

        Assert.Equal(actual,
            Assert.Single(assessment.Differences)
                .Actual);
    }

    /// <summary>
    /// Escapes caller-supplied line breaks and terminal controls without altering the original report metadata.
    /// </summary>
    [Fact]
    public void BlockedPreflight_SanitizesCallerSuppliedControlCharactersInSummary()
    {
        // Arrange
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

        // Act
        var failure = Record.Exception(report.ThrowIfBlocked);

        // Assert
        var exception = Assert.IsType<SafeMigrationPreflightException>(failure);

        Assert.DoesNotContain('\n', exception.Message);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\u001b', exception.Message);
        Assert.Contains("column\\u000Aforged", exception.Message, StringComparison.Ordinal);
        Assert.Contains("analysis\\u001Bcode", exception.Message, StringComparison.Ordinal);
        Assert.Contains(@"decision\u000D\u000Acode", exception.Message, StringComparison.Ordinal);
        Assert.Equal(objectName, assessment.ObjectName);
        Assert.Equal(analysisCode, assessment.AnalysisCode);
        Assert.Equal(decisionCode, assessment.DecisionCode);
    }

    /// <summary>Separates accepted ready preflight from the unsupported postflight throwing contract.</summary>
    /// <param name="postflight">Whether the report is a blocked postflight instead of ready preflight.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowIfBlocked_RejectsPostflightAndAcceptsReadyPreflight(
        bool postflight
    )
    {
        // Arrange
        var report = CreateReport(
            postflight
                ? SafeMigrationReportMode.Postflight
                : SafeMigrationReportMode.Preflight,
            postflight
                ? SafeMigrationReportStatus.Blocked
                : SafeMigrationReportStatus.Ready,
            []);

        // Act
        var exception = Record.Exception(report.ThrowIfBlocked);

        // Assert
        if (postflight)
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
        else
        {
            Assert.Null(exception);
        }
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
