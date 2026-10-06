namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal enum SqlServerIdentifierScope : byte
{
    Schema = 1,
    SchemaObject = 2,
    Column = 3,
    Index = 4,
}

internal readonly record struct SqlServerIdentifierReference(
    SqlServerIdentifierScope Scope,
    string Schema,
    string? Table,
    string Name
);

/// <summary>Captures physical identifier scopes for SQL Server catalog-collation guards.</summary>
internal static class SqlServerIdentifierContract
{
    internal const string ResultVariable = "@doka_identity_safe";

    /// <summary>Collects distinct identifiers from an immutable safe operation stream.</summary>
    /// <param name="operations">The ordered operation stream.</param>
    /// <returns>The scoped names required by catalog and generated-command guards.</returns>
    public static IReadOnlyList<SqlServerIdentifierReference> Collect(
        IEnumerable<SafeMigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        var names = new HashSet<SqlServerIdentifierReference>();
        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);

            switch (operation.Intent)
            {
                case EnsureSchemaIntent value:
                    AddSchema(names, value.Name);
                    break;
                case DropSchemaIntent value:
                    AddSchema(names, value.Name);
                    break;
                case EnsureTableIntent value:
                    AddTableDefinition(names, value.Definition);
                    break;
                case DropTableIntent value:
                    AddTable(names, value.Schema, value.Table);
                    break;
                case RenameTableIntent value:
                    AddTable(names, value.Schema, value.Name);
                    AddTable(names, value.NewSchema ?? value.Schema, value.NewName ?? value.Name);
                    if ((value.NewName ?? value.Name) != value.Name
                        && (value.NewSchema ?? value.Schema ?? "dbo") != (value.Schema ?? "dbo"))
                    {
                        // WHY: The baseline first allocates the new name in
                        // the source schema, so that identity needs the same
                        // catalog-collation guard as the final destination.
                        AddTable(names, value.Schema, value.NewName ?? value.Name);
                    }

                    break;
                case EnsureColumnIntent value:
                    AddColumn(names, value.Schema, value.Table, value.Definition.Name);
                    break;
                case DropColumnIntent value:
                    AddColumn(names, value.Schema, value.Table, value.Name);
                    break;
                case RenameColumnIntent value:
                    AddColumn(names, value.Schema, value.Table, value.Name);
                    AddColumn(names, value.Schema, value.Table, value.NewName);
                    break;
                case AlterColumnIntent value:
                    AddColumn(names, value.Schema, value.Table, value.Definition.Name);
                    break;
                case EnsureIndexIntent value:
                    AddIndex(names, value.Definition.Schema, value.Definition.Table, value.Definition.Name);
                    foreach (var key in value.Definition.Keys)
                    {
                        if (key.Column is { } column)
                        {
                            AddColumn(names, value.Definition.Schema, value.Definition.Table, column);
                        }
                    }

                    foreach (var column in value.Definition.IncludedColumns)
                    {
                        AddColumn(names, value.Definition.Schema, value.Definition.Table, column);
                    }

                    break;
                case DropIndexIntent value:
                    AddIndex(names, value.Schema, value.Table, value.Name);
                    break;
                case RenameIndexIntent value:
                    AddIndex(names, value.Schema, value.Table, value.Name);
                    AddIndex(names, value.Schema, value.Table, value.NewName);
                    break;
                case EnsurePrimaryKeyIntent value:
                    AddKey(names, value.Definition.Schema, value.Definition.Table,
                        value.Definition.Name, value.Definition.Columns);
                    break;
                case DropPrimaryKeyIntent value:
                    AddKeyName(names, value.Schema, value.Table, value.Name);
                    break;
                case EnsureUniqueConstraintIntent value:
                    AddKey(names, value.Definition.Schema, value.Definition.Table,
                        value.Definition.Name, value.Definition.Columns);
                    break;
                case DropUniqueConstraintIntent value:
                    AddKeyName(names, value.Schema, value.Table, value.Name);
                    break;
                case EnsureCheckConstraintIntent value:
                    AddConstraint(names, value.Definition.Schema, value.Definition.Table,
                        value.Definition.Name);
                    break;
                case DropCheckConstraintIntent value:
                    AddConstraint(names, value.Schema, value.Table, value.Name);
                    break;
                case EnsureForeignKeyIntent value:
                    AddForeignKey(names, value.Definition);
                    break;
                case DropForeignKeyIntent value:
                    AddConstraint(names, value.Schema, value.Table, value.Name);
                    break;
                case ModelManagedDataIntent value:
                    AddTable(names, value.Schema, value.Table);
                    foreach (var column in value.KeyColumns.Concat(value.Columns))
                    {
                        AddColumn(names, value.Schema, value.Table, column);
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        return names.ToArray();
    }

    /// <summary>Builds bounded catalog-collation checks in one operation-owned temporary scope.</summary>
    /// <param name="references">The physical names collected from the operation stream.</param>
    /// <param name="throwOnCollision">Whether an unsafe identity throws instead of returning a proof result.</param>
    /// <returns>The ordered statements forming the complete identity guard.</returns>
    public static IReadOnlyList<string> BuildCollisionGuardCommands(
        IReadOnlyList<SqlServerIdentifierReference> references,
        bool throwOnCollision
    )
        => BuildCollisionGuardCommands(references, throwOnCollision, out _);

    /// <summary>Builds bounded checks and exposes the unique temporary scope for analyzer recovery.</summary>
    /// <param name="references">The physical names collected from the operation stream.</param>
    /// <param name="throwOnCollision">Whether an unsafe identity throws instead of returning a proof result.</param>
    /// <param name="temporaryTable">The owned scope requiring cleanup, or null when no scope is created.</param>
    /// <returns>The ordered statements forming the complete identity guard.</returns>
    internal static IReadOnlyList<string> BuildCollisionGuardCommands(
        IReadOnlyList<SqlServerIdentifierReference> references,
        bool throwOnCollision,
        out string? temporaryTable
    )
    {
        ArgumentNullException.ThrowIfNull(references);

        temporaryTable = null;

        if (references.Any(static reference => !IsValidIdentifier(reference.Name)
            || !IsValidIdentifier(reference.Schema)
            || reference.Table is { } table && !IsValidIdentifier(table)))
        {
            return [EndGuard(throwOnCollision, safe: false)];
        }

        // WHY: Collect also emits every parent schema and table in their own
        // physical scope. An alias hidden in a qualifier therefore appears
        // in one exact-scope group before this SQL-free fast path is taken.
        var possibleCollision = HasRepeatedPhysicalScope(references);

        if (!possibleCollision)
        {
            return [EndGuard(throwOnCollision, safe: true)];
        }

        // WHY: A caller may own a similarly named local temporary table on
        // this connection. Per-invocation SQL text is intentional: a reserved
        // fixed name would block recovery after an interrupted earlier guard.
        temporaryTable = "#doka_sm_identifiers_" + Guid.NewGuid().ToString("N");
        var commands = new List<string>();
        commands.Add($"IF OBJECT_ID(N'tempdb..{temporaryTable}') IS NOT NULL "
            + "THROW 51002, N'doka_sm_unsupported', 1; "
            + $"CREATE TABLE {temporaryTable} (scope tinyint NOT NULL, "
            + "schema_name nvarchar(128) COLLATE CATALOG_DEFAULT NOT NULL, "
            + "table_name nvarchar(128) COLLATE CATALOG_DEFAULT NOT NULL, "
            + "name nvarchar(128) COLLATE CATALOG_DEFAULT NOT NULL);");
        var batch = new StringBuilder(4096);
        var batchBytes = 0;
        var count = 0;
        foreach (var reference in references)
        {
            var row = "(" + ((byte)reference.Scope).ToString(CultureInfo.InvariantCulture)
                + ", " + Literal(reference.Schema)
                + ", " + Literal(reference.Table ?? string.Empty)
                + ", " + Literal(reference.Name) + ")";

            var bytes = Encoding.UTF8.GetByteCount(row);
            if (count == 512 || batchBytes + bytes > 1024 * 1024)
            {
                commands.Add($"INSERT INTO {temporaryTable} VALUES " + batch + ";");
                batch.Clear();
                batchBytes = 0;
                count = 0;
            }

            if (count > 0)
            {
                batch.Append(", ");
                batchBytes += 2;
            }

            batch.Append(row);
            batchBytes += bytes;
            count++;
        }

        if (count > 0)
        {
            commands.Add($"INSERT INTO {temporaryTable} VALUES " + batch + ";");
        }

        // WHY: CATALOG_DEFAULT, not DATABASE_DEFAULT, is the physical name
        // collation in a contained database. BIN2 plus DATALENGTH detects
        // distinct spellings that catalog collation or padding equates.
        var check = $"EXISTS (SELECT 1 FROM {temporaryTable} "
            + "GROUP BY scope, schema_name, table_name, name "
            + "HAVING MIN(name COLLATE Latin1_General_100_BIN2) "
            + "<> MAX(name COLLATE Latin1_General_100_BIN2) "
            + "OR MIN(DATALENGTH(name)) <> MAX(DATALENGTH(name)) "
            + "OR MIN(schema_name COLLATE Latin1_General_100_BIN2) "
            + "<> MAX(schema_name COLLATE Latin1_General_100_BIN2) "
            + "OR MIN(DATALENGTH(schema_name)) <> MAX(DATALENGTH(schema_name)) "
            + "OR MIN(table_name COLLATE Latin1_General_100_BIN2) "
            + "<> MAX(table_name COLLATE Latin1_General_100_BIN2) "
            + "OR MIN(DATALENGTH(table_name)) <> MAX(DATALENGTH(table_name)))";

        commands.Add("DECLARE " + ResultVariable + " int = CASE WHEN " + check
            + $" THEN 0 ELSE 1 END; DROP TABLE {temporaryTable}; "
            + EndGuard(throwOnCollision, safe: null));

        return commands;
    }

    /// <summary>
    /// Detects a second reference in the same exact physical scope without materializing full groups.
    /// </summary>
    /// <param name="references">
    /// The completed identifier references, inspected without mutation or deduplication.
    /// </param>
    /// <returns>Whether two references share the same scope and ordinal schema/table identity.</returns>
    /// <exception cref="ArgumentNullException">The reference collection is null.</exception>
    internal static bool HasRepeatedPhysicalScope(
        IReadOnlyList<SqlServerIdentifierReference> references
    )
    {
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count < 2)
        {
            return false;
        }

        // WHY: The caller only needs to know whether a scope repeats, not its
        // grouped names. Stop at the first repeat while leaving every original
        // reference available to the unchanged catalog guard after validation.
        var scopes = new HashSet<(SqlServerIdentifierScope Scope, string Schema, string? Table)>();
        for (var index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            if (!scopes.Add((reference.Scope, reference.Schema, reference.Table)))
            {
                return true;
            }
        }

        return false;
    }

    private static string EndGuard(
        bool throwOnCollision,
        bool? safe
    )
    {
        var declaration = safe is null
            ? string.Empty
            : "DECLARE " + ResultVariable + " int = " + (safe.Value ? "1" : "0") + "; ";

        return declaration + (throwOnCollision
            ? "IF " + ResultVariable + " <> 1 THROW 51002, N'doka_sm_unsupported', 1;"
            : "SELECT " + ResultVariable + ";");
    }

    private static bool IsValidIdentifier(string value)
    {
        if (value.Length > 128 || value.Contains('\0'))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string Literal(string value)
        => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static void AddTableDefinition(
        HashSet<SqlServerIdentifierReference> names,
        ExpectedTableDefinition definition
    )
    {
        AddTable(names, definition.Schema, definition.Table);
        foreach (var column in definition.Columns)
        {
            AddColumn(names, definition.Schema, definition.Table, column.Name);
        }

        if (definition.PrimaryKey is { } primaryKey)
        {
            AddKey(names, definition.Schema, definition.Table, primaryKey.Name, primaryKey.Columns);
        }

        foreach (var unique in definition.UniqueConstraints)
        {
            AddKey(names, definition.Schema, definition.Table, unique.Name, unique.Columns);
        }

        foreach (var check in definition.CheckConstraints)
        {
            AddConstraint(names, definition.Schema, definition.Table, check.Name);
        }

        foreach (var foreignKey in definition.ForeignKeys)
        {
            AddForeignKey(names, foreignKey);
        }
    }

    private static void AddForeignKey(
        HashSet<SqlServerIdentifierReference> names,
        ExpectedForeignKeyDefinition definition
    )
    {
        AddConstraint(names, definition.Schema, definition.Table, definition.Name);
        foreach (var column in definition.Columns)
        {
            AddColumn(names, definition.Schema, definition.Table, column);
        }

        AddTable(names, definition.PrincipalSchema, definition.PrincipalTable);
        foreach (var column in definition.PrincipalColumns)
        {
            AddColumn(names, definition.PrincipalSchema, definition.PrincipalTable, column);
        }
    }

    private static void AddKey(
        HashSet<SqlServerIdentifierReference> names,
        string? schema,
        string table,
        string name,
        IReadOnlyList<string> columns
    )
    {
        AddKeyName(names, schema, table, name);
        foreach (var column in columns)
        {
            AddColumn(names, schema, table, column);
        }
    }

    private static void AddKeyName(
        HashSet<SqlServerIdentifierReference> names,
        string? schema,
        string table,
        string name
    )
    {
        AddConstraint(names, schema, table, name);
        AddIndex(names, schema, table, name);
    }

    private static void AddSchema(
        HashSet<SqlServerIdentifierReference> names,
        string? schema
    )
    {
        var effective = schema ?? "dbo";
        names.Add(new SqlServerIdentifierReference(
            SqlServerIdentifierScope.Schema, string.Empty, null, effective));
    }

    private static void AddTable(
        HashSet<SqlServerIdentifierReference> names,
        string? schema,
        string table
    )
    {
        AddSchema(names, schema);
        names.Add(new SqlServerIdentifierReference(
            SqlServerIdentifierScope.SchemaObject, schema ?? "dbo", null, table));
    }

    private static void AddColumn(
        HashSet<SqlServerIdentifierReference> names,
        string? schema,
        string table,
        string column
    )
    {
        AddTable(names, schema, table);
        names.Add(new SqlServerIdentifierReference(
            SqlServerIdentifierScope.Column, schema ?? "dbo", table, column));
    }

    private static void AddConstraint(
        HashSet<SqlServerIdentifierReference> names,
        string? schema,
        string table,
        string name
    )
    {
        AddTable(names, schema, table);
        names.Add(new SqlServerIdentifierReference(
            SqlServerIdentifierScope.SchemaObject, schema ?? "dbo", null, name));
    }

    private static void AddIndex(
        HashSet<SqlServerIdentifierReference> names,
        string? schema,
        string table,
        string name
    )
    {
        AddTable(names, schema, table);
        names.Add(new SqlServerIdentifierReference(
            SqlServerIdentifierScope.Index, schema ?? "dbo", table, name));
    }
}
