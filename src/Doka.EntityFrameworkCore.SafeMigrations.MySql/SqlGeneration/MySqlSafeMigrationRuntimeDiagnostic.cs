namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Correlates one authored operation with its native assertion without storing live row evidence.</summary>
internal sealed class MySqlSafeMigrationRuntimeDiagnostic
{
    internal const string MarkerPrefix = "/*doka_sm_runtime:";
    private const string MarkerSuffix = "*/";

    /// <summary>Creates a bounded native-error correlation and retains only authored generation context.</summary>
    /// <param name="operation">The operation owning the native scope.</param>
    /// <param name="ordinal">Its ordinal in the provider's generation call, not a migration identifier.</param>
    /// <param name="plan">The immutable proof shapes whose materialized facts identify precise failures.</param>
    internal MySqlSafeMigrationRuntimeDiagnostic(
        SafeMigrationOperation operation,
        int ordinal,
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        Context = SafeMigrationRuntimeDiagnosticContext.Create(operation);
        Ordinal = ordinal;
        Correlation = Guid.NewGuid().ToString("N");
        var ordinalText = ordinal.ToString(CultureInfo.InvariantCulture);

        // WHY: Keep the nonce fresh while avoiding the five-piece concatenation
        // argument array on every generated operation. The marker text is exact.
        Marker = string.Create(MarkerPrefix.Length + ordinalText.Length + 1 + Correlation.Length + MarkerSuffix.Length,
            (Ordinal: ordinalText, Correlation), static (destination, state) =>
            {
                var offset = 0;
                MarkerPrefix.AsSpan().CopyTo(destination);
                offset += MarkerPrefix.Length;
                state.Ordinal.AsSpan().CopyTo(destination[offset..]);
                offset += state.Ordinal.Length;
                destination[offset++] = ':';
                state.Correlation.AsSpan().CopyTo(destination[offset..]);
                offset += state.Correlation.Length;
                MarkerSuffix.AsSpan().CopyTo(destination[offset..]);
            });
        QualificationCode = plan.CurrentDatabaseQualificationFailureCode;
        UnsupportedCode = plan.UnsupportedCode;
        HasRequiredAddStatus = plan.RequiredColumnAddPlan is not null;
        RequiredAddGuardName = plan.RequiredColumnAddPlan?.GuardName;
        RequiredAddOwner = (operation.Intent as EnsureColumnIntent)?.Table;
        HasNullabilityProof = plan.NullabilityDataProbe is not null;
        HasTransitionDataProof = plan.DataProbe is not null;
    }

    /// <summary>
    /// Gets captured identity, policy and shape without retaining SQL, defaults or managed row matrices.
    /// </summary>
    internal SafeMigrationRuntimeDiagnosticContext Context { get; }

    /// <summary>Gets the provider generation ordinal.</summary>
    internal int Ordinal { get; }

    /// <summary>Gets the 32-character ASCII value preserved by both engines' bounded duplicate-key payloads.</summary>
    internal string Correlation { get; }

    /// <summary>Gets the leading ownership marker used to attach a decorator to the exact generated command.</summary>
    internal string Marker { get; }

    private string? QualificationCode { get; }
    private string? UnsupportedCode { get; }
    private bool HasRequiredAddStatus { get; }
    private bool HasNullabilityProof { get; }
    private bool HasTransitionDataProof { get; }
    private string? RequiredAddGuardName { get; }
    private string? RequiredAddOwner { get; }

    /// <summary>Maps a bounded native payload slot back to an evidenced, non-sensitive reason code.</summary>
    /// <param name="slot">The slot emitted by the already materialized operation-local facts.</param>
    /// <returns>The exact reason code, or null when only the guard category was established.</returns>
    internal string? GetReasonCode(int slot) => slot switch
    {
        0 => null,
        1 => QualificationCode,
        2 => UnsupportedCode,
        3 when HasRequiredAddStatus => "column_atomic_add_guard_conflict",
        4 when HasRequiredAddStatus => "column_atomic_add_unavailable",
        5 when HasRequiredAddStatus => "column_set_null_dependency",
        6 when HasRequiredAddStatus => "column_atomic_add_resume",
        7 when HasRequiredAddStatus => "required_column_backfill_missing",
        8 when HasNullabilityProof => "column_nullability_data_blocked",
        9 when HasTransitionDataProof => "column_transition_data_blocked",
        10 when HasRequiredAddStatus => "column_atomic_add_check_failed",
        _ => null,
    };

    /// <summary>Uses completed proof variables only; reporting must not issue another catalog or row query.</summary>
    internal string BuildReasonSlotExpression()
    {
        if (QualificationCode is null && UnsupportedCode is null && !HasRequiredAddStatus
            && !HasNullabilityProof && !HasTransitionDataProof)
        {
            return "0";
        }

        var builder = new StringBuilder("CASE ");
        if (QualificationCode is not null)
        {
            builder.Append("WHEN @doka_sm_state = 'unsupported' AND NOT @doka_sm_prerequisite_ok THEN 1 ");
        }

        if (HasRequiredAddStatus)
        {
            builder.Append("WHEN @doka_sm_add_status = 'conflict' THEN 3 ")
                .Append("WHEN @doka_sm_add_status = 'unavailable' THEN 4 ")
                .Append("WHEN @doka_sm_add_status = 'set_null' THEN 5 ")
                .Append("WHEN @doka_sm_add_status = 'staged' AND @doka_sm_state = 'data_blocked' THEN 6 ")
                .Append("WHEN @doka_sm_add_status = 'fresh' AND @doka_sm_state = 'data_blocked' THEN 7 ");
        }

        if (HasNullabilityProof)
        {
            builder.Append("WHEN @doka_sm_state = 'data_blocked' AND @doka_sm_nullability_blocked THEN 8 ");
        }

        if (HasTransitionDataProof)
        {
            builder.Append("WHEN @doka_sm_state = 'data_blocked' AND @doka_sm_data_blocked THEN 9 ");
        }

        if (UnsupportedCode is not null)
        {
            builder.Append("WHEN @doka_sm_state = 'unsupported' THEN 2 ");
        }

        return builder.Append("ELSE 0 END").ToString();
    }

    /// <summary>Recognizes only a complete leading marker and rejects stale or malformed ownership.</summary>
    /// <param name="sql">The original native command text, including scoped fragments.</param>
    /// <param name="ordinal">The parsed operation ordinal.</param>
    /// <param name="correlation">The bounded correlation span without a copied SQL payload.</param>
    /// <returns>Whether an exact authored marker is available.</returns>
    internal static bool TryReadMarker(string sql, out int ordinal, out ReadOnlySpan<char> correlation)
    {
        ordinal = -1;
        correlation = default;
        var text = sql.AsSpan().TrimStart();
        if (!text.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        text = text[MarkerPrefix.Length..];
        var separator = text.IndexOf(':');
        if (separator is < 1 or > 10
            || (separator > 1 && text[0] == '0')
            || !int.TryParse(text[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
            || ordinal < 0
            || text.Length < separator + 35)
        {
            return false;
        }

        var token = text.Slice(separator + 1, 32);
        if (!text[(separator + 33)..].StartsWith(MarkerSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in token)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        correlation = token;

        return true;
    }

    /// <summary>
    /// Reads only the exact native duplicate assertion, not arbitrary similarly named user constraints.
    /// </summary>
    /// <param name="failure">The exception returned after the original scoped command finished its cleanup.</param>
    /// <param name="guardCode">The exact owned guard constraint.</param>
    /// <param name="reasonSlot">The bounded materialized diagnostic slot.</param>
    /// <returns>Whether the SQLSTATE, correlation value and key all identify this operation.</returns>
    internal bool TryReadFailure(MySqlException failure, out string guardCode, out int reasonSlot)
        => TryReadFailure(failure.Number, failure.SqlState, failure.Message, out guardCode, out reasonSlot);

    /// <summary>Parses the closed native transport independently of exception construction.</summary>
    /// <param name="number">The original server error number.</param>
    /// <param name="sqlState">The original server SQLSTATE.</param>
    /// <param name="message">The original bounded server message, not a user-supplied diagnostic.</param>
    /// <param name="guardCode">The exact owned guard constraint.</param>
    /// <param name="reasonSlot">The materialized diagnostic slot.</param>
    /// <returns>Whether all correlation and native-error invariants hold.</returns>
    internal bool TryReadFailure(int number, string? sqlState, string message, out string guardCode, out int reasonSlot)
    {
        guardCode = string.Empty;
        reasonSlot = 0;
        if (TryReadRequiredAddCheckFailure(number, sqlState, message))
        {
            guardCode = "doka_sm_data_blocked";
            reasonSlot = 10;

            return true;
        }

        if (number != 1062 || !StringComparer.Ordinal.Equals(sqlState, "23000"))
        {
            return false;
        }

        var remaining = message.AsSpan();
        const string prefix = "Duplicate entry '";
        if (!remaining.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        remaining = remaining[prefix.Length..];
        if (!remaining.StartsWith(Correlation, StringComparison.Ordinal)
            || remaining.Length < Correlation.Length + 8
            || remaining[Correlation.Length] != '-')
        {
            return false;
        }

        remaining = remaining[(Correlation.Length + 1)..];
        var slotEnd = remaining.IndexOf('\'');
        if (slotEnd is < 1 or > 2
            || (slotEnd > 1 && remaining[0] == '0')
            || !int.TryParse(remaining[..slotEnd], NumberStyles.None, CultureInfo.InvariantCulture, out reasonSlot)
            || reasonSlot is < 0 or > 9
            || (reasonSlot != 0 && GetReasonCode(reasonSlot) is null))
        {
            return false;
        }

        const string keySeparator = "' for key '";
        if (!remaining[slotEnd..].StartsWith(keySeparator, StringComparison.Ordinal))
        {
            return false;
        }

        remaining = remaining[(slotEnd + keySeparator.Length)..];
        if (remaining.StartsWith("__doka_sm_assert.", StringComparison.Ordinal))
        {
            remaining = remaining["__doka_sm_assert.".Length..];
        }

        if (remaining.IsEmpty || remaining[^1] != '\'')
        {
            return false;
        }

        var key = remaining[..^1];
        foreach (var candidate in GuardCodes)
        {
            if (key.Equals(candidate, StringComparison.Ordinal) && ReasonSlotAppliesToGuard(reasonSlot, candidate))
            {
                guardCode = candidate;

                return true;
            }
        }

        return false;
    }

    /// <summary>Recognizes the certified atomic NOT NULL barrier by its entire owner-and-target fingerprint.</summary>
    /// <remarks>There is no namespace-prefix or error-number-only match and no inferred writer timing.</remarks>
    private bool TryReadRequiredAddCheckFailure(int number, string? sqlState, string message)
    {
        if (RequiredAddGuardName is null || RequiredAddOwner is null)
        {
            return false;
        }

        // WHY: The admitted native two-stage body and fresh status own this
        // exact CHECK contract. Its durable recovery name cannot use the
        // per-generation nonce; foreign or merely similarly named CHECKs
        // are not evidence of this target and must keep their native errors.
        if (number == 3819 && StringComparer.Ordinal.Equals(sqlState, "HY000"))
        {
            return StringComparer.Ordinal.Equals(message, "Check constraint '" + RequiredAddGuardName
                + "' is violated.");
        }

        var prefix = "CONSTRAINT `" + RequiredAddGuardName + "` failed for `";

        return number == 4025 && StringComparer.Ordinal.Equals(sqlState, "23000")
            && message.StartsWith(prefix, StringComparison.Ordinal)
            && message.AsSpan(prefix.Length).EndsWith("`.`" + RequiredAddOwner + "`", StringComparison.Ordinal);
    }

    /// <summary>Rejects slot/category combinations that the selected runtime branch cannot produce.</summary>
    internal static bool ReasonSlotAppliesToGuard(int slot, string guard) => slot == 0 || (guard switch
    {
        "doka_sm_unsupported" => slot is >= 1 and <= 5,
        "doka_sm_different" => slot == 3,
        "doka_sm_data_blocked" => slot is >= 6 and <= 10,
        _ => false,
    });

    /// <summary>Gets the immutable existing native constraint vocabulary.</summary>
    internal static IReadOnlyList<string> GuardCodes { get; } = Array.AsReadOnly<string>(
        ["doka_sm_different", "doka_sm_unsupported", "doka_sm_data_blocked", "doka_sm_prerequisite_missing",
            "doka_sm_postcondition"]);
}
