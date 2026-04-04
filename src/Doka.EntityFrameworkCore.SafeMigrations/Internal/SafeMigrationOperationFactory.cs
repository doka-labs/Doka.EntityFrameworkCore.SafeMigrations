namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationOperationFactory
{
    public static DropTableOperation CreateDropTableOperation(
        string table,
        string? schema
    )
    {
        var operation = new DropTableOperation
        {
            Name = table,
            Schema = schema,
        };

        SafeMigrationSqlHelper.ApplyCommonAnnotations(
            operation,
            SafeMigrationStrictMode.None,
            expectedDefinition: null,
            ExistenceCheck.IfExists);

        return operation;
    }

    public static DropColumnOperation CreateDropColumnOperation(
        string name,
        string table,
        string? schema
    )
    {
        var operation = new DropColumnOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
        };

        SafeMigrationSqlHelper.ApplyCommonAnnotations(
            operation,
            SafeMigrationStrictMode.None,
            expectedDefinition: null,
            ExistenceCheck.IfExists);

        return operation;
    }

    public static DropIndexOperation CreateDropIndexOperation(
        string name,
        string table,
        string? schema
    )
    {
        var operation = new DropIndexOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
        };

        SafeMigrationSqlHelper.ApplyCommonAnnotations(
            operation,
            SafeMigrationStrictMode.None,
            expectedDefinition: null,
            ExistenceCheck.IfExists);

        return operation;
    }

    public static DropPrimaryKeyOperation CreateDropPrimaryKeyOperation(
        string table,
        string? name,
        string? schema
    )
    {
        var operation = new DropPrimaryKeyOperation
        {
            // Name is unused by MariaDB (DROP PRIMARY KEY carries no name); PostgreSQL resolves the constraint via catalog lookup.
            Name = name ?? string.Empty,
            Table = table,
            Schema = schema,
        };

        SafeMigrationSqlHelper.ApplyCommonAnnotations(
            operation,
            SafeMigrationStrictMode.None,
            expectedDefinition: null,
            ExistenceCheck.IfExists);

        return operation;
    }

    public static DropUniqueConstraintOperation CreateDropUniqueConstraintOperation(
        string name,
        string table,
        string? schema
    )
    {
        var operation = new DropUniqueConstraintOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
        };

        SafeMigrationSqlHelper.ApplyCommonAnnotations(
            operation,
            SafeMigrationStrictMode.None,
            expectedDefinition: null,
            ExistenceCheck.IfExists);

        return operation;
    }

    public static DropForeignKeyOperation CreateDropForeignKeyOperation(
        string name,
        string table,
        string? schema
    )
    {
        var operation = new DropForeignKeyOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
        };

        SafeMigrationSqlHelper.ApplyCommonAnnotations(
            operation,
            SafeMigrationStrictMode.None,
            expectedDefinition: null,
            ExistenceCheck.IfExists);

        return operation;
    }

    public static DropCheckConstraintOperation CreateDropCheckConstraintOperation(
        string name,
        string table,
        string? schema
    )
    {
        var operation = new DropCheckConstraintOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
        };

        SafeMigrationSqlHelper.ApplyCommonAnnotations(
            operation,
            SafeMigrationStrictMode.None,
            expectedDefinition: null,
            ExistenceCheck.IfExists);

        return operation;
    }

    public static AddColumnOperation CreateAddColumnOperation<T>(
        string name,
        string table,
        string? type,
        string? schema,
        bool nullable,
        object? defaultValue,
        string? defaultValueSql,
        string? computedColumnSql,
        string? comment,
        string? collation,
        int? precision,
        int? scale,
        bool? stored,
        SafeMigrationStrictMode strictMode
    )
    {
        var operation = new AddColumnOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
            ClrType = typeof(T),
            ColumnType = type,
            IsNullable = nullable,
            DefaultValue = defaultValue,
            DefaultValueSql = defaultValueSql,
            ComputedColumnSql = computedColumnSql,
            Comment = comment,
            Collation = collation,
            Precision = precision,
            Scale = scale,
            IsStored = stored,
        };

        var (defaultValueTypeName, defaultValueJson) = SafeMigrationDefaultValueSerializer.Capture(defaultValue);
        var expectedDefinition = new ExpectedColumnDefinition(
            name,
            type,
            nullable,
            DefaultValueLiteral: SafeMigrationDefaultValueSerializer.ToLegacyLiteral(defaultValue),
            DefaultValueSql: defaultValueSql,
            DefaultValueTypeName: defaultValueTypeName,
            DefaultValueJson: defaultValueJson,
            ComputedColumnSql: computedColumnSql,
            Precision: precision,
            Scale: scale,
            Collation: collation,
            IsStored: stored);

        SafeMigrationSqlHelper.ApplyCommonAnnotations(
            operation,
            strictMode,
            expectedDefinition,
            ExistenceCheck.IfNotExists);

        return operation;
    }

    public static AddColumnOperation CreateAddColumnOperation<T>(
        string name,
        string table,
        string? type,
        string? schema,
        bool nullable,
        object? defaultValue,
        string? defaultValueSql,
        string? computedColumnSql,
        string? comment,
        string? collation,
        int? precision,
        int? scale,
        bool? stored,
        SafeMigrationExecutionOptions execution
    )
    {
        var operation = CreateAddColumnOperation<T>(
            name,
            table,
            type,
            schema,
            nullable,
            defaultValue,
            defaultValueSql,
            computedColumnSql,
            comment,
            collation,
            precision,
            scale,
            stored,
            SafeMigrationExecutionAnnotationHelper.GetCompatibleStrictMode(execution));

        SafeMigrationExecutionAnnotationHelper.Apply(operation, execution);
        return operation;
    }

    public static CreateIndexOperation CreateIndexOperation(
        string name,
        string table,
        string[] columns,
        string? schema,
        bool unique,
        string? filter,
        bool[]? descending,
        SafeMigrationStrictMode strictMode
    )
    {
        var operation = new CreateIndexOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
            Columns = columns,
            IsUnique = unique,
            Filter = filter,
            IsDescending = descending,
        };

        var expectedDefinition = new ExpectedIndexDefinition(
            name,
            table,
            schema,
            columns,
            unique,
            filter,
            descending);

        SafeMigrationSqlHelper.ApplyCommonAnnotations(
            operation,
            strictMode,
            expectedDefinition,
            ExistenceCheck.IfNotExists);

        return operation;
    }

    public static CreateIndexOperation CreateIndexOperation(
        string name,
        string table,
        string[] columns,
        string? schema,
        bool unique,
        string? filter,
        bool[]? descending,
        SafeMigrationExecutionOptions execution
    )
    {
        var operation = CreateIndexOperation(
            name,
            table,
            columns,
            schema,
            unique,
            filter,
            descending,
            SafeMigrationExecutionAnnotationHelper.GetCompatibleStrictMode(execution));

        SafeMigrationExecutionAnnotationHelper.Apply(operation, execution);
        return operation;
    }

    public static SafeAddPrimaryKeyOperation CreatePrimaryKeyOperation(
        string name,
        string table,
        string[] columns,
        string? schema,
        SafeMigrationStrictMode strictMode
    )
    {
        var operation = new SafeAddPrimaryKeyOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
            Columns = columns,
            StrictMode = strictMode,
            ExpectedDefinition = new ExpectedPrimaryKeyDefinition(
                name,
                table,
                schema,
                columns),
        };

        return operation;
    }

    public static SafeAddPrimaryKeyOperation CreatePrimaryKeyOperation(
        string name,
        string table,
        string[] columns,
        string? schema,
        SafeMigrationExecutionOptions execution
    )
    {
        var operation = CreatePrimaryKeyOperation(
            name,
            table,
            columns,
            schema,
            SafeMigrationExecutionAnnotationHelper.GetCompatibleStrictMode(execution));

        operation.Execution = execution;
        return operation;
    }

    public static SafeAddUniqueConstraintOperation CreateUniqueConstraintOperation(
        string name,
        string table,
        string[] columns,
        string? schema,
        SafeMigrationStrictMode strictMode
    )
    {
        var operation = new SafeAddUniqueConstraintOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
            Columns = columns,
            StrictMode = strictMode,
            ExpectedDefinition = new ExpectedUniqueConstraintDefinition(
                name,
                table,
                schema,
                columns),
        };

        return operation;
    }

    public static SafeAddUniqueConstraintOperation CreateUniqueConstraintOperation(
        string name,
        string table,
        string[] columns,
        string? schema,
        SafeMigrationExecutionOptions execution
    )
    {
        var operation = CreateUniqueConstraintOperation(
            name,
            table,
            columns,
            schema,
            SafeMigrationExecutionAnnotationHelper.GetCompatibleStrictMode(execution));

        operation.Execution = execution;
        return operation;
    }

    public static SafeAddForeignKeyOperation CreateForeignKeyOperation(
        string name,
        string table,
        string[] columns,
        string principalTable,
        string[] principalColumns,
        string? schema,
        string? principalSchema,
        ReferentialAction onUpdate,
        ReferentialAction onDelete,
        SafeMigrationStrictMode strictMode
    )
    {
        var operation = new SafeAddForeignKeyOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
            Columns = columns,
            PrincipalTable = principalTable,
            PrincipalSchema = principalSchema,
            PrincipalColumns = principalColumns,
            OnUpdate = onUpdate,
            OnDelete = onDelete,
            StrictMode = strictMode,
            ExpectedDefinition = new ExpectedForeignKeyDefinition(
                name,
                table,
                schema,
                columns,
                principalTable,
                principalSchema,
                principalColumns,
                onUpdate,
                onDelete),
        };

        return operation;
    }

    public static SafeAddForeignKeyOperation CreateForeignKeyOperation(
        string name,
        string table,
        string[] columns,
        string principalTable,
        string[] principalColumns,
        string? schema,
        string? principalSchema,
        ReferentialAction onUpdate,
        ReferentialAction onDelete,
        SafeMigrationExecutionOptions execution
    )
    {
        var operation = CreateForeignKeyOperation(
            name,
            table,
            columns,
            principalTable,
            principalColumns,
            schema,
            principalSchema,
            onUpdate,
            onDelete,
            SafeMigrationExecutionAnnotationHelper.GetCompatibleStrictMode(execution));

        operation.Execution = execution;
        return operation;
    }

    public static SafeAddCheckConstraintOperation CreateCheckConstraintOperation(
        string name,
        string table,
        string sql,
        string? schema,
        SafeMigrationStrictMode strictMode
    )
    {
        var operation = new SafeAddCheckConstraintOperation
        {
            Name = name,
            Table = table,
            Schema = schema,
            Sql = sql,
            StrictMode = strictMode,
            ExpectedDefinition = new ExpectedCheckConstraintDefinition(
                name,
                table,
                schema,
                sql),
        };

        return operation;
    }

    public static SafeAddCheckConstraintOperation CreateCheckConstraintOperation(
        string name,
        string table,
        string sql,
        string? schema,
        SafeMigrationExecutionOptions execution
    )
    {
        var operation = CreateCheckConstraintOperation(
            name,
            table,
            sql,
            schema,
            SafeMigrationExecutionAnnotationHelper.GetCompatibleStrictMode(execution));

        operation.Execution = execution;
        return operation;
    }
}
