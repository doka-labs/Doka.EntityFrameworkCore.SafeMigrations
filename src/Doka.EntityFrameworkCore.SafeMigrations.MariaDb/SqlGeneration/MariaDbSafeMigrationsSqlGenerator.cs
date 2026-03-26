namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

#pragma warning disable EF1001
/// <summary>
/// Generates MariaDB-specific SQL for the safe migration operations exposed by this library.
/// </summary>
public sealed class MariaDbSafeMigrationsSqlGenerator : MySqlMigrationsSqlGenerator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MariaDbSafeMigrationsSqlGenerator"/> class.
    /// </summary>
    /// <param name="dependencies">The shared SQL-generator dependencies.</param>
    /// <param name="commandBatchPreparer">The command batch preparer used by the base generator.</param>
    /// <param name="options">The active MariaDB provider options.</param>
    public MariaDbSafeMigrationsSqlGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        ICommandBatchPreparer commandBatchPreparer,
        IMySqlOptions options)
        : base(dependencies, commandBatchPreparer, options)
    {
    }

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
                    "PRIMARY KEY",
                    "primary key",
                    BuildPrimaryKeyMatchesSql,
                    (innerOperation, _, innerBuilder) => AppendPrimaryKeyConstraint(innerOperation, innerBuilder));
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
                    "UNIQUE",
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
                    "FOREIGN KEY",
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
                    "CHECK",
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
        CheckSchema(operation);

        var createTableSql = BuildCreateTableSql(operation, model);
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
        CheckSchema(operation);

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

        CheckSchema(operation);
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

        CheckSchema(operation);
        var execution = SafeMigrationExecutionAnnotationHelper.GetExecutionOptions(operation);
        var expectedDefinition = GetExpectedDefinition<ExpectedIndexDefinition>(operation);
        var differentDecision = MariaDbSafeMigrationPlanner.PlanIndex(
            execution,
            SafeMigrationComparisonState.Different,
            expectedDefinition);

        if (differentDecision.Outcome == SafeMigrationExecutionOutcome.Rejected
            && !string.IsNullOrWhiteSpace(expectedDefinition.Filter))
        {
            throw new NotSupportedException(differentDecision.Reason);
        }

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

        CheckSchema(operation);
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
        EnsureSchemaOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder
    )
    {
        builder
            .Append("CREATE SCHEMA IF NOT EXISTS ")
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

        CheckSchema(operation);
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

        CheckSchema(operation);
        builder
            .Append("DROP INDEX IF EXISTS ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name))
            .Append(" ON ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table!, operation.Schema));
        EndStatement(builder, terminate);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropForeignKeyOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate
    )
    {
        if (!IsIfExists(operation))
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        CheckSchema(operation);
        builder
            .Append("ALTER TABLE ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema))
            .Append(" DROP FOREIGN KEY IF EXISTS ")
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

        CheckSchema(operation);
        GeneratePreparedStatementGuard(
            builder,
            ExistsPrimaryKeySql(operation.Schema, operation.Table),
            $"ALTER TABLE {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema)} DROP PRIMARY KEY",
            terminate);
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

        CheckSchema(operation);
        GeneratePreparedStatementGuard(
            builder,
            ExistsConstraintSql(operation.Schema, operation.Table, operation.Name, "UNIQUE"),
            $"ALTER TABLE {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema)} DROP INDEX {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name)}",
            terminate: true);
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

        CheckSchema(operation);
        GeneratePreparedStatementGuard(
            builder,
            ExistsConstraintSql(operation.Schema, operation.Table, operation.Name, "CHECK"),
            $"ALTER TABLE {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Table, operation.Schema)} DROP CONSTRAINT {Dependencies.SqlGenerationHelper.DelimitIdentifier(operation.Name)}",
            terminate: true);
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

        CheckSchema(operation);
        GeneratePreparedStatementGuard(
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

        CheckSchema(operation);
        GeneratePreparedStatementGuard(
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

        CheckSchema(operation);
        var table = operation.Table ?? throw new InvalidOperationException("Safe rename-index requires a table name for MariaDB.");
        GeneratePreparedStatementGuard(
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
        Action<MigrationCommandListBuilder> buildAlterStatement
    )
    {
        CheckSchema(schema, table);

        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        buildAlterStatement(innerBuilder);
        EndStatement(innerBuilder);
        var sql = SingleLine(innerBuilder.GetCommandList().Single().CommandText).TrimEnd(';');
        var existsSql = ExistsConstraintSql(schema, table, name, constraintType);

        if (strictMode == SafeMigrationStrictMode.None)
        {
            GeneratePreparedStatementGuard(
                builder,
                existsSql,
                sql,
                terminate: true,
                runWhenExists: false);
            return;
        }

        GenerateConditionalDdl(builder, existsSql, matchesSql, sql, mismatchMessage, terminate: true);
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
    ) where TOperation : MigrationOperation
      where TDefinition : class
    {
        var schema = getSchema(operation);
        var table = getTable(operation);
        var name = getName(operation);
        var execution = getExecution(operation) ?? new SafeMigrationExecutionOptions(
            getStrictMode(operation) == SafeMigrationStrictMode.ThrowIfDifferent
                ? SafeMigrationConflictMode.ThrowIfDifferent
                : SafeMigrationConflictMode.None);
        var definition = getExpectedDefinition(operation)
            ?? throw new InvalidOperationException($"Expected {mismatchObjectType} definition is missing. This is required for all safe constraint operations.");

        var matchesSql = buildMatchesSql(definition);
        var mismatchMessage = BuildMismatchMessage(mismatchObjectType, name, table, definition);

        if (execution.PreflightOnly)
        {
            GenerateExistsPreflight(
                builder,
                execution,
                ExistsConstraintSql(schema, table, name, constraintType),
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
                innerBuilder.Append("ALTER TABLE ")
                    .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(table, schema))
                    .Append(" ADD ");
                appendConstraint(operation, model, innerBuilder);
            });
    }

    private void AppendPrimaryKeyConstraint(
        SafeAddPrimaryKeyOperation operation,
        MigrationCommandListBuilder builder
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(operation.Columns);

        builder.Append("PRIMARY KEY (")
            .Append(string.Join(", ", operation.Columns.Select(Dependencies.SqlGenerationHelper.DelimitIdentifier)))
            .Append(")");
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

        GenerateConditionalDdl(
            builder,
            existsSql,
            buildMatchesSql(),
            ddlSql,
            buildMismatchMessage(),
            terminate);
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
        const string procedureName = "`safe_migrations_guard`";
        var createProcedureSql = $"""
CREATE PROCEDURE {procedureName}()
BEGIN
    IF NOT EXISTS({SingleLine(existsSql)}) THEN
        {SingleLine(ddlSql).TrimEnd(';')};
    ELSEIF EXISTS({SingleLine(matchesSql)}) THEN
        SELECT 1;
    ELSE
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '{EscapeSqlLiteral(mismatchMessage)}';
    END IF;
END
""";

        builder.Append($"DROP PROCEDURE IF EXISTS {procedureName}");
        EndStatement(builder, true);

        builder
            .Append(createProcedureSql);
        EndStatement(builder, true);

        builder.Append($"CALL {procedureName}()");
        EndStatement(builder, true);

        builder.Append($"DROP PROCEDURE IF EXISTS {procedureName}");
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

        const string procedureName = "`safe_migrations_guard`";
        var createProcedureSql = $"""
CREATE PROCEDURE {procedureName}()
BEGIN
    IF NOT EXISTS({SingleLine(existsSql)}) THEN
        SELECT 1;
    ELSEIF EXISTS({SingleLine(matchesSql)}) THEN
        SELECT 1;
    ELSE
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '{EscapeSqlLiteral(mismatchMessage)}';
    END IF;
END
""";

        builder.Append($"DROP PROCEDURE IF EXISTS {procedureName}");
        EndStatement(builder, true);

        builder.Append(createProcedureSql);
        EndStatement(builder, true);

        builder.Append($"CALL {procedureName}()");
        EndStatement(builder, true);

        builder.Append($"DROP PROCEDURE IF EXISTS {procedureName}");
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
        const string procedureName = "`safe_migrations_guard`";
        var createProcedureSql = $"""
CREATE PROCEDURE {procedureName}()
BEGIN
    IF NOT EXISTS({SingleLine(existsSql)}) THEN
        {(allowMissing ? $"{SingleLine(ddlSql).TrimEnd(';')};" : $"SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '{EscapeSqlLiteral(missingMessage)}';")}
    ELSEIF EXISTS({SingleLine(matchesSql)}) THEN
        SELECT 1;
    ELSE
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '{EscapeSqlLiteral(mismatchMessage)}';
    END IF;
END
""";

        builder.Append($"DROP PROCEDURE IF EXISTS {procedureName}");
        EndStatement(builder, true);

        builder.Append(createProcedureSql);
        EndStatement(builder, true);

        builder.Append($"CALL {procedureName}()");
        EndStatement(builder, true);

        builder.Append($"DROP PROCEDURE IF EXISTS {procedureName}");
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
        const string procedureName = "`safe_migrations_guard`";
        var createProcedureSql = $"""
CREATE PROCEDURE {procedureName}()
BEGIN
    IF NOT EXISTS({SingleLine(existsSql)}) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '{EscapeSqlLiteral(missingMessage)}';
    ELSEIF EXISTS({SingleLine(matchesSql)}) THEN
        SELECT 1;
    ELSE
        {SingleLine(ddlSql).TrimEnd(';')};
    END IF;
END
""";

        builder.Append($"DROP PROCEDURE IF EXISTS {procedureName}");
        EndStatement(builder, true);

        builder.Append(createProcedureSql);
        EndStatement(builder, true);

        builder.Append($"CALL {procedureName}()");
        EndStatement(builder, true);

        builder.Append($"DROP PROCEDURE IF EXISTS {procedureName}");
        EndStatement(builder, terminate);
    }

    private void GeneratePreparedStatementGuard(
        MigrationCommandListBuilder builder,
        string existsSql,
        string ddlSql,
        bool terminate,
        bool runWhenExists = true
    )
    {
        var escapedDdl = EscapeSqlLiteral(SingleLine(ddlSql));
        var successSql = runWhenExists ? escapedDdl : "SELECT 1";
        var failureSql = runWhenExists ? "SELECT 1" : escapedDdl;

        builder
            .Append("SET @safe_migrations_sql = IF(EXISTS(")
            .Append(existsSql)
            .Append("), '")
            .Append(successSql)
            .Append("', '")
            .Append(failureSql)
            .Append("')");
        EndStatement(builder, true);

        builder.Append("PREPARE safe_migrations_stmt FROM @safe_migrations_sql");
        EndStatement(builder, true);

        builder.Append("EXECUTE safe_migrations_stmt");
        EndStatement(builder, true);

        builder.Append("DEALLOCATE PREPARE safe_migrations_stmt");
        EndStatement(builder, terminate);
    }

    private static bool IsIfExists(MigrationOperation operation)
        => operation[SafeMigrationAnnotationNames.IfExists] as bool? == true;

    private static bool IsIfNotExists(MigrationOperation operation)
        => operation[SafeMigrationAnnotationNames.IfNotExists] as bool? == true;

    private static bool IsAlterIfDifferent(MigrationOperation operation)
        => operation[SafeMigrationAnnotationNames.AlterIfDifferent] as bool? == true;

    private void CheckSchema(
        string? schema,
        string table
    )
        => CheckSchema(new CreateTableOperation { Schema = schema, Name = table });

    private static SafeMigrationStrictMode GetStrictMode(MigrationOperation operation)
        => operation[SafeMigrationAnnotationNames.StrictMode] is SafeMigrationStrictMode strictMode
            ? strictMode
            : SafeMigrationStrictMode.None;

    private static TDefinition GetExpectedDefinition<TDefinition>(MigrationOperation operation)
        where TDefinition : class
        => SafeMigrationDefinitionSerializer.Deserialize<TDefinition>(operation[SafeMigrationAnnotationNames.ExpectedDefinition] as string)
           ?? throw new InvalidOperationException($"Expected definition annotation missing for operation '{operation.GetType().Name}'.");

    private static string SingleLine(string sql)
        => string.Join(" ", sql.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string EscapeSqlLiteral(string value)
        => value.Replace("\\", @"\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);

    private static string SqlLiteral(string? value)
        => $"'{EscapeSqlLiteral(value ?? string.Empty)}'";

    private static string ConstraintSchema(string? schema)
        => schema is null ? "DATABASE()" : SqlLiteral(schema);

    private string BuildCreateTableSql(
        CreateTableOperation operation,
        IModel? model
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        innerBuilder
            .Append("CREATE TABLE IF NOT EXISTS ")
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
        return SingleLine(innerBuilder.GetCommandList().Single().CommandText).TrimEnd(';');
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

        ColumnDefinition(operation, model, innerBuilder);
        EndStatement(innerBuilder);
        return SingleLine(innerBuilder.GetCommandList().Single().CommandText).TrimEnd(';');
    }

    private string BuildAlterColumnSql(
        AlterColumnOperation operation,
        IModel? model
    )
    {
        var innerBuilder = new MigrationCommandListBuilder(Dependencies);
        base.Generate(operation, model, innerBuilder);

        var commands = innerBuilder.GetCommandList();
        return commands.Count == 1
            ? SingleLine(commands[0].CommandText).TrimEnd(';')
            : throw new NotSupportedException("Safe alter-column currently supports only single-statement MariaDB alterations.");
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
            .Append(" ");

        IndexTraits(operation, model, innerBuilder);
        innerBuilder.Append("(");
        GenerateIndexColumnList(operation, model, innerBuilder);
        innerBuilder.Append(")");
        IndexOptions(operation, model, innerBuilder);

        EndStatement(innerBuilder);
        return SingleLine(innerBuilder.GetCommandList().Single().CommandText).TrimEnd(';');
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
            ? SingleLine(commands[0].CommandText).TrimEnd(';')
            : throw new NotSupportedException("Safe rename-column currently supports only single-statement MariaDB renames.");
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
            ? SingleLine(commands[0].CommandText).TrimEnd(';')
            : throw new NotSupportedException("Safe rename-table currently supports only single-statement MariaDB renames.");
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
            ? SingleLine(commands[0].CommandText).TrimEnd(';')
            : throw new NotSupportedException("Safe rename-index currently supports only single-statement MariaDB renames.");
    }

    private static string BuildMismatchMessage(
        string objectType,
        string objectName,
        string table,
        object expectedDefinition
    )
        => $"Safe migration strict-mode mismatch for {objectType} '{objectName}' on table '{table}'. Expected: {SafeMigrationDefinitionSerializer.Serialize(expectedDefinition)}. Provider: MariaDB.";

    private static string BuildMissingMessage(
        string objectType,
        string objectName,
        string table
    )
        => $"Safe migration alter-if-different target {objectType} '{objectName}' on table '{table}' was not found. Provider: MariaDB.";

    private static string ExistsTableSql(
        string? schema,
        string table
    )
        => $"""
SELECT 1
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = {ConstraintSchema(schema)}
  AND TABLE_NAME = {SqlLiteral(table)}
""";

    private static string ExistsColumnSql(
        string? schema,
        string table,
        string column
    )
        => $"""
SELECT 1
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = {ConstraintSchema(schema)}
  AND TABLE_NAME = {SqlLiteral(table)}
  AND COLUMN_NAME = {SqlLiteral(column)}
""";

    private static string ExistsIndexSql(
        string? schema,
        string table,
        string name
    )
        => $"""
SELECT 1
FROM information_schema.STATISTICS
WHERE TABLE_SCHEMA = {ConstraintSchema(schema)}
  AND TABLE_NAME = {SqlLiteral(table)}
  AND INDEX_NAME = {SqlLiteral(name)}
""";

    private static string ExistsConstraintSql(
        string? schema,
        string table,
        string name,
        string constraintType
    )
        => $"""
SELECT 1
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = {ConstraintSchema(schema)}
  AND TABLE_NAME = {SqlLiteral(table)}
  AND CONSTRAINT_NAME = {SqlLiteral(name)}
  AND CONSTRAINT_TYPE = {SqlLiteral(constraintType)}
""";

    private static string ExistsPrimaryKeySql(
        string? schema,
        string table
    )
        => $"""
SELECT 1
FROM information_schema.TABLE_CONSTRAINTS
WHERE CONSTRAINT_SCHEMA = {ConstraintSchema(schema)}
  AND TABLE_NAME = {SqlLiteral(table)}
  AND CONSTRAINT_TYPE = 'PRIMARY KEY'
""";

    private string BuildTableMatchesSql(ExpectedTableDefinition expected)
    {
        var parts = new List<string>
        {
            $"(SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = {ConstraintSchema(expected.Schema)} AND TABLE_NAME = {SqlLiteral(expected.Table)}) = {expected.Columns.Count}"
        };

        parts.AddRange(expected.Columns.Select(column => $"EXISTS({SingleLine(BuildColumnMatchesSql(expected.Schema, expected.Table, column))})"));

        if (expected.PrimaryKey is not null)
        {
            parts.Add($"EXISTS({SingleLine(BuildPrimaryKeyMatchesSql(expected.PrimaryKey))})");
        }

        return $"""
SELECT 1
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = {ConstraintSchema(expected.Schema)}
  AND TABLE_NAME = {SqlLiteral(expected.Table)}
  AND {string.Join(" AND ", parts)}
""";
    }

    private string BuildColumnMatchesSql(
        string? schema,
        string table,
        ExpectedColumnDefinition expected
    )
    {
        var predicates = new List<string>
        {
            $"TABLE_SCHEMA = {ConstraintSchema(schema)}",
            $"TABLE_NAME = {SqlLiteral(table)}",
            $"COLUMN_NAME = {SqlLiteral(expected.Name)}",
            $"IS_NULLABLE = {(expected.IsNullable ? "'YES'" : "'NO'")}"
        };

        if (!string.IsNullOrWhiteSpace(expected.StoreType))
        {
            predicates.Add($"UPPER(COLUMN_TYPE) = UPPER({SqlLiteral(expected.StoreType)})");
        }

        if (expected.DefaultValueLiteral is not null)
        {
            predicates.Add(BuildMariaDbDefaultValueLiteralPredicate(expected));
        }

        if (expected.DefaultValueSql is not null)
        {
            predicates.Add($"LOWER({NormalizeSqlExpression("COALESCE(COLUMN_DEFAULT, '')")}) = LOWER({SqlLiteral(NormalizeLiteralSql(expected.DefaultValueSql))})");
        }

        if (expected.ComputedColumnSql is not null)
        {
            predicates.Add($"LOWER({NormalizeSqlExpression("COALESCE(GENERATION_EXPRESSION, '')")}) = LOWER({SqlLiteral(NormalizeLiteralSql(expected.ComputedColumnSql))})");
        }
        else
        {
            predicates.Add("COALESCE(GENERATION_EXPRESSION, '') = ''");
        }

        if (expected.Precision.HasValue)
        {
            predicates.Add($"COALESCE(NUMERIC_PRECISION, 0) = {expected.Precision.Value}");
        }

        if (expected.Scale.HasValue)
        {
            predicates.Add($"COALESCE(NUMERIC_SCALE, 0) = {expected.Scale.Value}");
        }

        if (expected.Collation is not null)
        {
            predicates.Add($"COALESCE(COLLATION_NAME, '') = {SqlLiteral(expected.Collation)}");
        }

        return $"""
SELECT 1
FROM information_schema.COLUMNS
WHERE {string.Join(" AND ", predicates)}
""";
    }

    private static string BuildIndexMatchesSql(ExpectedIndexDefinition expected)
    {
        var descending = expected.Descending is null
            ? null
            : string.Join(",", expected.Descending.Select(value => value ? "D" : "A"));

        return $"""
SELECT 1
FROM (
    SELECT
        INDEX_NAME,
        MAX(NON_UNIQUE) AS NON_UNIQUE,
        GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',') AS COLUMN_LIST,
        GROUP_CONCAT(COALESCE(COLLATION, 'A') ORDER BY SEQ_IN_INDEX SEPARATOR ',') AS SORT_LIST
    FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = {ConstraintSchema(expected.Schema)}
      AND TABLE_NAME = {SqlLiteral(expected.Table)}
      AND INDEX_NAME = {SqlLiteral(expected.Name)}
    GROUP BY INDEX_NAME
) AS IDX
WHERE IDX.NON_UNIQUE = {(expected.Unique ? 0 : 1)}
  AND IDX.COLUMN_LIST = {SqlLiteral(string.Join(",", expected.Columns))}
  {(descending is null ? string.Empty : $"AND IDX.SORT_LIST = {SqlLiteral(descending)}")}
""";
    }

    private static string BuildPrimaryKeyMatchesSql(ExpectedPrimaryKeyDefinition expected)
        => BuildKeyConstraintMatchesSql(expected.Schema, expected.Table, expected.Name, expected.Columns, "PRIMARY KEY");

    private static string BuildUniqueConstraintMatchesSql(ExpectedUniqueConstraintDefinition expected)
        => BuildKeyConstraintMatchesSql(expected.Schema, expected.Table, expected.Name, expected.Columns, "UNIQUE");

    private static string BuildKeyConstraintMatchesSql(
        string? schema,
        string table,
        string name,
        IReadOnlyList<string> columns,
        string constraintType
    )
        => $"""
SELECT 1
FROM (
    SELECT
        TC.CONSTRAINT_NAME,
        GROUP_CONCAT(KCU.COLUMN_NAME ORDER BY KCU.ORDINAL_POSITION SEPARATOR ',') AS COLUMN_LIST
    FROM information_schema.TABLE_CONSTRAINTS TC
    JOIN information_schema.KEY_COLUMN_USAGE KCU
      ON KCU.CONSTRAINT_SCHEMA = TC.CONSTRAINT_SCHEMA
     AND KCU.TABLE_NAME = TC.TABLE_NAME
     AND KCU.CONSTRAINT_NAME = TC.CONSTRAINT_NAME
    WHERE TC.CONSTRAINT_SCHEMA = {ConstraintSchema(schema)}
      AND TC.TABLE_NAME = {SqlLiteral(table)}
      AND TC.CONSTRAINT_NAME = {SqlLiteral(name)}
      AND TC.CONSTRAINT_TYPE = {SqlLiteral(constraintType)}
    GROUP BY TC.CONSTRAINT_NAME
) AS C
WHERE C.COLUMN_LIST = {SqlLiteral(string.Join(",", columns))}
""";

    private static string BuildForeignKeyMatchesSql(ExpectedForeignKeyDefinition expected)
        => $"""
SELECT 1
FROM (
    SELECT
        RC.CONSTRAINT_NAME,
        RC.UPDATE_RULE,
        RC.DELETE_RULE,
        MAX(KCU.REFERENCED_TABLE_NAME) AS REFERENCED_TABLE_NAME,
        GROUP_CONCAT(KCU.COLUMN_NAME ORDER BY KCU.ORDINAL_POSITION SEPARATOR ',') AS COLUMN_LIST,
        GROUP_CONCAT(KCU.REFERENCED_COLUMN_NAME ORDER BY KCU.ORDINAL_POSITION SEPARATOR ',') AS REFERENCED_COLUMN_LIST
    FROM information_schema.REFERENTIAL_CONSTRAINTS RC
    JOIN information_schema.KEY_COLUMN_USAGE KCU
      ON KCU.CONSTRAINT_SCHEMA = RC.CONSTRAINT_SCHEMA
     AND KCU.TABLE_NAME = RC.TABLE_NAME
     AND KCU.CONSTRAINT_NAME = RC.CONSTRAINT_NAME
    WHERE RC.CONSTRAINT_SCHEMA = {ConstraintSchema(expected.Schema)}
      AND RC.TABLE_NAME = {SqlLiteral(expected.Table)}
      AND RC.CONSTRAINT_NAME = {SqlLiteral(expected.Name)}
    GROUP BY RC.CONSTRAINT_NAME, RC.UPDATE_RULE, RC.DELETE_RULE
) AS FK
WHERE FK.REFERENCED_TABLE_NAME = {SqlLiteral(expected.PrincipalTable)}
  AND FK.COLUMN_LIST = {SqlLiteral(string.Join(",", expected.Columns))}
  AND FK.REFERENCED_COLUMN_LIST = {SqlLiteral(string.Join(",", expected.PrincipalColumns))}
  AND {BuildMariaDbReferentialRulePredicate("FK.UPDATE_RULE", expected.OnUpdate)}
  AND {BuildMariaDbReferentialRulePredicate("FK.DELETE_RULE", expected.OnDelete)}
""";

    private static string BuildCheckConstraintMatchesSql(ExpectedCheckConstraintDefinition expected)
        => $"""
SELECT 1
FROM information_schema.CHECK_CONSTRAINTS CC
JOIN information_schema.TABLE_CONSTRAINTS TC
  ON TC.CONSTRAINT_SCHEMA = CC.CONSTRAINT_SCHEMA
 AND TC.CONSTRAINT_NAME = CC.CONSTRAINT_NAME
WHERE TC.CONSTRAINT_SCHEMA = {ConstraintSchema(expected.Schema)}
  AND TC.TABLE_NAME = {SqlLiteral(expected.Table)}
  AND TC.CONSTRAINT_NAME = {SqlLiteral(expected.Name)}
  AND LOWER({NormalizeSqlExpression("CC.CHECK_CLAUSE")}) = LOWER({SqlLiteral(NormalizeLiteralSql(expected.Sql))})
""";

    private static string NormalizeSqlExpression(string sqlExpression)
        => $"REPLACE(REPLACE(REPLACE(REPLACE({sqlExpression}, ' ', ''), CHAR(10), ''), CHAR(13), ''), CHAR(9), '')";

    private static string NormalizeLiteralSql(string sql)
        => string.Concat(sql.Where(ch => !char.IsWhiteSpace(ch)));

    private string BuildMariaDbDefaultValueLiteralPredicate(ExpectedColumnDefinition expected)
    {
        var candidates = BuildTypedDefaultValueCandidates(expected);
        if (candidates.Count == 0 && expected.DefaultValueLiteral is not null)
        {
            candidates.Add(NormalizeLiteralSql(expected.DefaultValueLiteral));
        }

        if (candidates.Count == 0)
        {
            return "COALESCE(COLUMN_DEFAULT, '') = ''";
        }

        var normalizedColumnDefault = $"LOWER({NormalizeSqlExpression("COALESCE(COLUMN_DEFAULT, '')")})";
        var candidateSql = string.Join(", ", candidates.Select(SqlLiteral));
        return $"{normalizedColumnDefault} IN ({candidateSql})";
    }

    private List<string> BuildTypedDefaultValueCandidates(ExpectedColumnDefinition expected)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryGenerateSqlLiteral(expected, out var sqlLiteral))
        {
            AddDefaultValueCandidate(candidates, sqlLiteral);
            AddDefaultValueCandidate(candidates, ExtractQuotedSqlLiteral(sqlLiteral!));
        }

        AddDefaultValueCandidate(candidates, expected.DefaultValueLiteral);

        return candidates.ToList();
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
        if (mapping is null || value is null)
        {
            return false;
        }

        sqlLiteral = mapping.GenerateSqlLiteral(value);
        return true;
    }

    private static void AddDefaultValueCandidate(
        ISet<string> candidates,
        string? value
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        candidates.Add(NormalizeLiteralSql(value));
    }

    private static string? ExtractQuotedSqlLiteral(string sqlLiteral)
    {
        var firstQuote = sqlLiteral.IndexOf('\'');
        if (firstQuote < 0 || sqlLiteral[^1] != '\'')
        {
            return null;
        }

        return sqlLiteral[firstQuote..];
    }

    private static string ToReferentialRule(ReferentialAction action)
        => action switch
        {
            ReferentialAction.Cascade => "CASCADE",
            ReferentialAction.SetNull => "SET NULL",
            ReferentialAction.SetDefault => "SET DEFAULT",
            ReferentialAction.Restrict => "RESTRICT",
            _ => "NO ACTION"
        };

    private static string BuildMariaDbReferentialRulePredicate(
        string columnSql,
        ReferentialAction action
    )
        => action is ReferentialAction.NoAction or ReferentialAction.Restrict
            ? $"({columnSql} IN ('NO ACTION', 'RESTRICT'))"
            : $"{columnSql} = {SqlLiteral(ToReferentialRule(action))}";
}
#pragma warning restore EF1001
