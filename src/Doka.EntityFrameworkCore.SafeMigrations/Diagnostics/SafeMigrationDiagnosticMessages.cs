namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Renders bounded, payload-free explanations for reports and owned runtime guards.</summary>
/// <remarks>
/// Formatting is explicit and performs no I/O. Native guard categories are not evidence of a
/// particular row defect; detailed causes are used only when the classifier established them.
/// </remarks>
public static partial class SafeMigrationDiagnosticMessages
{
    private const int MaximumMessageLength = 2048;
    private const int MaximumIdentityLength = 1024;
    private const int MaximumIdentifierLength = 128;
    private const int MaximumDisplayedColumns = 4;

    /// <summary>Formats an owned runtime rejection without reading catalog or row data.</summary>
    /// <remarks>
    /// Recognized guard categories are preserved; the former presentation alias
    /// <c>doka_sm_postcondition_failed</c> is rendered as <c>doka_sm_postcondition</c>.
    /// Unrecognized guard codes are replaced with the display fallback <c>doka_sm_unknown</c>,
    /// which is not a native error category and does not establish exception ownership.
    /// </remarks>
    /// <param name="operation">The exact operation associated with the owned guard.</param>
    /// <param name="guardCode">The stable owned guard category, not a native provider error message.</param>
    /// <param name="operationOrdinal">The known operation ordinal, or null when unavailable.</param>
    /// <param name="diagnosticReasonCode">A reason established by the selected classifier branch, or null.</param>
    /// <returns>
    /// A printable ASCII diagnostic of at most 2048 characters, starting with the normalized guard identity.
    /// </returns>
    public static string FormatRuntime(
        SafeMigrationOperation operation,
        string guardCode,
        int? operationOrdinal = null,
        string? diagnosticReasonCode = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(guardCode);
        ValidateOrdinal(operationOrdinal);

        return FormatCapturedRuntime(SafeMigrationRuntimeDiagnosticContext.Create(operation),
            guardCode, operationOrdinal, diagnosticReasonCode);
    }

    /// <summary>Formats a runtime rejection with its actual classified analysis and planner decision.</summary>
    /// <param name="operation">The exact guarded operation.</param>
    /// <param name="analysis">The completed provider analysis, not an invented live observation.</param>
    /// <param name="decision">The actual planner decision.</param>
    /// <param name="operationOrdinal">The known operation ordinal, or null when unavailable.</param>
    /// <returns>A bounded diagnostic retaining the classifier's established reason.</returns>
    /// <exception cref="ArgumentException">The supplied decision does not reject execution.</exception>
    public static string FormatRuntimeFromAnalysis(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision,
        int? operationOrdinal = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(decision);
        ValidateOrdinal(operationOrdinal);

        if (!decision.Action.RejectsExecution())
        {
            throw new ArgumentException(
                "A runtime rejection diagnostic requires a rejecting decision.", nameof(decision));
        }

        var guardCode = decision.Action switch
        {
            SafeMigrationAction.RejectDifferent => "doka_sm_different",
            SafeMigrationAction.RejectUnsupported => "doka_sm_unsupported",
            SafeMigrationAction.RejectDataBlocked => "doka_sm_data_blocked",
            SafeMigrationAction.RejectPrerequisiteMissing => "doka_sm_prerequisite_missing",
            _ => "doka_sm_unknown",
        };

        var builder = new StringBuilder(256);
        AppendRuntimeCore(builder, operation.Policy, SafeMigrationOperationIdentity.Create(operation.Intent),
            guardCode, operationOrdinal,
            GetExplanation(operation.Intent, analysis.ObservedState, analysis.Code, decision.Code));
        builder.Append("; analysis=").Append(BoundForDisplay(analysis.Code));
        builder.Append("; decision=").Append(BoundForDisplay(decision.Code));
        ClampLength(builder, 0, MaximumMessageLength);

        return builder.ToString();
    }

    /// <summary>Formats a postcondition failure without inventing an approval decision or rollback guarantee.</summary>
    /// <param name="operation">The executed operation whose postcondition failed.</param>
    /// <param name="analysis">The actual post-execution classification.</param>
    /// <param name="operationOrdinal">The known operation ordinal, or null when unavailable.</param>
    /// <returns>A bounded postcondition diagnostic retaining the actual classifier code.</returns>
    internal static string FormatPostcondition(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        int? operationOrdinal
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(analysis);
        ValidateOrdinal(operationOrdinal);

        if (analysis.PostconditionSatisfied)
        {
            throw new ArgumentException("A postcondition failure diagnostic requires an unsatisfied condition.",
                nameof(analysis));
        }

        var builder = new StringBuilder(256);
        AppendRuntime(builder, operation, SafeMigrationOperationIdentity.Create(operation.Intent),
            "doka_sm_postcondition", operationOrdinal);
        builder.Append("; analysis=").Append(BoundForDisplay(analysis.Code));
        ClampLength(builder, 0, MaximumMessageLength);

        return builder.ToString();
    }

    /// <summary>Formats an assessment independently of its surrounding report or migration source.</summary>
    /// <param name="assessment">The immutable observed, projected or deferred assessment.</param>
    /// <returns>A printable ASCII summary of at most 2048 characters with an honest owner and reason.</returns>
    public static string FormatAssessment(SafeMigrationAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        var builder = new StringBuilder(256);
        builder.Append("operation ").Append(assessment.Ordinal.ToString(CultureInfo.InvariantCulture)).Append(' ');
        if (assessment.Identity is { } identity)
        {
            AppendIdentity(builder, identity);
        }
        else
        {
            AppendOperationKind(builder, assessment.OperationKind);
            builder.Append(" owner=<unavailable> object=");
            AppendIdentifier(builder, assessment.ObjectName ?? "<unavailable>");
        }

        builder.Append("; action=").Append(assessment.Action);
        AppendExplanation(builder, assessment.Explanation);
        if (assessment.MigrationId is { } migrationId)
        {
            builder.Append("; migration=").Append(BoundForDisplay(migrationId));
        }

        if (assessment.Policy is { } policy)
        {
            builder.Append("; policy=").Append(policy);
        }

        if (assessment.DeferredOrigin is { } origin)
        {
            builder.Append("; deferred-origin migration=");
            builder.Append(origin.MigrationId is null ? "<unavailable>" : BoundForDisplay(origin.MigrationId));
            builder.Append(" operation ").Append(origin.OperationOrdinal.ToString(CultureInfo.InvariantCulture));
            builder.Append(" type=").Append(BoundForDisplay(origin.OperationType));
        }

        builder.Append("; analysis=").Append(BoundForDisplay(assessment.AnalysisCode));
        builder.Append("; decision=").Append(BoundForDisplay(assessment.DecisionCode));
        for (var index = 0; index < Math.Min(assessment.Differences.Count, 2); index++)
        {
            var difference = assessment.Differences[index];
            builder.Append("; ").Append(BoundForDisplay(difference.Facet, 64));
            if (CanDisplayFacetValues(difference.Facet))
            {
                builder.Append(": expected ").Append(BoundForDisplay(difference.Expected, 96));
                builder.Append(", actual ").Append(BoundForDisplay(difference.Actual, 96));
            }
            else
            {
                // WHY: Public callers can construct printable differences containing SQL or
                // comments. Only explicitly safe schema facets belong in ordinary log text.
                builder.Append(": values omitted; inspect protected report evidence");
            }

        }

        ClampLength(builder, 0, MaximumMessageLength);

        return builder.ToString();
    }

    /// <summary>Formats qualified owners, named members, FK endpoints and rename targets.</summary>
    /// <param name="identity">The authored structural identity.</param>
    /// <returns>A printable ASCII label of at most 1024 characters with explicit truncation.</returns>
    public static string FormatIdentity(SafeMigrationOperationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var builder = new StringBuilder(128);
        AppendIdentity(builder, identity);

        return builder.ToString();
    }

    /// <summary>Appends a bounded runtime diagnostic while reusing captured immutable identity metadata.</summary>
    /// <param name="destination">The caller-owned generation buffer.</param>
    /// <param name="operation">The associated safe operation.</param>
    /// <param name="identity">The identifier-only identity captured once by the caller.</param>
    /// <param name="guardCode">The owned guard category.</param>
    /// <param name="operationOrdinal">The known operation ordinal.</param>
    /// <param name="diagnosticReasonCode">The branch-established reason, when available.</param>
    internal static void AppendRuntime(
        StringBuilder destination,
        SafeMigrationOperation operation,
        SafeMigrationOperationIdentity identity,
        string guardCode,
        int? operationOrdinal = null,
        string? diagnosticReasonCode = null
    )
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(guardCode);
        ValidateOrdinal(operationOrdinal);

        var context = new SafeMigrationRuntimeDiagnosticContext(identity, operation.Policy,
            operation.Intent is EnsureColumnIntent column
                && !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(column.Definition));

        AppendCapturedRuntime(destination, context, guardCode, operationOrdinal, diagnosticReasonCode);
    }

    /// <summary>Formats an owned runtime failure using payload-free retained command metadata.</summary>
    /// <param name="context">The exact immutable structural and policy facts captured during generation.</param>
    /// <param name="guardCode">The positively identified owned guard category.</param>
    /// <param name="operationOrdinal">The known operation ordinal, or null when unavailable.</param>
    /// <param name="diagnosticReasonCode">The selected classifier code, or null when unavailable.</param>
    /// <returns>The same bounded text as operation-based formatting without retaining an intent.</returns>
    internal static string FormatCapturedRuntime(
        SafeMigrationRuntimeDiagnosticContext context,
        string guardCode,
        int? operationOrdinal = null,
        string? diagnosticReasonCode = null
    )
    {
        var builder = new StringBuilder(256);
        AppendCapturedRuntime(builder, context, guardCode, operationOrdinal, diagnosticReasonCode);

        return builder.ToString();
    }

    private static void AppendCapturedRuntime(
        StringBuilder destination,
        SafeMigrationRuntimeDiagnosticContext context,
        string guardCode,
        int? operationOrdinal,
        string? diagnosticReasonCode
    )
    {
        ArgumentNullException.ThrowIfNull(guardCode);
        ValidateOrdinal(operationOrdinal);

        if (context.Identity is null)
        {
            throw new ArgumentException(
                "A runtime diagnostic context must contain a captured identity.", nameof(context));
        }

        var knownGuard = NormalizeGuardCode(guardCode);
        var explanation = GetRuntimeExplanation(context, knownGuard, diagnosticReasonCode);
        var start = destination.Length;
        AppendRuntimeCore(destination, context.Policy, context.Identity, knownGuard, operationOrdinal, explanation);
        if (diagnosticReasonCode is { } code
            && code != explanation.ReasonCode
            && IsMachineCode(code))
        {
            // WHY: A future or provider-specific stable code remains useful machine evidence
            // even when Core cannot give a more specific diagnosis. Native text is not a code.
            destination.Append("; analysis=").Append(code);
            ClampLength(destination, start, MaximumMessageLength);
        }

    }

    /// <summary>Escapes diagnostic identifiers without exposing arbitrary payloads or control characters.</summary>
    /// <param name="value">A structural identifier or bounded machine code, never SQL or a row value.</param>
    /// <param name="maximumLength">The maximum emitted ASCII length, including truncation marks.</param>
    /// <returns>The original printable ASCII string when safe, otherwise a bounded escaped representation.</returns>
    internal static string BoundForDisplay(string value, int maximumLength = MaximumIdentifierLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 4);

        var escapedLength = 0;
        var requiresEscaping = false;
        foreach (var character in value)
        {
            var width = GetDisplayWidth(character);
            requiresEscaping |= width != 1;
            escapedLength += width;
            if (escapedLength > maximumLength)
            {
                break;
            }

        }

        if (!requiresEscaping && escapedLength <= maximumLength && value.Length <= maximumLength)
        {
            return value;
        }

        // WHY: Scan and copy only the bounded prefix. Hostile oversized identifiers must not
        // allocate a same-size temporary string merely to produce a short error message.
        var truncated = escapedLength > maximumLength;
        var writableLength = maximumLength - (truncated ? 3 : 0);
        var builder = new StringBuilder(Math.Min(maximumLength, 256));
        foreach (var character in value)
        {
            if (builder.Length + GetDisplayWidth(character) > writableLength)
            {
                break;
            }

            AppendEscapedCharacter(builder, character);
        }

        if (truncated)
        {
            builder.Append("...");
        }

        return builder.ToString();
    }

    private static void AppendRuntimeCore(
        StringBuilder destination,
        SafeMigrationPolicy policy,
        SafeMigrationOperationIdentity identity,
        string guardCode,
        int? ordinal,
        SafeMigrationDiagnosticExplanation explanation
    )
    {
        var start = destination.Length;
        destination.Append(guardCode).Append(": SafeMigrations rejected ");
        AppendIdentity(destination, identity);
        if (ordinal is { } value)
        {
            destination.Append("; operation ").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        destination.Append("; policy=").Append(policy);
        AppendExplanation(destination, explanation);
        ClampLength(destination, start, MaximumMessageLength);
    }

    private static void AppendExplanation(StringBuilder builder, SafeMigrationDiagnosticExplanation explanation)
    {
        builder.Append("; reasonCode=").Append(explanation.ReasonCode);
        builder.Append("; reason=").Append(explanation.Reason);
        if (explanation.NextStep.Length != 0)
        {
            builder.Append("; next=").Append(explanation.NextStep);
        }

    }

    private static void AppendIdentity(StringBuilder builder, SafeMigrationOperationIdentity identity)
    {
        var start = builder.Length;
        AppendOperationKind(builder, identity.OperationKind);
        builder.Append(" owner=");
        if (identity.Table is { } table)
        {
            AppendOwner(builder, identity.Schema, table);
        }
        else if (identity.Schema is { } schema)
        {
            builder.Append("schema ");
            AppendIdentifier(builder, schema);
        }
        else
        {
            builder.Append("<unavailable>");
        }

        builder.Append(" object=");
        AppendIdentifier(builder, identity.ObjectName);
        AppendColumns(builder, " columns=", identity.Columns);
        if (identity.PrincipalTable is { } principalTable)
        {
            builder.Append(" principal=");
            AppendOwner(builder, identity.PrincipalSchema, principalTable);
            AppendColumns(builder, " principalColumns=", identity.PrincipalColumns);
        }

        if (identity.NewTable is { } newTable)
        {
            builder.Append(" target=");
            AppendOwner(builder, identity.NewSchema ?? identity.Schema, newTable);
        }

        if (identity.NewObjectName is { } newObjectName)
        {
            builder.Append(" targetObject=");
            AppendIdentifier(builder, newObjectName);
        }

        ClampLength(builder, start, MaximumIdentityLength);
    }

    private static void AppendOperationKind(StringBuilder builder, SafeMigrationOperationKind? kind)
    {
        builder.Append(kind?.ToString() ?? "ProviderOwnedOperation");
    }

    private static void AppendOwner(StringBuilder builder, string? schema, string table)
    {
        if (schema is null)
        {
            builder.Append("<unqualified>");
        }
        else
        {
            AppendIdentifier(builder, schema);
        }

        builder.Append('.');
        AppendIdentifier(builder, table);
    }

    private static void AppendIdentifier(StringBuilder builder, string identifier)
    {
        var bounded = BoundForDisplay(identifier);
        builder.Append('[');
        foreach (var character in bounded)
        {
            builder.Append(character);
            if (character == ']')
            {
                builder.Append(']');
            }

        }

        builder.Append(']');
    }

    private static void AppendColumns(StringBuilder builder, string label, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            return;
        }

        builder.Append(label).Append('(');
        for (var index = 0; index < Math.Min(columns.Count, MaximumDisplayedColumns); index++)
        {
            if (index != 0)
            {
                builder.Append(',');
            }

            AppendIdentifier(builder, columns[index]);
        }

        if (columns.Count > MaximumDisplayedColumns)
        {
            builder.Append(",...");
        }

        builder.Append(')');
    }

    private static int GetDisplayWidth(char character) => character switch
    {
        '\\' => 2,
        >= ' ' and <= '~' => 1,
        _ => 6,
    };

    private static void AppendEscapedCharacter(StringBuilder builder, char character)
    {
        switch (character)
        {
            case '\\': builder.Append("\\\\"); break;
            case >= ' ' and <= '~': builder.Append(character); break;
            default:
                const string digits = "0123456789ABCDEF";
                builder.Append("\\u");
                builder.Append(digits[(character >> 12) & 15]);
                builder.Append(digits[(character >> 8) & 15]);
                builder.Append(digits[(character >> 4) & 15]);
                builder.Append(digits[character & 15]);
                break;
        }

    }

    private static void ClampLength(StringBuilder builder, int start, int maximumLength)
    {
        if (builder.Length - start > maximumLength)
        {
            // WHY: Identifiers are escaped before the aggregate cap is applied.
            // A second hard cut would turn an intact Unicode or backslash unit into misleading text.
            builder.Length = GetDisplayPrefixLength(builder, start, start + maximumLength - 3);
            builder.Append("...");
        }

    }

    /// <summary>Finds a complete display-escape boundary without flattening or modifying a bounded buffer.</summary>
    /// <remarks>
    /// The range must start on a logical boundary in Core's printable ASCII display text.
    /// SQL escaping and native formatting expansion are applied only after selecting this boundary.
    /// </remarks>
    /// <param name="message">The bounded buffer containing complete display escapes.</param>
    /// <param name="start">The first logical character of the range.</param>
    /// <param name="end">The proposed exclusive end, within the original buffer.</param>
    /// <returns>The absolute exclusive end, moved back only when it would split a display escape.</returns>
    internal static int GetDisplayPrefixLength(StringBuilder message, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, message.Length);

        // WHY: A Unicode display unit contains six ASCII characters. Only the
        // last five positions can contain its incomplete start. Backslash-run
        // parity distinguishes an escape from text after a literal backslash.
        for (var offset = end - 1; offset >= Math.Max(start, end - 5); offset--)
        {
            if (message[offset] != '\\')
            {
                continue;
            }

            var runStart = offset;
            while (runStart > start && message[runStart - 1] == '\\')
            {
                runStart--;
            }

            if ((offset - runStart + 1) % 2 == 0)
            {
                return end;
            }

            var width = offset + 1 < message.Length && message[offset + 1] == 'u' ? 6 : 2;

            return offset + width > end ? offset : end;
        }

        return end;
    }

    private static bool CanDisplayFacetValues(string facet) => facet is
        "column_store_type" or "column_max_length" or "column_nullability" or "column_precision"
        or "column_scale" or "column_unicode" or "column_fixed_length" or "column_collation"
        or "column_row_version" or "primary_key_key_semantics" or "unique_key_semantics"
        or "foreign_key_delete_behavior" or "foreign_key_update_behavior";

    private static bool IsMachineCode(string value)
    {
        if (value.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_')
            {
                return false;
            }

        }

        return true;
    }

    private static void ValidateOrdinal(int? operationOrdinal)
    {
        if (operationOrdinal is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationOrdinal),
                "A known operation ordinal must be non-negative; use null when it is unavailable.");
        }

    }
}
