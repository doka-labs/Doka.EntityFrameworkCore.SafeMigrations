namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Shares exact owner bindings between analysis, generation, and current-schema alias contracts.</summary>
internal static class PostgreSqlSafeMigrationSchemaIdentity
{
    /// <summary>Collapses only an explicit schema proven to be the active creation schema.</summary>
    /// <param name="schema">The authored optional qualifier.</param>
    /// <param name="currentSchema">The schema read from the active connection, or null without proof.</param>
    /// <returns>The canonical current-schema alias, retaining every other quoted schema identity.</returns>
    public static string? Normalize(string? schema, string? currentSchema)
        => schema is not null && StringComparer.Ordinal.Equals(schema, currentSchema) ? null : schema;

    /// <summary>Obtains the owner whose native relation lookup must match catalog classification.</summary>
    /// <param name="intent">The immutable provider-neutral intent.</param>
    /// <returns>The relation owner, or an empty owner for schema-only operations.</returns>
    public static (string? Table, string? Schema) GetOwner(SafeMigrationIntent intent) => intent switch
    {
        EnsureTableIntent value => (value.Definition.Table, value.Definition.Schema),
        DropTableIntent value => (value.Table, value.Schema),
        RenameTableIntent value => (value.Name, value.Schema),
        EnsureColumnIntent value => (value.Table, value.Schema),
        AlterColumnIntent value => (value.Table, value.Schema),
        DropColumnIntent value => (value.Table, value.Schema),
        RenameColumnIntent value => (value.Table, value.Schema),
        EnsureIndexIntent value => (value.Definition.Table, value.Definition.Schema),
        DropIndexIntent value => (value.Table, value.Schema),
        RenameIndexIntent value => (value.Table, value.Schema),
        EnsurePrimaryKeyIntent value => (value.Definition.Table, value.Definition.Schema),
        DropPrimaryKeyIntent value => (value.Table, value.Schema),
        EnsureUniqueConstraintIntent value => (value.Definition.Table, value.Definition.Schema),
        DropUniqueConstraintIntent value => (value.Table, value.Schema),
        EnsureCheckConstraintIntent value => (value.Definition.Table, value.Definition.Schema),
        DropCheckConstraintIntent value => (value.Table, value.Schema),
        EnsureForeignKeyIntent value => (value.Definition.Table, value.Definition.Schema),
        DropForeignKeyIntent value => (value.Table, value.Schema),
        ModelManagedDataIntent value => (value.Table, value.Schema),
        EnsureSchemaIntent or DropSchemaIntent => default,
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent.GetType().FullName,
            "Unknown PostgreSQL SafeMigrations relation binding."),
    };

    /// <summary>Reads authored ordinary-operation qualifiers without interpreting opaque SQL text.</summary>
    /// <param name="operation">The original typed EF operation in the complete analysis stream.</param>
    /// <returns>The named table or schema-scoped index resource, otherwise an empty owner.</returns>
    public static (string? Table, string? Schema) GetProviderOwner(MigrationOperation operation) => operation switch
    {
        ColumnOperation value => (value.Table, value.Schema),
        DropColumnOperation value => (value.Table, value.Schema),
        RenameColumnOperation value => (value.Table, value.Schema),
        CreateTableOperation value => (value.Name, value.Schema),
        AlterTableOperation value => (value.Name, value.Schema),
        DropTableOperation value => (value.Name, value.Schema),
        RenameTableOperation value => (value.Name, value.Schema),
        CreateIndexOperation value => (value.Table, value.Schema),
        DropIndexOperation value => (value.Table ?? value.Name, value.Schema),
        RenameIndexOperation value => (value.Table ?? value.Name, value.Schema),
        AddPrimaryKeyOperation value => (value.Table, value.Schema),
        DropPrimaryKeyOperation value => (value.Table, value.Schema),
        AddUniqueConstraintOperation value => (value.Table, value.Schema),
        DropUniqueConstraintOperation value => (value.Table, value.Schema),
        AddCheckConstraintOperation value => (value.Table, value.Schema),
        DropCheckConstraintOperation value => (value.Table, value.Schema),
        AddForeignKeyOperation value => (value.Table, value.Schema),
        DropForeignKeyOperation value => (value.Table, value.Schema),
        InsertDataOperation value => (value.Table, value.Schema),
        UpdateDataOperation value => (value.Table, value.Schema),
        DeleteDataOperation value => (value.Table, value.Schema),
        _ => default,
    };

    /// <summary>Recognizes command-local path fences without allocating a native binding set.</summary>
    /// <param name="intent">The authored safe operation and its native dependencies.</param>
    /// <returns>Whether any native owner or principal requires the command-local identity fence.</returns>
    public static bool HasUnqualifiedNativeBindings(SafeMigrationIntent intent)
    {
        var owner = GetOwner(intent);
        if (owner is { Table: not null, Schema: null })
        {
            return true;
        }

        switch (intent)
        {
            case EnsureForeignKeyIntent foreignKey:
                return foreignKey.Definition.PrincipalSchema is null;
            case EnsureTableIntent table:
                // WHY: Indexing avoids an interface enumerator for this immutable FK snapshot.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < table.Definition.ForeignKeys.Count; index++)
                {
                    if (table.Definition.ForeignKeys[index].PrincipalSchema is null)
                    {
                        return true;
                    }
                }

                break;
            case DeleteModelManagedDataIntent delete:
                // WHY: Indexing avoids an interface enumerator for this immutable FK snapshot.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < delete.ForeignKeys.Count; index++)
                {
                    if (delete.ForeignKeys[index].Schema is null)
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    /// <summary>Captures every unqualified native relation used by an operation without folding identifiers.</summary>
    /// <param name="intent">The immutable operation being classified.</param>
    /// <param name="tables">The invocation-owned deduplicating destination.</param>
    public static void CollectUnqualifiedTables(SafeMigrationIntent intent, HashSet<string> tables)
    {
        var owner = GetOwner(intent);
        Add(owner.Table, owner.Schema, tables);
        switch (intent)
        {
            case DropIndexIntent value:
                // WHY: DROP/ALTER INDEX resolves the source index name, not
                // merely the table supplied for catalog ownership validation.
                Add(value.Name, value.Schema, tables);
                break;
            case RenameIndexIntent value:
                Add(value.Name, value.Schema, tables);
                break;
            case EnsureTableIntent value:
                foreach (var foreignKey in value.Definition.ForeignKeys)
                {
                    Add(foreignKey.PrincipalTable, foreignKey.PrincipalSchema, tables);
                }

                break;
            case EnsureForeignKeyIntent value:
                Add(value.Definition.PrincipalTable, value.Definition.PrincipalSchema, tables);
                break;
            case DeleteModelManagedDataIntent value:
                foreach (var foreignKey in value.ForeignKeys)
                {
                    Add(foreignKey.Table, foreignKey.Schema, tables);
                }

                break;
        }
    }

    /// <summary>Identifies a native source which cannot mutate a fallback when absent locally.</summary>
    /// <param name="intent">The immutable operation whose baseline can create or consume a relation name.</param>
    /// <param name="currentSchemaCandidate">The runtime-conditioned alias, or null for authored null owners.</param>
    /// <returns>The optional absent binding, or null when every native reference must be proven.</returns>
    public static string? GetOptionalAbsentNativeBinding(SafeMigrationIntent intent, string? currentSchemaCandidate)
    {
        if (intent is EnsureTableIntent table)
        {
            // WHY: This guard is captured for every generated table plan.
            // An indexed scan avoids a captured delegate and iterator allocation.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < table.Definition.ForeignKeys.Count; index++)
            {
                var foreignKey = table.Definition.ForeignKeys[index];
                if (foreignKey.PrincipalSchema is null
                    && StringComparer.Ordinal.Equals(foreignKey.PrincipalTable, table.Definition.Table))
                {
                    // WHY: Name deduplication cannot exempt an actual inline
                    // FK lookup against a same-name TEMP/fallback principal.

                    return null;
                }
            }
        }

        var binding = intent switch
        {
            EnsureTableIntent value => (Name: value.Definition.Table, value.Definition.Schema),
            DropTableIntent value => (Name: value.Table, value.Schema),
            RenameTableIntent value => (Name: value.Name, value.Schema),
            DropIndexIntent value => (Name: value.Name, value.Schema),
            RenameIndexIntent value => (Name: value.Name, value.Schema),
            _ => default,
        };

        // WHY: CREATE TABLE targets the current creation schema, not a same-name
        // fallback. DROP/RENAME never execute their native baseline when the
        // exact current-schema source is absent. Other references still need
        // native ownership proof before any row query or executable baseline.
        return binding.Schema is null || (currentSchemaCandidate is not null
            && StringComparer.Ordinal.Equals(binding.Schema, currentSchemaCandidate)) ? binding.Name : null;
    }

    /// <summary>Removes consumed sources and adds surviving native destination bindings after accepted DDL.</summary>
    /// <param name="intent">The operation whose baseline just executed.</param>
    /// <param name="tables">The invocation-owned native bindings, optionally including a connected family.</param>
    /// <param name="currentSchemaCandidate">The runtime-conditioned alias, or null for authored null owners.</param>
    public static void AdvancePostExecutionBindings(
        SafeMigrationIntent intent,
        HashSet<string> tables,
        string? currentSchemaCandidate = null
    )
    {
        switch (intent)
        {
            case DropTableIntent value when IsCurrentAlias(value.Schema, currentSchemaCandidate):
                tables.Remove(value.Table);
                break;
            case RenameTableIntent value:
                if (IsCurrentAlias(value.Schema, currentSchemaCandidate))
                {
                    tables.Remove(value.Name);
                }

                Add(value.NewName ?? value.Name, value.NewSchema ?? value.Schema, tables);
                break;
            case DropIndexIntent value when IsCurrentAlias(value.Schema, currentSchemaCandidate):
                tables.Remove(value.Name);
                break;
            case RenameIndexIntent value:
                if (IsCurrentAlias(value.Schema, currentSchemaCandidate))
                {
                    tables.Remove(value.Name);
                }

                Add(value.NewName, value.Schema, tables);
                break;
            case EnsureIndexIntent value:
                Add(value.Definition.Name, value.Definition.Schema, tables);
                break;
        }

        return;

        static bool IsCurrentAlias(string? schema, string? candidate)
            => schema is null || (candidate is not null && StringComparer.Ordinal.Equals(schema, candidate));
    }

    /// <summary>Records only actual unqualified bindings; explicit schemas never enter a native-path guard.</summary>
    private static void Add(string? table, string? schema, HashSet<string> tables)
    {
        if (table is not null && schema is null)
        {
            tables.Add(table);
        }
    }
}
