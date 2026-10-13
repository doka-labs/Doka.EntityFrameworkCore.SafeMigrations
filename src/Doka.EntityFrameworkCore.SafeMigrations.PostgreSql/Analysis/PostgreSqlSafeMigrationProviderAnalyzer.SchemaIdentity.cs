namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private const string SchemaIdentityMismatchCode = "schema_search_path_mismatch";
    private string? _currentSchema;
    private readonly HashSet<int> _schemaIdentityMismatchOrdinals = [];
    private int? _schemaIdentityOriginOrdinal;
    private IReadOnlyList<MigrationOperation>? _analysisOperationStream;

    /// <inheritdoc />
    StringComparer ISafeMigrationProviderObjectIdentityNormalizer.IdentifierComparer => StringComparer.Ordinal;

    /// <inheritdoc />
    bool ISafeMigrationProviderObjectIdentityNormalizer.IndexUsesSchemaScopedIdentity => true;

    /// <inheritdoc />
    bool ISafeMigrationProviderObjectIdentityNormalizer.KeyConstraintsUseSchemaScopedBackingIndexNames => true;

    /// <inheritdoc />
    string ISafeMigrationProviderObjectIdentityNormalizer.NormalizeIdentifier(string identifier) => identifier;

    /// <inheritdoc />
    string? ISafeMigrationProviderObjectIdentityNormalizer.NormalizeSchema(string? schema)
        => PostgreSqlSafeMigrationSchemaIdentity.Normalize(schema, _currentSchema);

    /// <inheritdoc />
    bool ISafeMigrationProviderObjectIdentityNormalizer.IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis)
        => analysis.Code == SchemaIdentityMismatchCode
            && (analysis.IsInvariantUnsupported
                || !(_schemaIdentityOriginOrdinal is { } origin && _projectedOperationOrdinal > origin
                    || _triggerOriginOrdinal is { } triggerOrigin && _projectedOperationOrdinal > triggerOrigin));

    /// <summary>Scopes postflight aliases to the final stable path without guessing historical owners.</summary>
    /// <param name="operations">The complete executed stream, including ordinary provider boundaries.</param>
    /// <returns>An immutable schema/ordinal snapshot used only for final-writer reduction.</returns>
    public ISafeMigrationProviderObjectIdentityNormalizer GetPostflightIdentityNormalizer(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        bool[]? unproven = null;
        var laterIdentityBoundary = false;
        HashSet<string>? bindings = null;
        for (var ordinal = operations.Count - 1; ordinal >= 0; ordinal--)
        {
            var operation = operations[ordinal];
            if (operation is SafeMigrationOperation safeOperation)
            {
                if (laterIdentityBoundary)
                {
                    bindings ??= new HashSet<string>(StringComparer.Ordinal);
                    bindings.Clear();
                    PostgreSqlSafeMigrationSchemaIdentity.CollectUnqualifiedTables(safeOperation.Intent, bindings);
                    if (bindings.Count > 0)
                    {
                        (unproven ??= new bool[operations.Count])[ordinal] = true;
                    }
                }

                laterIdentityBoundary |= safeOperation.Intent is EnsureSchemaIntent or DropSchemaIntent
                    || PostflightOperationMayChangeSchemaIdentity(safeOperation);

                continue;
            }

            laterIdentityBoundary |= operation is not SafeMigrationDesignTimeServicesRequiredOperation
                && !ProviderOperationPreservesTableState(operation);
        }

        // WHY: A final current_schema() does not reveal which schema an earlier
        // unqualified operation used before SET search_path or schema DDL. Keep
        // its postcondition unknown instead of falsely superseding another owner.

        return new PostflightSchemaIdentity(_currentSchema, unproven);
    }

    /// <summary>Uses captured callback risk without parsing user functions or inventing execution history.</summary>
    /// <param name="operation">The safe operation which could have executed before final verification.</param>
    /// <returns>Whether its known native command shape can reach an identity-changing callback.</returns>
    private bool PostflightOperationMayChangeSchemaIdentity(SafeMigrationOperation operation)
    {
        if (PostgreSqlSafeMigrationSchemaIdentity.HasUnqualifiedNativeBindings(operation.Intent))
        {
            // WHY: These commands freeze current_schema() and reprove native
            // owners after their baseline. Their callback cannot successfully
            // finish under another creation schema; fully qualified commands
            // have no such path fence and require historical qualification.

            return false;
        }

        if (operation.Intent is ModelManagedDataIntent)
        {
            return _dmlTriggerRisk != PostgreSqlTriggerRisk.None;
        }

        if (operation.Intent is RenameTableIntent table
                && (table.NewName is null || table.NewName == table.Name)
                && (table.NewSchema is null || table.NewSchema == table.Schema)
            || operation.Intent is RenameIndexIntent index && index.Name == index.NewName)
        {
            return false;
        }

        // WHY: A final matching catalog does not prove an earlier qualified
        // command skipped execution. Active/hidden callbacks can change the
        // session path while leaving that explicitly qualified owner valid.
        // Reuse the already captured risk and known backfill command shapes;
        // certified command-free operations do not introduce a boundary.

        return _eventTriggerRisk != PostgreSqlTriggerRisk.None
            || _dmlTriggerRisk != PostgreSqlTriggerRisk.None && (operation.Intent switch
            {
                EnsureColumnIntent column => operation.Policy == SafeMigrationPolicy.RepairIfSafe
                    && !column.Definition.IsNullable
                    && column.Definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None,
                AlterColumnIntent column => column.OldDefinition?.IsNullable == true
                    && !column.Definition.IsNullable
                    && column.Definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None,
                _ => false,
            });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<IModel?>? targetModels,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);
        if (targetModels is not null && targetModels.Count != operations.Count)
        {
            throw new ArgumentException("The target-model list must match the operation stream.", nameof(targetModels));
        }

        if (_analysisOperationStream is not null)
        {
            throw new InvalidOperationException("A PostgreSQL ordered analysis invocation is already active.");
        }

        // WHY: The established interface exposes ordinary-operation barriers
        // which the safe-only overload cannot see. Target models are not used
        // to strengthen contracts or change PostgreSQL model ownership.
        _analysisOperationStream = operations;
        try
        {
            return await AnalyzeAsync(context, operations.OfType<SafeMigrationOperation>().ToArray(),
                cancellationToken);
        }
        finally
        {
            _analysisOperationStream = null;
        }
    }

    /// <summary>Captures the proven active schema required to compare qualified and unqualified identities.</summary>
    private async Task CaptureCurrentSchemaIdentityAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        _currentSchema = null;
        _schemaIdentityMismatchOrdinals.Clear();
        _schemaIdentityOriginOrdinal = null;

        var hasUnqualifiedOwner = false;
        var hasQualifiedOwner = false;
        foreach (var operation in operations)
        {
            if (operation is null)
            {
                throw new ArgumentException("The operation batch cannot contain null entries.", nameof(operations));
            }

            var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(operation.Intent);
            hasUnqualifiedOwner |= owner is { Table: not null, Schema: null };
            hasQualifiedOwner |= owner.Schema is not null;
            if (operation.Intent is EnsureForeignKeyIntent foreignKey)
            {
                hasUnqualifiedOwner |= foreignKey.Definition.PrincipalSchema is null;
                hasQualifiedOwner |= foreignKey.Definition.PrincipalSchema is not null;
            }

            if (operation.Intent is EnsureTableIntent table)
            {
                foreach (var foreignKeyDefinition in table.Definition.ForeignKeys)
                {
                    hasUnqualifiedOwner |= foreignKeyDefinition.PrincipalSchema is null;
                    hasQualifiedOwner |= foreignKeyDefinition.PrincipalSchema is not null;
                }
            }

            if (operation.Intent is DeleteModelManagedDataIntent delete)
            {
                foreach (var foreignKeyDefinition in delete.ForeignKeys)
                {
                    hasUnqualifiedOwner |= foreignKeyDefinition.Schema is null;
                    hasQualifiedOwner |= foreignKeyDefinition.Schema is not null;
                }
            }
        }

        if (_analysisOperationStream is not null)
        {
            foreach (var operation in _analysisOperationStream)
            {
                if (operation is SafeMigrationOperation || ProviderOperationPreservesTableState(operation))
                {
                    continue;
                }

                var owner = PostgreSqlSafeMigrationSchemaIdentity.GetProviderOwner(operation);
                hasUnqualifiedOwner |= owner is { Table: not null, Schema: null };
                hasQualifiedOwner |= owner is { Table: not null, Schema: not null };
                if (operation is AddForeignKeyOperation foreignKey)
                {
                    hasUnqualifiedOwner |= foreignKey.PrincipalSchema is null;
                    hasQualifiedOwner |= foreignKey.PrincipalSchema is not null;
                }

                if (operation is CreateTableOperation table)
                {
                    for (var index = 0; index < table.ForeignKeys.Count; index++)
                    {
                        hasUnqualifiedOwner |= table.ForeignKeys[index].PrincipalSchema is null;
                        hasQualifiedOwner |= table.ForeignKeys[index].PrincipalSchema is not null;
                    }
                }
            }
        }

        if (!hasUnqualifiedOwner || !hasQualifiedOwner)
        {
            return;
        }

        // WHY: A qualifier supplied by an author is not evidence of the active
        // search path. Capture it inside this invocation's live analysis scope;
        // do not retain a connection-string assumption between analyses.
        _currentSchema = await GetCurrentSchemaAsync(connection, commandTimeout, cancellationToken);
    }

    /// <summary>Preserves the identity-first diagnostic from the catalog-only prerequisite phase.</summary>
    private SafeMigrationProviderAnalysis ShortCircuitAnalysis(int ordinal, SafeMigrationObservedState state)
        => new(state, SafeMigrationRepairCapability.None, false,
            _schemaIdentityMismatchOrdinals.Contains(ordinal) ? SchemaIdentityMismatchCode
                : _indexInputShortCircuitCodes?.TryGetValue(ordinal, out var code) == true ? code
                : ClassificationCode(state));

    /// <summary>Prevents reuse of initial schema aliases after an executed schema/search-path boundary.</summary>
    private SafeMigrationProviderAnalysis? QualifyProjectedSchemaIdentity(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        var origin = _schemaIdentityOriginOrdinal
            ?? (analysis.Code == SchemaIdentityMismatchCode ? _triggerOriginOrdinal : null);

        if (origin is not { } ordinal || analysis.IsInvariantUnsupported
            || analysis.ObservedState == SafeMigrationObservedState.Unsupported
                && analysis.Code != SchemaIdentityMismatchCode)
        {
            return null;
        }

        var bindings = new HashSet<string>(StringComparer.Ordinal);
        PostgreSqlSafeMigrationSchemaIdentity.CollectUnqualifiedTables(operation.Intent, bindings);
        if (bindings.Count == 0)
        {
            return null;
        }

        // WHY: Creating a schema can activate a previously missing search-path
        // entry, dropping one can select a later schema, and user triggers can
        // change the effective path. Preserve existing trigger diagnostics for
        // other states while recording this identity-specific mutable boundary.

        return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false, "projected_schema_identity_state_unknown")
        {
            ProviderDeferredOriginOrdinal = ordinal,
        };
    }

    /// <summary>Provides stable exact identifier rules and bounded historical-identity exclusions.</summary>
    private sealed class PostflightSchemaIdentity(string? currentSchema, bool[]? unproven)
        : ISafeMigrationProviderObjectIdentityNormalizer
    {
        /// <inheritdoc />
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        /// <inheritdoc />
        public bool IndexUsesSchemaScopedIdentity => true;

        /// <inheritdoc />
        public bool KeyConstraintsUseSchemaScopedBackingIndexNames => true;

        /// <inheritdoc />
        public string NormalizeIdentifier(string identifier) => identifier;

        /// <inheritdoc />
        public string? NormalizeSchema(string? schema)
            => PostgreSqlSafeMigrationSchemaIdentity.Normalize(schema, currentSchema);

        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis)
            => analysis.Code == SchemaIdentityMismatchCode;

        /// <inheritdoc />
        public bool IsPostflightIdentityProven(SafeMigrationIntent intent, int ordinal)
        {
            ArgumentNullException.ThrowIfNull(intent);

            return unproven is null || !unproven[ordinal];
        }
    }
}
