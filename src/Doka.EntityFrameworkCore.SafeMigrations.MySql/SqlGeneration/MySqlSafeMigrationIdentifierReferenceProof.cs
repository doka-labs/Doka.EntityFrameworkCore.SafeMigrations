namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Proves one MariaDB identifier head using native preparation without executing a data query.</summary>
internal sealed record MySqlSafeMigrationIdentifierReferenceProof
{
    internal const string MarkerPrefix = "__DOKA_SM_IDENTIFIER_REFERENCE_";

    private readonly string _scope;

    /// <summary>Creates a closed parse-only proof with invocation-owned prepared and session identities.</summary>
    /// <param name="marker">The structural proof token with a canonical non-negative ordinal.</param>
    /// <param name="rootIdentifier">One identifier head, never a qualified name or SQL fragment.</param>
    /// <param name="scopeId">A fresh GUID in its 32-character hexadecimal form.</param>
    /// <param name="binding">The independently completed Boolean or runtime result variable.</param>
    internal MySqlSafeMigrationIdentifierReferenceProof(
        string marker,
        string rootIdentifier,
        string scopeId,
        string? binding = null
    )
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(rootIdentifier);
        ArgumentNullException.ThrowIfNull(scopeId);

        var digits = marker.AsSpan();
        if (!digits.StartsWith(MarkerPrefix, StringComparison.Ordinal)
            || !digits.EndsWith("__", StringComparison.Ordinal))
        {
            throw new ArgumentException("The identifier proof requires an owned structural marker.", nameof(marker));
        }

        digits = digits[MarkerPrefix.Length..^2];
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            || ordinal < 0
            || (digits.Length > 1 && digits[0] == '0'))
        {
            throw new ArgumentException("The identifier proof ordinal is not canonical.", nameof(marker));
        }

        if (rootIdentifier.Length is < 1 or > 64
            || !SafeMigrationSqlExpressionParser.TryCollectUnquotedColumnRootIdentifiers(rootIdentifier, out var roots)
            || roots.Length != 1
            || !string.Equals(roots[0], rootIdentifier, StringComparison.Ordinal))
        {
            throw new ArgumentException("The identifier proof requires one bounded head.", nameof(rootIdentifier));
        }

        if (scopeId.Length != 32
            || !Guid.TryParseExact(scopeId, "N", out _))
        {
            throw new ArgumentException("The identifier proof requires a hexadecimal GUID scope.", nameof(scopeId));
        }

        Marker = marker;
        RootIdentifier = rootIdentifier;
        ScopeId = scopeId;
        Binding = binding;
        _scope = scopeId + "_" + ordinal.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the structural token consumed by the owning runtime plan.</summary>
    internal string Marker { get; }

    /// <summary>Gets the unquoted reference head whose native grammatical role needs proof.</summary>
    internal string RootIdentifier { get; }

    /// <summary>Gets the invocation-owned hexadecimal identity shared by this operation's proof scope.</summary>
    internal string ScopeId { get; }

    /// <summary>Gets or initializes the independently completed proof result.</summary>
    internal string? Binding { get; init; }

    /// <summary>Gets the session-local result identity without including authored names.</summary>
    internal string ResultVariable => "@doka_sm_ir_result_" + _scope;

    /// <summary>Gets the owned native statement used only for identifier preparation.</summary>
    internal string InnerStatementName => "doka_sm_ir_inner_" + _scope;

    /// <summary>Gets the owned native statement containing the local syntax-error handler.</summary>
    internal string OuterStatementName => "doka_sm_ir_outer_" + _scope;

    private string InnerSqlVariable => "@doka_sm_ir_source_" + _scope;
    private string OuterSqlVariable => "@doka_sm_ir_block_" + _scope;
    private string CleanupStatementName => "doka_sm_ir_cleanup_" + _scope;
    private string CleanupSqlVariable => "@doka_sm_ir_cleanup_" + _scope;

    /// <summary>Builds one guarded parse-only proof compatible with native CLI statement delimiters.</summary>
    /// <param name="proof">The validated operation-owned reference head and session identities.</param>
    /// <param name="executionGuard">The preceding catalog-only execution barrier.</param>
    /// <param name="eligibilityExpression">The native keyword condition selecting heads that need preparation.</param>
    /// <returns>Only preparation, execution of an owned handler block, and immediate prepared cleanup.</returns>
    internal static string BuildEvaluationSql(
        MySqlSafeMigrationIdentifierReferenceProof proof,
        string executionGuard,
        string eligibilityExpression
    )
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionGuard);
        ArgumentException.ThrowIfNullOrWhiteSpace(eligibilityExpression);

        var query = "SELECT "
            + proof.RootIdentifier
            + " FROM (SELECT 0 AS "
            + proof.RootIdentifier
            + ") doka_sm_source";

        // WHY: MariaDB lists both reserved and legal nonreserved words without a
        // reservedness bit. Native PREPARE proves reference-head grammar while
        // the synthetic source resolves names without touching application rows.
        // Only syntax error 1064 is evidence of an invalid head; resource errors,
        // permission failures and cancellation must retain their native failure.
        var block = "BEGIN NOT ATOMIC DECLARE EXIT HANDLER FOR 1064 SET "
            + proof.ResultVariable
            + " = FALSE;SET "
            + proof.InnerSqlVariable
            + " = "
            + HexLiteral(query)
            + ";PREPARE "
            + proof.InnerStatementName
            + " FROM "
            + proof.InnerSqlVariable
            + ";DEALLOCATE PREPARE "
            + proof.InnerStatementName
            + ";SET "
            + proof.ResultVariable
            + " = TRUE;END";

        var builder = new StringBuilder();
        builder
            .Append("SET ")
            .Append(proof.ResultVariable)
            .Append(" = FALSE;SET ")
            .Append(proof.OuterSqlVariable)
            .Append(" = CASE WHEN COALESCE((")
            .Append(executionGuard)
            .Append("), FALSE) AND COALESCE((")
            .Append(eligibilityExpression)
            .Append("), FALSE) THEN ")
            .Append(HexLiteral(block))
            .Append(" ELSE 'DO 0' END;PREPARE ")
            .Append(proof.OuterStatementName)
            .Append(" FROM ")
            .Append(proof.OuterSqlVariable)
            .Append(";EXECUTE ")
            .Append(proof.OuterStatementName)
            .Append(";DEALLOCATE PREPARE ")
            .Append(proof.OuterStatementName)
            .Append(";SET ")
            .Append(proof.InnerSqlVariable)
            .Append(" = NULL, ")
            .Append(proof.OuterSqlVariable)
            .Append(" = NULL;");

        return builder.ToString();
    }

    /// <summary>Releases only this proof's identities and variables, including interrupted preparation.</summary>
    /// <param name="proof">The immutable scope whose cleanup can safely run more than once.</param>
    /// <returns>An idempotent cleanup block; errors other than missing owned statements are propagated.</returns>
    internal static string BuildCleanupSql(
        MySqlSafeMigrationIdentifierReferenceProof proof
    )
    {
        ArgumentNullException.ThrowIfNull(proof);

        // WHY: Cancellation can occur between DEALLOCATE and an acquired-flag
        // update. Ignore only 1243 for absent GUID-owned statements during
        // cleanup, rather than masking the original execution failure or relying
        // on metadata visibility to discover which preparation completed.
        var block = "BEGIN NOT ATOMIC DECLARE CONTINUE HANDLER FOR 1243 BEGIN END;DEALLOCATE PREPARE "
            + proof.InnerStatementName
            + ";DEALLOCATE PREPARE "
            + proof.OuterStatementName
            + ";SET "
            + proof.ResultVariable
            + " = NULL, "
            + proof.InnerSqlVariable
            + " = NULL, "
            + proof.OuterSqlVariable
            + " = NULL;END";

        return "SET "
            + proof.CleanupSqlVariable
            + " = "
            + HexLiteral(block)
            + ";PREPARE "
            + proof.CleanupStatementName
            + " FROM "
            + proof.CleanupSqlVariable
            + ";EXECUTE "
            + proof.CleanupStatementName
            + ";DEALLOCATE PREPARE "
            + proof.CleanupStatementName
            + ";SET "
            + proof.CleanupSqlVariable
            + " = NULL;";
    }

    private static string HexLiteral(
        string value
    ) => "CONVERT(0x" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + " USING utf8mb4)";
}
