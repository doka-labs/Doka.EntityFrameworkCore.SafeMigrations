namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>
/// Composes the standard EF Core SQLite generator with transactionally guarded
/// SafeMigrations commands for live migration and bundle execution.
/// </summary>
public sealed class SqliteSafeMigrationsSqlGenerator : IMigrationsSqlGenerator
{
    private readonly ISqliteSafeMigrationsBaselineGenerator _baselineGenerator;
    private readonly SqliteSafeMigrationProviderAnalyzer _analyzer;
    private readonly SqliteSafeMigrationExecutionState _executionState;
    private readonly MigrationsSqlGeneratorDependencies _dependencies;
    private readonly SqliteSafeMigrationSqlExpressionRenderer _expressionRenderer;

    /// <summary>Initializes the composed SQLite SafeMigrations generator.</summary>
    /// <param name="baselineGenerator">The configured standard SQLite migrations SQL generator.</param>
    /// <param name="analyzer">The provider live-state analyzer.</param>
    /// <param name="executionState">The scoped runtime catalog cache.</param>
    /// <param name="dependencies">The EF Core SQL-generator dependencies.</param>
    internal SqliteSafeMigrationsSqlGenerator(
        ISqliteSafeMigrationsBaselineGenerator baselineGenerator,
        ISafeMigrationProviderAnalyzer analyzer,
        SqliteSafeMigrationExecutionState executionState,
        MigrationsSqlGeneratorDependencies dependencies
    )
    {
        ArgumentNullException.ThrowIfNull(baselineGenerator);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(executionState);
        ArgumentNullException.ThrowIfNull(dependencies);

        _baselineGenerator = baselineGenerator;
        _analyzer = analyzer as SqliteSafeMigrationProviderAnalyzer
            ?? throw new InvalidOperationException("The SQLite SafeMigrations analyzer registration is inconsistent.");
        _executionState = executionState;
        _dependencies = dependencies;
        _expressionRenderer = new SqliteSafeMigrationSqlExpressionRenderer(
            dependencies.TypeMappingSource,
            dependencies.SqlGenerationHelper);
    }

    /// <inheritdoc />
    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        // WHY: SafeMigrations decisions require the live SQLite catalog. Emitting
        // partial script SQL before reaching a safe operation would be unsafe.
        if ((options & MigrationsSqlGenerationOptions.Script) != 0
            && operations.Any(static operation => operation is SafeMigrationOperation))
        {
            throw new NotSupportedException(
                "SQLite cannot express SafeMigrations runtime catalog decisions in a SQL script. "
                + "Apply migrations through EF Core or a migration bundle instead.");
        }

        SafeMigrationExpectedIndexTransitions.Validate(operations);
        var runtimeModel = _dependencies.CurrentContext.Context.Model;
        var effectiveModel = ReferenceEquals(model, runtimeModel)
            ? _dependencies.CurrentContext.Context.GetService<IDesignTimeModel>().Model
            : model;

        var result = new List<MigrationCommand>(operations.Count);
        for (var ordinal = 0; ordinal < operations.Count;)
        {
            var operation = operations[ordinal];
            if (operation is SafeMigrationDesignTimeServicesRequiredOperation)
            {
                if (ordinal != 0)
                {
                    throw new InvalidOperationException(
                        "The SafeMigrations design-time-services guard must be the first migration operation.");
                }

                ordinal++;
                continue;
            }

            // WHY: SQLite implements several ALTER operations by rebuilding the
            // table. Keeping adjacent structural operations together preserves
            // their order while limiting each table to one atomic rebuild.
            if (SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(operation))
            {
                var structuralOperations = new List<MigrationOperation>();
                while (ordinal < operations.Count
                       && SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(operations[ordinal]))
                {
                    structuralOperations.Add(operations[ordinal]);
                    ordinal++;
                }

                if (structuralOperations.Any(static candidate => candidate is SafeMigrationOperation))
                {
                    var rebuildArtifacts = SqliteRebuildArtifactContract.FromModel(
                        effectiveModel,
                        structuralOperations);

                    result.Add(
                        new SqliteSafeMigrationBatchCommand(
                            structuralOperations.AsReadOnly(),
                            _baselineGenerator,
                            _analyzer,
                            _executionState,
                            _expressionRenderer,
                            rebuildArtifacts,
                            effectiveModel,
                            options,
                            CreatePlaceholder(
                                $"-- SQLite SafeMigrations structural batch: {structuralOperations.Count}"),
                            _dependencies.CurrentContext.Context,
                            _dependencies.Logger));
                }
                else
                {
                    foreach (var command in _baselineGenerator.Generate(structuralOperations, effectiveModel, options))
                    {
                        result.Add(WrapProviderCommand(command, invalidatesCatalog: true));
                    }
                }

                continue;
            }

            if (operation is not SafeMigrationOperation safeOperation)
            {
                var providerOperations = new List<MigrationOperation>();
                while (ordinal < operations.Count
                       && operations[ordinal] is not SafeMigrationOperation
                       && operations[ordinal] is not SafeMigrationDesignTimeServicesRequiredOperation
                       && !SqliteSafeMigrationOperationClassifier.CanParticipateInStructuralBatch(operations[ordinal]))
                {
                    providerOperations.Add(operations[ordinal]);
                    ordinal++;
                }

                var invalidatesCatalog = providerOperations.Any(InvalidatesCatalog);
                foreach (var command in _baselineGenerator.Generate(providerOperations, effectiveModel, options))
                {
                    result.Add(WrapProviderCommand(command, invalidatesCatalog));
                }

                continue;
            }

            var applyCommands = RenderBaseline(safeOperation, effectiveModel, options);
            ValidateBaseline(applyCommands);
            var operationArtifacts = SqliteRebuildArtifactContract.FromOperations([safeOperation]);

            result.Add(
                new SqliteSafeMigrationCommand(
                    safeOperation,
                    applyCommands,
                    _analyzer,
                    _executionState,
                    operationArtifacts,
                    CreatePlaceholder(safeOperation),
                    _dependencies.CurrentContext.Context,
                    _dependencies.Logger));
            ordinal++;
        }

        return result.AsReadOnly();
    }

    private IReadOnlyList<MigrationCommand> RenderBaseline(
        SafeMigrationOperation operation,
        IModel? model,
        MigrationsSqlGenerationOptions options
    )
    {
        if (operation.Intent is ModelManagedDataIntent or EnsureSchemaIntent or DropSchemaIntent or RenameIndexIntent)
        {
            return [];
        }

        if (operation.Intent is EnsureColumnIntent column
            && (column.Definition.ComputedColumnSql is not null || column.Definition.ComputedExpression is not null))
        {
            return [CreateCommand(RenderGeneratedColumn(column))];
        }

        if (operation.Intent is EnsureIndexIntent index
            && RequiresCustomIndexSql(index.Definition))
        {
            return [CreateCommand(RenderExpressionIndex(index.Definition))];
        }

        var standardOperation = SafeMigrationStandardOperationFactory.Create(
            operation.Intent,
            _expressionRenderer.Render,
            static collation => collation.Schema is null ? collation.Name : null);

        return _baselineGenerator.Generate([standardOperation], model, options);
    }

    private SqliteInvalidatingMigrationCommand WrapProviderCommand(
        MigrationCommand command,
        bool invalidatesCatalog
    ) => new(
        command,
        _executionState,
        invalidatesCatalog,
        CreatePlaceholder(command.CommandText),
        _dependencies.CurrentContext.Context,
        _dependencies.Logger);

    private static bool InvalidatesCatalog(
        MigrationOperation operation
    ) => operation is not InsertDataOperation and not UpdateDataOperation and not DeleteDataOperation;

    private IRelationalCommand CreatePlaceholder(
        SafeMigrationOperation operation
    ) => CreatePlaceholder($"-- SQLite SafeMigrations: {operation.Intent.Kind} {operation.Intent.ObjectName}");

    private IRelationalCommand CreatePlaceholder(
        string text
    ) => _dependencies
        .CommandBuilderFactory
        .Create()
        .Append(text)
        .Build();

    private MigrationCommand CreateCommand(
        string commandText
    ) => new(
        _dependencies
            .CommandBuilderFactory
            .Create()
            .Append(commandText)
            .Build(),
        _dependencies.CurrentContext.Context,
        _dependencies.Logger,
        transactionSuppressed: false);

    private string RenderGeneratedColumn(
        EnsureColumnIntent intent
    )
    {
        var definition = intent.Definition;
        var storeType = definition.StoreType
            ?? _dependencies.TypeMappingSource.FindMapping(
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
            ?? throw new NotSupportedException($"SQLite has no type mapping for generated column '{definition.Name}'.");

        var expression = definition.ComputedExpression is null
            ? definition.ComputedColumnSql!
            : _expressionRenderer.Render(definition.ComputedExpression);

        var collation = definition.Collation is null
            ? string.Empty
            : " COLLATE " + _dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Collation.Name);

        return "ALTER TABLE "
            + _dependencies.SqlGenerationHelper.DelimitIdentifier(intent.Table)
            + " ADD "
            + _dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name)
            + " "
            + storeType
            + collation
            + (definition.IsNullable ? string.Empty : " NOT NULL")
            + " GENERATED ALWAYS AS ("
            + expression
            + ") "
            + (definition.IsStored == true ? "STORED" : "VIRTUAL")
            + ";";
    }

    private string RenderExpressionIndex(
        ExpectedIndexDefinition definition
    )
    {
        var keys = definition.Keys.Select(key =>
        {
            var value = key.Column is not null
                ? _dependencies.SqlGenerationHelper.DelimitIdentifier(key.Column)
                : "("
                + _expressionRenderer.Render(key.StructuredExpression ?? SafeMigrationSql.Opaque(key.Expression!))
                + ")";

            if (key.Collation is not null)
            {
                value += " COLLATE " + _dependencies.SqlGenerationHelper.DelimitIdentifier(key.Collation.Name);
            }

            if (key.SortOrder == SafeMigrationIndexSortOrder.Descending)
            {
                value += " DESC";
            }

            return value;
        });
        var filter = definition.StructuredFilter is null
            ? definition.Filter
            : _expressionRenderer.Render(definition.StructuredFilter);

        return "CREATE "
            + (definition.Unique ? "UNIQUE " : string.Empty)
            + "INDEX "
            + _dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Name)
            + " ON "
            + _dependencies.SqlGenerationHelper.DelimitIdentifier(definition.Table)
            + " ("
            + string.Join(", ", keys)
            + ")"
            + (filter is null ? string.Empty : " WHERE " + filter)
            + ";";
    }

    private static bool RequiresCustomIndexSql(
        ExpectedIndexDefinition definition
    ) => definition.Keys.Any(static key =>
        key.Expression is not null || key.StructuredExpression is not null || key.Collation is not null);

    private static void ValidateBaseline(
        IReadOnlyList<MigrationCommand> commands
    )
    {
        foreach (var command in commands)
        {
            if (command.TransactionSuppressed
                && !command
                    .CommandText
                    .AsSpan()
                    .Trim()
                    .StartsWith("PRAGMA foreign_keys", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "SQLite SafeMigrations cannot guard an unexpected transaction-suppressed baseline command.");
            }
        }
    }
}
