namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    private const string ScriptReasonSlotExpression = "CAST(SUBSTRING(@doka_sm_diag, 34) AS UNSIGNED)";
    /// <summary>Publishes a bounded result row before a raw script raises its unchanged native assertion.</summary>
    /// <param name="diagnostic">The known authored operation, ordinal and proof-code vocabulary.</param>
    /// <param name="postcondition">Whether this is the final verification rather than the decision gate.</param>
    /// <returns>A conditional result row containing actionable context without row values or SQL payloads.</returns>
    private static string BuildDiagnosticResultSql(MySqlSafeMigrationRuntimeDiagnostic diagnostic, bool postcondition)
    {
        // WHY: PREPARE on MySQL cannot carry SIGNAL. A failure-only row gives
        // direct script clients the complete diagnosis before the bounded native
        // duplicate error. Normal managed execution does not format or emit it.
        var builder = new StringBuilder("SELECT 'safe_migration_runtime_diagnostic' AS documentKind, ");
        builder.Append(diagnostic.Ordinal.ToString(CultureInfo.InvariantCulture)).Append(" AS operationOrdinal, ");
        if (postcondition)
        {
            builder.Append("'doka_sm_postcondition' AS guardCode, 'postcondition_failed' AS reasonCode, ")
                .Append(MessageLiteral(diagnostic, "doka_sm_postcondition", reasonCode: null))
                .Append(" AS message WHERE NOT COALESCE(@doka_sm_post_ok, FALSE);");

            return builder.ToString();
        }

        builder.Append("CASE @doka_sm_action ");
        foreach (var guard in MySqlSafeMigrationRuntimeDiagnostic.GuardCodes)
        {
            if (guard == "doka_sm_postcondition")
            {
                continue;
            }

            builder.Append("WHEN '").Append(ActionForGuard(guard)).Append("' THEN '").Append(guard).Append("' ");
        }

        var genericReason = BuildGenericReasonCodeExpression(diagnostic);
        builder.Append("ELSE NULL END AS guardCode, CASE ").Append(ScriptReasonSlotExpression)
            .Append(" WHEN 0 THEN ")
            .Append(genericReason).Append(' ');
        // WHY: Render the fixed reason-slot protocol in numeric order without materializing a range.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var slot = 1; slot <= 9; slot++)
        {
            if (diagnostic.GetReasonCode(slot) is { } code)
            {
                builder.Append("WHEN ").Append(slot.ToString(CultureInfo.InvariantCulture))
                    .Append(" THEN '")
                    .Append(SafeMigrationDiagnosticMessages.GetRuntimeExplanation(diagnostic.Context,
                        slot <= 5 ? "doka_sm_unsupported" : "doka_sm_data_blocked", code).ReasonCode)
                    .Append("' ");
            }
        }

        builder.Append("ELSE ").Append(genericReason).Append(" END AS reasonCode, CASE @doka_sm_action ");
        foreach (var guard in MySqlSafeMigrationRuntimeDiagnostic.GuardCodes)
        {
            if (guard == "doka_sm_postcondition")
            {
                continue;
            }

            var genericMessage = MessageLiteral(diagnostic, guard, reasonCode: null);
            builder.Append("WHEN '").Append(ActionForGuard(guard)).Append("' THEN ");
            var hasPreciseReason = false;
            // WHY: Render the fixed reason-slot protocol in numeric order without materializing a range.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var slot = 1; slot <= 9; slot++)
            {
                if (MySqlSafeMigrationRuntimeDiagnostic.ReasonSlotAppliesToGuard(slot, guard)
                    && diagnostic.GetReasonCode(slot) is { } code)
                {
                    if (!hasPreciseReason)
                    {
                        builder.Append("CASE ").Append(ScriptReasonSlotExpression).Append(' ');
                        hasPreciseReason = true;
                    }

                    builder.Append("WHEN ").Append(slot.ToString(CultureInfo.InvariantCulture)).Append(" THEN ")
                        .Append(MessageLiteral(diagnostic, guard, code)).Append(' ');
                }
            }

            if (hasPreciseReason)
            {
                builder.Append("ELSE ").Append(genericMessage).Append(" END ");
            }
            else
            {
                builder.Append(genericMessage).Append(' ');
            }
        }

        return builder.Append("ELSE NULL END AS message WHERE @doka_sm_action IN ")
            .Append("('reject_different', 'reject_unsupported', 'reject_data_blocked', 'reject_prerequisite_missing');")
            .ToString();
    }

    /// <summary>Uses the same shared fallback vocabulary as managed errors rather than relabeling raw state as a reason.</summary>
    private static string BuildGenericReasonCodeExpression(MySqlSafeMigrationRuntimeDiagnostic diagnostic)
    {
        var builder = new StringBuilder("CASE @doka_sm_action ");
        foreach (var guard in MySqlSafeMigrationRuntimeDiagnostic.GuardCodes)
        {
            if (guard == "doka_sm_postcondition")
            {
                continue;
            }

            var reason = SafeMigrationDiagnosticMessages.GetRuntimeExplanation(diagnostic.Context, guard);
            builder.Append("WHEN '").Append(ActionForGuard(guard)).Append("' THEN '")
                .Append(reason.ReasonCode).Append("' ");
        }

        return builder.Append("ELSE NULL END").ToString();
    }

    /// <summary>Uses UTF-8 hexadecimal constants so session SQL modes cannot reinterpret diagnostic text.</summary>
    private static string MessageLiteral(MySqlSafeMigrationRuntimeDiagnostic diagnostic, string guard, string? reasonCode)
    {
        var message = SafeMigrationDiagnosticMessages.FormatCapturedRuntime(diagnostic.Context, guard,
            diagnostic.Ordinal, reasonCode);

        return "CONVERT(0x" + Convert.ToHexString(Encoding.UTF8.GetBytes(message)) + " USING utf8mb4)";
    }

    /// <summary>Maps only the existing planner rejection vocabulary to the corresponding stable native key.</summary>
    private static string ActionForGuard(string guard) => guard switch
    {
        "doka_sm_different" => "reject_different",
        "doka_sm_unsupported" => "reject_unsupported",
        "doka_sm_data_blocked" => "reject_data_blocked",
        "doka_sm_prerequisite_missing" => "reject_prerequisite_missing",
        _ => throw new ArgumentOutOfRangeException(nameof(guard)),
    };
}
