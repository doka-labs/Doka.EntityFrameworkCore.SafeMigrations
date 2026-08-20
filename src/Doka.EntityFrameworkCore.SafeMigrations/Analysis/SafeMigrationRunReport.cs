namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Identifies whether a report was produced before or after migration execution.</summary>
public enum SafeMigrationReportMode
{
    /// <summary>Read-only analysis before migration execution.</summary>
    Preflight = 0,

    /// <summary>Read-only target verification after migration execution.</summary>
    Postflight = 1,
}

/// <summary>Summarizes whether a safe migration run can proceed or has converged.</summary>
public enum SafeMigrationReportStatus
{
    /// <summary>No migration operations were supplied or remain pending.</summary>
    NoOperations = 0,

    /// <summary>Every safe operation is accepted and no provider-owned operation is present.</summary>
    Ready = 1,

    /// <summary>
    /// Safe operations are accepted, while ordinary provider-owned EF operations
    /// are listed but cannot be read-only classified by SafeMigrations.
    /// </summary>
    ReadyWithProviderOperations = 2,

    /// <summary>At least one safe operation is rejected or has not converged.</summary>
    Blocked = 3,
}

/// <summary>Contains one immutable operation assessment.</summary>
public sealed class SafeMigrationAssessment
{
    /// <summary>Initializes an assessment.</summary>
    /// <param name="ordinal">The zero-based operation ordinal.</param>
    /// <param name="operationType">The exact CLR migration-operation type name.</param>
    /// <param name="isSafeOperation">Whether the assessment represents a SafeMigrations operation.</param>
    /// <param name="operationKind">The SafeMigrations operation family.</param>
    /// <param name="objectName">The database object name, or null for provider-owned operations.</param>
    /// <param name="observedState">The provider-classified live state.</param>
    /// <param name="action">The provider-neutral action selected for the operation.</param>
    /// <param name="postconditionSatisfied">Whether the operation's final target condition currently holds.</param>
    /// <param name="code">The stable low-cardinality result code.</param>
    public SafeMigrationAssessment(
        int ordinal,
        string operationType,
        bool isSafeOperation,
        SafeMigrationOperationKind? operationKind,
        string? objectName,
        SafeMigrationObservedState? observedState,
        SafeMigrationAction? action,
        bool? postconditionSatisfied,
        string code
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        Ordinal = ordinal;
        OperationType = operationType;
        IsSafeOperation = isSafeOperation;
        OperationKind = operationKind;
        ObjectName = objectName;
        ObservedState = observedState;
        Action = action;
        PostconditionSatisfied = postconditionSatisfied;
        Code = code;
    }

    /// <summary>Gets the zero-based operation ordinal.</summary>
    public int Ordinal { get; }

    /// <summary>Gets the exact CLR operation type name.</summary>
    public string OperationType { get; }

    /// <summary>Gets whether this assessment belongs to a safe envelope.</summary>
    public bool IsSafeOperation { get; }

    /// <summary>Gets the safe operation kind when applicable.</summary>
    public SafeMigrationOperationKind? OperationKind { get; }

    /// <summary>Gets the database object name when the operation is safe.</summary>
    public string? ObjectName { get; }

    /// <summary>Gets the provider-classified live state.</summary>
    public SafeMigrationObservedState? ObservedState { get; }

    /// <summary>Gets the provider-neutral planned action.</summary>
    public SafeMigrationAction? Action { get; }

    /// <summary>Gets whether the final target condition currently holds.</summary>
    public bool? PostconditionSatisfied { get; }

    /// <summary>Gets the stable assessment code.</summary>
    public string Code { get; }
}

/// <summary>Contains an immutable preflight or postflight report.</summary>
public sealed class SafeMigrationRunReport
{
    /// <summary>Gets the current machine-readable report schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Initializes a run report and snapshots its assessments.</summary>
    /// <param name="mode">Whether this is a preflight analysis or postflight verification.</param>
    /// <param name="status">The aggregate report status.</param>
    /// <param name="generatedAtUtc">The report timestamp; it is normalized to UTC.</param>
    /// <param name="instanceId">The caller-generated pseudonymous database-instance identifier.</param>
    /// <param name="environment">The live provider environment.</param>
    /// <param name="targetMigrationId">The intended target migration identifier, or null when unknown.</param>
    /// <param name="modelFingerprint">The fingerprint of the canonical target model.</param>
    /// <param name="contractFingerprint">The fingerprint of the ordered migration contract.</param>
    /// <param name="assessments">The ordered operation assessments to snapshot.</param>
    /// <param name="unexpectedObjects">The unexpected live database objects to snapshot.</param>
    public SafeMigrationRunReport(
        SafeMigrationReportMode mode,
        SafeMigrationReportStatus status,
        DateTimeOffset generatedAtUtc,
        string instanceId,
        SafeMigrationProviderEnvironment environment,
        string? targetMigrationId,
        string modelFingerprint,
        string contractFingerprint,
        IEnumerable<SafeMigrationAssessment> assessments,
        IEnumerable<SafeMigrationUnexpectedObject>? unexpectedObjects = null
    )
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(environment);

        if (targetMigrationId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetMigrationId);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(modelFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractFingerprint);
        ArgumentNullException.ThrowIfNull(assessments);

        Mode = mode;
        Status = status;
        SchemaVersion = CurrentSchemaVersion;
        GeneratedAtUtc = generatedAtUtc.ToUniversalTime();

        InstanceId = instanceId;
        Environment = environment;
        TargetMigrationId = targetMigrationId;

        ModelFingerprint = modelFingerprint;
        ContractFingerprint = contractFingerprint;

        Assessments = Array.AsReadOnly(assessments.ToArray());
        UnexpectedObjects = Array.AsReadOnly((unexpectedObjects ?? []).ToArray());
    }

    /// <summary>Gets the machine-readable report schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the report mode.</summary>
    public SafeMigrationReportMode Mode { get; }

    /// <summary>Gets the aggregate status.</summary>
    public SafeMigrationReportStatus Status { get; }

    /// <summary>Gets when the report was generated, normalized to UTC.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>Gets the caller-generated pseudonymous instance identifier.</summary>
    public string InstanceId { get; }

    /// <summary>Gets the live provider environment.</summary>
    public SafeMigrationProviderEnvironment Environment { get; }

    /// <summary>Gets the intended target migration when known.</summary>
    public string? TargetMigrationId { get; }

    /// <summary>Gets the SHA-256 target-model fingerprint.</summary>
    public string ModelFingerprint { get; }

    /// <summary>Gets the SHA-256 fingerprint of the ordered migration contract.</summary>
    public string ContractFingerprint { get; }

    /// <summary>Gets the ordered operation assessments.</summary>
    public IReadOnlyList<SafeMigrationAssessment> Assessments { get; }

    /// <summary>
    /// Gets additive live objects outside supplied complete table definitions.
    /// These findings do not authorize deletion or semantic inference.
    /// </summary>
    public IReadOnlyList<SafeMigrationUnexpectedObject> UnexpectedObjects { get; }
}
