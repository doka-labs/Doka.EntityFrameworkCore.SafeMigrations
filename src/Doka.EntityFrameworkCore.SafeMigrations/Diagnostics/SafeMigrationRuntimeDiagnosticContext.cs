namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Retains only the structural and policy facts needed to diagnose an owned runtime failure.</summary>
/// <remarks>Neither the original intent nor model-managed row/default/expression payloads are retained.</remarks>
internal readonly struct SafeMigrationRuntimeDiagnosticContext
{
    /// <summary>Initializes a payload-free runtime diagnostic context from already captured facts.</summary>
    /// <param name="identity">The immutable authored structural identity.</param>
    /// <param name="policy">The exact authored conflict policy.</param>
    /// <param name="requiredColumnReplacementUnproven">Whether the required shape declares no replacement.</param>
    internal SafeMigrationRuntimeDiagnosticContext(
        SafeMigrationOperationIdentity identity,
        SafeMigrationPolicy policy,
        bool requiredColumnReplacementUnproven
    )
    {
        ArgumentNullException.ThrowIfNull(identity);

        Identity = identity;
        Policy = policy;
        RequiredColumnReplacementUnproven = requiredColumnReplacementUnproven;
    }

    /// <summary>Gets the immutable identifier-only identity.</summary>
    internal SafeMigrationOperationIdentity Identity { get; }

    /// <summary>Gets the exact authored policy without retaining the operation.</summary>
    internal SafeMigrationPolicy Policy { get; }

    /// <summary>Gets whether shape alone proves no declared replacement, not whether a column is absent.</summary>
    internal bool RequiredColumnReplacementUnproven { get; }

    /// <summary>Captures bounded structural facts without retaining operation or row payloads.</summary>
    /// <param name="operation">The actual operation associated with the runtime command.</param>
    /// <returns>A payload-free immutable context for later failure-only formatting.</returns>
    internal static SafeMigrationRuntimeDiagnosticContext Create(SafeMigrationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // WHY: Scoped commands outlive SQL generation. Retaining a safe operation here also
        // retains its seed matrices and payloads, even though diagnostics need only identifiers.
        return new SafeMigrationRuntimeDiagnosticContext(
            SafeMigrationOperationIdentity.Create(operation.Intent),
            operation.Policy,
            operation.Intent is EnsureColumnIntent column
                && !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(column.Definition));
    }
}
