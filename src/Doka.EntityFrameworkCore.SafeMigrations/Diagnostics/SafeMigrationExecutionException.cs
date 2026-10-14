namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Represents a positively identified SafeMigrations runtime rejection with
/// its bounded operation context and original native failure.
/// </summary>
/// <remarks>
/// Providers create this exception only after their owned execution scope has
/// completed cleanup. Ordinary database errors, cancellation, transport errors
/// and uncertain cleanup failures retain their original exception contracts.
/// </remarks>
public sealed class SafeMigrationExecutionException : InvalidOperationException
{
    /// <summary>Initializes an operation-associated runtime rejection.</summary>
    /// <param name="operation">The authored safe operation, used only to capture structural context.</param>
    /// <param name="guardCode">The established native SafeMigrations guard identity.</param>
    /// <param name="operationOrdinal">The zero-based generating-stream operation ordinal, when known.</param>
    /// <param name="innerException">The original native rejection after owned cleanup.</param>
    /// <param name="diagnosticReasonCode">The precise reason established by the guard, when available.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="operation" /> or <paramref name="innerException" /> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="guardCode" /> is not an owned guard identity.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operationOrdinal" /> is negative.</exception>
    public SafeMigrationExecutionException(
        SafeMigrationOperation operation,
        string guardCode,
        int? operationOrdinal,
        Exception innerException,
        string? diagnosticReasonCode = null
    ) : base(CreateMessage(operation, guardCode, operationOrdinal, innerException, diagnosticReasonCode),
        innerException)
    {
        // WHY: Capturing only identifiers avoids retaining an entire authored
        // managed-data intent and its private row matrices on the exception.
        Identity = SafeMigrationOperationIdentity.Create(operation.Intent);
        GuardCode = guardCode;
        OperationOrdinal = operationOrdinal;
        Policy = operation.Policy;
        var explanation = SafeMigrationDiagnosticMessages.GetRuntimeExplanation(
            operation.Intent, guardCode, diagnosticReasonCode);

        ReasonCode = explanation.ReasonCode;
    }

    /// <summary>Diagnoses a scoped command without retaining its original operation or row payloads.</summary>
    /// <param name="context">The identifier-only context captured during generation.</param>
    /// <param name="guardCode">The positively attributed native guard category.</param>
    /// <param name="operationOrdinal">The known zero-based generating-stream ordinal.</param>
    /// <param name="innerException">The exact original native failure after cleanup.</param>
    /// <param name="diagnosticReasonCode">The established classifier reason, when available.</param>
    internal SafeMigrationExecutionException(
        SafeMigrationRuntimeDiagnosticContext context,
        string guardCode,
        int? operationOrdinal,
        Exception innerException,
        string? diagnosticReasonCode = null
    ) : base(CreateCapturedMessage(context, guardCode, operationOrdinal, innerException, diagnosticReasonCode),
        innerException)
    {
        Identity = context.Identity;
        GuardCode = guardCode;
        OperationOrdinal = operationOrdinal;
        Policy = context.Policy;
        ReasonCode = SafeMigrationDiagnosticMessages.GetRuntimeExplanation(
            context, guardCode, diagnosticReasonCode).ReasonCode;
    }

    /// <summary>Gets the original native guard identity, independent of human-readable text.</summary>
    public string GuardCode { get; }

    /// <summary>Gets the bounded explanatory reason selected from proven guard evidence.</summary>
    public string ReasonCode { get; }

    /// <summary>Gets the authored structural identity without application row values.</summary>
    public SafeMigrationOperationIdentity Identity { get; }

    /// <summary>Gets the zero-based generating-stream ordinal, or null when unavailable.</summary>
    /// <remarks>This is not a fabricated pending-migration global ordinal or migration identifier.</remarks>
    public int? OperationOrdinal { get; }

    /// <summary>Gets the source-frozen policy of the rejected operation.</summary>
    public SafeMigrationPolicy Policy { get; }

    /// <summary>Validates the correlated failure before formatting its operator message.</summary>
    private static string CreateMessage(
        SafeMigrationOperation operation,
        string guardCode,
        int? operationOrdinal,
        Exception innerException,
        string? diagnosticReasonCode
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(innerException);
        ArgumentException.ThrowIfNullOrWhiteSpace(guardCode);

        if (operationOrdinal is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationOrdinal));
        }

        if (!IsOwnedGuard(guardCode))
        {
            throw new ArgumentException(
                "The failure must identify a recognized SafeMigrations runtime guard, not an arbitrary provider error.",
                nameof(guardCode));
        }

        return SafeMigrationDiagnosticMessages.FormatRuntime(operation, guardCode, operationOrdinal,
            diagnosticReasonCode);
    }

    /// <summary>Validates a captured command failure without restoring a payload-bearing intent.</summary>
    private static string CreateCapturedMessage(
        SafeMigrationRuntimeDiagnosticContext context,
        string guardCode,
        int? operationOrdinal,
        Exception innerException,
        string? diagnosticReasonCode
    )
    {
        if (context.Identity is null)
        {
            throw new ArgumentException("A captured operation identity is required.", nameof(context));
        }

        ArgumentNullException.ThrowIfNull(innerException);
        ArgumentException.ThrowIfNullOrWhiteSpace(guardCode);

        if (operationOrdinal is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationOrdinal));
        }

        if (!IsOwnedGuard(guardCode))
        {
            throw new ArgumentException(
                "The failure must identify a recognized SafeMigrations runtime guard, not an arbitrary provider error.",
                nameof(guardCode));
        }

        return SafeMigrationDiagnosticMessages.FormatCapturedRuntime(
            context, guardCode, operationOrdinal, diagnosticReasonCode);
    }

    /// <summary>Accepts only the closed native rejection contract, not lookalike provider errors.</summary>
    private static bool IsOwnedGuard(
        string guardCode
    ) => guardCode switch
    {
        "doka_sm_different" or "doka_sm_unsupported" or "doka_sm_data_blocked"
            or "doka_sm_prerequisite_missing" or "doka_sm_postcondition" => true,
        _ => false,
    };
}
