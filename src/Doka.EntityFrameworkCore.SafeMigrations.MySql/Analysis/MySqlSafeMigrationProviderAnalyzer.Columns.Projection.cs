namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationProjectedColumnAnalyzer
{
    private readonly Dictionary<SafeMigrationProviderAnalysis, bool> _projectedExplicitTimestampDefaults =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<SafeMigrationProviderAnalysis, ProjectedColumnSessionEnvironment>
        _projectedColumnSessionEnvironments = new(ReferenceEqualityComparer.Instance);

    private Dictionary<SafeMigrationOperation, RequiredColumnAddCapabilities>? _projectedRequiredAddCapsByOperation;

    private Dictionary<SafeMigrationProviderAnalysis, RequiredColumnAddCapabilities>?
        _projectedRequiredAddCapsByAnalysis;

    SafeMigrationProviderAnalysis ISafeMigrationProjectedColumnAnalyzer.ValidateProjectedMissingColumnAdd(
        EnsureColumnIntent intent,
        SafeMigrationProjectedMissingColumnContext context,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        if (MySqlSafeMigrationCatalogSqlBuilder.RequiresNativeEnumNoBackslashEscapes(intent.Definition)
            && (!_projectedColumnSessionEnvironments.TryGetValue(liveAnalysis, out var nativeEnvironment)
                || !nativeEnvironment.NoBackslashEscapes))
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Unsupported,
                SafeMigrationRepairCapability.None,
                false,
                "column_native_enum_mode_conflict");
        }

        if (!RequiresProjectedMissingAddQualification(intent.Definition))
        {
            return projectedAnalysis;
        }

        var sqlDefault = MySqlSafeMigrationCatalogSqlBuilder.RequiresRequiredSqlDefaultAddQualification(intent.Definition);
        var unavailable = sqlDefault ? "column_required_sql_add_unavailable" : "column_atomic_add_unavailable";
        if (!SupportsRetainedColumnProof(intent.Definition)
            || MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(intent.Definition)
            || (!sqlDefault && RequiresProjectedTimestampDefaults(intent.Definition))
            || !_projectedColumnSessionEnvironments.TryGetValue(liveAnalysis, out var environment))
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Unsupported,
                SafeMigrationRepairCapability.None,
                false,
                unavailable);
        }

        var innoDb = context.TableWasCreated
            ? environment.DefaultEngineIsInnoDb
            : environment.ExistingInnoDbTables.Contains(intent.Table);

        var sessionIsSafe = sqlDefault
            ? MySqlProjectedColumnTransition.HasSafeRequiredSqlDefaultAddEnvironment(
                intent.Definition,
                _typeMappingSource,
                innoDb,
                environment.StrictSqlMode,
                environment.ExplicitTimestampDefaults)
            && MySqlProjectedColumnTransition.HasSafeRequiredAddLiteralMode(
                intent.Definition,
                requiresBackslashEscapes: false,
                environment.BackslashEscapesEnabled,
                environment.DoubleQuotesAreLiteral)
            : _projectedRequiredAddCapsByAnalysis is { } capabilities
            && capabilities.TryGetValue(liveAnalysis, out var protocol)
            && MySqlProjectedColumnTransition.HasSafeAtomicRequiredColumnAddEnvironment(
                innoDb,
                environment.StrictSqlMode,
                environment.ChecksEnforced,
                protocol.SupportsCheck)
            && MySqlProjectedColumnTransition.HasSafeRequiredAddLiteralMode(
                intent.Definition,
                protocol.BackslashEscapes,
                environment.BackslashEscapesEnabled,
                environment.DoubleQuotesAreLiteral);
        if (!sessionIsSafe)
        {
            return new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Unsupported,
                SafeMigrationRepairCapability.None,
                false,
                unavailable);
        }

        // WHY: Atomic strict DDL prevents fabricated values, but it does not
        // prove that a required absent or SQL NULL default can populate rows.
        if (!SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition)
            && !context.ProvesEmpty)
        {
            if (liveAnalysis.ObservedState == SafeMigrationObservedState.DataBlocked
                && context is { TableWasCreated: false, HasDataMutation: false })
            {
                return liveAnalysis;
            }

            var currentOwnedStage =
                liveAnalysis is
                {
                    ObservedState: SafeMigrationObservedState.Missing, RequiredColumnAddResumesOwnedStaging: true
                }
                && context is { TableWasCreated: false, ColumnWasDropped: false, HasDataMutation: false };

            if (!currentOwnedStage
                && (liveAnalysis.ObservedState != SafeMigrationObservedState.Missing
                    || !liveAnalysis.RequiresLiveDataProof
                    || context.HasDataMutation
                    || context.TableWasCreated))
            {
                return new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.PrerequisiteMissing,
                    SafeMigrationRepairCapability.None,
                    false,
                    "projected_required_add_rows_unproven");
            }

            if (liveAnalysis.RequiredColumnAddResumesOwnedStaging
                && (context.TableWasCreated || context.ColumnWasDropped))
            {
                return new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.PrerequisiteMissing,
                    SafeMigrationRepairCapability.None,
                    false,
                    "projected_required_add_rows_unproven");
            }
        }

        return projectedAnalysis;
    }

    /// <inheritdoc />
    bool ISafeMigrationProjectedColumnAnalyzer.PreservesIntrinsicColumnSources(
        SafeMigrationIntent intent
    )
    {
        // WHY: Native PRIMARY/FK and sibling-column removal preserve surviving
        // column values and intrinsic facets, not the complete key namespace.
        // Constraint creation, functional/unique indexes and arbitrary DDL
        // carry no such certificate.

        return intent is DropPrimaryKeyIntent or DropForeignKeyIntent or DropColumnIntent
            || (intent is EnsureIndexIntent { Definition: { Unique: false } index }
                && (index.Method is null || StringComparer.OrdinalIgnoreCase.Equals(index.Method, "BTREE"))
                && index.Filter is null
                && index.StructuredFilter is null
                && index.IncludedColumns.Count == 0
                && index.Keys.All(static key =>
                    key.Column is not null
                    && key.Collation is null
                    && key.OperatorClass is null
                    && key.PrefixLength is null
                    && key.NullOrder == SafeMigrationIndexNullOrder.ProviderDefault));
    }

    /// <inheritdoc />
    bool ISafeMigrationProjectedColumnAnalyzer.CanReuseLiveColumnProofAfterKeyChanges(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => !liveAnalysis.RepairMutatesData
        && ((ISafeMigrationProjectedColumnAnalyzer)this).CanReuseLiveColumnProofAfterForeignKeyDrops(
            intent,
            liveAnalysis);

    /// <inheritdoc />
    bool ISafeMigrationProjectedColumnAnalyzer.CanReuseLiveColumnProofAfterKeyChanges(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching)
        {
            return liveAnalysis.PostconditionSatisfied && SupportsRetainedColumnProof(intent.Definition);
        }

        if (liveAnalysis.ObservedState != SafeMigrationObservedState.Different
            || liveAnalysis.RepairCapability != SafeMigrationRepairCapability.Safe
            || liveAnalysis.RepairMutatesData
            || !SupportsRetainedColumnProof(intent.Definition)
            || liveAnalysis.Differences.Count == 0)
        {
            return false;
        }

        // WHY: Ensure has no authored source. Complete native facet evidence must
        // prove a nullability-only change; an earlier widening certificate can
        // become invalid when a new physical index constrains the column width.
        foreach (var difference in liveAnalysis.Differences)
        {
            if (!StringComparer.Ordinal.Equals(difference.Facet, "column_nullability"))
            {
                return false;
            }
        }

        return true;
    }

    bool ISafeMigrationProjectedColumnAnalyzer.CanReuseLiveColumnProofAfterForeignKeyDrops(
        EnsureColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => SupportsRetainedColumnProof(intent.Definition)
        && liveAnalysis is { ObservedState: SafeMigrationObservedState.Matching, PostconditionSatisfied: true }
            or
        {
            ObservedState: SafeMigrationObservedState.Different,
            RepairCapability: SafeMigrationRepairCapability.Safe
        };

    bool ISafeMigrationProjectedColumnAnalyzer.PreservesSiblingColumnProofsAfterForeignKeyDrops(
        EnsureColumnIntent intent,
        ExpectedColumnDefinition source,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis is
    {
        ObservedState: SafeMigrationObservedState.Different,
        RepairCapability: SafeMigrationRepairCapability.Safe,
        RepairMutatesData: false
    }
        && source.IsNullable != intent.Definition.IsNullable
        && HasSameReviewedColumnLayout(source, intent.Definition, StringComparer.OrdinalIgnoreCase);

    bool ISafeMigrationProjectedColumnAnalyzer.CanReuseLiveColumnProofAfterForeignKeyDrops(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching)
        {
            return liveAnalysis.PostconditionSatisfied && SupportsRetainedColumnProof(intent.Definition);
        }

        // WHY: Authored old metadata is not a live-source proof. The captured
        // Safe result must independently bind this exact unchanged-layout transition.
        return liveAnalysis is
        {
            ObservedState: SafeMigrationObservedState.Different,
            RepairCapability: SafeMigrationRepairCapability.Safe
        }
            && intent.OldDefinition is { } source
            && HasSafeProjectedColumnSession(
                source,
                intent.Definition,
                liveAnalysis,
                canReuseLiveProof: true,
                useCreationEnvironment: false)
            && (liveAnalysis.RepairMutatesData
                ? IsCapturedGuidBackfillTransition(source, intent.Definition)
                : IsSameLayoutNullabilityTransition(source, intent.Definition));
    }

    bool ISafeMigrationProjectedColumnAnalyzer.AreProjectedColumnSourcesEquivalent(
        ExpectedColumnDefinition accepted,
        ExpectedColumnDefinition source
    ) => accepted.IsNullable == source.IsNullable
        && HasSameReviewedColumnLayout(accepted, source, StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates exact physical metadata for a value-preserving nullability-only transition.</summary>
    /// <param name="source">The independently reviewed immutable source declaration.</param>
    /// <param name="target">The immutable target declaration.</param>
    /// <returns>True only when nullability and a validated nonphysical review marker may differ.</returns>
    internal static bool IsSameLayoutNullabilityTransition(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target
    ) => source.IsNullable != target.IsNullable && HasSameReviewedColumnLayout(source, target);

    /// <summary>Compares intrinsic facets and validated physical metadata without copying column definitions.</summary>
    private static bool HasSameReviewedColumnLayout(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target,
        StringComparer? nameComparer = null
    ) => SafeMigrationDefinitionEquivalence.ColumnIntrinsicExceptNullability(source, target, nameComparer)
        && SafeMigrationColumnRepairHelper.HasRepairableIntrinsicShape(source)
        && HasSamePhysicalColumnMetadata(source, target);

    /// <summary>Allows the first captured same-storage tightening with a literal GUID backfill.</summary>
    private static bool IsCapturedGuidBackfillTransition(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target
    ) => source.IsNullable
        && !target.IsNullable
        && target.ClrType == typeof(Guid)
        && target.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal
        && target.DefaultValue.GetLiteralValue() is Guid
        && SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(target.DefaultValue)
        && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(source, target)
        && SafeMigrationColumnRepairHelper.HasRepairableIntrinsicShape(source)
        && SafeMigrationColumnRepairHelper.HasRepairableIntrinsicShape(target)
        && HasSamePhysicalColumnMetadata(source, target);

    /// <summary>Validates every annotation and compares the typed facets that affect physical column storage.</summary>
    private static bool HasSamePhysicalColumnMetadata(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target
    )
    {
        if (source.ProviderAnnotations.Count == 0
            && target.ProviderAnnotations.Count == 0)
        {
            return true;
        }

        // WHY: The boolean explicit-backfill marker requests review, not a new
        // storage shape. Both metadata contracts must still be fully supported.
        return MySqlSafeMigrationColumnMetadata.TryCreate(source, out var sourceMetadata)
            && MySqlSafeMigrationColumnMetadata.TryCreate(target, out var targetMetadata)
            && sourceMetadata.GuidFormat == targetMetadata.GuidFormat
            && sourceMetadata.ValueGenerationStrategy == targetMetadata.ValueGenerationStrategy;
    }

    /// <summary>Checks supported intrinsic facets and validates only present immutable provider metadata.</summary>
    private static bool SupportsRetainedColumnProof(
        ExpectedColumnDefinition definition
    ) => SafeMigrationColumnRepairHelper.HasRepairableIntrinsicShape(definition)
        && (definition.ProviderAnnotations.Count == 0 || MySqlSafeMigrationColumnMetadata.TryCreate(definition, out _));

    SafeMigrationProviderAnalysis ISafeMigrationProjectedColumnAnalyzer.ValidateProjectedAlterColumn(
        AlterColumnIntent intent,
        ExpectedColumnDefinition source,
        SafeMigrationProjectedAlterColumnContext context,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        if (projectedAnalysis.ObservedState == SafeMigrationObservedState.Matching)
        {
            return projectedAnalysis;
        }

        if (!HasSafeProjectedColumnSession(
                source,
                intent.Definition,
                liveAnalysis,
                context.CanReuseLiveProof,
                context.HasCompleteTable))
        {
            return DifferentProjectedTransition();
        }

        if (!context.HasCompleteTable)
        {
            if (SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(
                    source,
                    intent.Definition,
                    StringComparer.OrdinalIgnoreCase)
                && context.CanReuseLiveProof)
            {
                return projectedAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
                    ? projectedAnalysis.WithRepairPreservesPhysicalKeys()
                    : projectedAnalysis;
            }

            if (context is { PreservesLiveValueDomain: true, HasDataMutation: false, HasForeignKeyDependency: false }
                && HasSafeRetainedAlterValueDomain(intent, source, context.Table, liveAnalysis))
            {
                // WHY: A certified widening does not populate the larger
                // domain. The immutable original declaration remains a row
                // proof only while Core certifies every intervening step.
                return new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.Different,
                    SafeMigrationRepairCapability.Safe,
                    postconditionSatisfied: false,
                    "projected_retained_column_domain_safe",
                    SafeMigrationOperationalImpact.TableRewritePossible,
                    differences: null)
                {
                    RepairPreservesLiveValueDomain = true,
                }.WithRepairPreservesPhysicalKeys();
            }

            // WHY: Independent source-bound proofs do not compose by themselves:
            // earlier VARCHAR targets also consume row and composite-key bytes.
            // Recheck their cumulative shape without repeating row scans.
            return liveAnalysis is
            {
                ObservedState: SafeMigrationObservedState.Different,
                RepairCapability: SafeMigrationRepairCapability.Safe
            }
                && !(context.HasDataMutation && liveAnalysis.RequiresLiveDataProof)
                && !context.HasForeignKeyDependency
                && HasSafeCumulativeAlterPhysicalShape(intent, context.Table, liveAnalysis)
                    ? liveAnalysis.WithRepairPreservesPhysicalKeys()
                    : DifferentProjectedTransition();
        }

        if (SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(
                source,
                intent.Definition,
                StringComparer.OrdinalIgnoreCase))
        {
            return projectedAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
                ? projectedAnalysis.WithRepairPreservesPhysicalKeys()
                : projectedAnalysis;
        }

        if (!context.ProvesEmpty
            || context.HasForeignKeyDependency
            || !MySqlProjectedColumnTransition.IsSafe(
                intent,
                source,
                context.Table,
                liveAnalysis.IndexPhysicalEnvironment,
                _typeMappingSource,
                context.CanReuseCreationCharacterSet))
        {
            return DifferentProjectedTransition();
        }

        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            postconditionSatisfied: false,
            "projected_column_transition_safe",
            SafeMigrationOperationalImpact.TableRewritePossible,
            differences: null).WithRepairPreservesPhysicalKeys();
    }

    /// <summary>Retains captured session prerequisites even when projection proves an empty table.</summary>
    private bool HasSafeProjectedColumnSession(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target,
        SafeMigrationProviderAnalysis liveAnalysis,
        bool canReuseLiveProof,
        bool useCreationEnvironment
    )
    {
        var capturedSafeSource = canReuseLiveProof
            && liveAnalysis is
            {
                ObservedState: SafeMigrationObservedState.Different,
                RepairCapability: SafeMigrationRepairCapability.Safe
            };

        var environment = useCreationEnvironment
            ? liveAnalysis.IndexPhysicalEnvironment?.NewTableEnvironment ?? liveAnalysis.IndexPhysicalEnvironment
            : liveAnalysis.IndexPhysicalEnvironment;

        // WHY: Empty and retained-domain proofs do not exclude rows arriving
        // before MODIFY. Permissive engines can fabricate an unintended value.
        if (source.IsNullable
            && !target.IsNullable
            && (!environment?.StrictSqlMode ?? !capturedSafeSource))
        {
            return false;
        }

        // WHY: Legacy TIMESTAMP assignment semantics apply even to authored
        // non-null defaults and already-required columns. Only captured session
        // facts, or a still-current source-bound Safe result, qualify repair.
        if (RequiresProjectedTimestampDefaults(target)
            && !(_projectedExplicitTimestampDefaults.TryGetValue(liveAnalysis, out var explicitDefaults)
                ? explicitDefaults
                : capturedSafeSource))
        {
            return false;
        }

        return !MySqlSafeMigrationCatalogSqlBuilder.RequiresNativeEnumNoBackslashEscapes(target)
            || (_projectedColumnSessionEnvironments.TryGetValue(liveAnalysis, out var nativeEnvironment)
                ? nativeEnvironment.NoBackslashEscapes
                : capturedSafeSource);
    }

    /// <summary>Qualifies resolved temporal types without extending the public index environment.</summary>
    private bool RequiresProjectedTimestampDefaults(
        ExpectedColumnDefinition definition
    ) => MySqlSafeMigrationCatalogSqlBuilder.RequiresExplicitTimestampDefaults(
        definition,
        MySqlProjectedColumnTransition.ResolveStoreType(definition, _typeMappingSource));

    /// <summary>Reads bounded storage/session facts on the connection that owns the immutable analysis.</summary>
    private async Task<ProjectedColumnSessionEnvironment?> ReadProjectedColumnSessionEnvironmentAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        int maximumPayloadBytes,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var requiredAddTables = operations
            .Where(static operation => operation.Intent is EnsureColumnIntent intent
                && RequiresProjectedMissingAddQualification(intent.Definition))
            .Select(static operation => ((EnsureColumnIntent)operation.Intent).Table)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requiredAddTables.Length == 0
            && !operations.Any(operation => operation.Intent is AlterColumnIntent intent
                && (RequiresProjectedTimestampDefaults(intent.Definition)
                    || MySqlSafeMigrationCatalogSqlBuilder.RequiresNativeEnumNoBackslashEscapes(intent.Definition)))
            && !operations.Any(static operation => operation.Intent is EnsureColumnIntent ensure
                && MySqlSafeMigrationCatalogSqlBuilder.RequiresNativeEnumNoBackslashEscapes(ensure.Definition)))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        ApplyCommandTimeout(command, commandTimeout);
        var isMariaDb = MySqlServerVersion.AutoDetect(connection, MySqlServerVersionCompatibilityMode.AllowUnsupported)
            .IsMariaDb;

        command.CommandText = "SELECT @@default_storage_engine, @@SESSION.sql_mode, "
            + "@@SESSION.explicit_defaults_for_timestamp, "
            + (isMariaDb ? "@@SESSION.check_constraint_checks;" : "1;");

        bool defaultEngineIsInnoDb;
        bool strict;
        bool explicitTimestampDefaults;
        bool checksEnforced;
        bool backslashEscapesEnabled;
        bool doubleQuotesAreLiteral;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)
                || reader.IsDBNull(0)
                || reader.IsDBNull(2))
            {
                throw new InvalidOperationException("MySQL did not return its column repair environment.");
            }

            defaultEngineIsInnoDb = StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(0), "InnoDB");
            var sqlMode = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            strict = sqlMode
                .Split(',')
                .Any(static mode => mode
                        .AsSpan()
                        .Trim()
                        .Equals("STRICT_TRANS_TABLES", StringComparison.OrdinalIgnoreCase)
                    || mode
                        .AsSpan()
                        .Trim()
                        .Equals("STRICT_ALL_TABLES", StringComparison.OrdinalIgnoreCase));

            var enabled = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            var checks = reader.IsDBNull(3) ? -1 : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
            if (enabled is not 0 and not 1
                || checks is not 0 and not 1
                || await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("MySQL returned an unknown column repair environment.");
            }

            explicitTimestampDefaults = enabled == 1;
            checksEnforced = checks == 1;
            backslashEscapesEnabled = !sqlMode
                .Split(',')
                .Any(static mode => mode
                    .AsSpan()
                    .Trim()
                    .Equals("NO_BACKSLASH_ESCAPES", StringComparison.OrdinalIgnoreCase));

            doubleQuotesAreLiteral = !sqlMode
                .Split(',')
                .Any(static mode => mode
                    .AsSpan()
                    .Trim()
                    .Equals("ANSI_QUOTES", StringComparison.OrdinalIgnoreCase));
        }

        var innoDbTables = new HashSet<string>(StringComparer.Ordinal);
        await SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            requiredAddTables.Length,
            maximumPayloadBytes,
            commandTimeout,
            (probe, offset) =>
            {
                var parameterizer = new MySqlCatalogQueryParameterizer(probe, _typeMappingSource);
                var selections = new List<string>();
                var ownerParameters = new List<string>();
                var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
                var sqlBytes = Encoding.UTF8.GetByteCount(
                    BuildRequiredColumnAddOwnerEngineQuery(string.Empty, string.Empty));

                while (offset + selections.Count < requiredAddTables.Length
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var checkpoint = parameterizer.Capture();
                    var ordinal = offset + selections.Count;
                    var ownerParameter = parameterizer.AddString(requiredAddTables[ordinal]);
                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)} AS ordinal, "
                        + $"{ownerParameter} AS owner";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + Encoding.UTF8.GetByteCount(ownerParameter)
                        + (selections.Count == 0 ? 0 : separatorBytes + 2);

                    var payload = checked(sqlBytes + selectionBytes + parameterizer.Utf8PayloadBytes);
                    if (!SafeMigrationCatalogQueryLimits.Exceeded(parameterizer.Count, payload, maximumPayloadBytes))
                    {
                        selections.Add(selection);
                        ownerParameters.Add(ownerParameter);
                        sqlBytes += selectionBytes;

                        continue;
                    }

                    var parameters = parameterizer.Count;
                    parameterizer.Rollback(checkpoint);
                    if (selections.Count == 0)
                    {
                        throw SafeMigrationCatalogQueryLimits.OversizedOperation(offset, parameters, payload);
                    }

                    break;
                }

                probe.CommandText = BuildRequiredColumnAddOwnerEngineQuery(
                    string.Join(SafeMigrationCatalogQueryLimits.Separator, selections),
                    string.Join(", ", ownerParameters));

                return new SafeMigrationCatalogProbeStatement(selections.Count, parameterizer.Utf8PayloadBytes, offset);
            },
            async (reader, offset, count, token) =>
            {
                var row = 0;
                while (await reader.ReadAsync(token))
                {
                    if (row >= count
                        || reader.GetInt32(0) != offset + row
                        || reader.IsDBNull(1))
                    {
                        throw new InvalidOperationException("MySQL returned an unowned column-add engine fact.");
                    }

                    var innoDb = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                    if (innoDb is not 0 and not 1)
                    {
                        throw new InvalidOperationException("MySQL returned an unknown column-add engine fact.");
                    }

                    if (innoDb == 1)
                    {
                        innoDbTables.Add(requiredAddTables[offset + row]);
                    }

                    row++;
                }

                if (row != count)
                {
                    throw new InvalidOperationException("MySQL returned an incomplete column-add engine inventory.");
                }
            },
            cancellationToken);

        return new ProjectedColumnSessionEnvironment(
            strict,
            defaultEngineIsInnoDb,
            explicitTimestampDefaults,
            checksEnforced,
            backslashEscapesEnabled,
            doubleQuotesAreLiteral,
            innoDbTables);
    }

    /// <summary>Binds native engine evidence to authored ordinals without folding unrelated owner identities.</summary>
    /// <param name="authoredOwners">The trusted bounded SELECT of fixed ordinals and bound owner parameters.</param>
    /// <param name="ownerParameters">The same bounded owner parameter names for the indexed catalog prefilter.</param>
    /// <returns>Exactly one engine fact per request, retaining native mode-zero/one/two owner semantics.</returns>
    internal static string BuildRequiredColumnAddOwnerEngineQuery(
        string authoredOwners,
        string ownerParameters
    )
    {
        ArgumentNullException.ThrowIfNull(authoredOwners);
        ArgumentNullException.ThrowIfNull(ownerParameters);

        var tableMatches = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
            "physical_owner.TABLE_NAME",
            "source_owner.owner");

        var schemaMatches = MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
            "physical_owner.TABLE_SCHEMA",
            "DATABASE()");

        // WHY: Native folding can change catalog spelling. Return the fixed
        // request ordinal, not a physical name used as an authored cache key.
        return "SELECT source_owner.ordinal, EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES physical_owner "
            + "WHERE physical_owner.TABLE_SCHEMA = DATABASE() AND physical_owner.TABLE_TYPE = 'BASE TABLE' "
            + "AND UPPER(physical_owner.ENGINE) = 'INNODB' "
            + $"AND physical_owner.TABLE_NAME IN ({ownerParameters}) "
            + "AND physical_owner.TABLE_NAME = source_owner.owner "
            + $"AND {tableMatches} AND {schemaMatches}) FROM ({authoredOwners}) source_owner ORDER BY 1;";
    }

    /// <summary>Binds captured session/storage facts to final analysis identities, never inferred aliases.</summary>
    private void AttachProjectedColumnSessionEnvironment(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses,
        ProjectedColumnSessionEnvironment? environment
    )
    {
        if (environment is null)
        {
            return;
        }

        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal].Intent is AlterColumnIntent intent
                && RequiresProjectedTimestampDefaults(intent.Definition))
            {
                _projectedExplicitTimestampDefaults[analyses[ordinal]] = environment.ExplicitTimestampDefaults;
            }

            if (operations[ordinal].Intent is EnsureColumnIntent ensure
                && (RequiresProjectedMissingAddQualification(ensure.Definition)
                    || MySqlSafeMigrationCatalogSqlBuilder.RequiresNativeEnumNoBackslashEscapes(ensure.Definition)))
            {
                _projectedColumnSessionEnvironments[analyses[ordinal]] = environment;
                if (_projectedRequiredAddCapsByOperation is { } capabilities
                    && capabilities.TryGetValue(operations[ordinal], out var protocol))
                {
                    (_projectedRequiredAddCapsByAnalysis ??=
                        new Dictionary<SafeMigrationProviderAnalysis, RequiredColumnAddCapabilities>(
                            ReferenceEqualityComparer.Instance))[analyses[ordinal]] = protocol;
                }
            }

            if (operations[ordinal].Intent is AlterColumnIntent alter
                && MySqlSafeMigrationCatalogSqlBuilder.RequiresNativeEnumNoBackslashEscapes(alter.Definition))
            {
                _projectedColumnSessionEnvironments[analyses[ordinal]] = environment;
            }
        }

        _projectedRequiredAddCapsByOperation?.Clear();
    }

    private sealed record ProjectedColumnSessionEnvironment(
        bool StrictSqlMode,
        bool DefaultEngineIsInnoDb,
        bool ExplicitTimestampDefaults,
        bool ChecksEnforced,
        bool BackslashEscapesEnabled,
        bool DoubleQuotesAreLiteral,
        IReadOnlySet<string> ExistingInnoDbTables
    )
    {
        /// <summary>Gets the original native literal mode without duplicating a stored session flag.</summary>
        internal bool NoBackslashEscapes => !BackslashEscapesEnabled;
    }

    /// <summary>Records enriched producer plans without deriving feature support from SQL or versions.</summary>
    private void CaptureProjectedRequiredAddPlans(
        SafeMigrationOperation[] operations,
        MySqlSafeMigrationRuntimePlan[] plans
    )
    {
        for (var ordinal = 0; ordinal < operations.Length; ordinal++)
        {
            if (plans[ordinal].RequiredColumnAddPlan is { } protocol)
            {
                (_projectedRequiredAddCapsByOperation ??=
                    new Dictionary<SafeMigrationOperation, RequiredColumnAddCapabilities>(
                        ReferenceEqualityComparer.Instance))[operations[ordinal]] = new RequiredColumnAddCapabilities(
                    protocol.SupportsCheck,
                    protocol.BackslashEscapes);
            }
        }
    }

    /// <summary>Limits environmental capture to the provider's missing-only qualification families.</summary>
    private static bool RequiresProjectedMissingAddQualification(
        ExpectedColumnDefinition definition
    ) => MySqlSafeMigrationCatalogSqlBuilder.RequiresRequiredSqlDefaultAddQualification(definition)
        || MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(definition)
        || MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(definition);

    /// <summary>Retains only the two consumed canonical flags, never a protocol's SQL or staging identities.</summary>
    /// <param name="SupportsCheck">Whether canonical provider features enforce CHECK constraints.</param>
    /// <param name="BackslashEscapes">Whether the native member declaration requires standard escape decoding.</param>
    private readonly record struct RequiredColumnAddCapabilities(
        bool SupportsCheck,
        bool BackslashEscapes
    );

    private static SafeMigrationProviderAnalysis DifferentProjectedTransition() => new(
        SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "projected_column_transition_unproven");
}
