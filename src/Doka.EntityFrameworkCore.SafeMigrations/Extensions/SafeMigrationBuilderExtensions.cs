namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Provides safe, idempotent migration operations for <see cref="MigrationBuilder"/>.
/// </summary>
public static class SafeMigrationBuilderExtensions
{
    /// <summary>
    /// Creates a table only when it does not already exist.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="table">The table name.</param>
    /// <param name="columns">A delegate that defines the table's columns.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    /// <param name="constraints">An optional delegate that defines table-level constraints.</param>
    /// <param name="comment">An optional comment for the table.</param>
    /// <param name="strictMode">Controls behavior when the table already exists but differs from the expected definition.</param>
    public static OperationBuilder<CreateTableOperation> CreateTableIfNotExists<TColumns>(
        this MigrationBuilder migrationBuilder,
        string table,
        Func<ColumnsBuilder, TColumns> columns,
        string? schema = null,
        Action<CreateTableBuilder<TColumns>>? constraints = null,
        string? comment = null,
        SafeMigrationStrictMode strictMode = SafeMigrationStrictMode.None
    )
    {
        var builder = migrationBuilder.CreateTable(
            table,
            columns,
            schema,
            constraints,
            comment);

        Debug.Assert(
            migrationBuilder.Operations[^1] is CreateTableOperation,
            "EF Core did not append CreateTableOperation as the last operation. "
            + "Review this assumption against the current EF Core version.");

        var operation = (CreateTableOperation)migrationBuilder.Operations[^1];

        operation[SafeMigrationAnnotationNames.IfNotExists] = true;
        operation[SafeMigrationAnnotationNames.StrictMode] = strictMode;
        operation[SafeMigrationAnnotationNames.ExpectedDefinition] = SafeMigrationDefinitionSerializer.Serialize(
            BuildExpectedTableDefinition(operation));

        return builder;
    }

    /// <summary>
    /// Ensures that a schema exists, emitting <c>CREATE SCHEMA IF NOT EXISTS</c> when the
    /// safe-migrations SQL generator is registered.
    /// </summary>
    /// <remarks>
    /// This method is a semantic alias for <see cref="MigrationBuilder.EnsureSchema"/>. The
    /// idempotency is provided entirely by the registered
    /// <c>SafeMigrationsSqlGenerator</c> override for <c>EnsureSchemaOperation</c>, which
    /// emits <c>CREATE SCHEMA IF NOT EXISTS</c> instead of the standard <c>CREATE SCHEMA</c>.
    /// When a non-safe generator is registered, the behavior is identical to calling
    /// <see cref="MigrationBuilder.EnsureSchema"/> directly.
    /// </remarks>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The schema name to ensure exists.</param>
    public static OperationBuilder<EnsureSchemaOperation> EnsureSchemaExists(
        this MigrationBuilder migrationBuilder,
        string name
    ) => migrationBuilder.EnsureSchema(name);

    /// <summary>
    /// Drops a table only when it already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<DropTableOperation> DropTableIfExists(
        this MigrationBuilder migrationBuilder,
        string table,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateDropTableOperation(table, schema)));

    /// <summary>
    /// Drops a schema only when it already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The schema name.</param>
    public static OperationBuilder<DropSchemaOperation> DropSchemaIfExists(
        this MigrationBuilder migrationBuilder,
        string name
    )
    {
        var builder = migrationBuilder.DropSchema(name);

        Debug.Assert(
            migrationBuilder.Operations[^1] is DropSchemaOperation,
            "EF Core did not append DropSchemaOperation as the last operation. "
            + "Review this assumption against the current EF Core version.");

        var operation = (DropSchemaOperation)migrationBuilder.Operations[^1];
        operation[SafeMigrationAnnotationNames.IfExists] = true;

        return builder;
    }

    /// <summary>
    /// Renames a table only when the source table already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The current table name.</param>
    /// <param name="newName">The new table name, if changing the name.</param>
    /// <param name="schema">The current schema that contains the table.</param>
    /// <param name="newSchema">The new schema, if moving the table to a different schema.</param>
    public static OperationBuilder<RenameTableOperation> RenameTableIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string? newName = null,
        string? schema = null,
        string? newSchema = null
    )
    {
        var builder = migrationBuilder.RenameTable(
            name,
            schema,
            newName,
            newSchema);

        Debug.Assert(
            migrationBuilder.Operations[^1] is RenameTableOperation,
            "EF Core did not append RenameTableOperation as the last operation. "
            + "Review this assumption against the current EF Core version.");

        var operation = (RenameTableOperation)migrationBuilder.Operations[^1];
        operation[SafeMigrationAnnotationNames.IfExists] = true;

        return builder;
    }

    /// <summary>
    /// Adds a column only when it does not already exist.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The column name.</param>
    /// <param name="table">The table that contains the column.</param>
    /// <param name="type">The store type for the column, or <see langword="null"/> to infer from <typeparamref name="T"/>.</param>
    /// <param name="unicode">Whether the column supports Unicode data, if applicable.</param>
    /// <param name="maxLength">The maximum length of data that can be stored in the column, if applicable.</param>
    /// <param name="rowVersion">Whether the column acts as an automatic row version for optimistic concurrency.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    /// <param name="nullable">Whether the column allows <see langword="null"/> values.</param>
    /// <param name="defaultValue">The default value for the column, or <see langword="null"/> for none.</param>
    /// <param name="defaultValueSql">The SQL expression to use as the column default, or <see langword="null"/> for none.</param>
    /// <param name="computedColumnSql">The SQL expression for a computed column, or <see langword="null"/> for none.</param>
    /// <param name="fixedLength">Whether the column uses fixed-length storage, if applicable.</param>
    /// <param name="comment">An optional comment for the column.</param>
    /// <param name="collation">The optional collation for the column.</param>
    /// <param name="precision">The optional precision for numeric columns.</param>
    /// <param name="scale">The optional scale for numeric columns.</param>
    /// <param name="stored">Whether a computed column is stored on disk, if applicable.</param>
    /// <param name="strictMode">Controls behavior when the column already exists but differs from the expected definition.</param>
    /// <typeparam name="T">The CLR type of the column.</typeparam>
    public static OperationBuilder<AddColumnOperation> AddColumnIfNotExists<T>(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? type = null,
        bool? unicode = null,
        int? maxLength = null,
        bool rowVersion = false,
        string? schema = null,
        bool nullable = false,
        object? defaultValue = null,
        string? defaultValueSql = null,
        string? computedColumnSql = null,
        bool? fixedLength = null,
        string? comment = null,
        string? collation = null,
        int? precision = null,
        int? scale = null,
        bool? stored = null,
        SafeMigrationStrictMode strictMode = SafeMigrationStrictMode.None
    )
    {
        var operation = SafeMigrationOperationFactory.CreateAddColumnOperation<T>(
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
            strictMode);

        operation.IsUnicode = unicode;
        operation.MaxLength = maxLength;
        operation.IsRowVersion = rowVersion;
        operation.IsFixedLength = fixedLength;

        return new OperationBuilder<AddColumnOperation>(migrationBuilder.Operations.AddAndReturn(operation));
    }

    /// <summary>
    /// Adds a column only when it does not already exist, using the extended execution pipeline.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The column name.</param>
    /// <param name="table">The table that contains the column.</param>
    /// <param name="execution">The execution options controlling conflict behavior and preflight mode.</param>
    /// <param name="type">The store type for the column, or <see langword="null"/> to infer from <typeparamref name="T"/>.</param>
    /// <param name="unicode">Whether the column supports Unicode data, if applicable.</param>
    /// <param name="maxLength">The maximum length of data that can be stored in the column, if applicable.</param>
    /// <param name="rowVersion">Whether the column acts as an automatic row version for optimistic concurrency.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    /// <param name="nullable">Whether the column allows <see langword="null"/> values.</param>
    /// <param name="defaultValue">The default value for the column, or <see langword="null"/> for none.</param>
    /// <param name="defaultValueSql">The SQL expression to use as the column default, or <see langword="null"/> for none.</param>
    /// <param name="computedColumnSql">The SQL expression for a computed column, or <see langword="null"/> for none.</param>
    /// <param name="fixedLength">Whether the column uses fixed-length storage, if applicable.</param>
    /// <param name="comment">An optional comment for the column.</param>
    /// <param name="collation">The optional collation for the column.</param>
    /// <param name="precision">The optional precision for numeric columns.</param>
    /// <param name="scale">The optional scale for numeric columns.</param>
    /// <param name="stored">Whether a computed column is stored on disk, if applicable.</param>
    /// <typeparam name="T">The CLR type of the column.</typeparam>
    public static OperationBuilder<AddColumnOperation> AddColumnIfNotExists<T>(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        SafeMigrationExecutionOptions execution,
        string? type = null,
        bool? unicode = null,
        int? maxLength = null,
        bool rowVersion = false,
        string? schema = null,
        bool nullable = false,
        object? defaultValue = null,
        string? defaultValueSql = null,
        string? computedColumnSql = null,
        bool? fixedLength = null,
        string? comment = null,
        string? collation = null,
        int? precision = null,
        int? scale = null,
        bool? stored = null
    )
    {
        var operation = SafeMigrationOperationFactory.CreateAddColumnOperation<T>(
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
            execution);

        operation.IsUnicode = unicode;
        operation.MaxLength = maxLength;
        operation.IsRowVersion = rowVersion;
        operation.IsFixedLength = fixedLength;

        return new OperationBuilder<AddColumnOperation>(migrationBuilder.Operations.AddAndReturn(operation));
    }

    /// <summary>
    /// Drops a column only when it already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The column name.</param>
    /// <param name="table">The table that contains the column.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<DropColumnOperation> DropColumnIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateDropColumnOperation(name, table, schema)));

    /// <summary>
    /// Renames a column only when the source column already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The current column name.</param>
    /// <param name="table">The table that contains the column.</param>
    /// <param name="newName">The new column name.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<RenameColumnOperation> RenameColumnIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string newName,
        string? schema = null
    )
    {
        var builder = migrationBuilder.RenameColumn(
            name,
            table,
            newName,
            schema);

        Debug.Assert(
            migrationBuilder.Operations[^1] is RenameColumnOperation,
            "EF Core did not append RenameColumnOperation as the last operation. "
            + "Review this assumption against the current EF Core version.");

        var operation = (RenameColumnOperation)migrationBuilder.Operations[^1];
        operation[SafeMigrationAnnotationNames.IfExists] = true;

        return builder;
    }

    /// <summary>
    /// Renames an index only when the source index already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The current index name.</param>
    /// <param name="newName">The new index name.</param>
    /// <param name="table">The optional table that contains the index.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<RenameIndexOperation> RenameIndexIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string newName,
        string? table = null,
        string? schema = null
    )
    {
        var builder = migrationBuilder.RenameIndex(
            name,
            newName,
            table,
            schema);

        Debug.Assert(
            migrationBuilder.Operations[^1] is RenameIndexOperation,
            "EF Core did not append RenameIndexOperation as the last operation. "
            + "Review this assumption against the current EF Core version.");

        var operation = (RenameIndexOperation)migrationBuilder.Operations[^1];
        operation[SafeMigrationAnnotationNames.IfExists] = true;

        return builder;
    }

    /// <summary>
    /// Alters a column only when the existing definition differs from the expected definition.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The column name.</param>
    /// <param name="table">The table that contains the column.</param>
    /// <param name="type">The new store type, or <see langword="null"/> to infer from <typeparamref name="T"/>.</param>
    /// <param name="unicode">Whether the column supports Unicode data after the alteration, if applicable.</param>
    /// <param name="maxLength">The maximum length after the alteration, if applicable.</param>
    /// <param name="rowVersion">Whether the column acts as an automatic row version after the alteration.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    /// <param name="nullable">Whether the column allows <see langword="null"/> values after the alteration.</param>
    /// <param name="defaultValue">The new default value, or <see langword="null"/> to remove the default.</param>
    /// <param name="defaultValueSql">The new SQL default expression, or <see langword="null"/> to remove it.</param>
    /// <param name="computedColumnSql">The new computed-column expression, or <see langword="null"/> to remove it.</param>
    /// <param name="oldClrType">The CLR type of the column before the alteration.</param>
    /// <param name="oldType">The store type before the alteration.</param>
    /// <param name="oldUnicode">Whether the column supported Unicode data before the alteration, if applicable.</param>
    /// <param name="oldMaxLength">The maximum length before the alteration, if applicable.</param>
    /// <param name="oldRowVersion">Whether the column acted as a row version before the alteration.</param>
    /// <param name="oldNullable">Whether the column allowed nulls before the alteration.</param>
    /// <param name="oldDefaultValue">The default value before the alteration.</param>
    /// <param name="oldDefaultValueSql">The SQL default expression before the alteration.</param>
    /// <param name="oldComputedColumnSql">The computed-column expression before the alteration.</param>
    /// <param name="fixedLength">Whether the column uses fixed-length storage after the alteration, if applicable.</param>
    /// <param name="oldFixedLength">Whether the column used fixed-length storage before the alteration, if applicable.</param>
    /// <param name="comment">The new column comment, if any.</param>
    /// <param name="oldComment">The column comment before the alteration, if any.</param>
    /// <param name="collation">The new collation, if any.</param>
    /// <param name="oldCollation">The collation before the alteration, if any.</param>
    /// <param name="precision">The new precision for numeric columns, if applicable.</param>
    /// <param name="oldPrecision">The precision before the alteration, if applicable.</param>
    /// <param name="scale">The new scale for numeric columns, if applicable.</param>
    /// <param name="oldScale">The scale before the alteration, if applicable.</param>
    /// <param name="stored">Whether a computed column is stored on disk after the alteration, if applicable.</param>
    /// <param name="oldStored">Whether a computed column was stored on disk before the alteration, if applicable.</param>
    /// <typeparam name="T">The CLR type of the column after the alteration.</typeparam>
    public static OperationBuilder<AlterColumnOperation> AlterColumnIfDifferent<T>(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? type = null,
        bool? unicode = null,
        int? maxLength = null,
        bool rowVersion = false,
        string? schema = null,
        bool nullable = false,
        object? defaultValue = null,
        string? defaultValueSql = null,
        string? computedColumnSql = null,
        Type? oldClrType = null,
        string? oldType = null,
        bool? oldUnicode = null,
        int? oldMaxLength = null,
        bool oldRowVersion = false,
        bool oldNullable = false,
        object? oldDefaultValue = null,
        string? oldDefaultValueSql = null,
        string? oldComputedColumnSql = null,
        bool? fixedLength = null,
        bool? oldFixedLength = null,
        string? comment = null,
        string? oldComment = null,
        string? collation = null,
        string? oldCollation = null,
        int? precision = null,
        int? oldPrecision = null,
        int? scale = null,
        int? oldScale = null,
        bool? stored = null,
        bool? oldStored = null
    )
    {
        var builder = migrationBuilder.AlterColumn<T>(
            name: name,
            table: table,
            type: type,
            unicode: unicode,
            maxLength: maxLength,
            rowVersion: rowVersion,
            schema: schema,
            nullable: nullable,
            defaultValue: defaultValue,
            defaultValueSql: defaultValueSql,
            computedColumnSql: computedColumnSql,
            oldClrType: oldClrType,
            oldType: oldType,
            oldUnicode: oldUnicode,
            oldMaxLength: oldMaxLength,
            oldRowVersion: oldRowVersion,
            oldNullable: oldNullable,
            oldDefaultValue: oldDefaultValue,
            oldDefaultValueSql: oldDefaultValueSql,
            oldComputedColumnSql: oldComputedColumnSql,
            fixedLength: fixedLength,
            oldFixedLength: oldFixedLength,
            comment: comment,
            oldComment: oldComment,
            collation: collation,
            oldCollation: oldCollation,
            precision: precision,
            oldPrecision: oldPrecision,
            scale: scale,
            oldScale: oldScale,
            stored: stored,
            oldStored: oldStored);

        Debug.Assert(
            migrationBuilder.Operations[^1] is AlterColumnOperation,
            "EF Core did not append AlterColumnOperation as the last operation. "
            + "Review this assumption against the current EF Core version.");

        var operation = (AlterColumnOperation)migrationBuilder.Operations[^1];
        operation[SafeMigrationAnnotationNames.AlterIfDifferent] = true;
        operation[SafeMigrationAnnotationNames.ExpectedDefinition] = SafeMigrationDefinitionSerializer.Serialize(
            BuildExpectedColumnDefinition(operation));

        return builder;
    }

    /// <summary>
    /// Creates an index only when it does not already exist.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The index name.</param>
    /// <param name="table">The table to index.</param>
    /// <param name="columns">The ordered list of columns to include in the index.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    /// <param name="filter">An optional filter expression for a partial index.</param>
    /// <param name="descending">Per-column sort direction; <see langword="null"/> means all ascending.</param>
    /// <param name="strictMode">Controls behavior when the index already exists but differs from the expected definition.</param>
    public static OperationBuilder<CreateIndexOperation> CreateIndexIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        string? schema = null,
        bool unique = false,
        string? filter = null,
        bool[]? descending = null,
        SafeMigrationStrictMode strictMode = SafeMigrationStrictMode.None
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateIndexOperation(
                name,
                table,
                columns,
                schema,
                unique,
                filter,
                descending,
                strictMode)));

    /// <summary>
    /// Creates an index only when it does not already exist, using the extended execution pipeline.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The index name.</param>
    /// <param name="table">The table to index.</param>
    /// <param name="columns">The ordered list of columns to include in the index.</param>
    /// <param name="execution">The execution options controlling conflict behavior and preflight mode.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    /// <param name="filter">An optional filter expression for a partial index.</param>
    /// <param name="descending">Per-column sort direction; <see langword="null"/> means all ascending.</param>
    public static OperationBuilder<CreateIndexOperation> CreateIndexIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        SafeMigrationExecutionOptions execution,
        string? schema = null,
        bool unique = false,
        string? filter = null,
        bool[]? descending = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateIndexOperation(
                name,
                table,
                columns,
                schema,
                unique,
                filter,
                descending,
                execution)));

    /// <summary>
    /// Drops an index only when it already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The index name.</param>
    /// <param name="table">The table that contains the index.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<DropIndexOperation> DropIndexIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateDropIndexOperation(name, table, schema)));

    /// <summary>
    /// Adds a primary key only when it does not already exist.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The primary key constraint name.</param>
    /// <param name="table">The table to add the primary key to.</param>
    /// <param name="columns">The columns that form the primary key.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    /// <param name="strictMode">Controls behavior when the primary key already exists but differs from the expected definition.</param>
    public static OperationBuilder<AddPrimaryKeyOperation> AddPrimaryKeyIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        string? schema = null,
        SafeMigrationStrictMode strictMode = SafeMigrationStrictMode.None
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreatePrimaryKeyOperation(
                name,
                table,
                columns,
                schema,
                strictMode)));

    /// <summary>
    /// Adds a primary key only when it does not already exist, using the extended execution pipeline.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The primary key constraint name.</param>
    /// <param name="table">The table to add the primary key to.</param>
    /// <param name="columns">The columns that form the primary key.</param>
    /// <param name="execution">The execution options controlling conflict behavior and preflight mode.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<AddPrimaryKeyOperation> AddPrimaryKeyIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        SafeMigrationExecutionOptions execution,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreatePrimaryKeyOperation(
                name,
                table,
                columns,
                schema,
                execution)));

    /// <summary>
    /// Drops a primary key only when it already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="table">The table whose primary key should be dropped.</param>
    /// <param name="name">The constraint name, if required by the target provider (unused for MariaDB).</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<DropPrimaryKeyOperation> DropPrimaryKeyIfExists(
        this MigrationBuilder migrationBuilder,
        string table,
        string? name = null,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateDropPrimaryKeyOperation(table, name, schema)));

    /// <summary>
    /// Adds a unique constraint only when it does not already exist.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The unique constraint name.</param>
    /// <param name="table">The table to add the constraint to.</param>
    /// <param name="columns">The columns covered by the constraint.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    /// <param name="strictMode">Controls behavior when the constraint already exists but differs from the expected definition.</param>
    public static OperationBuilder<AddUniqueConstraintOperation> AddUniqueConstraintIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        string? schema = null,
        SafeMigrationStrictMode strictMode = SafeMigrationStrictMode.None
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateUniqueConstraintOperation(
                name,
                table,
                columns,
                schema,
                strictMode)));

    /// <summary>
    /// Adds a unique constraint only when it does not already exist, using the extended execution pipeline.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The unique constraint name.</param>
    /// <param name="table">The table to add the constraint to.</param>
    /// <param name="columns">The columns covered by the constraint.</param>
    /// <param name="execution">The execution options controlling conflict behavior and preflight mode.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<AddUniqueConstraintOperation> AddUniqueConstraintIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        SafeMigrationExecutionOptions execution,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateUniqueConstraintOperation(
                name,
                table,
                columns,
                schema,
                execution)));

    /// <summary>
    /// Drops a unique constraint only when it already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The constraint name.</param>
    /// <param name="table">The table that owns the constraint.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<DropUniqueConstraintOperation> DropUniqueConstraintIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateDropUniqueConstraintOperation(name, table, schema)));

    /// <summary>
    /// Adds a foreign key only when it does not already exist.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The foreign key constraint name.</param>
    /// <param name="table">The dependent table that holds the foreign key columns.</param>
    /// <param name="columns">The foreign key columns on the dependent table.</param>
    /// <param name="principalTable">The principal table being referenced.</param>
    /// <param name="principalColumns">The referenced columns on the principal table.</param>
    /// <param name="schema">The optional schema that contains the dependent table.</param>
    /// <param name="principalSchema">The optional schema that contains the principal table.</param>
    /// <param name="onUpdate">The referential action on update.</param>
    /// <param name="onDelete">The referential action on delete.</param>
    /// <param name="strictMode">Controls behavior when the foreign key already exists but differs from the expected definition.</param>
    public static OperationBuilder<AddForeignKeyOperation> AddForeignKeyIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        string principalTable,
        string[] principalColumns,
        string? schema = null,
        string? principalSchema = null,
        ReferentialAction onUpdate = ReferentialAction.NoAction,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        SafeMigrationStrictMode strictMode = SafeMigrationStrictMode.None
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateForeignKeyOperation(
                name,
                table,
                columns,
                principalTable,
                principalColumns,
                schema,
                principalSchema,
                onUpdate,
                onDelete,
                strictMode)));

    /// <summary>
    /// Adds a foreign key only when it does not already exist, using the extended execution pipeline.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The foreign key constraint name.</param>
    /// <param name="table">The dependent table that holds the foreign key columns.</param>
    /// <param name="columns">The foreign key columns on the dependent table.</param>
    /// <param name="principalTable">The principal table being referenced.</param>
    /// <param name="principalColumns">The referenced columns on the principal table.</param>
    /// <param name="execution">The execution options controlling conflict behavior and preflight mode.</param>
    /// <param name="schema">The optional schema that contains the dependent table.</param>
    /// <param name="principalSchema">The optional schema that contains the principal table.</param>
    /// <param name="onUpdate">The referential action on update.</param>
    /// <param name="onDelete">The referential action on delete.</param>
    public static OperationBuilder<AddForeignKeyOperation> AddForeignKeyIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        string principalTable,
        string[] principalColumns,
        SafeMigrationExecutionOptions execution,
        string? schema = null,
        string? principalSchema = null,
        ReferentialAction onUpdate = ReferentialAction.NoAction,
        ReferentialAction onDelete = ReferentialAction.NoAction
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateForeignKeyOperation(
                name,
                table,
                columns,
                principalTable,
                principalColumns,
                schema,
                principalSchema,
                onUpdate,
                onDelete,
                execution)));

    /// <summary>
    /// Drops a foreign key only when it already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The constraint name.</param>
    /// <param name="table">The table that owns the foreign key.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<DropForeignKeyOperation> DropForeignKeyIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateDropForeignKeyOperation(name, table, schema)));

    /// <summary>
    /// Adds a check constraint only when it does not already exist.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The check constraint name.</param>
    /// <param name="table">The table to add the constraint to.</param>
    /// <param name="sql">The SQL boolean expression that the constraint enforces.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    /// <param name="strictMode">Controls behavior when the constraint already exists but differs from the expected definition.</param>
    public static OperationBuilder<AddCheckConstraintOperation> AddCheckConstraintIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string sql,
        string? schema = null,
        SafeMigrationStrictMode strictMode = SafeMigrationStrictMode.None
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateCheckConstraintOperation(
                name,
                table,
                sql,
                schema,
                strictMode)));

    /// <summary>
    /// Adds a check constraint only when it does not already exist, using the extended execution pipeline.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The check constraint name.</param>
    /// <param name="table">The table to add the constraint to.</param>
    /// <param name="sql">The SQL boolean expression that the constraint enforces.</param>
    /// <param name="execution">The execution options controlling conflict behavior and preflight mode.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<AddCheckConstraintOperation> AddCheckConstraintIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string sql,
        SafeMigrationExecutionOptions execution,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateCheckConstraintOperation(
                name,
                table,
                sql,
                schema,
                execution)));

    /// <summary>
    /// Drops a check constraint only when it already exists.
    /// </summary>
    /// <param name="migrationBuilder">The <see cref="MigrationBuilder"/> to extend.</param>
    /// <param name="name">The constraint name.</param>
    /// <param name="table">The table that owns the constraint.</param>
    /// <param name="schema">The optional schema that contains the table.</param>
    public static OperationBuilder<DropCheckConstraintOperation> DropCheckConstraintIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => new(
        migrationBuilder.Operations.AddAndReturn(
            SafeMigrationOperationFactory.CreateDropCheckConstraintOperation(name, table, schema)));

    private static TOperation AddAndReturn<TOperation>(
        this List<MigrationOperation> operations,
        TOperation operation
    )
        where TOperation : MigrationOperation
    {
        operations.Add(operation);
        return operation;
    }

    private static ExpectedTableDefinition BuildExpectedTableDefinition(
        CreateTableOperation operation
    )
    {
        var columns = operation
            .Columns.Select(BuildExpectedColumnDefinition)
            .ToArray();

        var primaryKey = operation.PrimaryKey is null
            ? null
            : new ExpectedPrimaryKeyDefinition(
                operation.PrimaryKey.Name,
                operation.Name,
                operation.Schema,
                operation.PrimaryKey.Columns);

        return new ExpectedTableDefinition(
            operation.Name,
            operation.Schema,
            columns,
            primaryKey);
    }

    private static ExpectedColumnDefinition BuildExpectedColumnDefinition(
        ColumnOperation operation
    )
    {
        var (defaultValueTypeName, defaultValueJson) =
            SafeMigrationDefaultValueSerializer.Capture(operation.DefaultValue);

        return new ExpectedColumnDefinition(
            operation.Name,
            operation.ColumnType,
            operation.IsNullable,
            DefaultValueLiteral: SafeMigrationDefaultValueSerializer.ToLegacyLiteral(operation.DefaultValue),
            DefaultValueSql: operation.DefaultValueSql,
            DefaultValueTypeName: defaultValueTypeName,
            DefaultValueJson: defaultValueJson,
            ComputedColumnSql: operation.ComputedColumnSql,
            Precision: operation.Precision,
            Scale: operation.Scale,
            Collation: operation.Collation,
            IsStored: operation.IsStored);
    }
}
