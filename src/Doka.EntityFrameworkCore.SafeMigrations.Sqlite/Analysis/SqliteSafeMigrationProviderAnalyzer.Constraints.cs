namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <summary>Qualifies the declared primary key against physical membership and existing rows.</summary>
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
            // WHY: Same column names do not imply the equality or sort shape
            // emitted by a plain authored key. A false NoOp also lends an
            // invalid candidate-key certificate to subsequent FK projection.
            return SqliteCatalogEquivalence.KeyMatchesDeclaredColumns(
                table,
                table.PrimaryKeyKeys,
                intent.Definition.Columns,
                requireDefaultSortOrder: true)
                ? Matching(postconditionSatisfied: true, matchedObjectName: table.PrimaryKeyName)
                : Different("primary_key_key_semantics", "declared_column_collations_ascending", "different");
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

    /// <summary>Classifies removal of the declared primary-key identity.</summary>
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

    /// <summary>Qualifies a unique constraint against physical shape and nullable row semantics.</summary>
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
            if (!IdentifiersEqual(named.Columns, intent.Definition.Columns))
            {
                return Different("unique_columns", Join(intent.Definition.Columns), Join(named.Columns));
            }

            return SqliteCatalogEquivalence.KeyMatchesDeclaredColumns(
                table,
                named.Keys,
                intent.Definition.Columns,
                requireDefaultSortOrder: true)
                ? Matching(postconditionSatisfied: true, matchedObjectName: named.Name)
                : Different("unique_key_semantics", "declared_column_collations_ascending", "different");
        }

        var semantic = table.UniqueConstraints.FirstOrDefault(value =>
            SqliteCatalogEquivalence.KeyMatchesDeclaredColumns(
                table,
                value.Keys,
                intent.Definition.Columns,
                requireDefaultSortOrder: true));

        if (semantic is not null)
        {
            // WHY: A constraint symbol and its sqlite_autoindex are different
            // resources. DROP CONSTRAINT addresses only the named symbol.
            return Matching(postconditionSatisfied: true, matchedObjectName: semantic.Name);
        }

        if (table.UniqueConstraints.Any(value => IdentifiersEqual(value.Columns, intent.Definition.Columns)))
        {
            // WHY: An alias must not treat a different native key equality as
            // the plain column-default unique contract simply because columns
            // have the same spelling. Preserve live drift, never fake absence.
            return Different("unique_key_semantics", "declared_column_collations_ascending", "different");
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

    /// <summary>Classifies removal of a unique constraint using its verified physical identity.</summary>
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
            : Matching(postconditionSatisfied: false, matchedObjectName: constraint.Name);
    }

    /// <summary>Qualifies a check constraint against expression identity and retained rows.</summary>
    private SafeMigrationProviderAnalysis AnalyzeEnsureCheck(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsureCheckConstraintIntent intent,
        SqliteRebuildArtifactContract rebuildArtifacts
    )
    {
        var expectedSql = RenderCheck(intent.Definition);
        if (expectedSql is null)
        {
            // WHY: Catalog changes can satisfy table prerequisites, not an
            // expression the provider cannot represent.
            return InvariantUnsupported("check_expression");
        }

        if (!snapshot.Tables.TryGetValue(intent.Definition.Table, out var table))
        {
            return PrerequisiteMissing();
        }

        var named = table.Checks.FirstOrDefault(value =>
            value.Name is not null && s_identifierComparer.Equals(value.Name, intent.Definition.Name));

        if (named is not null)
        {
            if (!ExpressionMatches(named.Expression, expectedSql, intent.Definition.Expression))
            {
                return Different("check_expression", Bound(expectedSql), Bound(named.Expression));
            }

            return HasMissingLiveExpressionColumns(intent.Definition.Expression, table)
                ? PrerequisiteMissing()
                : Matching(postconditionSatisfied: true, matchedObjectName: named.Name);
        }

        var semantic = table.Checks.FirstOrDefault(value => ExpressionMatches(
            value.Expression,
            expectedSql,
            intent.Definition.Expression));

        if (semantic is not null)
        {
            return HasMissingLiveExpressionColumns(intent.Definition.Expression, table)
                ? PrerequisiteMissing()
                : Matching(postconditionSatisfied: true, matchedObjectName: semantic.Name);
        }

        if (HasMissingLiveExpressionColumns(intent.Definition.Expression, table)
            || rebuildArtifacts.HasUnmaterializedReferencedColumns(snapshot, intent.Definition.Table, expectedSql))
        {
            return PrerequisiteMissing();
        }

        return HasCheckViolations(connection, transaction, intent.Definition.Table, expectedSql)
            ? DataBlocked("check_constraint_violated")
            : Missing(postconditionSatisfied: false);
    }

    /// <summary>Classifies removal of a named check without assuming an unverified alias.</summary>
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

    /// <summary>Qualifies foreign-key shape, principal membership and existing dependent rows.</summary>
    private static SafeMigrationProviderAnalysis AnalyzeEnsureForeignKey(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsureForeignKeyIntent intent
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
            // WHY: The immutable snapshot cannot prove a missing column's row
            // values. Only Core's ordered accepted ADD certificate can establish
            // NULL preservation; target-model nullability or a future ADD cannot.
            return PrerequisiteMissing();
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

    /// <summary>Classifies removal of a foreign key using its verified constraint identity.</summary>
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

    /// <summary>Renders a structured or source-compatible check expression for comparison.</summary>
    private string? RenderCheck(
        ExpectedCheckConstraintDefinition definition
    )
    {
        if (!CapturedSourceDialectCompatible(definition.CapturedSql))
        {
            return null;
        }

        if (definition.Expression is not null)
        {
            return _expressionRenderer.GetUnsupportedFeature(definition.Expression) is null
                ? definition.CapturedSql ?? _expressionRenderer.Render(definition.Expression)
                : null;
        }

        return definition.Sql;
    }

    /// <summary>Compares native SQL only within an explicitly structured expected expression contract.</summary>
    /// <param name="actual">The expression captured from the live SQLite catalog.</param>
    /// <param name="renderedExpected">The captured native source, or rendered explicit expected expression.</param>
    /// <param name="expectedExpression">The explicit structural proof, or null for an opaque contract.</param>
    /// <returns>Whether the expression has the same bounded structure and literal values.</returns>
    private static bool ExpressionMatches(
        string? actual,
        string? renderedExpected,
        SafeMigrationSqlExpression? expectedExpression
    )
    {
        if (expectedExpression is null)
        {
            return SqlEquivalent(actual, renderedExpected);
        }

        // WHY: Native rebuilds use the model's original identifier quoting and
        // parentheses. Only an explicit structured proof permits normalization;
        // numeric source tokens, literal bytes, and opaque authored text stay strict.
        return SqliteSqlNormalizer.EquivalentCapturedSource(actual, renderedExpected);
    }

    /// <summary>Retains structured catalog proofs for provider-owned EF model expressions during rebuilds.</summary>
    /// <param name="actual">The physical expression, or null when absent.</param>
    /// <param name="expectedSql">The expression declared in the provider-owned target model.</param>
    /// <returns>Whether exact text or a bounded model-expression proof establishes equivalence.</returns>
    private bool ModelExpressionMatches(
        string? actual,
        string? expectedSql
    )
    {
        if ((actual is not null && SqliteSqlIdentifierScanner.HasNonNativeWhitespace(actual))
            || (expectedSql is not null && SqliteSqlIdentifierScanner.HasNonNativeWhitespace(expectedSql))
            || !CapturedSourceDialectCompatible(expectedSql))
        {
            return false;
        }

        if (SqlEquivalent(actual, expectedSql))
        {
            return true;
        }

        if (expectedSql is null
            || !SafeMigrationSqlExpressionParser.TryParseCapturedSource(expectedSql, out var expression)
            || _expressionRenderer.GetUnsupportedFeature(expression) is not null)
        {
            return false;
        }

        // WHY: Model metadata is the same bounded provider input consumed by
        // generated helpers, not an explicitly opaque expected definition.
        // Rebuild ownership must survive the native model's original spelling.

        return ExpressionMatches(actual, expectedSql, expression);
    }
}
