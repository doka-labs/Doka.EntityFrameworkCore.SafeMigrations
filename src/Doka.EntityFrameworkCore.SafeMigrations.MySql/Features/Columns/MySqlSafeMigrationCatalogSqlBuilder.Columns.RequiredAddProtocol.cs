namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private const string RequiredColumnAddGuardPrefix = "doka_sm_add1_nn_";

    /// <summary>Identifies intrinsic required additions whose writes can be protected by an owned CHECK.</summary>
    /// <param name="definition">The immutable authored target.</param>
    /// <returns>Whether the two-stage no-default protocol can preserve this target exactly.</returns>
    internal static bool CanUseRequiredColumnAddProtocol(
        ExpectedColumnDefinition definition
    )
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition is { IsNullable: false, DefaultValue.Kind: SafeMigrationDefaultValueKind.None }
            && SafeMigrationColumnRepairHelper.HasRepairableIntrinsicShape(definition)
            && (definition.StoreType is null
                || !definition
                    .StoreType
                    .AsSpan()
                    .TrimStart()
                    .StartsWith("timestamp", StringComparison.OrdinalIgnoreCase))
            && !HasStandardOnlyEnumSetNativeCommentScope(definition)
            && MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata)
            && metadata.ValueGenerationStrategy is null
                or MySqlValueGenerationStrategy.None
                or MySqlValueGenerationStrategy.ClientGuid;
    }

    /// <summary>Identifies unsafe missing additions that must not fall back to implicit native values.</summary>
    /// <param name="definition">The immutable authored target.</param>
    /// <returns>Whether only the missing state must reject an unsupported add protocol.</returns>
    internal static bool RequiresUnsupportedMissingRequiredAdd(
        ExpectedColumnDefinition definition
    )
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None
            && !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(definition)
            && !CanUseRequiredColumnAddProtocol(definition)
            && (!MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata)
                || metadata.ValueGenerationStrategy != MySqlValueGenerationStrategy.AutoIncrement);
    }

    /// <summary>Identifies required SQL-default additions needing strict native conversion qualification.</summary>
    /// <param name="definition">The immutable target and authored SQL-default expression.</param>
    /// <returns>Whether missing-column addition needs engine and session evidence.</returns>
    internal static bool RequiresRequiredSqlDefaultAddQualification(
        ExpectedColumnDefinition definition
    )
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition is { IsNullable: false, DefaultValue.Kind: SafeMigrationDefaultValueKind.Sql };
    }

    /// <summary>Identifies required-add paths whose missing qualification needs native owner identity.</summary>
    /// <param name="definition">The immutable target requiring an owned stage or missing-only add refusal.</param>
    /// <returns>Whether catalog shadows must not supply presence or terminal matching for the addition.</returns>
    private static bool RequiresNativeRequiredColumnAddOwnerQualification(
        ExpectedColumnDefinition definition
    ) => CanUseRequiredColumnAddProtocol(definition)
        || RequiresUnsupportedMissingRequiredAdd(definition)
        || RequiresRequiredSqlDefaultAddQualification(definition);

    /// <summary>Identifies required TIMESTAMP repairs requiring explicit session semantics.</summary>
    /// <param name="definition">The immutable target whose MODIFY must not create temporal data or defaults.</param>
    /// <returns>Whether repair requires explicit_defaults_for_timestamp to remain enabled.</returns>
    private static bool RequiresExplicitTimestampDefaults(
        ExpectedColumnDefinition definition
    ) => RequiresExplicitTimestampDefaults(definition, definition.StoreType);

    /// <summary>Shares required TIMESTAMP session qualification with resolved projected type contracts.</summary>
    /// <param name="definition">The immutable target whose nullability requires qualification.</param>
    /// <param name="resolvedStoreType">The authored or provider-resolved store type, when known.</param>
    /// <returns>Whether required-column repair needs explicit timestamp defaults.</returns>
    internal static bool RequiresExplicitTimestampDefaults(
        ExpectedColumnDefinition definition,
        string? resolvedStoreType
    )
    {
        ArgumentNullException.ThrowIfNull(definition);

        return !definition.IsNullable
            && resolvedStoreType is { } storeType
            && storeType
                .AsSpan()
                .TrimStart()
                .StartsWith("timestamp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates a versioned owner-and-target-bound guard identity within MySQL's name limit.</summary>
    /// <param name="intent">The raw owner, schema and immutable target contract.</param>
    /// <returns>The fixed prefix followed by 48 canonical fingerprint characters.</returns>
    /// <remarks>Qualified and unqualified owner contracts intentionally do not share recovery identities.</remarks>
    internal static string GetRequiredColumnAddGuardName(
        EnsureColumnIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        var fingerprint = SafeMigrationContractFingerprint.Create(
            [new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent)]);

        return RequiredColumnAddGuardPrefix + fingerprint[..48];
    }

    /// <summary>Selects possible protocol names without establishing recovery ownership.</summary>
    /// <param name="name">The catalog constraint name to prefilter.</param>
    /// <returns>Whether the name has the exact versioned prefix and 48 lowercase hexadecimal characters.</returns>
    /// <remarks>An exact authored fingerprint and fresh live ownership proof remain mandatory.</remarks>
    internal static bool IsRequiredColumnAddGuardNameCandidate(
        string name
    )
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length != 64
            || !name.StartsWith(RequiredColumnAddGuardPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in name.AsSpan(RequiredColumnAddGuardPrefix.Length))
        {
            if (character is not ((>= '0' and <= '9') or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Qualifies missing and exactly owned staged states while retaining ordinary repair contracts.</summary>
    /// <param name="intent">The immutable authored owner and target contract.</param>
    /// <param name="isMariaDb">Whether CHECK identity is table-local.</param>
    /// <param name="supportsCheck">Whether the captured server can enforce the owned CHECK.</param>
    /// <param name="plan">The ordinary matching, missing and repair plan.</param>
    /// <returns>The plan qualified by one catalog-only atomic-add status.</returns>
    private MySqlSafeMigrationRuntimePlan ApplyRequiredColumnAddProtocol(
        EnsureColumnIntent intent,
        bool isMariaDb,
        bool supportsCheck,
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        var columnExists = RequiresNativeRequiredColumnAddOwnerQualification(intent.Definition)
            ? BuildRequiredColumnAddColumnExists(intent.Table, intent.Definition.Name)
            : ColumnExists(intent.Table, intent.Definition.Name);
        if (!CanUseRequiredColumnAddProtocol(intent.Definition))
        {
            return RequiresUnsupportedMissingRequiredAdd(intent.Definition)
                ? plan with
                {
                    StateExpression =
                    $"CASE WHEN NOT {columnExists} THEN 'unsupported' " + $"ELSE ({plan.StateExpression}) END",
                    StateEvaluationGuardExpression = columnExists,
                    StateEvaluationGuardFailureExpression = "'unsupported'",
                    ClassificationCodeExpression = $"CASE WHEN NOT {columnExists} "
                    + "THEN 'column_atomic_add_unavailable' ELSE NULL END",
                }
                : plan;
        }

        columnExists = BuildRequiredColumnAddColumnExists(intent.Table, intent.Definition.Name);
        var tableExists = BuildRequiredColumnAddBaseTableExists(intent.Table);
        var guardName = GetRequiredColumnAddGuardName(intent);
        var guardScope = "tc.CONSTRAINT_SCHEMA = DATABASE() "
            + $"AND tc.CONSTRAINT_NAME = {Literal(guardName)} "
            + (isMariaDb ? $"AND tc.TABLE_NAME = {Literal(intent.Table)} " : string.Empty);

        var guardExists = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " + $"WHERE {guardScope})";
        var oneGuard = "(SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " + $"WHERE {guardScope}) = 1";
        var terminalMatching = $"({plan.Postcondition}) AND NOT ({guardExists})";
        var escapedDoubleQuotes = false;
        var backslashEscapes = intent.Definition.StoreType is { } storeType
            && BuildEnumSetCatalogDisplay(storeType, backslashEscapes: true, out escapedDoubleQuotes) is not null
            && BuildEnumSetCatalogDisplay(storeType, backslashEscapes: false, out _) is null;

        var environment = supportsCheck
            ? BuildRequiredColumnAddEnvironment(
                intent.Table,
                isMariaDb,
                backslashEscapes,
                escapedDoubleQuotes,
                RequiresNativeEnumNoBackslashEscapes(intent.Definition))
            : "FALSE";

        var nullableTarget = CreateRequiredColumnAddNullableTarget(intent.Definition);
        var check = new ExpectedCheckConstraintDefinition(
            guardName,
            intent.Table,
            _sqlGenerationHelper.DelimitIdentifier(intent.Definition.Name) + " IS NOT NULL",
            intent.Schema);

        var exactExpressionCandidates = new[] { check.Sql!, $"({check.Sql})" }
            .Concat(
                MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
                    check.Sql!,
                    includeMySqlEncodedDisplay: !isMariaDb))
            .Distinct(StringComparer.Ordinal)
            .Select(Literal);

        // WHY: Owner case follows the server's native table-name mode. CHECK
        // names and expressions must not inherit catalog collation folding.
        var exactCheckIdentity = $"CAST(tc.CONSTRAINT_NAME AS BINARY) = CAST({Literal(guardName)} AS BINARY) "
            + $"AND {BuildRequiredColumnAddOwnerMatches("tc.TABLE_NAME", Literal(intent.Table))} "
            + $"AND {BuildRequiredColumnAddOwnerMatches("tc.CONSTRAINT_SCHEMA", "DATABASE()")} "
            + "AND CAST(cc.CONSTRAINT_SCHEMA AS BINARY) = CAST(tc.CONSTRAINT_SCHEMA AS BINARY) "
            + "AND CAST(cc.CONSTRAINT_NAME AS BINARY) = CAST(tc.CONSTRAINT_NAME AS BINARY) "
            + (isMariaDb ? "AND CAST(cc.TABLE_NAME AS BINARY) = CAST(tc.TABLE_NAME AS BINARY) " : string.Empty)
            + $"AND CAST(cc.CHECK_CLAUSE AS BINARY) IN ({string.Join(", ", exactExpressionCandidates)})";

        var ownership = supportsCheck
            ? $"({oneGuard}) AND ({BuildColumnMatches(intent.Table, nullableTarget, isMariaDb,
                requireRepairSafeExtra: true, requireExactIdentity: true)}) "
            + $"AND ({CheckConstraintMatches(check, isMariaDb, exactCheckIdentity)})"
            : "FALSE";

        var noSetNullDependency = BuildNoOutgoingSetNullForeignKey(intent.Table, intent.Definition.Name);
        var ordinaryInvariant = plan.NullabilityDataProbe?.RepairInvariantExpression ?? "FALSE";
        var status = MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder;
        var resume = MySqlSafeMigrationRequiredColumnAddPlan.ResumeExpression;

        // WHY: Materialize complete catalog facts once per operation. State,
        // repair, NULL eligibility and diagnostics consume only this closed
        // status, not repeated copies of the full ownership predicate.
        var statusExpression = $"CASE WHEN ({terminalMatching}) THEN 'complete' "
            + $"WHEN NOT ({columnExists}) THEN CASE WHEN ({guardExists}) THEN 'conflict' "
            + $"WHEN NOT ({environment}) THEN 'unavailable' ELSE 'fresh' END "
            + $"WHEN ({guardExists}) THEN CASE WHEN NOT ({ownership}) THEN 'conflict' "
            + $"WHEN NOT ({environment}) THEN 'unavailable' "
            + $"WHEN NOT ({noSetNullDependency}) THEN 'set_null' ELSE 'staged' END ELSE 'ordinary' END";

        // WHY: A certified stage may contain explicit writer values. Its
        // Missing/Apply classification is recovery, not evidence of an empty
        // table; the distinct certificate code travels into ordered analysis.
        var probeInvariant = $"({resume}) OR (({status}) = 'ordinary' AND ({ordinaryInvariant}))";
        var nullProbe = new MySqlSafeMigrationNullabilityDataProbe(
            BuildRequiredColumnAddColumnExists(intent.Table, intent.Definition.Name, "c.IS_NULLABLE = 'YES'"),
            probeInvariant,
            $"EXISTS (SELECT 1 FROM {Delimited(intent.Table, intent.Schema)} WHERE "
            + $"{Delimited(intent.Definition.Name)} IS NULL LIMIT 1)");

        var missingRows = $"EXISTS (SELECT 1 FROM {Delimited(intent.Table, intent.Schema)} LIMIT 1)";
        var oldCode = plan.ClassificationCodeExpression ?? "NULL";

        return plan with
        {
            RequiredColumnAddPlan =
            new MySqlSafeMigrationRequiredColumnAddPlan(guardName, statusExpression, isMariaDb)
            {
                BackslashEscapes = backslashEscapes,
                SupportsCheck = supportsCheck,
            },
            Postcondition = terminalMatching,
            ExecutionPostcondition = terminalMatching,
            PrerequisiteExpression = tableExists,
            StateExpression =
            $"CASE WHEN NOT {tableExists} THEN 'prerequisite_missing' "
            + $"WHEN ({status}) = 'complete' THEN 'matching' "
            + $"WHEN ({status}) = 'conflict' THEN CASE WHEN ({columnExists}) "
            + "THEN 'different' ELSE 'unsupported' END "
            + $"WHEN ({status}) IN ('unavailable', 'set_null') THEN 'unsupported' "
            + $"WHEN ({resume}) THEN CASE WHEN {MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder} "
            + $"THEN 'data_blocked' ELSE 'missing' END ELSE ({plan.StateExpression}) END",
            RepairPrecondition = $"({status}) = 'ordinary' AND ({plan.RepairPrecondition})",
            ClassificationCodeExpression = $"CASE WHEN ({status}) = 'complete' THEN NULL "
            + $"WHEN ({status}) = 'conflict' THEN 'column_atomic_add_guard_conflict' "
            + $"WHEN ({status}) = 'unavailable' THEN 'column_atomic_add_unavailable' "
            + $"WHEN ({status}) = 'set_null' THEN 'column_set_null_dependency' "
            + $"WHEN ({resume}) THEN 'column_atomic_add_resume' ELSE ({oldCode}) END",
            NullabilityDataProbe = nullProbe,
            MayRequireNullabilityDataProof = true,
            StateEvaluationGuardExpression = columnExists,
            // WHY: The lazy missing-column branch runs before StateExpression.
            // Qualification must live here as well or an empty table could
            // bypass engine, session and ownership checks entirely.
            StateEvaluationGuardFailureExpression = $"CASE WHEN ({status}) <> 'fresh' THEN 'unsupported' "
            + $"WHEN {missingRows} THEN 'data_blocked' ELSE 'missing' END",
        };
    }

    /// <summary>Requires to be enforced transactional CHECK protection and strict final nullability conversion.</summary>
    /// <param name="table">The exact local table identity.</param>
    /// <param name="isMariaDb">Whether the CHECK session switch must also be qualified.</param>
    /// <param name="backslashEscapes">Whether only standard native literal decoding can represent the target.</param>
    /// <param name="doubleQuotes">Whether target members use mode-sensitive double quotation marks.</param>
    /// <param name="requiresNoBackslashEscapes">Whether a native comment scope needs the same original mode.</param>
    /// <returns>The catalog and session eligibility predicate without a table-row scan.</returns>
    private string BuildRequiredColumnAddEnvironment(
        string table,
        bool isMariaDb,
        bool backslashEscapes,
        bool doubleQuotes,
        bool requiresNoBackslashEscapes
    ) => "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES atomic_table "
        + $"WHERE atomic_table.TABLE_SCHEMA = DATABASE() AND atomic_table.TABLE_NAME = {Literal(table)} "
        + $"AND {BuildRequiredColumnAddOwnerMatches("atomic_table.TABLE_SCHEMA", "DATABASE()")} "
        + $"AND {BuildRequiredColumnAddOwnerMatches("atomic_table.TABLE_NAME", Literal(table))} "
        + "AND atomic_table.TABLE_TYPE = 'BASE TABLE' AND UPPER(atomic_table.ENGINE) = 'INNODB') "
        + $"AND ({StrictSqlModeExpression}) "
        + (isMariaDb ? "AND @@SESSION.check_constraint_checks = 1 " : string.Empty)
        + (backslashEscapes ? "AND FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) = 0 " : string.Empty)
        + (doubleQuotes ? "AND FIND_IN_SET('ANSI_QUOTES', @@SESSION.sql_mode) = 0 " : string.Empty)
        + (requiresNoBackslashEscapes ? $"AND ({NativeEnumNoBackslashEscapesExpression})" : string.Empty);

    /// <summary>Proves the requested protocol table exists without borrowing a case-distinct catalog shadow.</summary>
    /// <param name="table">The raw authored owner bound to the protocol name.</param>
    /// <returns>A native-mode-qualified base-table presence predicate.</returns>
    private string BuildRequiredColumnAddBaseTableExists(
        string table
    ) => "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES t "
        + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(table)} "
        + $"AND {BuildRequiredColumnAddOwnerMatches("t.TABLE_SCHEMA", "DATABASE()")} "
        + $"AND {BuildRequiredColumnAddOwnerMatches("t.TABLE_NAME", Literal(table))} "
        + "AND t.TABLE_TYPE = 'BASE TABLE')";

    /// <summary>Proves column presence for the native owner with native column-name case semantics.</summary>
    /// <param name="table">The raw authored owner bound to the protocol name.</param>
    /// <param name="column">The case-insensitive native column member name.</param>
    /// <param name="invariant">An optional trusted catalog-only facet predicate.</param>
    /// <returns>A native-owner-qualified column presence or facet predicate.</returns>
    private string BuildRequiredColumnAddColumnExists(
        string table,
        string column,
        string? invariant = null
    ) => "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
        + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
        + $"AND c.COLUMN_NAME = {Literal(column)} "
        + $"AND {BuildRequiredColumnAddOwnerMatches("c.TABLE_SCHEMA", "DATABASE()")} "
        + $"AND {BuildRequiredColumnAddOwnerMatches("c.TABLE_NAME", Literal(table))}"
        + (invariant is not null ? $" AND ({invariant})" : string.Empty)
        + ")";

    /// <summary>Qualifies a protocol owner using the server's native table and schema case rules.</summary>
    /// <param name="catalogExpression">The trusted SQL expression for the catalog table or schema identity.</param>
    /// <param name="expectedExpression">The rendered owner literal, bound parameter or database expression.</param>
    /// <returns>An exact mode-zero predicate or binary-folded mode-one/two predicate.</returns>
    internal static string BuildRequiredColumnAddOwnerMatches(
        string catalogExpression,
        string expectedExpression
    ) => "(CASE WHEN @@lower_case_table_names = 0 THEN "
        + $"CAST({catalogExpression} AS BINARY) = CAST({expectedExpression} AS BINARY) "
        + "WHEN @@lower_case_table_names IN (1, 2) THEN "
        + $"CAST(LOWER({catalogExpression}) AS BINARY) = CAST(LOWER({expectedExpression}) AS BINARY) ELSE FALSE END)";

    /// <summary>Changes only nullability when comparing a staged column against the full authored target.</summary>
    /// <param name="definition">The immutable final target and provider metadata.</param>
    /// <returns>The complete nullable staging contract without changing the original definition.</returns>
    internal static ExpectedColumnDefinition CreateRequiredColumnAddNullableTarget(
        ExpectedColumnDefinition definition
    )
    {
        ArgumentNullException.ThrowIfNull(definition);

        // WHY: Staging changes only nullability. Named facets retain the full
        // immutable contract, including provider-owned metadata, while the
        // completeness regression requires explicit handling of future facets.
        return new ExpectedColumnDefinition(
            name: definition.Name,
            clrType: definition.ClrType,
            isNullable: true,
            storeType: definition.StoreType,
            isUnicode: definition.IsUnicode,
            maxLength: definition.MaxLength,
            isFixedLength: definition.IsFixedLength,
            isRowVersion: definition.IsRowVersion,
            precision: definition.Precision,
            scale: definition.Scale,
            collation: definition.Collation,
            comment: definition.Comment,
            defaultValue: definition.DefaultValue,
            computedColumnSql: definition.ComputedColumnSql,
            isStored: definition.IsStored,
            computedExpression: definition.ComputedExpression)
        {
            ProviderAnnotations = definition.ProviderAnnotations,
            CapturedComputedSql = definition.CapturedComputedSql,
        };
    }
}

/// <summary>Describes the exact owned, recoverable staging boundary for a no-default required-column add.</summary>
/// <param name="GuardName">The versioned owner-and-target-bound CHECK name.</param>
/// <param name="StatusExpression">The complete catalog-only classifier for the owned protocol boundary.</param>
/// <param name="IsMariaDb">Whether finish DDL uses MariaDB's constraint-drop grammar.</param>
internal sealed record MySqlSafeMigrationRequiredColumnAddPlan(
    string GuardName,
    string StatusExpression,
    bool IsMariaDb
)
{
    /// <summary>Identifies an exactly owned and feasible interrupted add from the structural status token.</summary>
    public const string ResumeExpression =
        "(" + MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder + ") = 'staged'";

    /// <summary>Gets the operation-local materialized status expression, when bound.</summary>
    public string? StatusBinding { get; init; }

    /// <summary>Gets whether standard-only ENUM/SET literals require qualified native backslash decoding.</summary>
    /// <remarks>The handler rejects native SQL-mode setup for this certified class before decoding a body.</remarks>
    public bool BackslashEscapes { get; init; }

    /// <summary>Gets the configured feature proof that the server can enforce the owned CHECK.</summary>
    /// <remarks>Projected missing qualification consumes this producer fact without guessing server versions.</remarks>
    public bool SupportsCheck { get; init; }
}
