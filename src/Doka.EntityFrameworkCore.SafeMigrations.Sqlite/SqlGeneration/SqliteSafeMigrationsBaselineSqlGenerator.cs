namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Retains explicit physical facets for provider-generated safe table columns.</summary>
internal sealed class SqliteSafeMigrationsBaselineSqlGenerator : SqliteMigrationsSqlGenerator
{
    private bool _preserveGeneratedFacets;
    private List<MigrationOperation>? _capturedOperations;
    private HashSet<CreateTableOperation>? _completeCreators;
    private SqliteGeneratedColumnOwnership? _ownership;

    /// <summary>Initializes the native generator specialization without replacing its operation pipeline.</summary>
    /// <param name="dependencies">The official provider's SQL generation dependencies.</param>
    /// <param name="annotations">The official provider's relational annotation provider.</param>
    public SqliteSafeMigrationsBaselineSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        IRelationalAnnotationProvider annotations
    ) : base(dependencies, annotations)
    {
    }

    /// <summary>Generates a guarded baseline with complete generated-column physical declarations.</summary>
    /// <param name="operations">The ordered standard operations accepted by the safe runtime planner.</param>
    /// <param name="model">The target relational model, when available.</param>
    /// <param name="options">The native SQL generation options.</param>
    /// <param name="guardedOperations">The original safe/provider stream before safe intent conversion.</param>
    /// <returns>The provider's commands with complete generated-column declarations.</returns>
    public IReadOnlyList<MigrationCommand> GenerateSafe(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model,
        MigrationsSqlGenerationOptions options,
        IReadOnlyList<MigrationOperation> guardedOperations
    )
    {
        ArgumentNullException.ThrowIfNull(guardedOperations);

        if (!CouldContainGeneratedColumns(operations, model))
        {
            return base.Generate(operations, model, options);
        }

        var ownership = new SqliteGeneratedColumnOwnership(operations, guardedOperations);
        if (!ownership.RequiresCompleteDeclarations(model))
        {
            return base.Generate(operations, model, options);
        }

        var previousCapture = _capturedOperations;
        var previousCreators = _completeCreators;
        var previousOwnership = _ownership;
        var previousOptions = Options;
        var rewritten = new List<MigrationOperation>(operations.Count);
        _capturedOperations = rewritten;

        try
        {
            // WHY: The documented protected dispatcher observes the complete
            // native rewrite before any SQL is rendered. Actual rename metadata
            // then identifies rebuilt owners without private API access, model
            // mutation, SQL rewriting, or assumptions about temporary names.
            _ = base.Generate(operations, model, options);
            _capturedOperations = null;
            _completeCreators = ownership.ResolveCreators(rewritten);
            _ownership = ownership;
            Options = options;
            var builder = new MigrationCommandListBuilder(Dependencies);
            foreach (var operation in rewritten)
            {
                base.Generate(operation, model, builder);
            }

            return builder.GetCommandList();
        }
        finally
        {
            _capturedOperations = previousCapture;
            _completeCreators = previousCreators;
            _ownership = previousOwnership;
            Options = previousOptions;
        }
    }

    /// <inheritdoc />
    protected override void Generate(MigrationOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        if (_capturedOperations is not null)
        {
            _capturedOperations.Add(operation);

            return;
        }

        base.Generate(operation, model, builder);
    }

    /// <inheritdoc />
    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        var previous = _preserveGeneratedFacets;
        _preserveGeneratedFacets = _completeCreators?.Contains(operation) == true;
        try
        {
            base.Generate(operation, model, builder, terminate);
        }
        finally
        {
            _preserveGeneratedFacets = previous;
        }
    }

    /// <summary>Avoids capture structures for the ordinary non-generated migration path.</summary>
    private static bool CouldContainGeneratedColumns(IReadOnlyList<MigrationOperation> operations, IModel? model)
    {
        foreach (var operation in operations)
        {
            if (operation is ColumnOperation { ComputedColumnSql: not null }
                || operation is CreateTableOperation creator
                && creator.Columns.Any(static column => column.ComputedColumnSql is not null))
            {
                return true;
            }
        }

        return model?.GetRelationalModel().Tables.Any(static table =>
            table.Columns.Any(static column => column.ComputedColumnSql is not null)) == true;
    }

    /// <inheritdoc />
    protected override void ComputedColumnDefinition(
        string? schema,
        string table,
        string name,
        ColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (!_preserveGeneratedFacets || _ownership?.IsProviderOperation(operation) == true)
        {
            // WHY: Ordinary EF operations remain provider-owned. Only a safe
            // definition promises the explicit physical facets checked afterward.
            base.ComputedColumnDefinition(schema, table, name, operation, model, builder);

            return;
        }

        // WHY: EF's SQLite computed renderer deliberately omits type and NOT
        // NULL. SQLite does not infer either from the expression, so using that
        // SQL would violate the exact definition immediately after creation.
        var expression = operation.ComputedColumnSql
            ?? throw new InvalidOperationException("A generated column requires its SQL expression.");

        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(name))
            .Append(" ")
            .Append(operation.ColumnType ?? GetColumnType(schema, table, name, operation, model));

        if (operation.Collation is not null)
        {
            builder.Append(" COLLATE ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Collation));
        }

        if (!operation.IsNullable)
        {
            builder.Append(" NOT NULL");
        }

        builder.Append(" GENERATED ALWAYS AS (")
            .Append(expression)
            .Append(operation.IsStored == true ? ") STORED" : ") VIRTUAL");
    }
}
