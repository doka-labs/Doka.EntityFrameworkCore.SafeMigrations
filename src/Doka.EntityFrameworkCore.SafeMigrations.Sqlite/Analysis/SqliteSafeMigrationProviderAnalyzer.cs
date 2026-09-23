namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Analyzes SQLite catalog state and ordered migration transitions.</summary>
internal sealed class SqliteSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer,
    ISafeMigrationProviderTargetModelAnalyzer, ISafeMigrationProviderObjectIdentityNormalizer,
    ISafeMigrationProviderOperationProjection
{
    private static readonly StringComparer s_identifierComparer = SqliteIdentifierComparer.Instance;

    private readonly SqliteSafeMigrationSqlExpressionRenderer _expressionRenderer;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    /// <summary>Initializes the SQLite provider analyzer.</summary>
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

        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction);
        var fallbackModel = context.GetService<IDesignTimeModel>().Model;
        var runtimeInitializer = context.GetService<IModelRuntimeInitializer>();
        var initializedModels = new Dictionary<IModel, IModel>(ReferenceEqualityComparer.Instance);
        var cumulativeOperations = new List<MigrationOperation>(operations.Count);
        var contracts = new SqliteRebuildArtifactContract?[operations.Count];
        var results = new List<SafeMigrationProviderAnalysis>(
            operations.Count(static operation => operation is SafeMigrationOperation));

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

                var contract = SqliteRebuildArtifactContract.FromModel(initializedModel, cumulativeOperations);
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

        return Task.FromResult<IReadOnlyList<SafeMigrationProviderAnalysis>>(results.AsReadOnly());

        IModel GetTargetModel(
            int index
        ) => targetModels?[index] ?? fallbackModel;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = SqliteSafeMigrationCatalog.Read(
            context.Database.GetDbConnection(),
            context.Database.CurrentTransaction?.GetDbTransaction());

        var expectedTables = SafeMigrationExpectedCatalog
            .Create(operations, NormalizeSchema)
            .Where(static table => table.Schema is null)
            .ToDictionary(static table => table.Table, s_identifierComparer);

        var result = new List<SafeMigrationUnexpectedObject>();

        foreach (var table in snapshot.Tables.Values.OrderBy(static value => value.Name, s_identifierComparer))
        {
            if (!expectedTables.TryGetValue(table.Name, out var expectedTable))
            {
                result.Add(
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Table,
                        schema: null,
                        table: null,
                        table.Name,
                        "unexpected_table"));
                continue;
            }

            foreach (var column in table.Columns.Values.Where(column => !Contains(expectedTable.Columns, column.Name)))
            {
                result.Add(
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Column,
                        schema: null,
                        table.Name,
                        column.Name,
                        "unexpected_column"));
            }

            foreach (var index in table.Indexes.Where(index => index.Origin == "c"
                         && !Contains(expectedTable.Indexes, index.Name)))
            {
                result.Add(
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Index,
                        schema: null,
                        table.Name,
                        index.Name,
                        "unexpected_index"));
            }

            if (table.PrimaryKeyColumns.Count > 0)
            {
                AddUnexpectedConstraint(
                    result,
                    expectedTable,
                    table.Name,
                    table.PrimaryKeyName ?? "sqlite_primary_key",
                    SafeMigrationDatabaseObjectKind.PrimaryKey);
            }

            foreach (var unique in table.UniqueConstraints)
            {
                AddUnexpectedConstraint(
                    result,
                    expectedTable,
                    table.Name,
                    unique.Name ?? unique.PhysicalName,
                    SafeMigrationDatabaseObjectKind.UniqueConstraint);
            }

            for (var index = 0; index < table.Checks.Count; index++)
            {
                AddUnexpectedConstraint(
                    result,
                    expectedTable,
                    table.Name,
                    table.Checks[index].Name ?? $"sqlite_check_{index.ToString(CultureInfo.InvariantCulture)}",
                    SafeMigrationDatabaseObjectKind.CheckConstraint);
            }

            foreach (var foreignKey in table.ForeignKeys)
            {
                AddUnexpectedConstraint(
                    result,
                    expectedTable,
                    table.Name,
                    foreignKey.Name ?? $"sqlite_fk_{foreignKey.Id.ToString(CultureInfo.InvariantCulture)}",
                    SafeMigrationDatabaseObjectKind.ForeignKey);
            }
        }

        return RemoveSemanticAliasesAsync(context, operations, result, cancellationToken);
    }

    private async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> RemoveSemanticAliasesAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        List<SafeMigrationUnexpectedObject> findings,
        CancellationToken cancellationToken
    )
    {
        if (findings.Count == 0)
        {
            return findings.AsReadOnly();
        }

        var semanticAliases = new HashSet<int>();
        foreach (var candidates in SafeMigrationSemanticCandidateFactory
                     .Create(operations, findings, projectUniqueIndexesAsUniqueConstraints: true)
                     .Chunk(SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
        {
            var analyses = await AnalyzeAsync(
                context,
                candidates
                    .Select(static candidate => candidate.Operation)
                    .ToArray(),
                cancellationToken);

            for (var index = 0; index < candidates.Length; index++)
            {
                if (analyses[index].ObservedState == SafeMigrationObservedState.Matching)
                {
                    _ = semanticAliases.Add(candidates[index].UnexpectedObjectIndex);
                }
            }
        }

        if (semanticAliases.Count == 0)
        {
            return findings.AsReadOnly();
        }

        return findings
            .Where((_, index) => !semanticAliases.Contains(index))
            .ToArray();
    }

    private static void AddUnexpectedConstraint(
        List<SafeMigrationUnexpectedObject> result,
        SafeMigrationExpectedTableInventory expectedTable,
        string table,
        string name,
        SafeMigrationDatabaseObjectKind kind
    )
    {
        if (expectedTable.Constraints.Any(value => value.Value == kind && s_identifierComparer.Equals(value.Key, name)))
        {
            return;
        }

        result.Add(
            new SafeMigrationUnexpectedObject(
                kind,
                schema: null,
                table,
                name,
                kind switch
                {
                    SafeMigrationDatabaseObjectKind.PrimaryKey => "unexpected_primary_key",
                    SafeMigrationDatabaseObjectKind.UniqueConstraint => "unexpected_unique_constraint",
                    SafeMigrationDatabaseObjectKind.CheckConstraint => "unexpected_check_constraint",
                    SafeMigrationDatabaseObjectKind.ForeignKey => "unexpected_foreign_key",
                    SafeMigrationDatabaseObjectKind.Table
                        or SafeMigrationDatabaseObjectKind.Column
                        or SafeMigrationDatabaseObjectKind.Index => throw new UnreachableException(
                            $"Object kind '{kind}' is not a constraint."),
                    _ => throw new UnreachableException(
                        $"Object kind value '{(int)kind}' is not defined."),
                }));
    }

    private static bool Contains(
        IReadOnlySet<string> values,
        string value
    ) => values.Any(candidate => s_identifierComparer.Equals(candidate, value));

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

        return operation is SqlOperation;
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
            return Unsupported("sqlite_version");
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
            return Unsupported("virtual_table");
        }

        var analysis = operation.Intent switch
        {
            EnsureSchemaIntent value => AnalyzeEnsureSchema(value),
            DropSchemaIntent => Unsupported("schema_operations"),
            EnsureTableIntent value => AnalyzeEnsureTable(snapshot, value),
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
                value,
                rebuildArtifacts),
            DropForeignKeyIntent value => AnalyzeDropForeignKey(snapshot, value),
            ModelManagedDataIntent value => AnalyzeModelManagedData(snapshot, connection, transaction, value),
            _ => Unsupported("operation_kind"),
        };

        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        if (tableName is not null
            && SqliteSafeMigrationOperationClassifier.RequiresTableRebuild(operation.Intent)
            && decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair)
        {
            var unsupportedRebuild = rebuildArtifacts.GetUnsupportedRebuildFeature(
                snapshot,
                tableName,
                ModelColumnMatches,
                ModelIndexMatches);

            if (unsupportedRebuild is not null)
            {
                return Unsupported(unsupportedRebuild);
            }

            if (snapshot.ForeignKeysEnabled
                && HasRetainedRebuildForeignKeyViolation(
                    snapshot,
                    connection,
                    transaction,
                    tableName,
                    operation,
                    rebuildArtifacts))
            {
                return DataBlocked("table_rebuild_foreign_key_violation");
            }
        }

        return analysis;
    }

    private static SafeMigrationProviderAnalysis AnalyzeEnsureSchema(
        EnsureSchemaIntent intent
    ) => s_identifierComparer.Equals(intent.Name, "main")
        ? Matching(postconditionSatisfied: true)
        : Unsupported("database_qualifier_mismatch");

    private SafeMigrationProviderAnalysis AnalyzeEnsureTable(
        SqliteCatalogSnapshot snapshot,
        EnsureTableIntent intent
    )
    {
        var unsupported = TableUnsupportedFeature(intent.Definition);
        if (unsupported is not null)
        {
            return Unsupported(unsupported);
        }

        if (!snapshot.Tables.TryGetValue(intent.Definition.Table, out var table))
        {
            return Missing(postconditionSatisfied: false);
        }

        if (intent.Mode == SafeMigrationTableMode.ConvergenceContainer)
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: table.Name);
        }

        var matching = TableMatches(snapshot, table, intent.Definition);

        return matching
            ? Matching(postconditionSatisfied: true, matchedObjectName: table.Name)
            : Different("table_definition", "matching", "different", matchedObjectName: table.Name);
    }

    private static SafeMigrationProviderAnalysis AnalyzeDropTable(
        SqliteCatalogSnapshot snapshot,
        DropTableIntent intent,
        SafeMigrationOperation operation,
        SqliteRebuildArtifactContract rebuildArtifacts
    )
    {
        if (rebuildArtifacts.HasUnresolvedIncomingForeignKey(snapshot, intent.Table, operation))
        {
            return Unsupported("table_drop_foreign_key_dependency");
        }

        return snapshot.Tables.ContainsKey(intent.Table)
            ? Matching(postconditionSatisfied: false, matchedObjectName: intent.Table)
            : Missing(postconditionSatisfied: true);
    }

    private static SafeMigrationProviderAnalysis AnalyzeRenameTable(
        SqliteCatalogSnapshot snapshot,
        RenameTableIntent intent
    )
    {
        if (intent.NewSchema is not null
            && !IsMain(intent.NewSchema))
        {
            return Unsupported("database_qualifier_mismatch");
        }

        var targetName = intent.NewName ?? intent.Name;
        var sourceExists = snapshot.Tables.ContainsKey(intent.Name);
        var targetExists = snapshot.Tables.ContainsKey(targetName);

        return sourceExists switch
        {
            true when !targetExists => snapshot.LegacyAlterTableEnabled
                ? Unsupported("legacy_alter_table")
                : Matching(postconditionSatisfied: false, matchedObjectName: intent.Name),
            false when targetExists => Missing(postconditionSatisfied: true),
            _ => sourceExists
                ? Different("rename_target", "missing", "existing")
                : Missing(postconditionSatisfied: false)
        };
    }

    private SafeMigrationProviderAnalysis AnalyzeEnsureColumn(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsureColumnIntent intent
    )
    {
        var unsupported = ColumnUnsupportedFeature(intent.Definition);
        if (unsupported is not null)
        {
            return Unsupported(unsupported);
        }

        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return PrerequisiteMissing();
        }

        if (!table.Columns.TryGetValue(intent.Definition.Name, out var column))
        {
            if (intent.Definition.IsStored == true)
            {
                return Unsupported("stored_generated_column_add");
            }

            if (intent.Definition is
                {
                    IsNullable: false,
                    DefaultValue.Kind: SafeMigrationDefaultValueKind.None,
                    ComputedColumnSql: null,
                    ComputedExpression: null
                }
                && TableHasRows(connection, transaction, intent.Table))
            {
                return DataBlocked("required_column_has_rows");
            }

            return Missing(postconditionSatisfied: false);
        }

        return ColumnMatches(column, intent.Definition)
            ? Matching(postconditionSatisfied: true, matchedObjectName: column.Name)
            : ColumnDifferent(column, intent.Definition);
    }

    private SafeMigrationProviderAnalysis AnalyzeAlterColumn(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        AlterColumnIntent intent
    )
    {
        var unsupported = ColumnUnsupportedFeature(intent.Definition)
            ?? (intent.OldDefinition is null ? null : ColumnUnsupportedFeature(intent.OldDefinition));

        if (unsupported is not null)
        {
            return Unsupported(unsupported);
        }

        if (!snapshot.Tables.TryGetValue(intent.Table, out var table)
            || !table.Columns.TryGetValue(intent.Definition.Name, out var column))
        {
            return PrerequisiteMissing();
        }

        if (ColumnMatches(column, intent.Definition))
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: column.Name);
        }

        if (!intent.Definition.IsNullable
            && column.IsNullable
            && ColumnHasNulls(connection, transaction, intent.Table, intent.Definition.Name))
        {
            return DataBlocked("column_contains_nulls");
        }

        var oldDefinitionMatches = intent.OldDefinition is not null && ColumnMatches(column, intent.OldDefinition);

        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            oldDefinitionMatches ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            oldDefinitionMatches ? "classified_different_repairable" : "classified_different",
            oldDefinitionMatches
                ? SafeMigrationOperationalImpact.TableRewritePossible
                : SafeMigrationOperationalImpact.NotApplicable,
            ColumnDifferences(column, intent.Definition))
        {
            MatchedObjectName = column.Name,
        };
    }

    private static SafeMigrationProviderAnalysis AnalyzeDropColumn(
        SqliteCatalogSnapshot snapshot,
        DropColumnIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return Missing(postconditionSatisfied: true);
        }

        return table.Columns.ContainsKey(intent.Name)
            ? Matching(postconditionSatisfied: false, matchedObjectName: intent.Name)
            : Missing(postconditionSatisfied: true);
    }

    private static SafeMigrationProviderAnalysis AnalyzeRenameColumn(
        SqliteCatalogSnapshot snapshot,
        RenameColumnIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return PrerequisiteMissing();
        }

        var sourceExists = table.Columns.ContainsKey(intent.Name);
        var targetExists = table.Columns.ContainsKey(intent.NewName);
        return sourceExists switch
        {
            true when !targetExists => Matching(postconditionSatisfied: false, matchedObjectName: intent.Name),
            false when targetExists => Missing(postconditionSatisfied: true),
            _ => sourceExists
                ? Different("rename_target", "missing", "existing")
                : Missing(postconditionSatisfied: false)
        };
    }

    private SafeMigrationProviderAnalysis AnalyzeEnsureIndex(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsureIndexIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Definition.Table, out var table)
            || intent.Definition.Keys.Any(key => key.Column is not null && !table.Columns.ContainsKey(key.Column)))
        {
            return PrerequisiteMissing();
        }

        var unsupported = IndexUnsupportedFeature(intent.Definition);
        if (unsupported is not null)
        {
            return Unsupported(unsupported);
        }

        var named = table.Indexes.FirstOrDefault(index =>
            s_identifierComparer.Equals(index.Name, intent.Definition.Name));

        if (named is not null)
        {
            return IndexMatches(named, intent.Definition, table.Columns)
                ? Matching(postconditionSatisfied: true, matchedObjectName: named.Name)
                : Different("index_definition", "matching", "different", matchedObjectName: named.Name);
        }

        var semantic = table.Indexes.FirstOrDefault(index => index.Origin == "c"
            && IndexMatches(index, intent.Definition, table.Columns));

        if (semantic is not null)
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: semantic.Name);
        }

        if (intent.Definition.Unique
            && HasDuplicateIndexKey(connection, transaction, intent.Definition))
        {
            return DataBlocked("unique_index_duplicate_values");
        }

        return Missing(postconditionSatisfied: false);
    }

    private static SafeMigrationProviderAnalysis AnalyzeDropIndex(
        SqliteCatalogSnapshot snapshot,
        DropIndexIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return Missing(postconditionSatisfied: true);
        }

        var index = table.Indexes.FirstOrDefault(value => s_identifierComparer.Equals(value.Name, intent.Name));

        return index is null
            ? Missing(postconditionSatisfied: true)
            : index.Origin == "c"
                ? Matching(postconditionSatisfied: false, matchedObjectName: index.Name)
                : Different("index_origin", "created", index.Origin, matchedObjectName: index.Name);
    }

    private static SafeMigrationProviderAnalysis AnalyzeRenameIndex(
        SqliteCatalogSnapshot snapshot,
        RenameIndexIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return PrerequisiteMissing();
        }

        var source = table.Indexes.FirstOrDefault(value => s_identifierComparer.Equals(value.Name, intent.Name));
        var target = table.Indexes.FirstOrDefault(value => s_identifierComparer.Equals(value.Name, intent.NewName));
        if (source is not null
            && target is null)
        {
            return source.Origin == "c"
                ? Matching(postconditionSatisfied: false, matchedObjectName: source.Name)
                : Different("index_origin", "created", source.Origin, matchedObjectName: source.Name);
        }

        return source is null && target is not null
            ? Missing(postconditionSatisfied: true)
            : source is null
                ? Missing(postconditionSatisfied: false)
                : Different("rename_target", "missing", "existing");
    }

    private static SafeMigrationProviderAnalysis AnalyzeEnsurePrimaryKey(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsurePrimaryKeyIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Definition.Table, out var table)
            || intent.Definition.Columns.Any(column => !table.Columns.ContainsKey(column)))
        {
            return PrerequisiteMissing();
        }

        if (IdentifiersEqual(table.PrimaryKeyColumns, intent.Definition.Columns))
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: table.PrimaryKeyName);
        }

        if (table.PrimaryKeyColumns.Count > 0)
        {
            return Different("primary_key_columns", Join(intent.Definition.Columns), Join(table.PrimaryKeyColumns));
        }

        if (HasNullKey(connection, transaction, intent.Definition.Table, intent.Definition.Columns)
            || HasDuplicateKey(
                connection,
                transaction,
                intent.Definition.Table,
                intent.Definition.Columns,
                excludeNulls: false))
        {
            return DataBlocked("primary_key_invalid_values");
        }

        return Missing(postconditionSatisfied: false);
    }

    private static SafeMigrationProviderAnalysis AnalyzeDropPrimaryKey(
        SqliteCatalogSnapshot snapshot,
        DropPrimaryKeyIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table)
            || table.PrimaryKeyColumns.Count == 0)
        {
            return Missing(postconditionSatisfied: true);
        }

        if (table.PrimaryKeyName is null
            || !s_identifierComparer.Equals(table.PrimaryKeyName, intent.Name))
        {
            return Different("primary_key_name", intent.Name, table.PrimaryKeyName ?? "anonymous");
        }

        return Matching(postconditionSatisfied: false, matchedObjectName: table.PrimaryKeyName);
    }

    private static SafeMigrationProviderAnalysis AnalyzeEnsureUnique(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsureUniqueConstraintIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Definition.Table, out var table)
            || intent.Definition.Columns.Any(column => !table.Columns.ContainsKey(column)))
        {
            return PrerequisiteMissing();
        }

        var named = table.UniqueConstraints.FirstOrDefault(value =>
            value.Name is not null && s_identifierComparer.Equals(value.Name, intent.Definition.Name));

        if (named is not null)
        {
            return IdentifiersEqual(named.Columns, intent.Definition.Columns)
                ? Matching(postconditionSatisfied: true, matchedObjectName: named.PhysicalName)
                : Different("unique_columns", Join(intent.Definition.Columns), Join(named.Columns));
        }

        var semantic = table.UniqueConstraints.FirstOrDefault(value =>
            IdentifiersEqual(value.Columns, intent.Definition.Columns));

        if (semantic is not null)
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: semantic.PhysicalName);
        }

        return HasDuplicateKey(
            connection,
            transaction,
            intent.Definition.Table,
            intent.Definition.Columns,
            excludeNulls: true)
            ? DataBlocked("unique_constraint_duplicate_values")
            : Missing(postconditionSatisfied: false);
    }

    private static SafeMigrationProviderAnalysis AnalyzeDropUnique(
        SqliteCatalogSnapshot snapshot,
        DropUniqueConstraintIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return Missing(postconditionSatisfied: true);
        }

        var constraint = table.UniqueConstraints.FirstOrDefault(value =>
            value.Name is not null && s_identifierComparer.Equals(value.Name, intent.Name));

        return constraint is null
            ? Missing(postconditionSatisfied: true)
            : Matching(postconditionSatisfied: false, matchedObjectName: constraint.PhysicalName);
    }

    private SafeMigrationProviderAnalysis AnalyzeEnsureCheck(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsureCheckConstraintIntent intent,
        SqliteRebuildArtifactContract rebuildArtifacts
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Definition.Table, out var table))
        {
            return PrerequisiteMissing();
        }

        var expectedSql = RenderCheck(intent.Definition);
        if (expectedSql is null)
        {
            return Unsupported("check_expression");
        }

        var named = table.Checks.FirstOrDefault(value =>
            value.Name is not null && s_identifierComparer.Equals(value.Name, intent.Definition.Name));

        if (named is not null)
        {
            return SqlEquivalent(named.Expression, expectedSql)
                ? Matching(postconditionSatisfied: true, matchedObjectName: named.Name)
                : Different("check_expression", Bound(expectedSql), Bound(named.Expression));
        }

        var semantic = table.Checks.FirstOrDefault(value => SqlEquivalent(value.Expression, expectedSql));
        if (semantic is not null)
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: semantic.Name);
        }

        if (rebuildArtifacts.HasUnmaterializedReferencedColumns(snapshot, intent.Definition.Table, expectedSql))
        {
            return PrerequisiteMissing();
        }

        return HasCheckViolations(connection, transaction, intent.Definition.Table, expectedSql)
            ? DataBlocked("check_constraint_violated")
            : Missing(postconditionSatisfied: false);
    }

    private static SafeMigrationProviderAnalysis AnalyzeDropCheck(
        SqliteCatalogSnapshot snapshot,
        DropCheckConstraintIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return Missing(postconditionSatisfied: true);
        }

        var constraint = table.Checks.FirstOrDefault(value =>
            value.Name is not null && s_identifierComparer.Equals(value.Name, intent.Name));

        return constraint is null
            ? Missing(postconditionSatisfied: true)
            : Matching(postconditionSatisfied: false, matchedObjectName: constraint.Name);
    }

    private static SafeMigrationProviderAnalysis AnalyzeEnsureForeignKey(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsureForeignKeyIntent intent,
        SqliteRebuildArtifactContract rebuildArtifacts
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Definition.Table, out var table)
            || !snapshot.Tables.TryGetValue(intent.Definition.PrincipalTable, out var principalTable))
        {
            return PrerequisiteMissing();
        }

        if (intent.Definition.Columns.Any(column => !table.Columns.ContainsKey(column))
            || intent.Definition.PrincipalColumns.Any(column => !principalTable.Columns.ContainsKey(column)))
        {
            return rebuildArtifacts.CanTreatProjectedForeignKeyAsMissing(snapshot, intent.Definition)
                ? Missing(postconditionSatisfied: false)
                : PrerequisiteMissing();
        }

        var named = table.ForeignKeys.FirstOrDefault(value =>
            value.Name is not null && s_identifierComparer.Equals(value.Name, intent.Definition.Name));

        if (named is not null)
        {
            return SqliteCatalogEquivalence.ForeignKeyMatches(
                snapshot,
                named,
                intent.Definition.PrincipalTable,
                intent.Definition.Columns,
                intent.Definition.PrincipalColumns,
                intent.Definition.OnUpdate,
                intent.Definition.OnDelete)
                ? Matching(postconditionSatisfied: true, matchedObjectName: named.Name)
                : Different("foreign_key_definition", "matching", "different", matchedObjectName: named.Name);
        }

        var semantic = table.ForeignKeys.FirstOrDefault(value => SqliteCatalogEquivalence.ForeignKeyMatches(
            snapshot,
            value,
            intent.Definition.PrincipalTable,
            intent.Definition.Columns,
            intent.Definition.PrincipalColumns,
            intent.Definition.OnUpdate,
            intent.Definition.OnDelete));

        if (semantic is not null)
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: semantic.Name);
        }

        return HasForeignKeyViolations(connection, transaction, intent.Definition)
            ? DataBlocked("foreign_key_orphans")
            : Missing(postconditionSatisfied: false);
    }

    private static SafeMigrationProviderAnalysis AnalyzeDropForeignKey(
        SqliteCatalogSnapshot snapshot,
        DropForeignKeyIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return Missing(postconditionSatisfied: true);
        }

        var constraint = table.ForeignKeys.FirstOrDefault(value =>
            value.Name is not null && s_identifierComparer.Equals(value.Name, intent.Name));

        return constraint is null
            ? Missing(postconditionSatisfied: true)
            : Matching(postconditionSatisfied: false, matchedObjectName: constraint.Name);
    }

    private static SafeMigrationProviderAnalysis AnalyzeModelManagedData(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table)
            || intent.KeyColumns.Any(column => !table.Columns.ContainsKey(column))
            || intent.Columns.Any(column => !table.Columns.ContainsKey(column)))
        {
            return PrerequisiteMissing();
        }

        if (intent is DeleteModelManagedDataIntent deletion)
        {
            if (deletion.ForeignKeys.Any(foreignKey =>
                    !snapshot.Tables.TryGetValue(foreignKey.Table, out var dependentTable)
                    || foreignKey.Columns.Any(column => !dependentTable.Columns.ContainsKey(column))))
            {
                return PrerequisiteMissing();
            }

            if (HasUnmodeledIncomingForeignKey(snapshot, deletion))
            {
                return Unsupported("model_managed_unmodeled_dependency");
            }
        }

        var states = ReadModelManagedRowStates(connection, transaction, intent);
        var dependencyCounts = intent is DeleteModelManagedDataIntent delete
            ? ReadModelManagedDependencyCounts(connection, transaction, delete)
            : [];

        var uniqueCollision = intent switch
        {
            EnsureModelManagedDataIntent ensure => HasModelManagedUniqueCollision(
                connection,
                transaction,
                ensure,
                ensure.Values,
                ensure.UniqueKeys),
            UpdateModelManagedDataIntent update => HasModelManagedUniqueCollision(
                connection,
                transaction,
                update,
                update.NewValues,
                update.UniqueKeys),
            DeleteModelManagedDataIntent => false,
            _ => throw new UnreachableException(),
        };

        var analysis = intent switch
        {
            EnsureModelManagedDataIntent => states.All(static state =>
                state == SafeMigrationModelManagedRowState.Target)
                ? Matching(postconditionSatisfied: true)
                : states.Any(static state => state == SafeMigrationModelManagedRowState.Different)
                    ? Different("model_managed_row", "target", "different")
                    : uniqueCollision
                        ? DataBlocked("model_managed_unique_collision")
                        : Missing(postconditionSatisfied: false),
            UpdateModelManagedDataIntent => states.Any(static state =>
                state == SafeMigrationModelManagedRowState.Missing)
                ? PrerequisiteMissing()
                : states.Any(static state => state == SafeMigrationModelManagedRowState.Different)
                    ? Different("model_managed_row", "source_or_target", "different")
                    : uniqueCollision
                        ? DataBlocked("model_managed_unique_collision")
                        : states.All(static state => state == SafeMigrationModelManagedRowState.Target)
                            ? Matching(postconditionSatisfied: true)
                            : TransitionReady(),
            DeleteModelManagedDataIntent => states.Any(static state =>
                state == SafeMigrationModelManagedRowState.Different)
                ? Different("model_managed_row", "source_or_missing", "different")
                : dependencyCounts.Any(static count => count > 0)
                    ? DataBlocked("model_managed_dependency")
                    : states.All(static state => state == SafeMigrationModelManagedRowState.Missing)
                        ? Missing(postconditionSatisfied: true)
                        : TransitionReady(),
            _ => throw new UnreachableException(),
        };

        return new SafeMigrationProviderAnalysis(
            analysis.ObservedState,
            analysis.RepairCapability,
            analysis.PostconditionSatisfied,
            analysis.Code,
            analysis.OperationalImpact,
            analysis.Differences)
        {
            ModelManagedDataEvidence = new SafeMigrationModelManagedDataEvidence(states, dependencyCounts),
        };
    }

    private bool TableMatches(
        SqliteCatalogSnapshot snapshot,
        SqliteTableSnapshot table,
        ExpectedTableDefinition expected
    )
    {
        if (expected.Comment is not null
            || table.Columns.Count != expected.Columns.Count
            || !expected.Columns.All(column => table.Columns.TryGetValue(column.Name, out var actual)
                && ColumnMatches(actual, column)))
        {
            return false;
        }

        if ((expected.PrimaryKey is null) != (table.PrimaryKeyColumns.Count == 0)
            || (expected.PrimaryKey is not null
                && !IdentifiersEqual(expected.PrimaryKey.Columns, table.PrimaryKeyColumns))
            || expected.UniqueConstraints.Count != table.UniqueConstraints.Count
            || expected.UniqueConstraints.Any(definition => !table.UniqueConstraints.Any(actual =>
                IdentifiersEqual(definition.Columns, actual.Columns)))
            || expected.ForeignKeys.Count != table.ForeignKeys.Count
            || expected.ForeignKeys.Any(definition => !table.ForeignKeys.Any(actual =>
                SqliteCatalogEquivalence.ForeignKeyMatches(
                    snapshot,
                    actual,
                    definition.PrincipalTable,
                    definition.Columns,
                    definition.PrincipalColumns,
                    definition.OnUpdate,
                    definition.OnDelete))))
        {
            return false;
        }

        if (expected.CheckConstraints.Count != table.Checks.Count)
        {
            return false;
        }

        foreach (var check in expected.CheckConstraints)
        {
            var expectedSql = RenderCheck(check);
            if (expectedSql is null
                || !table.Checks.Any(actual => SqlEquivalent(actual.Expression, expectedSql)))
            {
                return false;
            }
        }

        return true;
    }

    private string? TableUnsupportedFeature(
        ExpectedTableDefinition definition
    )
    {
        if (definition.Comment is not null)
        {
            return "table_comments";
        }

        foreach (var column in definition.Columns)
        {
            var unsupported = ColumnUnsupportedFeature(column);
            if (unsupported is not null)
            {
                return unsupported;
            }
        }

        foreach (var check in definition.CheckConstraints)
        {
            if (check.Expression is not null)
            {
                var unsupported = _expressionRenderer.GetUnsupportedFeature(check.Expression);
                if (unsupported is not null)
                {
                    return unsupported;
                }
            }
        }

        return null;
    }

    private string? ColumnUnsupportedFeature(
        ExpectedColumnDefinition definition
    )
    {
        if (definition.Comment is not null)
        {
            return "column_comments";
        }

        if (definition.IsRowVersion)
        {
            return "row_version";
        }

        if (definition.Collation?.Schema is not null)
        {
            return "schema_qualified_collation";
        }

        if (definition.ProviderAnnotations.Any(static annotation => annotation.Name != "Sqlite:Autoincrement"))
        {
            return "column_provider_annotation";
        }

        if (definition.DefaultValue.StructuredExpression is not null)
        {
            var unsupported = _expressionRenderer.GetUnsupportedFeature(definition.DefaultValue.StructuredExpression);

            if (unsupported is not null)
            {
                return unsupported;
            }
        }

        return definition.ComputedExpression is not null
            ? _expressionRenderer.GetUnsupportedFeature(definition.ComputedExpression)
            : null;
    }

    private bool ColumnMatches(
        SqliteColumnSnapshot actual,
        ExpectedColumnDefinition expected
    )
    {
        var expectedStoreType = ExpectedStoreType(expected);
        var expectedDefault = ExpectedDefaultSql(expected);
        var expectedComputed = expected.ComputedExpression is null
            ? expected.ComputedColumnSql
            : _expressionRenderer.Render(expected.ComputedExpression);

        var expectedAutoincrement = expected.ProviderAnnotations
                .FirstOrDefault(static annotation => annotation.Name == "Sqlite:Autoincrement")
                ?.Value as bool?
            ?? false;

        var unsupportedAnnotation = expected.ProviderAnnotations.Any(static annotation =>
            annotation.Name != "Sqlite:Autoincrement");

        return !unsupportedAnnotation
            && expected.Comment is null
            && !expected.IsRowVersion
            && s_identifierComparer.Equals(actual.Name, expected.Name)
            && StringComparer.Ordinal.Equals(
                NormalizeStoreType(actual.StoreType),
                NormalizeStoreType(expectedStoreType))
            && actual.IsNullable == expected.IsNullable
            && SqlEquivalent(actual.DefaultSql, expectedDefault)
            && SqlEquivalent(actual.GeneratedSql, expectedComputed)
            && (expectedComputed is null || actual.IsStored == (expected.IsStored ?? false))
            && CollationMatches(actual.Collation, expected.Collation)
            && actual.AutoIncrement == expectedAutoincrement;
    }

    private bool ModelColumnMatches(
        SqliteColumnSnapshot actual,
        IColumn expected
    )
    {
        var expectedDefault = expected.DefaultValueSql;
        if (expectedDefault is null
            && expected.TryGetDefaultValue(out var defaultValue))
        {
            expectedDefault = defaultValue is null
                ? "NULL"
                : expected.StoreTypeMapping.GenerateSqlLiteral(defaultValue);
        }

        var expectedAutoincrement = expected.FindAnnotation("Sqlite:Autoincrement")
                ?.Value as bool?
            ?? false;

        return s_identifierComparer.Equals(actual.Name, expected.Name)
            && StringComparer.Ordinal.Equals(
                NormalizeStoreType(actual.StoreType),
                NormalizeStoreType(expected.StoreType))
            && actual.IsNullable == expected.IsNullable
            && ModelDefaultMatches(actual, expected, expectedDefault)
            && SqlEquivalent(actual.GeneratedSql, expected.ComputedColumnSql)
            && (expected.ComputedColumnSql is null || actual.IsStored == (expected.IsStored ?? false))
            && s_identifierComparer.Equals(actual.Collation ?? "BINARY", expected.Collation ?? "BINARY")
            && actual.AutoIncrement == expectedAutoincrement;
    }

    private bool ModelDefaultMatches(
        SqliteColumnSnapshot actual,
        IColumn expected,
        string? expectedDefault
    )
    {
        if (SqlEquivalent(actual.DefaultSql, expectedDefault))
        {
            return true;
        }

        if (expectedDefault is not null
            || expected.IsNullable
            || actual.DefaultSql is null
            || !TryCreateEfBackfillDefault(expected, out var backfillDefault))
        {
            return false;
        }

        // WHY: EF scaffolds a transient CLR-default literal when a required
        // column is added to a populated SQLite table. The model intentionally
        // omits that default, and a later rebuild is the point where EF removes it.

        return SqlEquivalent(actual.DefaultSql, backfillDefault);
    }

    private bool TryCreateEfBackfillDefault(
        IColumn expected,
        [NotNullWhen(true)] out string? sql
    )
    {
        var providerClrType = expected.StoreTypeMapping.Converter?.ProviderClrType ?? expected.StoreTypeMapping.ClrType;

        var underlyingType = Nullable.GetUnderlyingType(providerClrType) ?? providerClrType;
        object? value;
        if (underlyingType == typeof(string))
        {
            value = string.Empty;
        }
        else if (underlyingType == typeof(byte[]))
        {
            value = Array.Empty<byte>();
        }
        else
        {
            value = underlyingType.IsValueType ? Activator.CreateInstance(underlyingType) : null;
        }

        if (value is null)
        {
            sql = null;

            return false;
        }

        var literalMapping = _typeMappingSource.FindMapping(value.GetType(), expected.StoreType);
        if (literalMapping is null)
        {
            sql = null;

            return false;
        }

        sql = literalMapping.GenerateSqlLiteral(value);

        return true;
    }

    private string ExpectedStoreType(
        ExpectedColumnDefinition definition
    ) => definition.StoreType
        ?? _typeMappingSource.FindMapping(
                definition.ClrType,
                storeTypeName: null,
                keyOrIndex: false,
                unicode: definition.IsUnicode,
                size: definition.MaxLength,
                rowVersion: definition.IsRowVersion,
                fixedLength: definition.IsFixedLength,
                precision: definition.Precision,
                scale: definition.Scale)
            ?.StoreType
        ?? throw new NotSupportedException($"SQLite has no type mapping for column '{definition.Name}'.");

    private string? ExpectedDefaultSql(
        ExpectedColumnDefinition definition
    ) => definition.DefaultValue.Kind switch
    {
        SafeMigrationDefaultValueKind.None => null,
        SafeMigrationDefaultValueKind.Sql when definition.DefaultValue.StructuredExpression is not null =>
            _expressionRenderer.Render(definition.DefaultValue.StructuredExpression),
        SafeMigrationDefaultValueKind.Sql => definition.DefaultValue.SqlExpression,
        SafeMigrationDefaultValueKind.Literal => GenerateLiteral(
            definition.DefaultValue.GetLiteralValue(),
            definition.StoreType),
        _ => throw new UnreachableException(),
    };

    private string GenerateLiteral(
        object? value,
        string? storeType
    )
    {
        if (value is null)
        {
            return "NULL";
        }

        var mapping = _typeMappingSource.FindMapping(value.GetType(), storeType)
            ?? _typeMappingSource.FindMapping(value.GetType())
            ?? throw new NotSupportedException(
                $"SQLite has no type mapping for default value '{value.GetType().FullName}'.");

        return mapping.GenerateSqlLiteral(value);
    }

    private SafeMigrationProviderAnalysis ColumnDifferent(
        SqliteColumnSnapshot actual,
        ExpectedColumnDefinition expected
    ) => new(
        SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "classified_different",
        SafeMigrationOperationalImpact.NotApplicable,
        ColumnDifferences(actual, expected))
    {
        MatchedObjectName = actual.Name,
    };

    private ReadOnlyCollection<SafeMigrationFacetDifference> ColumnDifferences(
        SqliteColumnSnapshot actual,
        ExpectedColumnDefinition expected
    )
    {
        var result = new List<SafeMigrationFacetDifference>();
        var expectedStoreType = ExpectedStoreType(expected);
        if (!StringComparer.Ordinal.Equals(NormalizeStoreType(actual.StoreType), NormalizeStoreType(expectedStoreType)))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_store_type",
                    Display(expectedStoreType),
                    Display(actual.StoreType)));
        }

        if (actual.IsNullable != expected.IsNullable)
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_nullability",
                    expected.IsNullable ? "nullable" : "required",
                    actual.IsNullable ? "nullable" : "required"));
        }

        var expectedDefault = ExpectedDefaultSql(expected);
        if (!SqlEquivalent(actual.DefaultSql, expectedDefault))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_default",
                    Bound(expectedDefault ?? "none"),
                    Bound(actual.DefaultSql ?? "none")));
        }

        var expectedComputed = expected.ComputedExpression is null
            ? expected.ComputedColumnSql
            : _expressionRenderer.Render(expected.ComputedExpression);

        if (!SqlEquivalent(actual.GeneratedSql, expectedComputed))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_generated_expression",
                    Bound(expectedComputed ?? "none"),
                    Bound(actual.GeneratedSql ?? "none")));
        }

        if (expectedComputed is not null
            && actual.IsStored != (expected.IsStored ?? false))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_generated_storage",
                    expected.IsStored == true ? "stored" : "virtual",
                    actual.IsStored ? "stored" : "virtual"));
        }

        if (!CollationMatches(actual.Collation, expected.Collation))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_collation",
                    Bound(expected.Collation?.Name ?? "BINARY"),
                    Bound(actual.Collation ?? "BINARY")));
        }

        var expectedAutoincrement = expected.ProviderAnnotations
                .FirstOrDefault(static annotation => annotation.Name == "Sqlite:Autoincrement")
                ?.Value as bool?
            ?? false;

        if (actual.AutoIncrement != expectedAutoincrement)
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_autoincrement",
                    expectedAutoincrement ? "enabled" : "disabled",
                    actual.AutoIncrement ? "enabled" : "disabled"));
        }

        if (result.Count == 0)
        {
            result.Add(new SafeMigrationFacetDifference("column_definition", "matching", "different"));
        }

        return result.AsReadOnly();
    }

    private string? RenderCheck(
        ExpectedCheckConstraintDefinition definition
    )
    {
        if (definition.Expression is not null)
        {
            return _expressionRenderer.GetUnsupportedFeature(definition.Expression) is null
                ? _expressionRenderer.Render(definition.Expression)
                : null;
        }

        return definition.Sql;
    }

    private bool IndexMatches(
        SqliteIndexSnapshot actual,
        ExpectedIndexDefinition expected,
        IReadOnlyDictionary<string, SqliteColumnSnapshot> columns
    )
    {
        var actualKeys = actual
            .Keys
            .Where(static value => value.IsKey)
            .OrderBy(static value => value.Ordinal)
            .ToArray();

        var expectedFilter = expected.StructuredFilter is null
            ? expected.Filter
            : _expressionRenderer.Render(expected.StructuredFilter);

        if (actual.Unique != expected.Unique
            || actual.Partial != (expectedFilter is not null)
            || actualKeys.Length != expected.Keys.Count
            || !SqlEquivalent(actual.Filter, expectedFilter))
        {
            return false;
        }

        for (var index = 0; index < actualKeys.Length; index++)
        {
            var actualKey = actualKeys[index];
            var expectedKey = expected.Keys[index];
            var expectedExpression = expectedKey.StructuredExpression is null
                ? expectedKey.Expression
                : _expressionRenderer.Render(expectedKey.StructuredExpression);

            var identityMatches = expectedKey.Column is not null
                ? actualKey.Expression is null && s_identifierComparer.Equals(actualKey.Column, expectedKey.Column)
                : actualKey.Column is null && SqlEquivalent(actualKey.Expression, expectedExpression);

            var expectedCollation = expectedKey.Collation?.Name
                ?? (expectedKey.Column is not null && columns.TryGetValue(expectedKey.Column, out var column)
                    ? column.Collation ?? "BINARY"
                    : "BINARY");

            if (!identityMatches
                || actualKey.Descending != (expectedKey.SortOrder == SafeMigrationIndexSortOrder.Descending)
                || !s_identifierComparer.Equals(actualKey.Collation, expectedCollation))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ModelIndexMatches(
        SqliteIndexSnapshot actual,
        ITableIndex expected
    )
    {
        var actualKeys = actual
            .Keys
            .Where(static value => value.IsKey)
            .OrderBy(static value => value.Ordinal)
            .ToArray();

        if (actual.Unique != expected.IsUnique
            || actual.Partial != (expected.Filter is not null)
            || actualKeys.Length != expected.Columns.Count
            || !SqlEquivalent(actual.Filter, expected.Filter))
        {
            return false;
        }

        var descending = expected.IsDescending;
        for (var index = 0; index < actualKeys.Length; index++)
        {
            var actualKey = actualKeys[index];
            var expectedCollation = expected.Columns[index].Collation ?? "BINARY";
            if (actualKey.Expression is not null
                || !s_identifierComparer.Equals(actualKey.Column, expected.Columns[index].Name)
                || actualKey.Descending != (descending is not null && index < descending.Count && descending[index])
                || !s_identifierComparer.Equals(actualKey.Collation, expectedCollation))
            {
                return false;
            }
        }

        return true;
    }

    private string? IndexUnsupportedFeature(
        ExpectedIndexDefinition definition
    )
    {
        if (definition.IncludedColumns.Count > 0
            || definition.Method is not null
            || definition.NullsDistinct is not null)
        {
            return "index_provider_option";
        }

        foreach (var key in definition.Keys)
        {
            if (key.PrefixLength is not null
                || key.OperatorClass is not null
                || key.NullOrder != SafeMigrationIndexNullOrder.ProviderDefault
                || key.Collation?.Schema is not null)
            {
                return "index_key_provider_option";
            }

            if (key.StructuredExpression is not null)
            {
                var unsupported = _expressionRenderer.GetUnsupportedFeature(key.StructuredExpression);
                if (unsupported is not null)
                {
                    return unsupported;
                }
            }
        }

        return definition.StructuredFilter is not null
            ? _expressionRenderer.GetUnsupportedFeature(definition.StructuredFilter)
            : null;
    }

    private static bool TableHasRows(
        DbConnection connection,
        DbTransaction? transaction,
        string table
    ) => ExecuteExists(connection, transaction, $"SELECT 1 FROM {Delimit(table)} LIMIT 1;");

    private static bool ColumnHasNulls(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        string column
    ) => ExecuteExists(
        connection,
        transaction,
        $"SELECT 1 FROM {Delimit(table)} WHERE {Delimit(column)} IS NULL LIMIT 1;");

    private static bool HasNullKey(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns
    ) => ExecuteExists(
        connection,
        transaction,
        $"SELECT 1 FROM {Delimit(table)} WHERE "
        + string.Join(" OR ", columns.Select(column => $"{Delimit(column)} IS NULL"))
        + " LIMIT 1;");

    private static bool HasDuplicateKey(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns,
        bool excludeNulls
    )
    {
        var identifiers = string.Join(", ", columns.Select(Delimit));
        var predicate = excludeNulls
            ? " WHERE " + string.Join(" AND ", columns.Select(column => $"{Delimit(column)} IS NOT NULL"))
            : string.Empty;

        return ExecuteExists(
            connection,
            transaction,
            $"SELECT 1 FROM {Delimit(table)}{predicate} GROUP BY {identifiers} HAVING COUNT(*) > 1 LIMIT 1;");
    }

    private bool HasDuplicateIndexKey(
        DbConnection connection,
        DbTransaction? transaction,
        ExpectedIndexDefinition definition
    )
    {
        var expressions = definition
            .Keys
            .Select(key => key.Column is not null
                ? Delimit(key.Column)
                + (key.Collation is null ? string.Empty : " COLLATE " + Delimit(key.Collation.Name))
                : key.StructuredExpression is not null
                    ? _expressionRenderer.Render(key.StructuredExpression)
                    : key.Expression
                    ?? throw new InvalidOperationException("A SQLite index key requires a column or expression."))
            .ToArray();

        var predicates = expressions
            .Select(expression => $"({expression}) IS NOT NULL")
            .ToList();
        var filter = definition.StructuredFilter is not null
            ? _expressionRenderer.Render(definition.StructuredFilter)
            : definition.Filter;

        if (filter is not null)
        {
            predicates.Add($"({filter})");
        }

        return ExecuteExists(
            connection,
            transaction,
            $"SELECT 1 FROM {Delimit(definition.Table)} WHERE {string.Join(" AND ", predicates)} "
            + $"GROUP BY {string.Join(", ", expressions)} HAVING COUNT(*) > 1 LIMIT 1;");
    }

    private static bool HasCheckViolations(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        string expression
    ) => ExecuteExists(
        connection,
        transaction,
        $"SELECT 1 FROM {Delimit(table)} WHERE NOT ({expression}) AND ({expression}) IS NOT NULL LIMIT 1;");

    private static bool HasForeignKeyViolations(
        DbConnection connection,
        DbTransaction? transaction,
        ExpectedForeignKeyDefinition definition
    ) => HasForeignKeyViolations(
        connection,
        transaction,
        definition.Table,
        definition.Columns,
        definition.PrincipalTable,
        definition.PrincipalColumns);

    private static bool HasRetainedRebuildForeignKeyViolation(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        string rebuiltTable,
        SafeMigrationOperation currentOperation,
        SqliteRebuildArtifactContract rebuildArtifacts
    )
    {
        var liveRebuiltTable = rebuildArtifacts.ResolveLiveTableName(rebuiltTable, currentOperation);
        foreach (var dependent in snapshot.Tables.Values)
        {
            foreach (var foreignKey in dependent.ForeignKeys)
            {
                if (!rebuildArtifacts.RetainsForeignKey(dependent.Name, foreignKey)
                    || (!s_identifierComparer.Equals(dependent.Name, liveRebuiltTable)
                        && !s_identifierComparer.Equals(foreignKey.PrincipalTable, liveRebuiltTable)))
                {
                    continue;
                }

                if (!snapshot.Tables.TryGetValue(foreignKey.PrincipalTable, out var principal))
                {
                    if (HasNonNullForeignKeyValues(connection, transaction, dependent.Name, foreignKey.Columns))
                    {
                        return true;
                    }

                    continue;
                }

                var principalColumns = foreignKey.PrincipalColumns.Any(static column => column.Length > 0)
                    ? foreignKey.PrincipalColumns
                    : principal.PrimaryKeyColumns;

                if (principalColumns.Count != foreignKey.Columns.Count
                    || HasForeignKeyViolations(
                        connection,
                        transaction,
                        dependent.Name,
                        foreignKey.Columns,
                        foreignKey.PrincipalTable,
                        principalColumns))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasForeignKeyViolations(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns,
        string principalTable,
        IReadOnlyList<string> principalColumns
    )
    {
        var join = new string[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            join[index] = $"p.{Delimit(principalColumns[index])} IS d.{Delimit(columns[index])}";
        }

        var nonNull = string.Join(" AND ", columns.Select(column => $"d.{Delimit(column)} IS NOT NULL"));

        return ExecuteExists(
            connection,
            transaction,
            $"SELECT 1 FROM {Delimit(table)} AS d "
            + $"LEFT JOIN {Delimit(principalTable)} AS p ON {string.Join(" AND ", join)} "
            + $"WHERE {nonNull} AND p.{Delimit(principalColumns[0])} IS NULL LIMIT 1;");
    }

    private static bool HasNonNullForeignKeyValues(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns
    )
    {
        var nonNull = string.Join(" AND ", columns.Select(column => $"{Delimit(column)} IS NOT NULL"));

        return ExecuteExists(connection, transaction, $"SELECT 1 FROM {Delimit(table)} WHERE {nonNull} LIMIT 1;");
    }

    private static bool ExecuteExists(
        DbConnection connection,
        DbTransaction? transaction,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return command.ExecuteScalar() is not null;
    }

    private static SafeMigrationModelManagedRowState[] ReadModelManagedRowStates(
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent
    )
    {
        var result = new SafeMigrationModelManagedRowState[intent.RowCount];
        var parameterCountPerRow = intent.KeyColumns.Count + intent.Columns.Count;
        if (intent is UpdateModelManagedDataIntent)
        {
            parameterCountPerRow += intent.Columns.Count;
        }

        var batchSize = Math.Max(1, SqliteSafeMigrationLimits.MaximumParametersPerCommand / parameterCountPerRow);

        for (var offset = 0; offset < intent.RowCount; offset += batchSize)
        {
            var count = Math.Min(batchSize, intent.RowCount - offset);
            ReadModelManagedRowStateBatch(connection, transaction, intent, offset, count, result);
        }

        return result;
    }

    private static void ReadModelManagedRowStateBatch(
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent,
        int offset,
        int count,
        SafeMigrationModelManagedRowState[] result
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var source = intent switch
        {
            UpdateModelManagedDataIntent update => update.OldValues,
            DeleteModelManagedDataIntent delete => delete.OldValues,
            EnsureModelManagedDataIntent ensure => ensure.Values,
            _ => throw new UnreachableException(),
        };

        var target = intent switch
        {
            UpdateModelManagedDataIntent update => update.NewValues,
            EnsureModelManagedDataIntent ensure => ensure.Values,
            DeleteModelManagedDataIntent => null,
            _ => throw new UnreachableException(),
        };

        var cteColumns = new List<string>(
            1
            + intent.KeyColumns.Count
            + source.ColumnCount
            + (target is null || ReferenceEquals(source, target) ? 0 : target.ColumnCount))
        {
            "ordinal",
        };

        cteColumns.AddRange(
            Enumerable
                .Range(0, intent.KeyColumns.Count)
                .Select(static index => $"key_{index}"));
        cteColumns.AddRange(
            Enumerable
                .Range(0, source.ColumnCount)
                .Select(static index => $"source_{index}"));
        if (target is not null
            && !ReferenceEquals(source, target))
        {
            cteColumns.AddRange(
                Enumerable
                    .Range(0, target.ColumnCount)
                    .Select(static index => $"target_{index}"));
        }

        var rows = new string[count];
        for (var localRow = 0; localRow < count; localRow++)
        {
            var row = offset + localRow;
            var parameters = new List<string>(cteColumns.Count)
            {
                row.ToString(CultureInfo.InvariantCulture),
            };

            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                var name = $"$r{localRow}_k{column}";
                parameters.Add(name);
                AddParameter(command, name, intent.KeyValues.GetUnsafeValue(row, column));
            }

            for (var column = 0; column < source.ColumnCount; column++)
            {
                var name = $"$r{localRow}_s{column}";
                parameters.Add(name);
                AddParameter(command, name, source.GetUnsafeValue(row, column));
            }

            if (target is not null
                && !ReferenceEquals(source, target))
            {
                for (var column = 0; column < target.ColumnCount; column++)
                {
                    var name = $"$r{localRow}_t{column}";
                    parameters.Add(name);
                    AddParameter(command, name, target.GetUnsafeValue(row, column));
                }
            }

            rows[localRow] = "(" + string.Join(", ", parameters) + ")";
        }

        var keyPredicate = string.Join(
            " AND ",
            intent.KeyColumns.Select((
                    column,
                    index
                ) => $"stored.{Delimit(column)} IS expected.{Delimit($"key_{index}")}"));

        var sourcePredicate = string.Join(
            " AND ",
            intent.Columns.Select((
                    column,
                    index
                ) => $"stored.{Delimit(column)} IS expected.{Delimit($"source_{index}")}"));

        var targetPredicate = target is null
            ? "0"
            : ReferenceEquals(source, target)
                ? sourcePredicate
                : string.Join(
                    " AND ",
                    intent.Columns.Select((
                            column,
                            index
                        ) => $"stored.{Delimit(column)} IS expected.{Delimit($"target_{index}")}"));

        const string marker = "__doka_sm_present_8f29d0d2";
        command.CommandText = $"WITH expected ({string.Join(", ", cteColumns.Select(Delimit))}) AS ("
            + $"VALUES {string.Join(", ", rows)}) "
            + $"SELECT expected.{Delimit("ordinal")}, "
            + $"COUNT(stored.{Delimit(marker)}), "
            + $"COALESCE(SUM(CASE WHEN {sourcePredicate} THEN 1 ELSE 0 END), 0), "
            + $"COALESCE(SUM(CASE WHEN {targetPredicate} THEN 1 ELSE 0 END), 0) "
            + $"FROM expected LEFT JOIN (SELECT 1 AS {Delimit(marker)}, source.* "
            + $"FROM {Delimit(intent.Table)} AS source) AS stored ON {keyPredicate} "
            + $"GROUP BY expected.{Delimit("ordinal")} ORDER BY expected.{Delimit("ordinal")};";

        using var reader = command.ExecuteReader();
        var observed = 0;
        while (reader.Read())
        {
            var row = reader.GetInt32(0);
            var matches = reader.GetInt32(1);
            var sourceMatches = reader.GetInt32(2);
            var targetMatches = reader.GetInt32(3);
            result[row] = matches switch
            {
                0 => SafeMigrationModelManagedRowState.Missing,
                > 1 => SafeMigrationModelManagedRowState.Different,
                _ when targetMatches == 1 => SafeMigrationModelManagedRowState.Target,
                _ when sourceMatches == 1 => SafeMigrationModelManagedRowState.Source,
                _ => SafeMigrationModelManagedRowState.Different,
            };

            observed++;
        }

        if (observed != count)
        {
            throw new InvalidOperationException("SQLite returned an incomplete model-managed data analysis batch.");
        }
    }

    private static bool HasModelManagedUniqueCollision(
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent,
        ModelManagedDataMatrix targetValues,
        IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> uniqueKeys
    )
    {
        foreach (var uniqueKey in uniqueKeys)
        {
            var uniqueOrdinals = uniqueKey
                .Columns
                .Select(column => IndexOf(intent.Columns, column))
                .ToArray();

            var parameterCountPerRow = intent.KeyColumns.Count + uniqueOrdinals.Length;
            var batchSize = Math.Max(1, SqliteSafeMigrationLimits.MaximumParametersPerCommand / parameterCountPerRow);

            for (var offset = 0; offset < intent.RowCount; offset += batchSize)
            {
                var count = Math.Min(batchSize, intent.RowCount - offset);
                if (HasModelManagedUniqueCollisionBatch(
                        connection,
                        transaction,
                        intent,
                        targetValues,
                        uniqueKey,
                        uniqueOrdinals,
                        offset,
                        count))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasModelManagedUniqueCollisionBatch(
        DbConnection connection,
        DbTransaction? transaction,
        ModelManagedDataIntent intent,
        ModelManagedDataMatrix targetValues,
        ExpectedModelManagedDataUniqueKeyDefinition uniqueKey,
        int[] uniqueOrdinals,
        int offset,
        int count
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var cteColumns = Enumerable
            .Range(0, intent.KeyColumns.Count)
            .Select(static index => $"key_{index}")
            .Concat(
                Enumerable
                    .Range(0, uniqueKey.Columns.Count)
                    .Select(static index => $"unique_{index}"))
            .ToArray();

        var rows = new string[count];
        for (var localRow = 0; localRow < count; localRow++)
        {
            var row = offset + localRow;
            var parameters = new string[cteColumns.Length];
            var parameterIndex = 0;
            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                var name = $"$r{localRow}_k{column}";
                parameters[parameterIndex++] = name;
                AddParameter(command, name, intent.KeyValues.GetUnsafeValue(row, column));
            }

            for (var column = 0; column < uniqueOrdinals.Length; column++)
            {
                var name = $"$r{localRow}_u{column}";
                parameters[parameterIndex++] = name;
                AddParameter(command, name, targetValues.GetUnsafeValue(row, uniqueOrdinals[column]));
            }

            rows[localRow] = "(" + string.Join(", ", parameters) + ")";
        }

        var uniqueMatch = string.Join(
            " AND ",
            uniqueKey.Columns.Select((
                    column,
                    index
                ) => $"stored.{Delimit(column)} IS incoming.{Delimit($"unique_{index}")}"));

        var nonNullTarget = string.Join(
            " AND ",
            uniqueKey.Columns.Select((
                    _,
                    index
                ) => $"incoming.{Delimit($"unique_{index}")} IS NOT NULL"));

        var samePrimaryKey = string.Join(
            " AND ",
            intent.KeyColumns.Select((
                    column,
                    index
                ) => $"stored.{Delimit(column)} IS incoming.{Delimit($"key_{index}")}"));

        command.CommandText = $"WITH incoming ({string.Join(", ", cteColumns.Select(Delimit))}) AS ("
            + $"VALUES {string.Join(", ", rows)}) SELECT 1 FROM incoming "
            + $"JOIN {Delimit(intent.Table)} AS stored ON {uniqueMatch} "
            + $"WHERE {nonNullTarget} AND NOT ({samePrimaryKey}) LIMIT 1;";

        return command.ExecuteScalar() is not null;
    }

    private static long[] ReadModelManagedDependencyCounts(
        DbConnection connection,
        DbTransaction? transaction,
        DeleteModelManagedDataIntent intent
    )
    {
        var result = new long[intent.ForeignKeys.Count];
        for (var foreignKeyIndex = 0; foreignKeyIndex < intent.ForeignKeys.Count; foreignKeyIndex++)
        {
            var foreignKey = intent.ForeignKeys[foreignKeyIndex];
            var principalOrdinals = foreignKey
                .PrincipalColumns
                .Select(column => IndexOf(intent.Columns, column))
                .ToArray();

            var parameterCountPerRow = principalOrdinals.Length;
            var batchSize = Math.Max(1, SqliteSafeMigrationLimits.MaximumParametersPerCommand / parameterCountPerRow);

            for (var offset = 0; offset < intent.RowCount; offset += batchSize)
            {
                var count = Math.Min(batchSize, intent.RowCount - offset);
                result[foreignKeyIndex] += ReadModelManagedDependencyCountBatch(
                    connection,
                    transaction,
                    intent,
                    foreignKey,
                    principalOrdinals,
                    offset,
                    count);
            }
        }

        return result;
    }

    private static long ReadModelManagedDependencyCountBatch(
        DbConnection connection,
        DbTransaction? transaction,
        DeleteModelManagedDataIntent intent,
        ExpectedModelManagedDataForeignKeyDefinition foreignKey,
        int[] principalOrdinals,
        int offset,
        int count
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var cteColumns = Enumerable
            .Range(0, principalOrdinals.Length)
            .Select(static index => $"principal_{index}")
            .ToArray();

        var rows = new string[count];
        for (var localRow = 0; localRow < count; localRow++)
        {
            var row = offset + localRow;
            var parameters = new string[principalOrdinals.Length];
            for (var column = 0; column < principalOrdinals.Length; column++)
            {
                var name = $"$r{localRow}_p{column}";
                parameters[column] = name;
                AddParameter(command, name, intent.OldValues.GetUnsafeValue(row, principalOrdinals[column]));
            }

            rows[localRow] = "(" + string.Join(", ", parameters) + ")";
        }

        var dependencyMatch = string.Join(
            " AND ",
            foreignKey.Columns.Select((
                    column,
                    index
                ) => $"dependent.{Delimit(column)} IS incoming.{Delimit($"principal_{index}")}"));

        command.CommandText = $"WITH incoming ({string.Join(", ", cteColumns.Select(Delimit))}) AS ("
            + $"VALUES {string.Join(", ", rows)}) SELECT COUNT(*) FROM incoming "
            + $"JOIN {Delimit(foreignKey.Table)} AS dependent ON {dependencyMatch};";

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool HasUnmodeledIncomingForeignKey(
        SqliteCatalogSnapshot snapshot,
        DeleteModelManagedDataIntent intent
    )
    {
        foreach (var dependentTable in snapshot.Tables.Values)
        {
            foreach (var foreignKey in dependentTable.ForeignKeys)
            {
                if (!s_identifierComparer.Equals(foreignKey.PrincipalTable, intent.Table))
                {
                    continue;
                }

                var principalColumns = foreignKey.PrincipalColumns.Any(static column => column.Length > 0)
                    ? foreignKey.PrincipalColumns
                    : snapshot.Tables.TryGetValue(intent.Table, out var principalTable)
                        ? principalTable.PrimaryKeyColumns
                        : foreignKey.PrincipalColumns;

                var modeled = intent.ForeignKeys.Any(expected =>
                    s_identifierComparer.Equals(expected.Table, dependentTable.Name)
                    && IdentifiersEqual(expected.Columns, foreignKey.Columns)
                    && IdentifiersEqual(expected.PrincipalColumns, principalColumns));

                if (!modeled)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int IndexOf(
        IReadOnlyList<string> columns,
        string column
    )
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(columns[index], column))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            $"Model-managed metadata references column '{column}' outside the captured value set.");
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }

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

    private static bool CollationMatches(
        string? actual,
        SafeMigrationCollationIdentifier? expected
    )
    {
        if (expected?.Schema is not null)
        {
            return false;
        }

        var actualName = actual ?? "BINARY";
        var expectedName = expected?.Name ?? "BINARY";

        return s_identifierComparer.Equals(actualName, expectedName);
    }

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

    private static SafeMigrationProviderAnalysis Matching(
        bool postconditionSatisfied,
        string? matchedObjectName = null
    ) => new(
        SafeMigrationObservedState.Matching,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied,
        "classified_matching")
    {
        MatchedObjectName = matchedObjectName,
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
