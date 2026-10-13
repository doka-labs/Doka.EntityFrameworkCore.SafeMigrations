namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <summary>Separates semantic table shape from actual named constraints in the same immutable snapshot.</summary>
    /// <param name="snapshot">The complete catalog already captured for this analysis invocation.</param>
    /// <param name="table">The physically matched table.</param>
    /// <param name="expected">The accepted authored table contract.</param>
    /// <returns>Owned name bindings, or null when no named member can be independently resolved.</returns>
    private List<SafeMigrationConstraintIdentityBinding>? CaptureTableConstraintIdentities(
        SqliteCatalogSnapshot snapshot,
        SqliteTableSnapshot table,
        ExpectedTableDefinition expected
    )
    {
        List<SafeMigrationConstraintIdentityBinding>? bindings = null;
        if (expected.PrimaryKey is { } primaryKey && table.PrimaryKeyName is { } primaryKeyName)
        {
            Add(SafeMigrationDatabaseObjectKind.PrimaryKey, primaryKey.Name, primaryKeyName);
        }

        // WHY: Table matching accepts semantic aliases. A named exact match
        // or a single semantic witness proves physical identity; anonymous or
        // ambiguous witnesses do not. Reuse the snapshot, without new queries
        // or capturing delegates per constraint.
        foreach (var definition in expected.UniqueConstraints)
        {
            string? exact = null;
            string? candidate = null;
            var count = 0;
            foreach (var actual in table.UniqueConstraints)
            {
                if (IdentifiersEqual(definition.Columns, actual.Columns))
                {
                    Observe(definition.Name, actual.Name, ref exact, ref candidate, ref count);
                }
            }

            Add(SafeMigrationDatabaseObjectKind.UniqueConstraint, definition.Name,
                exact ?? (count == 1 ? candidate : null));
        }

        foreach (var definition in expected.CheckConstraints)
        {
            var sql = RenderCheck(definition);
            string? exact = null;
            string? candidate = null;
            var count = 0;
            foreach (var actual in table.Checks)
            {
                if (sql is not null && SqlEquivalent(actual.Expression, sql))
                {
                    Observe(definition.Name, actual.Name, ref exact, ref candidate, ref count);
                }
            }

            Add(SafeMigrationDatabaseObjectKind.CheckConstraint, definition.Name,
                exact ?? (count == 1 ? candidate : null));
        }

        foreach (var definition in expected.ForeignKeys)
        {
            string? exact = null;
            string? candidate = null;
            var count = 0;
            foreach (var actual in table.ForeignKeys)
            {
                if (SqliteCatalogEquivalence.ForeignKeyMatches(snapshot, actual,
                        definition.PrincipalTable, definition.Columns, definition.PrincipalColumns,
                        definition.OnUpdate, definition.OnDelete))
                {
                    Observe(definition.Name, actual.Name, ref exact, ref candidate, ref count);
                }
            }

            Add(SafeMigrationDatabaseObjectKind.ForeignKey, definition.Name,
                exact ?? (count == 1 ? candidate : null));
        }

        return bindings;

        void Add(SafeMigrationDatabaseObjectKind kind, string authoredName, string? physicalName)
        {
            if (!string.IsNullOrWhiteSpace(physicalName))
            {
                (bindings ??= []).Add(new SafeMigrationConstraintIdentityBinding(
                    kind, authoredName, physicalName));
            }
        }
    }

    /// <summary>
    /// Retains exact-name priority without treating an ambiguous semantic match as a unique identity.
    /// </summary>
    /// <param name="authoredName">The authored alias being resolved.</param>
    /// <param name="actualName">The independently matched catalog name, or null for an anonymous constraint.</param>
    /// <param name="exact">The matching exact-name witness, if present.</param>
    /// <param name="candidate">The last matching semantic witness.</param>
    /// <param name="count">The complete number of matching witnesses, including anonymous members.</param>
    private static void Observe(
        string authoredName,
        string? actualName,
        ref string? exact,
        ref string? candidate,
        ref int count
    )
    {
        count++;
        candidate = actualName;
        if (actualName is not null && s_identifierComparer.Equals(authoredName, actualName))
        {
            exact = actualName;
        }
    }
}
