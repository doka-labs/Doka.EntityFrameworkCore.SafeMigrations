namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

#pragma warning disable EF1001
/// <summary>
/// Generates PostgreSQL-specific SQL for the safe migration operations exposed by this library.
/// </summary>
public sealed class PostgreSqlSafeMigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlSafeMigrationsSqlGenerator"/> class.
    /// </summary>
    /// <param name="dependencies">The shared SQL-generator dependencies.</param>
    /// <param name="npgsqlSingletonOptions">The active PostgreSQL provider singleton options.</param>
    public PostgreSqlSafeMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        INpgsqlSingletonOptions npgsqlSingletonOptions
    ) : base(dependencies, npgsqlSingletonOptions) { }

    /// <inheritdoc />
    protected override void Generate(
        MigrationOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        switch (operation)
        {
            case SafeAddPrimaryKeyOperation addPrimaryKeyOperation:
                GenerateSafeConstraintOperation(
                    builder,
                    model,
                    addPrimaryKeyOperation,
                    static operation => operation.Schema,
                    static operation => operation.Table,
                    static operation => operation.Name,
                    static operation => operation.StrictMode,
                    static operation => operation.Execution,
                    static operation => operation.ExpectedDefinition,
                    "p",
                    "primary key",
                    BuildPrimaryKeyMatchesSql,
                    PrimaryKeyConstraint);
                return;

            case SafeAddUniqueConstraintOperation addUniqueConstraintOperation:
                GenerateSafeConstraintOperation(
                    builder,
                    model,
                    addUniqueConstraintOperation,
                    static operation => operation.Schema,
                    static operation => operation.Table,
                    static operation => operation.Name,
                    static operation => operation.StrictMode,
                    static operation => operation.Execution,
                    static operation => operation.ExpectedDefinition,
                    "u",
                    "unique constraint",
                    BuildUniqueConstraintMatchesSql,
                    UniqueConstraint);
                return;

            case SafeAddForeignKeyOperation addForeignKeyOperation:
                GenerateSafeConstraintOperation(
                    builder,
                    model,
                    addForeignKeyOperation,
                    static operation => operation.Schema,
                    static operation => operation.Table,
                    static operation => operation.Name,
                    static operation => operation.StrictMode,
                    static operation => operation.Execution,
                    static operation => operation.ExpectedDefinition,
                    "f",
                    "foreign key",
                    BuildForeignKeyMatchesSql,
                    ForeignKeyConstraint);
                return;

            case SafeAddCheckConstraintOperation addCheckConstraintOperation:
                GenerateSafeConstraintOperation(
                    builder,
                    model,
                    addCheckConstraintOperation,
                    static operation => operation.Schema,
                    static operation => operation.Table,
                    static operation => operation.Name,
                    static operation => operation.StrictMode,
                    static operation => operation.Execution,
                    static operation => operation.ExpectedDefinition,
                    "c",
                    "check constraint",
                    BuildCheckConstraintMatchesSql,
                    CheckConstraint);
                return;

            default:
                base.Generate(operation, model, builder);
                return;
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        if (!IsIfNotExists(operation))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        var createTableSql = BuildCreateTableSql(operation, model, ifNotExists: true);
        var strictMode = GetStrictMode(operation);
        GenerateGuardedCreateOrAdd(
            builder,
            createTableSql,
            strictMode,
            ExistsTableSql(operation.Schema, operation.Name),
            () =>
            {
                var expectedDefinition = GetExpectedDefinition<ExpectedTableDefinition>(operation);
                return BuildTableMatchesSql(expectedDefinition);
            },
            () =>
            {
                var expectedDefinition = GetExpectedDefinition<ExpectedTableDefinition>(operation);
                return BuildMismatchMessage("table", operation.Name, operation.Name, expectedDefinition);
            },
            terminate);
    }

    /// <inheritdoc />
    protected override void Generate(
        AddColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        if (!IsIfNotExists(operation))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        var addColumnSql = BuildAddColumnSql(operation, model);
        var execution = SafeMigrationExecutionAnnotationHelper.GetExecutionOptions(operation);
        var hasExecutionOptions = operation[SafeMigrationAnnotationNames.ConflictMode] is SafeMigrationConflictMode;

        if (!hasExecutionOptions)
        {
            var strictMode = GetStrictMode(operation);
            GenerateGuardedCreateOrAdd(
                builder,
                addColumnSql,
                strictMode,
                ExistsColumnSql(operation.Schema, operation.Table, operation.Name),
                () =>
                {
                    var expectedDefinition = GetExpectedDefinition<ExpectedColumnDefinition>(operation);
                    return BuildColumnMatchesSql(operation.Schema, operation.Table, expectedDefinition);
                },
                () =>
                {
                    var expectedDefinition = GetExpectedDefinition<ExpectedColumnDefinition>(operation);
                    return BuildMismatchMessage("column", operation.Name, operation.Table, expectedDefinition);
                },
                terminate);
            return;
        }

        var expectedDefinition = GetExpectedDefinition<ExpectedColumnDefinition>(operation);
        var missingDecision = SafeMigrationDecisionPlanner.PlanColumn(
            execution,
            SafeMigrationComparisonState.Missing,
            expectedDefinition);
        var mismatchDecision = SafeMigrationDecisionPlanner.PlanColumn(
            execution,
            SafeMigrationComparisonState.Different,
            expectedDefinition);
        var existsSql = ExistsColumnSql(operation.Schema, operation.Table, operation.Name);
        var matchesSql = BuildColumnMatchesSql(operation.Schema, operation.Table, expectedDefinition);
        var mismatchMessage = BuildMismatchMessage("column", operation.Name, operation.Table, expectedDefinition);

        if (execution.PreflightOnly)
        {
            GenerateColumnPreflight(
                builder,
                execution,
                existsSql,
                matchesSql,
                missingDecision.Reason,
                mismatchMessage,
                terminate,
                allowMissing: missingDecision.Outcome != SafeMigrationExecutionOutcome.Rejected);
            return;
        }

        if (execution.ConflictMode == SafeMigrationConflictMode.None)
        {
            builder.Append(addColumnSql);
            EndStatement(builder, terminate);
            return;
        }

        if (missingDecision.Outcome == SafeMigrationExecutionOutcome.Rejected)
        {
            GenerateColumnDecisionBlock(
                builder,
                existsSql,
                matchesSql,
                addColumnSql,
                missingDecision.Reason,
                mismatchDecision.Reason,
                terminate,
                allowMissing: false);
            return;
        }

        GenerateGuardedCreateOrAdd(
            builder,
            addColumnSql,
            SafeMigrationExecutionAnnotationHelper.GetCompatibleStrictMode(execution),
            existsSql,
            () => matchesSql,
            () => mismatchMessage,
            terminate);
    }

    /// <inheritdoc />
    protected override void Generate(
        AlterColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (!IsAlterIfDifferent(operation))
        {
            base.Generate(operation, model, builder);
            return;
        }

        var expectedDefinition = GetExpectedDefinition<ExpectedColumnDefinition>(operation);
        var alterColumnSql = BuildAlterColumnSql(operation, model);

        GenerateExistingConditionalDdl(
            builder,
            ExistsColumnSql(operation.Schema, operation.Table, operation.Name),
            BuildColumnMatchesSql(operation.Schema, operation.Table, expectedDefinition),
            alterColumnSql,
            BuildMissingMessage("column", operation.Name, operation.Table),
            terminate: true);
    }

    /// <inheritdoc />
    protected override void Generate(
        CreateIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        if (!IsIfNotExists(operation))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        var execution = SafeMigrationExecutionAnnotationHelper.GetExecutionOptions(operation);
        var expectedDefinition = GetExpectedDefinition<ExpectedIndexDefinition>(operation);
        var createIndexSql = BuildCreateIndexSql(operation, model);
        var existsSql = ExistsIndexSql(operation.Schema, operation.Table, operation.Name);
        var matchesSql = BuildIndexMatchesSql(expectedDefinition);
        var mismatchMessage = BuildMismatchMessage("index", operation.Name, operation.Table, expectedDefinition);

        if (execution.PreflightOnly)
        {
            GenerateExistsPreflight(builder, execution, existsSql, matchesSql, mismatchMessage, terminate);
            return;
        }

        GenerateGuardedCreateOrAdd(
            builder,
            createIndexSql,
            SafeMigrationExecutionAnnotationHelper.GetCompatibleStrictMode(execution),
            existsSql,
            () => matchesSql,
            () => mismatchMessage,
            terminate);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        builder
            .Append("DROP TABLE IF EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema));
        EndStatement(builder, terminate);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder);
            return;
        }

        builder
            .Append("DROP SCHEMA IF EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder, true);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" DROP COLUMN IF EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder, terminate);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        builder.Append("DROP INDEX IF EXISTS ");
        if (operation.Schema is not null)
        {
            builder
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Schema))
                .Append(".");
        }

        builder.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder, terminate);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropForeignKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" DROP CONSTRAINT IF EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder, terminate);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropPrimaryKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        var schema = operation.Schema ?? "public";
        var table = operation.Table
            ?? throw new InvalidOperationException("DropPrimaryKeyOperation requires a table name.");
        var qualifiedTable = Dependencies.SqlGenerationHelper.DelimitIdentifier(table, operation.Schema);

        builder
            .AppendLine("DO $SAFE$")
            .AppendLine("DECLARE constraint_name text;")
            .AppendLine("BEGIN")
            .AppendLine("    SELECT c.conname")
            .AppendLine("    INTO constraint_name")
            .AppendLine("    FROM pg_constraint c")
            .AppendLine("    JOIN pg_class t ON t.oid = c.conrelid")
            .AppendLine("    JOIN pg_namespace n ON n.oid = t.relnamespace")
            .AppendLine($"    WHERE n.nspname = '{EscapeSqlLiteral(schema)}'")
            .AppendLine($"      AND t.relname = '{EscapeSqlLiteral(table)}'")
            .AppendLine("      AND c.contype = 'p'")
            .AppendLine("    LIMIT 1;")
            .AppendLine()
            .AppendLine("    IF constraint_name IS NOT NULL THEN")
            .AppendLine($"        EXECUTE 'ALTER TABLE {qualifiedTable} DROP CONSTRAINT IF EXISTS ' || quote_ident(constraint_name);")
            .AppendLine("    END IF;")
            .AppendLine("END")
            .Append("$SAFE$");
        EndStatement(builder, terminate);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropUniqueConstraintOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder);
            return;
        }

        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" DROP CONSTRAINT IF EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder, true);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropCheckConstraintOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder);
            return;
        }

        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" DROP CONSTRAINT IF EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name));
        EndStatement(builder, true);
    }

    /// <inheritdoc />
    protected override void Generate(
        RenameColumnOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder);
            return;
        }

        GenerateDoBlockGuard(
            builder,
            ExistsColumnSql(operation.Schema, operation.Table, operation.Name),
            BuildRenameColumnSql(operation, model),
            terminate: true);
    }

    /// <inheritdoc />
    protected override void Generate(
        RenameTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder);
            return;
        }

        GenerateDoBlockGuard(
            builder,
            ExistsTableSql(operation.Schema, operation.Name),
            BuildRenameTableSql(operation, model),
            terminate: true);
    }

    /// <inheritdoc />
    protected override void Generate(
        RenameIndexOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder);
            return;
        }

        var table = operation.Table
            ?? throw new InvalidOperationException("Safe rename-index requires a table name for PostgreSQL.");
        GenerateDoBlockGuard(
            builder,
            ExistsIndexSql(operation.Schema, table, operation.Name),
            BuildRenameIndexSql(operation, model),
            terminate: true);
    }

    private void GenerateGuardedAddConstraint(
        MigrationCommandListBuilder builder,
        string? schema,
        string table,
        string name,
        string constraintType,
        SafeMigrationStrictMode strictMode,
        string matchesSql,
        string mismatchMessage,
        Action<MigrationCommandListBuilder> buildStatement
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        buildStatement(innerBuilder);
        EndStatement(innerBuilder);
        var ddlSql = SingleLine(
                innerBuilder
                    .GetCommandList()
                    .Single()
                    .CommandText)
            .TrimEnd(';');
        var existsSql = ConstraintExistsSql(schema, table, name, constraintType);

        if (strictMode == SafeMigrationStrictMode.None)
        {
            GenerateDoBlockGuard(builder, existsSql, ddlSql, terminate: true, runWhenExists: false);
            return;
        }

        GenerateConditionalDdl(builder, existsSql, matchesSql, ddlSql, mismatchMessage, terminate: true);
    }

    private void GenerateSafeConstraintOperation<TOperation, TDefinition>(
        MigrationCommandListBuilder builder,
        IModel? model,
        TOperation operation,
        Func<TOperation, string?> getSchema,
        Func<TOperation, string> getTable,
        Func<TOperation, string> getName,
        Func<TOperation, SafeMigrationStrictMode> getStrictMode,
        Func<TOperation, SafeMigrationExecutionOptions?> getExecution,
        Func<TOperation, TDefinition?> getExpectedDefinition,
        string constraintType,
        string mismatchObjectType,
        Func<TDefinition, string> buildMatchesSql,
        Action<TOperation, IModel?, MigrationCommandListBuilder> appendConstraint
    )
        where TOperation : MigrationOperation
        where TDefinition : class
    {
        var schema = getSchema(operation);
        var table = getTable(operation);
        var name = getName(operation);
        var execution = getExecution(operation)
            ?? new SafeMigrationExecutionOptions(
                getStrictMode(operation) == SafeMigrationStrictMode.ThrowIfDifferent
                    ? SafeMigrationConflictMode.ThrowIfDifferent
                    : SafeMigrationConflictMode.None);
        var definition = getExpectedDefinition(operation)
            ?? throw new InvalidOperationException(
                $"Expected {mismatchObjectType} definition is missing. This is required for all safe constraint operations.");

        var matchesSql = buildMatchesSql(definition);
        var mismatchMessage = BuildMismatchMessage(mismatchObjectType, name, table, definition);

        if (execution.PreflightOnly)
        {
            GenerateExistsPreflight(
                builder,
                execution,
                ConstraintExistsSql(schema, table, name, constraintType),
                matchesSql,
                mismatchMessage,
                terminate: true);
            return;
        }

        GenerateGuardedAddConstraint(
            builder,
            schema,
            table,
            name,
            constraintType,
            SafeMigrationExecutionAnnotationHelper.GetCompatibleStrictMode(execution),
            matchesSql,
            mismatchMessage,
            innerBuilder =>
            {
                innerBuilder
                    .Append("ALTER TABLE ")
                    .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(table, schema))
                    .Append(" ADD ");
                appendConstraint(operation, model, innerBuilder);
            });
    }

    private void GenerateGuardedCreateOrAdd(
        MigrationCommandListBuilder builder,
        string ddlSql,
        SafeMigrationStrictMode strictMode,
        string existsSql,
        Func<string> buildMatchesSql,
        Func<string> buildMismatchMessage,
        bool terminate
    )
    {
        if (strictMode == SafeMigrationStrictMode.None)
        {
            builder.Append(ddlSql);
            EndStatement(builder, terminate);
            return;
        }

        GenerateConditionalDdl(builder, existsSql, buildMatchesSql(), ddlSql, buildMismatchMessage(), terminate);
    }

    private string BuildCreateTableSql(
        CreateTableOperation operation,
        IModel? model,
        bool ifNotExists
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        innerBuilder.Append("CREATE TABLE ");

        if (ifNotExists)
        {
            innerBuilder.Append("IF NOT EXISTS ");
        }

        innerBuilder
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name, operation.Schema))
            .AppendLine(" (");

        using (innerBuilder.Indent())
        {
            CreateTableColumns(operation, model, innerBuilder);
            CreateTableConstraints(operation, model, innerBuilder);
            innerBuilder.AppendLine();
        }

        innerBuilder.Append(")");
        EndStatement(innerBuilder);
        return SingleLine(
                innerBuilder
                    .GetCommandList()
                    .Single()
                    .CommandText)
            .TrimEnd(';');
    }

    private string BuildAddColumnSql(
        AddColumnOperation operation,
        IModel? model
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        innerBuilder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" ADD COLUMN IF NOT EXISTS ");
        ColumnDefinition(operation.Schema, operation.Table, operation.Name, operation, model, innerBuilder);
        EndStatement(innerBuilder);
        return SingleLine(
                innerBuilder
                    .GetCommandList()
                    .Single()
                    .CommandText)
            .TrimEnd(';');
    }

    private string BuildAlterColumnSql(
        AlterColumnOperation operation,
        IModel? model
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        base.Generate(operation, model, innerBuilder);

        var commands = innerBuilder.GetCommandList();
        if (commands.Count != 1)
        {
            throw new NotSupportedException(
                "Safe alter-column currently supports only single-statement PostgreSQL alterations.");
        }

        return SingleLine(commands[0].CommandText)
            .TrimEnd(';');
    }

    private string BuildCreateIndexSql(
        CreateIndexOperation operation,
        IModel? model
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        innerBuilder.Append("CREATE ");
        if (operation.IsUnique)
        {
            innerBuilder.Append("UNIQUE ");
        }

        innerBuilder
            .Append("INDEX IF NOT EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" (");

        GenerateIndexColumnList(operation, model, innerBuilder);
        innerBuilder.Append(")");
        IndexOptions(operation, model, innerBuilder);
        EndStatement(innerBuilder);
        return SingleLine(
                innerBuilder
                    .GetCommandList()
                    .Single()
                    .CommandText)
            .TrimEnd(';');
    }

    private string BuildRenameColumnSql(
        RenameColumnOperation operation,
        IModel? model
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        base.Generate(operation, model, innerBuilder);
        var commands = innerBuilder.GetCommandList();

        return commands.Count == 1
            ? SingleLine(commands[0].CommandText)
                .TrimEnd(';')
            : throw new NotSupportedException(
                "Safe rename-column currently supports only single-statement PostgreSQL renames.");
    }

    private string BuildRenameTableSql(
        RenameTableOperation operation,
        IModel? model
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        base.Generate(operation, model, innerBuilder);
        var commands = innerBuilder.GetCommandList();

        return commands.Count == 1
            ? SingleLine(commands[0].CommandText)
                .TrimEnd(';')
            : throw new NotSupportedException(
                "Safe rename-table currently supports only single-statement PostgreSQL renames.");
    }

    private string BuildRenameIndexSql(
        RenameIndexOperation operation,
        IModel? model
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        base.Generate(operation, model, innerBuilder);
        var commands = innerBuilder.GetCommandList();

        return commands.Count == 1
            ? SingleLine(commands[0].CommandText)
                .TrimEnd(';')
            : throw new NotSupportedException(
                "Safe rename-index currently supports only single-statement PostgreSQL renames.");
    }

    private void GenerateConditionalDdl(
        MigrationCommandListBuilder builder,
        string existsSql,
        string matchesSql,
        string ddlSql,
        string mismatchMessage,
        bool terminate
    )
    {
        builder
            .AppendLine("DO $SAFE$")
            .AppendLine("BEGIN")
            .AppendLine($"    IF NOT EXISTS ({SingleLine(existsSql)}) THEN")
            .AppendLine($"        EXECUTE {SqlLiteral(SingleLine(ddlSql).TrimEnd(';'))};")
            .AppendLine($"    ELSIF EXISTS ({SingleLine(matchesSql)}) THEN")
            .AppendLine("        PERFORM 1;")
            .AppendLine("    ELSE")
            .AppendLine($"        RAISE EXCEPTION USING MESSAGE = {SqlLiteral(mismatchMessage)};")
            .AppendLine("    END IF;")
            .AppendLine("END")
            .Append("$SAFE$");
        EndStatement(builder, terminate);
    }

    private void GenerateExistingConditionalDdl(
        MigrationCommandListBuilder builder,
        string existsSql,
        string matchesSql,
        string ddlSql,
        string missingMessage,
        bool terminate
    )
    {
        builder
            .AppendLine("DO $SAFE$")
            .AppendLine("BEGIN")
            .AppendLine($"    IF NOT EXISTS ({SingleLine(existsSql)}) THEN")
            .AppendLine($"        RAISE EXCEPTION USING MESSAGE = {SqlLiteral(missingMessage)};")
            .AppendLine($"    ELSIF EXISTS ({SingleLine(matchesSql)}) THEN")
            .AppendLine("        PERFORM 1;")
            .AppendLine("    ELSE")
            .AppendLine($"        EXECUTE {SqlLiteral(SingleLine(ddlSql).TrimEnd(';'))};")
            .AppendLine("    END IF;")
            .AppendLine("END")
            .Append("$SAFE$");
        EndStatement(builder, terminate);
    }

    private void GenerateDoBlockGuard(
        MigrationCommandListBuilder builder,
        string existsSql,
        string ddlSql,
        bool terminate,
        bool runWhenExists = true
    )
    {
        var condition = runWhenExists
            ? $"EXISTS ({SingleLine(existsSql)})"
            : $"NOT EXISTS ({SingleLine(existsSql)})";

        builder
            .AppendLine("DO $SAFE$")
            .AppendLine("BEGIN")
            .AppendLine($"    IF {condition} THEN")
            .AppendLine($"        EXECUTE {SqlLiteral(SingleLine(ddlSql).TrimEnd(';'))};")
            .AppendLine("    END IF;")
            .AppendLine("END")
            .Append("$SAFE$");
        EndStatement(builder, terminate);
    }

    private void GenerateExistsPreflight(
        MigrationCommandListBuilder builder,
        SafeMigrationExecutionOptions execution,
        string existsSql,
        string matchesSql,
        string mismatchMessage,
        bool terminate
    )
    {
        if (execution.ConflictMode == SafeMigrationConflictMode.None)
        {
            builder.Append("SELECT 1");
            EndStatement(builder, terminate);
            return;
        }

        builder
            .AppendLine("DO $SAFE$")
            .AppendLine("BEGIN")
            .AppendLine($"    IF NOT EXISTS ({SingleLine(existsSql)}) THEN")
            .AppendLine("        PERFORM 1;")
            .AppendLine($"    ELSIF EXISTS ({SingleLine(matchesSql)}) THEN")
            .AppendLine("        PERFORM 1;")
            .AppendLine("    ELSE")
            .AppendLine($"        RAISE EXCEPTION USING MESSAGE = {SqlLiteral(mismatchMessage)};")
            .AppendLine("    END IF;")
            .AppendLine("END")
            .Append("$SAFE$");
        EndStatement(builder, terminate);
    }

    private void GenerateColumnPreflight(
        MigrationCommandListBuilder builder,
        SafeMigrationExecutionOptions execution,
        string existsSql,
        string matchesSql,
        string missingMessage,
        string mismatchMessage,
        bool terminate,
        bool allowMissing
    )
    {
        if (execution.ConflictMode == SafeMigrationConflictMode.None)
        {
            builder.Append("SELECT 1");
            EndStatement(builder, terminate);
            return;
        }

        GenerateColumnDecisionBlock(
            builder,
            existsSql,
            matchesSql,
            ddlSql: "SELECT 1",
            missingMessage,
            mismatchMessage,
            terminate,
            allowMissing);
    }

    private void GenerateColumnDecisionBlock(
        MigrationCommandListBuilder builder,
        string existsSql,
        string matchesSql,
        string ddlSql,
        string missingMessage,
        string mismatchMessage,
        bool terminate,
        bool allowMissing
    )
    {
        builder
            .AppendLine("DO $SAFE$")
            .AppendLine("BEGIN")
            .AppendLine($"    IF NOT EXISTS ({SingleLine(existsSql)}) THEN")
            .AppendLine(
                allowMissing
                    ? $"        EXECUTE {SqlLiteral(SingleLine(ddlSql).TrimEnd(';'))};"
                    : $"        RAISE EXCEPTION USING MESSAGE = {SqlLiteral(missingMessage)};")
            .AppendLine($"    ELSIF EXISTS ({SingleLine(matchesSql)}) THEN")
            .AppendLine("        PERFORM 1;")
            .AppendLine("    ELSE")
            .AppendLine($"        RAISE EXCEPTION USING MESSAGE = {SqlLiteral(mismatchMessage)};")
            .AppendLine("    END IF;")
            .AppendLine("END")
            .Append("$SAFE$");
        EndStatement(builder, terminate);
    }

    private static string ConstraintExistsSql(
        string? schema,
        string table,
        string name,
        string constraintType
    ) => string.Join(
        Environment.NewLine,
        "SELECT 1",
        "FROM pg_constraint c",
        "JOIN pg_class t ON t.oid = c.conrelid",
        "JOIN pg_namespace n ON n.oid = t.relnamespace",
        $"WHERE n.nspname = {SqlLiteral(schema ?? "public")}",
        $"  AND t.relname = {SqlLiteral(table)}",
        $"  AND c.conname = {SqlLiteral(name)}",
        $"  AND c.contype = {SqlLiteral(constraintType)}");

    private static string ExistsTableSql(
        string? schema,
        string table
    ) => string.Join(
        Environment.NewLine,
        "SELECT 1",
        "FROM information_schema.tables",
        $"WHERE table_schema = {SqlLiteral(schema ?? "public")}",
        $"  AND table_name = {SqlLiteral(table)}");

    private static string ExistsColumnSql(
        string? schema,
        string table,
        string column
    ) => string.Join(
        Environment.NewLine,
        "SELECT 1",
        "FROM information_schema.columns",
        $"WHERE table_schema = {SqlLiteral(schema ?? "public")}",
        $"  AND table_name = {SqlLiteral(table)}",
        $"  AND column_name = {SqlLiteral(column)}");

    private static string ExistsIndexSql(
        string? schema,
        string table,
        string name
    ) => string.Join(
        Environment.NewLine,
        "SELECT 1",
        "FROM pg_class idx",
        "JOIN pg_namespace n ON n.oid = idx.relnamespace",
        "JOIN pg_index i ON i.indexrelid = idx.oid",
        "JOIN pg_class t ON t.oid = i.indrelid",
        $"WHERE n.nspname = {SqlLiteral(schema ?? "public")}",
        $"  AND t.relname = {SqlLiteral(table)}",
        $"  AND idx.relname = {SqlLiteral(name)}");

    private string BuildTableMatchesSql(
        ExpectedTableDefinition expected
    )
    {
        var parts = new List<string>
        {
            $"(SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = {SqlLiteral(expected.Schema ?? "public")} AND table_name = {SqlLiteral(expected.Table)}) = {expected.Columns.Count}"
        };

        parts.AddRange(
            expected.Columns.Select(column =>
                $"EXISTS ({SingleLine(BuildColumnMatchesSql(expected.Schema, expected.Table, column))})"));

        if (expected.PrimaryKey is not null)
        {
            parts.Add($"EXISTS ({SingleLine(BuildPrimaryKeyMatchesSql(expected.PrimaryKey))})");
        }

        return string.Join(
            Environment.NewLine,
            "SELECT 1",
            "FROM information_schema.tables",
            $"WHERE table_schema = {SqlLiteral(expected.Schema ?? "public")}",
            $"  AND table_name = {SqlLiteral(expected.Table)}",
            $"  AND {string.Join(" AND ", parts)}");
    }

    private string BuildColumnMatchesSql(
        string? schema,
        string table,
        ExpectedColumnDefinition expected
    )
    {
        var predicates = new List<string>
        {
            $"n.nspname = {SqlLiteral(schema ?? "public")}",
            $"t.relname = {SqlLiteral(table)}",
            $"a.attname = {SqlLiteral(expected.Name)}",
            $"a.attnotnull = {(expected.IsNullable ? "FALSE" : "TRUE")}"
        };

        if (!string.IsNullOrWhiteSpace(expected.StoreType))
        {
            predicates.Add($"LOWER(format_type(a.atttypid, a.atttypmod)) = LOWER({SqlLiteral(expected.StoreType)})");
        }

        if (expected.DefaultValueLiteral is not null)
        {
            predicates.Add(BuildPostgreSqlDefaultValueLiteralPredicate(expected));
        }

        if (expected.DefaultValueSql is not null)
        {
            predicates.Add(
                $"LOWER({NormalizeSqlExpression("COALESCE(pg_get_expr(d.adbin, d.adrelid), '')")}) = LOWER({SqlLiteral(NormalizeLiteralSql(expected.DefaultValueSql))})");
        }
        else if (expected.DefaultValueLiteral is null)
        {
            predicates.Add("d.adbin IS NULL");
        }

        if (expected.ComputedColumnSql is not null)
        {
            predicates.Add("a.attgenerated = 's'");
            predicates.Add(
                $"LOWER({NormalizeSqlExpression("COALESCE(pg_get_expr(d.adbin, d.adrelid), '')")}) = LOWER({SqlLiteral(NormalizeLiteralSql(expected.ComputedColumnSql))})");
        }
        else
        {
            predicates.Add("COALESCE(a.attgenerated, '') <> 's'");
        }

        if (expected.Collation is not null)
        {
            predicates.Add($"COALESCE(coll.collname, '') = {SqlLiteral(expected.Collation)}");
        }

        if (expected.IsStored.HasValue)
        {
            predicates.Add(expected.IsStored.Value ? "a.attgenerated = 's'" : "COALESCE(a.attgenerated, '') <> 's'");
        }

        return string.Join(
            Environment.NewLine,
            "SELECT 1",
            "FROM pg_attribute a",
            "JOIN pg_class t ON t.oid = a.attrelid",
            "JOIN pg_namespace n ON n.oid = t.relnamespace",
            "LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum",
            "LEFT JOIN pg_collation coll ON coll.oid = a.attcollation AND a.attcollation <> 0",
            "WHERE a.attnum > 0",
            "  AND NOT a.attisdropped",
            $"  AND {string.Join(" AND ", predicates)}");
    }

    private static string BuildIndexMatchesSql(
        ExpectedIndexDefinition expected
    )
    {
        var clauses = new List<string>
        {
            $"idx.unique_flag = {(expected.Unique ? "TRUE" : "FALSE")}",
            $"idx.column_list = {SqlLiteral(string.Join(",", expected.Columns))}"
        };

        if (expected.Filter is not null)
        {
            clauses.Add(
                $"LOWER({NormalizeSqlExpression("COALESCE(idx.filter_definition, '')")}) = LOWER({SqlLiteral(NormalizeLiteralSql(expected.Filter))})");
        }
        else
        {
            clauses.Add("COALESCE(idx.filter_definition, '') = ''");
        }

        if (expected.Descending is not null)
        {
            clauses.Add(
                $"idx.sort_list = {SqlLiteral(string.Join(",", expected.Descending.Select(value => value ? "D" : "A")))}");
        }

        return string.Join(
            Environment.NewLine,
            "SELECT 1",
            "FROM (",
            "    SELECT",
            "        idx.relname AS index_name,",
            "        i.indisunique AS unique_flag,",
            "        string_agg(att.attname, ',' ORDER BY keys.ordinality) AS column_list,",
            "        string_agg(CASE WHEN (opts.option & 1) = 1 THEN 'D' ELSE 'A' END, ',' ORDER BY keys.ordinality) AS sort_list,",
            "        pg_get_expr(i.indpred, i.indrelid) AS filter_definition",
            "    FROM pg_class idx",
            "    JOIN pg_namespace n ON n.oid = idx.relnamespace",
            "    JOIN pg_index i ON i.indexrelid = idx.oid",
            "    JOIN pg_class t ON t.oid = i.indrelid",
            "    JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS keys(attnum, ordinality) ON TRUE",
            "    JOIN LATERAL unnest(i.indoption) WITH ORDINALITY AS opts(option, ordinality) ON opts.ordinality = keys.ordinality",
            "    JOIN pg_attribute att ON att.attrelid = t.oid AND att.attnum = keys.attnum",
            $"    WHERE n.nspname = {SqlLiteral(expected.Schema ?? "public")}",
            $"      AND t.relname = {SqlLiteral(expected.Table)}",
            $"      AND idx.relname = {SqlLiteral(expected.Name)}",
            "    GROUP BY idx.relname, i.indisunique, i.indpred, i.indrelid",
            ") AS idx",
            $"WHERE {string.Join(" AND ", clauses)}");
    }

    private static string BuildPrimaryKeyMatchesSql(
        ExpectedPrimaryKeyDefinition expected
    ) => BuildKeyConstraintMatchesSql(expected.Schema, expected.Table, expected.Name, expected.Columns, "p");

    private static string BuildUniqueConstraintMatchesSql(
        ExpectedUniqueConstraintDefinition expected
    ) => BuildKeyConstraintMatchesSql(expected.Schema, expected.Table, expected.Name, expected.Columns, "u");

    private static string BuildKeyConstraintMatchesSql(
        string? schema,
        string table,
        string name,
        IReadOnlyList<string> columns,
        string constraintType
    ) => string.Join(
        Environment.NewLine,
        "SELECT 1",
        "FROM (",
        "    SELECT",
        "        c.conname,",
        "        string_agg(att.attname, ',' ORDER BY cols.ordinality) AS column_list",
        "    FROM pg_constraint c",
        "    JOIN pg_class t ON t.oid = c.conrelid",
        "    JOIN pg_namespace n ON n.oid = t.relnamespace",
        "    JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS cols(attnum, ordinality) ON TRUE",
        "    JOIN pg_attribute att ON att.attrelid = t.oid AND att.attnum = cols.attnum",
        $"    WHERE n.nspname = {SqlLiteral(schema ?? "public")}",
        $"      AND t.relname = {SqlLiteral(table)}",
        $"      AND c.conname = {SqlLiteral(name)}",
        $"      AND c.contype = {SqlLiteral(constraintType)}",
        "    GROUP BY c.conname",
        ") AS c",
        $"WHERE c.column_list = {SqlLiteral(string.Join(",", columns))}");

    private static string BuildForeignKeyMatchesSql(
        ExpectedForeignKeyDefinition expected
    ) => string.Join(
        Environment.NewLine,
        "SELECT 1",
        "FROM (",
        "    SELECT",
        "        c.conname,",
        "        tn.nspname AS table_schema,",
        "        t.relname AS table_name,",
        "        pn.nspname AS principal_schema,",
        "        pt.relname AS principal_table,",
        "        c.confupdtype AS update_rule,",
        "        c.confdeltype AS delete_rule,",
        "        string_agg(src.attname, ',' ORDER BY src_cols.ordinality) AS column_list,",
        "        string_agg(dst.attname, ',' ORDER BY dst_cols.ordinality) AS referenced_column_list",
        "    FROM pg_constraint c",
        "    JOIN pg_class t ON t.oid = c.conrelid",
        "    JOIN pg_namespace tn ON tn.oid = t.relnamespace",
        "    JOIN pg_class pt ON pt.oid = c.confrelid",
        "    JOIN pg_namespace pn ON pn.oid = pt.relnamespace",
        "    JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS src_cols(attnum, ordinality) ON TRUE",
        "    JOIN pg_attribute src ON src.attrelid = t.oid AND src.attnum = src_cols.attnum",
        "    JOIN LATERAL unnest(c.confkey) WITH ORDINALITY AS dst_cols(attnum, ordinality) ON dst_cols.ordinality = src_cols.ordinality",
        "    JOIN pg_attribute dst ON dst.attrelid = pt.oid AND dst.attnum = dst_cols.attnum",
        $"    WHERE tn.nspname = {SqlLiteral(expected.Schema ?? "public")}",
        $"      AND t.relname = {SqlLiteral(expected.Table)}",
        $"      AND c.conname = {SqlLiteral(expected.Name)}",
        "      AND c.contype = 'f'",
        "    GROUP BY c.conname, tn.nspname, t.relname, pn.nspname, pt.relname, c.confupdtype, c.confdeltype",
        ") AS fk",
        $"WHERE fk.principal_schema = {SqlLiteral(expected.PrincipalSchema ?? "public")}",
        $"  AND fk.principal_table = {SqlLiteral(expected.PrincipalTable)}",
        $"  AND fk.column_list = {SqlLiteral(string.Join(",", expected.Columns))}",
        $"  AND fk.referenced_column_list = {SqlLiteral(string.Join(",", expected.PrincipalColumns))}",
        $"  AND fk.update_rule = {SqlLiteral(ToReferentialRule(expected.OnUpdate))}",
        $"  AND fk.delete_rule = {SqlLiteral(ToReferentialRule(expected.OnDelete))}");

    private static string BuildCheckConstraintMatchesSql(
        ExpectedCheckConstraintDefinition expected
    ) => string.Join(
        Environment.NewLine,
        "SELECT 1",
        "FROM information_schema.check_constraints cc",
        "JOIN information_schema.table_constraints tc",
        "  ON tc.constraint_schema = cc.constraint_schema",
        " AND tc.constraint_name = cc.constraint_name",
        $"WHERE tc.constraint_schema = {SqlLiteral(expected.Schema ?? "public")}",
        $"  AND tc.table_name = {SqlLiteral(expected.Table)}",
        $"  AND tc.constraint_name = {SqlLiteral(expected.Name)}",
        "  AND tc.constraint_type = 'CHECK'",
        $"  AND LOWER({NormalizeSqlExpression("REPLACE(cc.check_clause, '\"', '')")}) = LOWER({SqlLiteral(NormalizeLiteralSql(expected.Sql.Replace("\"", string.Empty, StringComparison.Ordinal)))})");

    private static SafeMigrationStrictMode GetStrictMode(
        MigrationOperation operation
    ) => operation[SafeMigrationAnnotationNames.StrictMode] is SafeMigrationStrictMode strictMode
        ? strictMode
        : SafeMigrationStrictMode.None;

    private static TDefinition GetExpectedDefinition<TDefinition>(
        MigrationOperation operation
    )
        where TDefinition : class =>
        SafeMigrationDefinitionSerializer.Deserialize<TDefinition>(
            operation[SafeMigrationAnnotationNames.ExpectedDefinition] as string)
        ?? throw new InvalidOperationException(
            $"Expected definition annotation missing for operation '{operation.GetType().Name}'.");

    private static bool IsIfExists(
        MigrationOperation operation
    ) => (operation[SafeMigrationAnnotationNames.IfExists] as bool?) == true;

    private static bool IsIfNotExists(
        MigrationOperation operation
    ) => (operation[SafeMigrationAnnotationNames.IfNotExists] as bool?) == true;

    private static bool IsAlterIfDifferent(
        MigrationOperation operation
    ) => (operation[SafeMigrationAnnotationNames.AlterIfDifferent] as bool?) == true;

    private static string SingleLine(
        string sql
    ) => string.Join(
        " ",
        sql.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string EscapeSqlLiteral(
        string value
    ) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string SqlLiteral(
        string value
    ) => $"'{EscapeSqlLiteral(value)}'";

    private static string BuildMismatchMessage(
        string objectType,
        string objectName,
        string table,
        object expectedDefinition
    ) =>
        $"Safe migration strict-mode mismatch for {objectType} '{objectName}' on table '{table}'. Expected: {SafeMigrationDefinitionSerializer.Serialize(expectedDefinition)}. Provider: PostgreSQL.";

    private static string BuildMissingMessage(
        string objectType,
        string objectName,
        string table
    ) =>
        $"Safe migration alter-if-different target {objectType} '{objectName}' on table '{table}' was not found. Provider: PostgreSQL.";

    private static string NormalizeSqlExpression(
        string sqlExpression
    ) => $"regexp_replace({sqlExpression}, '\\s+', '', 'g')";

    private static string NormalizeLiteralSql(
        string sql
    ) => string.Concat(sql.Where(ch => !char.IsWhiteSpace(ch)));

    private string BuildPostgreSqlDefaultValueLiteralPredicate(
        ExpectedColumnDefinition expected
    )
    {
        var candidates = BuildTypedDefaultValueCandidates(expected);
        if (candidates.Count == 0
            && expected.DefaultValueLiteral is not null)
        {
            candidates.Add(NormalizeLiteralSql(expected.DefaultValueLiteral));
        }

        if (candidates.Count == 0)
        {
            return "d.adbin IS NULL";
        }

        var normalizedCatalogDefault =
            NormalizePostgreSqlDefaultExpression("COALESCE(pg_get_expr(d.adbin, d.adrelid), '')");
        return string.Join(
            " OR ",
            candidates.Select(candidate => $"LOWER({normalizedCatalogDefault}) = LOWER({SqlLiteral(candidate)})"));
    }

    private List<string> BuildTypedDefaultValueCandidates(
        ExpectedColumnDefinition expected
    )
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryGenerateSqlLiteral(expected, out var sqlLiteral))
        {
            AddDefaultValueCandidate(candidates, sqlLiteral);
            AddDefaultValueCandidate(candidates, ExtractQuotedSqlLiteral(sqlLiteral!));
        }

        AddDefaultValueCandidate(candidates, expected.DefaultValueLiteral);

        return [.. candidates];
    }

    private bool TryGenerateSqlLiteral(
        ExpectedColumnDefinition expected,
        out string? sqlLiteral
    )
    {
        sqlLiteral = null;

        if (!SafeMigrationDefaultValueSerializer.TryDeserialize(
                expected.DefaultValueTypeName,
                expected.DefaultValueJson,
                out var value,
                out var clrType))
        {
            return false;
        }

        RelationalTypeMapping? mapping = null;
        if (!string.IsNullOrWhiteSpace(expected.StoreType))
        {
            mapping = Dependencies.TypeMappingSource.FindMapping(
                clrType!,
                expected.StoreType!,
                keyOrIndex: false,
                unicode: null,
                size: null,
                rowVersion: null,
                fixedLength: null,
                precision: expected.Precision,
                scale: expected.Scale);
        }

        mapping ??= Dependencies.TypeMappingSource.FindMapping(clrType!);
        if (mapping is null
            || value is null)
        {
            return false;
        }

        sqlLiteral = mapping.GenerateSqlLiteral(value);
        return true;
    }

    private static void AddDefaultValueCandidate(
        HashSet<string> candidates,
        string? value
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        candidates.Add(NormalizeLiteralSql(value));
    }

    private static string? ExtractQuotedSqlLiteral(
        string sqlLiteral
    )
    {
        var firstQuote = sqlLiteral.IndexOf('\'');
        if (firstQuote < 0
            || sqlLiteral[^1] != '\'')
        {
            return null;
        }

        return sqlLiteral[firstQuote..];
    }

    private static string NormalizePostgreSqlDefaultExpression(
        string sqlExpression
    ) => StripOuterParentheses(StripPostgreSqlTypeCast(NormalizeSqlExpression(sqlExpression)));

    private static string StripOuterParentheses(
        string sqlExpression
    ) => $@"regexp_replace({sqlExpression}, '^\((.*)\)$', '\1')";

    private static string StripPostgreSqlTypeCast(
        string sqlExpression
    ) => $"regexp_replace({sqlExpression}, '::[A-Za-z0-9_\\.\\[\\]\" ]+$', '', 'g')";

    private static string ToReferentialRule(
        ReferentialAction action
    ) => action switch
    {
        ReferentialAction.Cascade => "c",
        ReferentialAction.SetNull => "n",
        ReferentialAction.SetDefault => "d",
        ReferentialAction.Restrict => "r",
        _ => "a",
    };
}
#pragma warning restore EF1001
