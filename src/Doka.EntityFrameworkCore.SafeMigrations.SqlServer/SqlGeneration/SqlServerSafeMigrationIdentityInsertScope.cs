namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Bridges one seed execution's session-state ownership across client attention.</summary>
internal static class SqlServerSafeMigrationIdentityInsertScope
{
    internal const string ContextKey = "Doka:SafeMigrations:IdentityInsert:v1";
    internal const string AcquiredMarkerComment = "/* doka_sm_identity_acquired */";

    /// <summary>Reserves a fresh invocation without modifying an existing caller-owned context value.</summary>
    /// <param name="token">The hexadecimal sixteen-byte invocation token.</param>
    /// <param name="scopeInUseMessage">A bounded authored diagnostic, or null when its owner is unavailable.</param>
    /// <returns>A bounded bootstrap statement that has no IDENTITY_INSERT side effect.</returns>
    internal static string BuildBootstrapSql(string token, string? scopeInUseMessage = null)
        => $"IF SESSION_CONTEXT(N'{ContextKey}') IS NOT NULL "
            + "THROW 51007, " + ScopeInUseLiteral(scopeInUseMessage) + ", 1; "
            + $"EXEC sys.sp_set_session_context @key = N'{ContextKey}', @value = 0x00{token};";

    /// <summary>
    /// Recovers only the exact invocation, without requiring fresh metadata or ALTER on rejection.
    /// </summary>
    /// <param name="token">The hexadecimal sixteen-byte invocation token.</param>
    /// <param name="cleanupSql">The certified OFF statement for the original physical target.</param>
    /// <param name="allowAcquiredIdentity">Whether the provider command could have executed its own ON.</param>
    /// <param name="acquisitionUnprovenMessage">A bounded diagnostic from the original operation, when known.</param>
    /// <returns>Conditional cleanup that rejects uncertain acquisition rather than guessing ownership.</returns>
    internal static string BuildRecoverySql(string token, string cleanupSql, bool allowAcquiredIdentity = true,
        string? acquisitionUnprovenMessage = null)
    {
        var acquiredCleanup = allowAcquiredIdentity
            ? "IF SUBSTRING(@doka_identity_scope, 1, 1) = 0x02 BEGIN " + cleanupSql + " END ELSE "
            : string.Empty;

        return $"DECLARE @doka_identity_owner sql_variant = SESSION_CONTEXT(N'{ContextKey}'); "
            + "IF @doka_identity_owner IS NULL RETURN; "
            + "IF SQL_VARIANT_PROPERTY(@doka_identity_owner, N'BaseType') <> N'varbinary' RETURN; "
            + "IF DATALENGTH(CONVERT(varbinary(max), @doka_identity_owner)) <> 17 RETURN; "
            + "DECLARE @doka_identity_scope varbinary(17) = CONVERT(varbinary(17), @doka_identity_owner); "
            + $"IF SUBSTRING(@doka_identity_scope, 2, 16) <> 0x{token} RETURN; "
            + acquiredCleanup + "IF SUBSTRING(@doka_identity_scope, 1, 1) <> 0x00 "
            + "THROW 51007, " + SqlServerSafeMigrationRuntimeDiagnostic.FormatLiteral(
                acquisitionUnprovenMessage ?? "doka_sm_identity_acquisition_unproven: SafeMigrations cannot prove "
                + "ownership of the attempted IDENTITY_INSERT state; next=Use a new context and connection.")
            + ", 1; "
            + $"EXEC sys.sp_set_session_context @key = N'{ContextKey}', @value = NULL;";
    }

    /// <summary>Captures attempted and confirmed ON states in the same session as the native seed mutation.</summary>
    /// <param name="table">The provider-delimited physical table whose identity state is acquired.</param>
    /// <param name="script">Whether SQL-only execution must reject every preexisting context value.</param>
    /// <param name="scopeInUseMessage">A bounded diagnostic from the exact authored operation, when known.</param>
    /// <returns>The acquisition prefix, with a private local copy retained for ordinary error recovery.</returns>
    internal static string BuildAcquisitionSql(string table, bool script = false, string? scopeInUseMessage = null)
    {
        // WHY: SQL Server attentions can interrupt between ON and the following
        // statement. Ready and acquired markers are safe to recover; attempting
        // is deliberately uncertain and requires session quarantine instead of
        // an OFF against potentially inherited state. SESSION_CONTEXT is read
        // only in scalar control flow, not in parallel data query plans.
        // Script generation has no client nonce and must reject every existing
        // value; managed generation consumes only its wrapper-reserved ready
        // value. Neither mode infers caller ownership from a matching shape.
        var scopeInUseLiteral = ScopeInUseLiteral(scopeInUseMessage);
        var existingScopeGuard = script
            ? "IF @doka_identity_owner IS NOT NULL THROW 51007, " + scopeInUseLiteral + ", 1; "
            : string.Empty;

        return "DECLARE @doka_identity_scope varbinary(17); "
            + "IF @doka_identity_insert = 1 BEGIN "
            + $"DECLARE @doka_identity_owner sql_variant = SESSION_CONTEXT(N'{ContextKey}'); "
            + existingScopeGuard
            + "IF @doka_identity_owner IS NULL BEGIN "
            + "SET @doka_identity_scope = 0x00 + CONVERT(binary(16), NEWID()); "
            + $"EXEC sys.sp_set_session_context @key = N'{ContextKey}', @value = @doka_identity_scope; END "
            + "ELSE BEGIN "
            + "IF SQL_VARIANT_PROPERTY(@doka_identity_owner, N'BaseType') <> N'varbinary' "
            + "OR DATALENGTH(CONVERT(varbinary(max), @doka_identity_owner)) <> 17 "
            + "THROW 51007, " + scopeInUseLiteral + ", 1; "
            + "SET @doka_identity_scope = CONVERT(varbinary(17), @doka_identity_owner); "
            + "IF SUBSTRING(@doka_identity_scope, 1, 1) <> 0x00 "
            + "THROW 51007, " + scopeInUseLiteral + ", 1; END; "
            + "DECLARE @doka_identity_attempt varbinary(17) = 0x01 + SUBSTRING(@doka_identity_scope, 2, 16); "
            + $"EXEC sys.sp_set_session_context @key = N'{ContextKey}', @value = @doka_identity_attempt; "
            + $"BEGIN TRY SET IDENTITY_INSERT {table} ON; END TRY "
            + $"BEGIN CATCH EXEC sys.sp_set_session_context @key = N'{ContextKey}', "
            + "@value = @doka_identity_scope; THROW; END CATCH; "
            + "DECLARE @doka_identity_acquired varbinary(17) = 0x02 + SUBSTRING(@doka_identity_scope, 2, 16); "
            + $"EXEC sys.sp_set_session_context @key = N'{ContextKey}', @value = @doka_identity_acquired; "
            + AcquiredMarkerComment + " END; ";
    }

    /// <summary>
    /// Clears the ready marker on successful NoOp/non-identity paths without touching other marker phases.
    /// </summary>
    /// <returns>A scalar control statement appended after the complete operation guard succeeds.</returns>
    internal static string ReadyCompletionSql()
        => $"DECLARE @doka_identity_completed sql_variant = SESSION_CONTEXT(N'{ContextKey}'); "
            + "IF SQL_VARIANT_PROPERTY(@doka_identity_completed, N'BaseType') = N'varbinary' "
            + "AND DATALENGTH(CONVERT(varbinary(max), @doka_identity_completed)) = 17 "
            + "AND SUBSTRING(CONVERT(varbinary(17), @doka_identity_completed), 1, 1) = 0x00 "
            + $"EXEC sys.sp_set_session_context @key = N'{ContextKey}', @value = NULL;";

    /// <summary>Explains unknown session ownership without exposing its caller-supplied marker value.</summary>
    private static string ScopeInUseLiteral(string? message)
        => SqlServerSafeMigrationRuntimeDiagnostic.FormatLiteral(message
            ?? "doka_sm_identity_scope_in_use: SafeMigrations cannot acquire caller-owned session state; "
            + "next=Use a clean context and connection without a preexisting identity marker.");
}
