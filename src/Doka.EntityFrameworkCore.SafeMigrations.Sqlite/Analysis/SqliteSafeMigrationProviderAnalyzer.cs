namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Analyzes SQLite catalog state and ordered migration transitions.</summary>
internal sealed partial class SqliteSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer,
    ISafeMigrationProviderTargetModelAnalyzer, ISafeMigrationProviderObjectIdentityNormalizer,
    ISafeMigrationProviderOperationProjection, ISafeMigrationProjectedColumnAnalyzer,
    ISafeMigrationLivePrerequisiteSource, ISafeMigrationProjectedDependencyAnalyzer
{
    private static readonly StringComparer s_identifierComparer = SqliteIdentifierComparer.Instance;

    private readonly SqliteSafeMigrationSqlExpressionRenderer _expressionRenderer;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    /// <summary>Initializes the SQLite provider analyzer.</summary>
    /// <param name="typeMappingSource">The active provider's literal and store-type mapping source.</param>
    /// <param name="sqlGenerationHelper">The active provider's SQL delimiter helper.</param>
    public SqliteSafeMigrationProviderAnalyzer(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _expressionRenderer = new SqliteSafeMigrationSqlExpressionRenderer(typeMappingSource, sqlGenerationHelper);
    }

    /// <inheritdoc />
    public string ProviderId => SqliteSafeMigrationSqlExpressionRenderer.ProviderId;

    /// <inheritdoc />
    public StringComparer IdentifierComparer => s_identifierComparer;

    /// <inheritdoc />
    public char? CapturedSqlIdentifierDelimiter => '"';

    /// <inheritdoc />
    public bool IndexUsesSchemaScopedIdentity => true;

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(
        AlterColumnIntent intent,
        ExpectedColumnDefinition source,
        SafeMigrationProjectedAlterColumnContext context,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        // WHY: Core verifies provider-neutral shape, but SQLite owns the
        // AUTOINCREMENT contract. Accepted creation/ensure evidence must not
        // authorize a generation change that live analysis would reject.
        return projectedAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
            && !SqliteColumnRepairProof.HasLosslessShape(source, intent.Definition)
                ? new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.Different,
                    SafeMigrationRepairCapability.None,
                    postconditionSatisfied: false,
                    "projected_different",
                    SafeMigrationOperationalImpact.NotApplicable,
                    differences: projectedAnalysis.Differences)
                : projectedAnalysis;
    }

    /// <inheritdoc />
    public void ValidateContext(
        DbContext context
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Database.GetDbConnection() is not SqliteConnection)
        {
            throw new InvalidOperationException("SQLite SafeMigrations requires a Microsoft.Data.Sqlite connection.");
        }
    }

    /// <inheritdoc />
    public Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var connection = context.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT sqlite_version();";
        var version = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("SQLite returned no engine version.");

        if (!SqliteSafeMigrationLimits.IsSupportedVersion(version))
        {
            throw new NotSupportedException(
                $"SQLite SafeMigrations requires SQLite {SqliteSafeMigrationLimits.MinimumVersion} or later; "
                + $"the connected engine reports {version}.");
        }

        return Task.FromResult(new SafeMigrationProviderEnvironment(ProviderId, "sqlite", version));
    }

    /// <inheritdoc />
    public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Database.CurrentTransaction is not null)
        {
            return Task.FromResult<IAsyncDisposable>(NoOpAsyncDisposable.Instance);
        }

        var connection = (SqliteConnection)context.Database.GetDbConnection();
        var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        IDbContextTransaction contextTransaction;
        try
        {
            contextTransaction = context.Database.UseTransaction(transaction)
                ?? throw new InvalidOperationException("SQLite did not accept the preflight read transaction.");
        }
        catch
        {
            transaction.Dispose();
            throw;
        }

        return Task.FromResult<IAsyncDisposable>(new TransactionAnalysisScope(contextTransaction, transaction));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        var targetModel = context.GetService<IDesignTimeModel>().Model;
        var targetModels = Enumerable
            .Repeat<IModel?>(targetModel, operations.Count)
            .ToArray();

        var migrationOperations = operations
            .Cast<MigrationOperation>()
            .ToArray();

        return AnalyzeAsync(context, migrationOperations, targetModels, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<IModel?>? targetModels,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);
        if (targetModels is not null
            && targetModels.Count != operations.Count)
        {
            throw new ArgumentException(
                "The SQLite target-model sequence must align with the safe-operation sequence.",
                nameof(targetModels));
        }

        cancellationToken.ThrowIfCancellationRequested();

        _livePrerequisiteSource = null;
        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction);
        ResetDropOnlyProjection(snapshot);
        var results = Analyze(context, operations, targetModels, snapshot, cancellationToken);

        // WHY: Publish only complete immutable capture, never partially analyzed
        // model wishes. Runner projection consumes these facts after analysis;
        // runtime batches inject their own transaction-owned current snapshot.
        _livePrerequisiteSource = new SqliteLivePrerequisiteSource(snapshot);

        return Task.FromResult<IReadOnlyList<SafeMigrationProviderAnalysis>>(results);
    }

    /// <summary>Analyzes cumulative structural windows without reusing a certificate across target models.</summary>
    private ReadOnlyCollection<SafeMigrationProviderAnalysis> Analyze(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<IModel?>? targetModels,
        SqliteCatalogSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        var fallbackModel = context.GetService<IDesignTimeModel>().Model;
        var runtimeInitializer = context.GetService<IModelRuntimeInitializer>();
        var initializedModels = new Dictionary<IModel, IModel>(ReferenceEqualityComparer.Instance);
        Dictionary<IModel, SqliteRebuildArtifactContract>? modelArtifacts = null;
        var cumulativeOperations = new List<MigrationOperation>(operations.Count);
        var contracts = new SqliteRebuildArtifactContract?[operations.Count];
        var results = new List<SafeMigrationProviderAnalysis>(
            operations.Count(static operation => operation is SafeMigrationOperation));

        // WHY: A single trailing scan identifies whether the first window is
        // also the last, even before ordinary SQL/DML suffixes. That common
        // case needs neither a model-sharing map nor an empty base contract.
        var lastStructuralIndex = operations.Count - 1;
        while (lastStructuralIndex >= 0
               && !SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(
                   operations[lastStructuralIndex]))
        {
            lastStructuralIndex--;
        }

        // WHY: Keep structural window boundaries and per-operation contract/result slots aligned.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < operations.Count;)
        {
            var targetModel = GetTargetModel(index);
            if (!initializedModels.TryGetValue(targetModel, out var initializedModel))
            {
                initializedModel = runtimeInitializer.Initialize(targetModel, designTime: true);
                initializedModels.Add(targetModel, initializedModel);
            }

            if (SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(operations[index]))
            {
                var segmentStart = index;
                while (index < operations.Count
                       && ReferenceEquals(GetTargetModel(index), targetModel)
                       && SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(operations[index]))
                {
                    cumulativeOperations.Add(operations[index]);
                    index++;
                }

                var segment = new MigrationOperation[index - segmentStart];
                // WHY: Keep structural window boundaries and per-operation contract/result slots aligned.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var ordinal = 0; ordinal < segment.Length; ordinal++)
                {
                    segment[ordinal] = operations[segmentStart + ordinal];
                }

                SqliteRebuildArtifactContract contract;
                if (modelArtifacts is null && index > lastStructuralIndex)
                {
                    contract = SqliteRebuildArtifactContract.FromModel(
                        initializedModel, cumulativeOperations, segment);
                }
                else
                {
                    // WHY: Repeated windows often use the same initialized model.
                    // Only its immutable table artifacts may be shared, and only
                    // inside this invocation. Prefix/window evidence is always new.
                    modelArtifacts ??= new Dictionary<IModel, SqliteRebuildArtifactContract>(
                        ReferenceEqualityComparer.Instance);

                    if (!modelArtifacts.TryGetValue(initializedModel, out var capturedModel))
                    {
                        capturedModel = SqliteRebuildArtifactContract.FromModel(initializedModel);
                        modelArtifacts.Add(initializedModel, capturedModel);
                    }

                    contract = capturedModel.WithOperationHistory(cumulativeOperations, segment);
                }

                // WHY: Keep structural window boundaries and per-operation contract/result slots aligned.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var ordinal = segmentStart; ordinal < index; ordinal++)
                {
                    if (operations[ordinal] is SafeMigrationOperation)
                    {
                        contracts[ordinal] = contract;
                    }
                }

                continue;
            }

            cumulativeOperations.Add(operations[index]);
            if (operations[index] is SafeMigrationOperation)
            {
                contracts[index] = SqliteRebuildArtifactContract.FromOperations([operations[index]]);
            }

            index++;
        }

        // WHY: Keep structural window boundaries and per-operation contract/result slots aligned.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operations[index] is SafeMigrationOperation safeOperation)
            {
                results.Add(
                    Analyze(
                        snapshot,
                        connection,
                        transaction,
                        safeOperation,
                        contracts[index]
                        ?? throw new InvalidOperationException("The SQLite rebuild contract sequence is incomplete.")));
            }
        }

        return results.AsReadOnly();

        IModel GetTargetModel(
            int index
        ) => targetModels?[index] ?? fallbackModel;
    }

    /// <inheritdoc />
    public string? NormalizeSchema(
        string? schema
    ) => schema is null || s_identifierComparer.Equals(schema, "main") ? null : schema;

    /// <inheritdoc />
    public string NormalizeIdentifier(
        string identifier
    ) => SqliteIdentifierComparer.Normalize(identifier);

    /// <inheritdoc />
    public bool IsObjectIdentityMismatch(
        SafeMigrationProviderAnalysis analysis
    )
    {
        ArgumentNullException.ThrowIfNull(analysis);

        return StringComparer.Ordinal.Equals(analysis.Code, "database_qualifier_mismatch");
    }

    /// <inheritdoc />
    public bool PreservesExistingTableState(
        MigrationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        // WHY: SQLite executes arbitrary SQL as a provider command and
        // invalidates its runtime catalog cache afterward. Read-only preflight
        // cannot claim that the same SQL preserves earlier catalog evidence.
        return false;
    }

    /// <inheritdoc />
    public bool PreservesUnrelatedColumnAbsence(
        RenameColumnIntent operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        // WHY: SQLite rewrites dependent schema text during RENAME COLUMN, but
        // it cannot create a third, unrelated column in the renamed table.
        return true;
    }

    /// <inheritdoc />
    public bool IsSequenceAwareAnalysis(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(analysis);

        return operation.Intent is DropTableIntent
            && StringComparer.Ordinal.Equals(analysis.Code, "table_drop_foreign_key_dependency");
    }

    /// <summary>Dispatches one operation using the invocation's catalog, connection and rebuild certificate.</summary>
    internal SafeMigrationProviderAnalysis Analyze(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        SafeMigrationOperation operation,
        SqliteRebuildArtifactContract rebuildArtifacts
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(rebuildArtifacts);

        if (!SqliteSafeMigrationLimits.IsSupportedVersion(snapshot.Version))
        {
            return InvariantUnsupported("sqlite_version");
        }

        if (HasForeignQualifier(operation.Intent))
        {
            return Unsupported("database_qualifier_mismatch");
        }

        var tableName = SqliteSafeMigrationOperationClassifier.TableName(operation.Intent);
        if (tableName is not null
            && snapshot.Tables.TryGetValue(tableName, out var targetTable)
            && targetTable.Sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
        {
            var invariantUnsupported = InvariantUnsupportedFeature(operation.Intent);
            if (invariantUnsupported is not null)
            {
                // WHY: Raw SQL may replace a virtual table with an ordinary
                // table, but cannot repair an unsupported target definition.
                return InvariantUnsupported(invariantUnsupported);
            }

            return Unsupported("virtual_table");
        }

        var analysis = operation.Intent switch
        {
            EnsureSchemaIntent value => AnalyzeEnsureSchema(value),
            DropSchemaIntent => InvariantUnsupported("schema_operations"),
            EnsureTableIntent value => AnalyzeEnsureTable(snapshot, value, operation, rebuildArtifacts),
            DropTableIntent value => AnalyzeDropTable(snapshot, value, operation, rebuildArtifacts),
            RenameTableIntent value => AnalyzeRenameTable(snapshot, value),
            EnsureColumnIntent value => AnalyzeEnsureColumn(snapshot, connection, transaction, value),
            AlterColumnIntent value => AnalyzeAlterColumn(snapshot, connection, transaction, value),
            DropColumnIntent value => AnalyzeDropColumn(snapshot, value),
            RenameColumnIntent value => AnalyzeRenameColumn(snapshot, value),
            EnsureIndexIntent value => AnalyzeEnsureIndex(snapshot, connection, transaction, value),
            DropIndexIntent value => AnalyzeDropIndex(snapshot, value),
            RenameIndexIntent value => AnalyzeRenameIndex(snapshot, value),
            EnsurePrimaryKeyIntent value => AnalyzeEnsurePrimaryKey(snapshot, connection, transaction, value),
            DropPrimaryKeyIntent value => AnalyzeDropPrimaryKey(snapshot, value),
            EnsureUniqueConstraintIntent value => AnalyzeEnsureUnique(snapshot, connection, transaction, value),
            DropUniqueConstraintIntent value => AnalyzeDropUnique(snapshot, value),
            EnsureCheckConstraintIntent value =>
                AnalyzeEnsureCheck(snapshot, connection, transaction, value, rebuildArtifacts),
            DropCheckConstraintIntent value => AnalyzeDropCheck(snapshot, value),
            EnsureForeignKeyIntent value => AnalyzeEnsureForeignKey(
                snapshot,
                connection,
                transaction,
                value),
            DropForeignKeyIntent value => AnalyzeDropForeignKey(snapshot, value),
            ModelManagedDataIntent value => AnalyzeModelManagedData(snapshot, connection, transaction, value),
            _ => InvariantUnsupported("operation_kind"),
        };

        var generatedDropBlocker = operation.Intent is DropColumnIntent dropColumn
            && analysis.ObservedState == SafeMigrationObservedState.Matching
                ? AnalyzeGeneratedColumnDrop(snapshot.Tables[dropColumn.Table], dropColumn.Name, dropped: null)
                : null;

        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        var rebuildTable = SqliteSafeMigrationOperationClassifier.RequiresTableRebuild(operation.Intent)
            ? tableName : null;

        var unsupportedRebuild = rebuildTable is not null
            ? rebuildArtifacts.GetUnsupportedRebuildFeature(
                snapshot, rebuildTable, ModelColumnMatches, ModelIndexMatches, ModelExpressionMatches)
            : null;

        if (unsupportedRebuild is not null)
        {
            // WHY: Native absence can become accepted presence earlier in this
            // same batch. Keep the independent metadata-only rebuild refusal
            // conditional on actual projected execution, not on initial state.
            CaptureDropProjectionEvidence(operation, Unsupported(unsupportedRebuild));
        }

        var nativeRebuild = rebuildTable is not null
            && decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair;

        if (nativeRebuild && unsupportedRebuild is not null)
        {
            return generatedDropBlocker ?? Unsupported(unsupportedRebuild);
        }

        if (rebuildTable is not null && unsupportedRebuild is null && snapshot.ForeignKeysEnabled
            && (nativeRebuild || (operation.Intent is DropColumnIntent drop
                && analysis.ObservedState == SafeMigrationObservedState.Missing
                && snapshot.Tables.ContainsKey(drop.Table)
                && rebuildArtifacts.MayRebuildMissingColumn(operation)))
            && HasRetainedRebuildForeignKeyViolation(
                snapshot, connection, transaction, rebuildTable, operation, rebuildArtifacts))
        {
            var rowBlocker = DataBlocked("table_rebuild_foreign_key_violation");

            // WHY: Exact projected column presence cannot discharge an
            // independent violation in retained incoming or outgoing rows.
            // A possible earlier ADD only requests this conditional proof;
            // actual native absence must remain a no-op until accepted presence.
            CaptureDropProjectionEvidence(operation, rowBlocker);
            if (nativeRebuild)
            {
                return generatedDropBlocker ?? rowBlocker;
            }
        }

        if (generatedDropBlocker is not null)
        {
            // WHY: Removing an earlier dependent can discharge this one native
            // blocker, but cannot manufacture rebuild ownership or row safety.
            // Retain the baseline only after those independent checks passed.
            CaptureDropProjectionEvidence(operation, analysis);
        }

        CaptureDropOnlyProjection(operation, analysis);

        return generatedDropBlocker ?? analysis;
    }

    /// <summary>Rejects unsupported definitions independently of transient projected catalog state.</summary>
    private string? InvariantUnsupportedFeature(
        SafeMigrationIntent intent
    ) => intent switch
    {
        EnsureTableIntent value => TableUnsupportedFeature(value.Definition),
        EnsureColumnIntent value => ColumnUnsupportedFeature(value.Definition),
        AlterColumnIntent value => ColumnUnsupportedFeature(value.Definition)
            ?? (value.OldDefinition is null ? null : ColumnUnsupportedFeature(value.OldDefinition)),
        EnsureIndexIntent value => IndexUnsupportedFeature(value.Definition),
        EnsureCheckConstraintIntent value when RenderCheck(value.Definition) is null => "check_expression",
        _ => null,
    };

    private static bool HasForeignQualifier(
        SafeMigrationIntent intent
    )
    {
        var qualifier = intent switch
        {
            EnsureTableIntent value => value.Definition.Schema,
            DropSchemaIntent value => value.Name,
            DropTableIntent value => value.Schema,
            RenameTableIntent value => value.Schema,
            EnsureColumnIntent value => value.Schema,
            AlterColumnIntent value => value.Schema,
            DropColumnIntent value => value.Schema,
            RenameColumnIntent value => value.Schema,
            EnsureIndexIntent value => value.Definition.Schema,
            DropIndexIntent value => value.Schema,
            RenameIndexIntent value => value.Schema,
            EnsurePrimaryKeyIntent value => value.Definition.Schema,
            DropPrimaryKeyIntent value => value.Schema,
            EnsureUniqueConstraintIntent value => value.Definition.Schema,
            DropUniqueConstraintIntent value => value.Schema,
            EnsureCheckConstraintIntent value => value.Definition.Schema,
            DropCheckConstraintIntent value => value.Schema,
            EnsureForeignKeyIntent value => value.Definition.Schema,
            DropForeignKeyIntent value => value.Schema,
            ModelManagedDataIntent value => value.Schema,
            _ => null,
        };

        return !IsMain(qualifier)
            || (intent is EnsureTableIntent table
                && table.Definition.ForeignKeys.Any(foreignKey => !IsMain(foreignKey.PrincipalSchema)))
            || intent is EnsureForeignKeyIntent foreignKey && !IsMain(foreignKey.Definition.PrincipalSchema)
            || (intent is DeleteModelManagedDataIntent deletion
                && deletion.ForeignKeys.Any(foreignKey => !IsMain(foreignKey.Schema)));
    }

    private static bool IsMain(
        string? schema
    ) => schema is null || s_identifierComparer.Equals(schema, "main");

    private static bool IdentifiersEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right
    ) => left.Count == right.Count
        && left
            .Zip(right)
            .All(pair => s_identifierComparer.Equals(pair.First, pair.Second));

    private static bool SqlEquivalent(
        string? left,
        string? right
    ) => SqliteSqlNormalizer.Equivalent(left, right);

    private static string NormalizeStoreType(
        string value
    ) => string
        .Concat(value.Where(static character => !char.IsWhiteSpace(character)))
        .ToUpperInvariant();

    private static string Delimit(
        string identifier
    ) => '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string Join(
        IReadOnlyList<string> values
    ) => Bound(string.Join(",", values));

    private static string Bound(
        string value
    ) => value.Length <= SafeMigrationFacetDifference.MaximumValueLength
        ? value
        : value[..(SafeMigrationFacetDifference.MaximumValueLength - 3)] + "...";

    private static string Display(
        string value
    ) => value.Length == 0 ? "<none>" : Bound(value);

    private static SafeMigrationProviderAnalysis Missing(
        bool postconditionSatisfied
    ) => new(
        SafeMigrationObservedState.Missing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied,
        "classified_missing");

    /// <summary>
    /// Creates matching evidence while keeping physical member names separate from semantic table shape.
    /// </summary>
    /// <param name="postconditionSatisfied">Whether the operation's final condition already holds.</param>
    /// <param name="matchedObjectName">The resolved physical object name, when independently known.</param>
    /// <param name="tableConstraintIdentities">
    /// Optional named members certified from the matched immutable table snapshot.
    /// </param>
    /// <param name="dropLeavesNoGeneratedColumns">
    /// Whether complete native metadata proves no generated member survives this exact DROP.
    /// </param>
    /// <returns>The immutable matching classification with only the supplied identity proofs.</returns>
    private static SafeMigrationProviderAnalysis Matching(
        bool postconditionSatisfied,
        string? matchedObjectName = null,
        IReadOnlyList<SafeMigrationConstraintIdentityBinding>? tableConstraintIdentities = null,
        bool dropLeavesNoGeneratedColumns = false
    ) => new(
        SafeMigrationObservedState.Matching,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied,
        "classified_matching")
    {
        MatchedObjectName = matchedObjectName,
        TableConstraintIdentities = tableConstraintIdentities,
        DropLeavesNoGeneratedColumns = dropLeavesNoGeneratedColumns,
    };

    private static SafeMigrationProviderAnalysis Different(
        string facet,
        string expected,
        string actual,
        string? matchedObjectName = null
    ) => new(
        SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "classified_different",
        SafeMigrationOperationalImpact.NotApplicable,
        [new SafeMigrationFacetDifference(facet, Bound(expected), Bound(actual))])
    {
        MatchedObjectName = matchedObjectName,
    };

    private static SafeMigrationProviderAnalysis Unsupported(
        string code
    ) => new(
        SafeMigrationObservedState.Unsupported,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        code);

    private static SafeMigrationProviderAnalysis InvariantUnsupported(
        string code
    ) => new(
        SafeMigrationObservedState.Unsupported,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        code)
    {
        // WHY: This failure depends on the immutable operation contract or
        // engine version, so preceding raw SQL cannot make it executable.
        IsInvariantUnsupported = true,
    };

    private static SafeMigrationProviderAnalysis DataBlocked(
        string code
    ) => new(
        SafeMigrationObservedState.DataBlocked,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        code);

    private static SafeMigrationProviderAnalysis PrerequisiteMissing() => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "classified_prerequisite_missing");

    private static SafeMigrationProviderAnalysis TransitionReady() => new(
        SafeMigrationObservedState.TransitionReady,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "classified_transition_ready");

    private sealed class TransactionAnalysisScope : IAsyncDisposable
    {
        private readonly IDbContextTransaction _transaction;
        private readonly SqliteTransaction _providerTransaction;

        public TransactionAnalysisScope(
            IDbContextTransaction transaction,
            SqliteTransaction providerTransaction
        )
        {
            _transaction = transaction;
            _providerTransaction = providerTransaction;
        }

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _providerTransaction.DisposeAsync();
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public static NoOpAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
