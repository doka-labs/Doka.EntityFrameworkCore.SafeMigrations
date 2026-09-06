namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Classifies the known execution shape of an accepted automatic repair.</summary>
public enum SafeMigrationOperationalImpact
{
    /// <summary>No automatic repair is planned for the assessment.</summary>
    NotApplicable = 0,

    /// <summary>The operation is data-safe, but the server may copy or rebuild the table.</summary>
    TableRewritePossible = 1,

    /// <summary>The provider cannot prove a more precise operational shape.</summary>
    Unknown = 2,
}

/// <summary>Describes one bounded, typed difference between live and expected metadata.</summary>
public sealed class SafeMigrationFacetDifference
{
    internal const int MaximumDifferenceCount = 16;

    internal const int MaximumValueLength = 256;

    /// <summary>Initializes one privacy-safe facet difference.</summary>
    /// <param name="facet">The stable low-cardinality facet code.</param>
    /// <param name="expected">The bounded expected metadata.</param>
    /// <param name="actual">The bounded live metadata.</param>
    public SafeMigrationFacetDifference(
        string facet,
        string expected,
        string actual
    )
    {
        ValidateFacet(facet);
        ValidateValue(expected, nameof(expected));
        ValidateValue(actual, nameof(actual));

        Facet = facet;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>Gets the stable low-cardinality facet code.</summary>
    public string Facet { get; }

    /// <summary>Gets the bounded expected metadata.</summary>
    public string Expected { get; }

    /// <summary>Gets the bounded live metadata.</summary>
    public string Actual { get; }

    private static void ValidateFacet(
        string facet
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facet);

        if (facet.Length > 64
            || facet.Any(static character => character is not (>= 'a' and <= 'z') and not '_'))
        {
            throw new ArgumentException(
                "A diagnostic facet must be a lowercase ASCII identifier no longer than 64 characters.",
                nameof(facet));
        }
    }

    private static void ValidateValue(
        string value,
        string parameterName
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length > MaximumValueLength
            || value.Any(static character => character is < ' ' or > '~'))
        {
            throw new ArgumentException(
                $"A diagnostic value must contain at most {MaximumValueLength} printable ASCII characters.",
                parameterName);
        }
    }
}

/// <summary>Represents a blocked SafeMigrations preflight with its complete immutable report.</summary>
public sealed class SafeMigrationPreflightException : InvalidOperationException
{
    private const int MaximumSummaryFieldLength = 128;

    /// <summary>Initializes a blocked-preflight exception.</summary>
    /// <param name="report">The immutable blocked preflight report.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="report"/> is not a blocked preflight report.
    /// </exception>
    public SafeMigrationPreflightException(
        SafeMigrationRunReport report
    ) : base(CreateMessage(report))
    {
        Report = report;
    }

    /// <summary>Gets the complete immutable report that caused the failure.</summary>
    public SafeMigrationRunReport Report { get; }

    private static string CreateMessage(
        SafeMigrationRunReport report
    )
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.Mode != SafeMigrationReportMode.Preflight
            || report.Status != SafeMigrationReportStatus.Blocked)
        {
            throw new ArgumentException(
                "A preflight exception requires a blocked preflight report.",
                nameof(report));
        }

        SafeMigrationAssessment? first = null;
        var blockedCount = 0;
        foreach (var assessment in report.Assessments)
        {
            if (assessment.Action is not (SafeMigrationAction.RejectDifferent
                or SafeMigrationAction.RejectUnsupported
                or SafeMigrationAction.RejectDataBlocked
                or SafeMigrationAction.RejectPrerequisiteMissing))
            {
                continue;
            }

            first ??= assessment;
            blockedCount++;
        }

        if (first is null)
        {
            return "SafeMigrations preflight is blocked. Inspect the attached report for details.";
        }

        var objectName = first.ObjectName is null
            ? string.Empty
            : $" {BoundForMessage(first.ObjectName)}";

        var difference = first.Differences.Count == 0
            ? null
            : first.Differences[0];

        var differenceText = difference is null
            ? string.Empty
            : $"; {difference.Facet}: expected {BoundForMessage(difference.Expected)}, "
            + $"actual {BoundForMessage(difference.Actual)}";

        return $"SafeMigrations preflight blocked {blockedCount} operations. "
            + $"First conflict: operation {first.Ordinal}, {first.OperationKind}{objectName}; "
            + $"analysis {BoundForMessage(first.AnalysisCode)}; "
            + $"decision {BoundForMessage(first.DecisionCode)}{differenceText}. "
            + "Inspect the attached report for all conflicts.";
    }

    private static string BoundForMessage(
        string value
    )
    {
        var isTruncated = value.Length > MaximumSummaryFieldLength;
        var contentLength = isTruncated
            ? MaximumSummaryFieldLength - 3
            : value.Length;

        return string.Create(
            contentLength + (isTruncated ? 3 : 0),
            (Value: value, ContentLength: contentLength, IsTruncated: isTruncated),
            static (destination, state) =>
            {
                for (var index = 0; index < state.ContentLength; index++)
                {
                    var character = state.Value[index];

                    // WHY: Public constructors retain their established
                    // compatibility, but exception text must not let caller-
                    // supplied control characters forge terminal or log lines.
                    destination[index] = character is >= ' ' and <= '~'
                        ? character
                        : '?';
                }

                if (state.IsTruncated)
                {
                    "...".AsSpan().CopyTo(destination[state.ContentLength..]);
                }
            });
    }
}
