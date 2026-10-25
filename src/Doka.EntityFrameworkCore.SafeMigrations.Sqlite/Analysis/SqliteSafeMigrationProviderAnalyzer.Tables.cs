namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <summary>Classifies the only SQLite schema identity that is intrinsically present.</summary>
    private static SafeMigrationProviderAnalysis AnalyzeEnsureSchema(
        EnsureSchemaIntent intent
    ) => s_identifierComparer.Equals(intent.Name, "main")
        ? Matching(postconditionSatisfied: true)
        : Unsupported("database_qualifier_mismatch");

    /// <summary>Compares a table against its declared transitional constraint contract.</summary>
    private SafeMigrationProviderAnalysis AnalyzeEnsureTable(
        SqliteCatalogSnapshot snapshot,
        EnsureTableIntent intent,
        SafeMigrationOperation operation,
        SqliteRebuildArtifactContract rebuildArtifacts
    )
    {
        var unsupported = TableUnsupportedFeature(intent.Definition);
        if (unsupported is not null)
        {
            return InvariantUnsupported(unsupported);
        }

        if (!snapshot.Tables.TryGetValue(intent.Definition.Table, out var table))
        {
            return HasMissingDeclaredExpressionColumns(intent.Definition)
                ? InvariantUnsupported("expression_column_reference_unproven")
                : Missing(postconditionSatisfied: false);
        }

        if (intent.Mode == SafeMigrationTableMode.ConvergenceContainer)
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: table.Name);
        }

        if (HasMissingDeclaredExpressionColumns(intent.Definition))
        {
            return InvariantUnsupported("expression_column_reference_unproven");
        }

        var matching = TableMatches(
            snapshot, table, intent.Definition, rebuildArtifacts.GetTableConstraintTransitions(operation));

        return matching
            ? Matching(postconditionSatisfied: true, matchedObjectName: table.Name,
                tableConstraintIdentities: CaptureTableConstraintIdentities(snapshot, table, intent.Definition))
            : Different("table_definition", "matching", "different", matchedObjectName: table.Name);
    }

    /// <summary>Classifies a table drop without bypassing retained incoming dependencies.</summary>
    private static SafeMigrationProviderAnalysis AnalyzeDropTable(
        SqliteCatalogSnapshot snapshot,
        DropTableIntent intent,
        SafeMigrationOperation operation,
        SqliteRebuildArtifactContract rebuildArtifacts
    )
    {
        if (rebuildArtifacts.HasUnresolvedIncomingForeignKey(snapshot, intent.Table, operation))
        {
            return Unsupported("table_drop_foreign_key_dependency");
        }

        return snapshot.Tables.ContainsKey(intent.Table)
            ? Matching(postconditionSatisfied: false, matchedObjectName: intent.Table)
            : Missing(postconditionSatisfied: true);
    }

    /// <summary>Classifies a table rename against both source and target identities.</summary>
    private static SafeMigrationProviderAnalysis AnalyzeRenameTable(
        SqliteCatalogSnapshot snapshot,
        RenameTableIntent intent
    )
    {
        if (intent.NewSchema is not null
            && !IsMain(intent.NewSchema))
        {
            return Unsupported("database_qualifier_mismatch");
        }

        var targetName = intent.NewName ?? intent.Name;
        var sourceExists = snapshot.Tables.ContainsKey(intent.Name);
        var targetExists = snapshot.Tables.ContainsKey(targetName);
        var targetOccupied = targetExists;
        if (!targetOccupied)
        {
            foreach (var schemaObject in snapshot.OtherObjects)
            {
                if (schemaObject.Type is "view" or "index"
                    && s_identifierComparer.Equals(schemaObject.Name, targetName))
                {
                    targetOccupied = true;

                    break;
                }
            }
        }

        var analysis = sourceExists switch
        {
            true when !targetOccupied => snapshot.LegacyAlterTableEnabled
                ? Unsupported("legacy_alter_table")
                : Matching(postconditionSatisfied: false, matchedObjectName: intent.Name),
            false => Missing(postconditionSatisfied: true),
            _ => Different("rename_target", "missing", "existing")
        };

        // WHY: Missing source also describes an idempotent replay. An earlier
        // accepted creation may establish the source, but cannot prove that its
        // destination is absent from the immutable live catalog. Tables, views
        // and indexes occupy that namespace; an unrelated trigger does not.
        return analysis.WithRenameTargetExists(targetOccupied,
            applyUnsupportedCode: snapshot.LegacyAlterTableEnabled ? "legacy_alter_table" : null);
    }

    /// <summary>Compares physical table facets while respecting explicitly authored constraint transitions.</summary>
    private bool TableMatches(
        SqliteCatalogSnapshot snapshot,
        SqliteTableSnapshot table,
        ExpectedTableDefinition expected,
        SafeMigrationExpectedTableConstraints? transitions
    )
    {
        if (expected.Comment is not null
            || table.Columns.Count != expected.Columns.Count
            || !expected.Columns.All(column => table.Columns.TryGetValue(column.Name, out var actual)
                && ColumnMatches(actual, column)))
        {
            return false;
        }

        var primaryKeys = transitions?.AllowedPrimaryKeys
            ?? (expected.PrimaryKey is null ? [] : [expected.PrimaryKey]);

        var primaryKeyMayBeAbsent = transitions?.PrimaryKeyMayBeAbsent ?? expected.PrimaryKey is null;
        var allowedUnique = transitions?.AllowedUniqueConstraints ?? expected.UniqueConstraints;
        var requiredUnique = transitions?.RequiredUniqueConstraints ?? expected.UniqueConstraints;
        var allowedChecks = transitions?.AllowedCheckConstraints ?? expected.CheckConstraints;
        var requiredChecks = transitions?.RequiredCheckConstraints ?? expected.CheckConstraints;
        var allowedForeignKeys = transitions?.AllowedForeignKeys ?? expected.ForeignKeys;
        var requiredForeignKeys = transitions?.RequiredForeignKeys ?? expected.ForeignKeys;

        // WHY: EF can append standalone constraints to a strict creator.
        // Only authored transitional shapes are optional here; untouched
        // constraints and every column remain exact. Standalone final-writer
        // postconditions independently require the terminal constraint.
        if ((table.PrimaryKeyColumns.Count == 0
                ? !primaryKeyMayBeAbsent
                : !primaryKeys.Any(definition => SqliteCatalogEquivalence.KeyMatchesDeclaredColumns(
                    table, table.PrimaryKeyKeys, definition.Columns, requireDefaultSortOrder: true)))
            || !SqliteConstraintShapeCapacity.Matches(
                table.UniqueConstraints, allowedUnique, requiredUnique, UniqueMatches, coalesceRequiredShapes: true)
            || !SqliteConstraintShapeCapacity.Matches(
                table.ForeignKeys, allowedForeignKeys, requiredForeignKeys, ForeignKeyMatches)
            || !SqliteConstraintShapeCapacity.Matches(table.Checks, allowedChecks, requiredChecks, CheckMatches))
        {
            return false;
        }

        return true;

        bool UniqueMatches(SqliteUniqueSnapshot actual, ExpectedUniqueConstraintDefinition definition)
            => SqliteCatalogEquivalence.KeyMatchesDeclaredColumns(
                table, actual.Keys, definition.Columns, requireDefaultSortOrder: true);

        bool ForeignKeyMatches(SqliteForeignKeySnapshot actual, ExpectedForeignKeyDefinition definition)
            => SqliteCatalogEquivalence.ForeignKeyMatches(
                snapshot, actual, definition.PrincipalTable, definition.Columns, definition.PrincipalColumns,
                definition.OnUpdate, definition.OnDelete);

        bool CheckMatches(SqliteCheckSnapshot actual, ExpectedCheckConstraintDefinition definition)
            => RenderCheck(definition) is { } sql && ExpressionMatches(actual.Expression, sql, definition.Expression);
    }

    /// <summary>Identifies unsupported table or nested column/check contracts before data probes.</summary>
    private string? TableUnsupportedFeature(
        ExpectedTableDefinition definition
    )
    {
        if (definition.Comment is not null)
        {
            return "table_comments";
        }

        foreach (var column in definition.Columns)
        {
            var unsupported = ColumnUnsupportedFeature(column);
            if (unsupported is not null)
            {
                return unsupported;
            }
        }

        foreach (var check in definition.CheckConstraints)
        {
            if (!CapturedSourceDialectCompatible(check.CapturedSql))
            {
                return "check_expression";
            }

            if (check.Expression is not null)
            {
                var unsupported = _expressionRenderer.GetUnsupportedFeature(check.Expression);
                if (unsupported is not null)
                {
                    return unsupported;
                }
            }
        }

        return null;
    }
}
