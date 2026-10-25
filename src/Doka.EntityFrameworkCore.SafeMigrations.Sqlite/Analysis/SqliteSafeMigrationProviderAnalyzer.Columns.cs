namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <summary>Classifies column creation or convergence without inventing values for existing rows.</summary>
    private SafeMigrationProviderAnalysis AnalyzeEnsureColumn(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        EnsureColumnIntent intent
    )
    {
        var unsupported = ColumnUnsupportedFeature(intent.Definition);
        if (unsupported is not null)
        {
            return InvariantUnsupported(unsupported);
        }

        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return PrerequisiteMissing();
        }

        if (!table.Columns.TryGetValue(intent.Definition.Name, out var column))
        {
            if (HasMissingLiveExpressionColumns(intent.Definition.ComputedExpression, table))
            {
                return PrerequisiteMissing();
            }

            if (intent.Definition.IsStored == true)
            {
                return Unsupported("stored_generated_column_add");
            }

            if (intent.Definition is
                {
                    IsNullable: false,
                    DefaultValue.Kind: SafeMigrationDefaultValueKind.None,
                    ComputedColumnSql: null,
                    ComputedExpression: null
                }
                && TableHasRows(connection, transaction, intent.Table))
            {
                return DataBlocked("required_column_has_rows");
            }

            return Missing(postconditionSatisfied: false);
        }

        if (!ColumnMatches(column, intent.Definition))
        {
            // WHY: A prior accepted rename can establish a future operand.
            // Preserve the actual intrinsic difference for that complete
            // ordered proof rather than treating future live names as absent.
            return ColumnDifferent(column, intent.Definition);
        }

        return HasMissingLiveExpressionColumns(intent.Definition.ComputedExpression, table)
            ? PrerequisiteMissing()
            : Matching(postconditionSatisfied: true, matchedObjectName: column.Name);
    }

    /// <summary>Qualifies a column transition against physical shape and required row evidence.</summary>
    private SafeMigrationProviderAnalysis AnalyzeAlterColumn(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        AlterColumnIntent intent
    )
    {
        var unsupported = ColumnUnsupportedFeature(intent.Definition)
            ?? (intent.OldDefinition is null ? null : ColumnUnsupportedFeature(intent.OldDefinition));

        if (unsupported is not null)
        {
            return InvariantUnsupported(unsupported);
        }

        if (!snapshot.Tables.TryGetValue(intent.Table, out var table)
            || !table.Columns.TryGetValue(intent.Definition.Name, out var column))
        {
            return PrerequisiteMissing();
        }

        if (ColumnMatches(column, intent.Definition))
        {
            return Matching(postconditionSatisfied: true, matchedObjectName: column.Name);
        }

        if (!intent.Definition.IsNullable
            && column.IsNullable
            && ColumnHasNulls(connection, transaction, intent.Table, intent.Definition.Name))
        {
            return DataBlocked("column_contains_nulls");
        }

        var repairIsLossless = intent.OldDefinition is not null
            && ColumnMatches(column, intent.OldDefinition)
            && SqliteColumnRepairProof.HasLosslessShape(intent.OldDefinition, intent.Definition);

        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            repairIsLossless ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            repairIsLossless ? "classified_different_repairable" : "classified_different",
            repairIsLossless
                ? SafeMigrationOperationalImpact.TableRewritePossible
                : SafeMigrationOperationalImpact.NotApplicable,
            ColumnDifferences(column, intent.Definition))
        {
            MatchedObjectName = column.Name,
            RequiresLiveDataProof = column.IsNullable && !intent.Definition.IsNullable,
            CanReuseAfterUnrelatedColumnDrops = repairIsLossless,
        };
    }

    /// <summary>Classifies a column drop while preserving primary-key and unique-key dependencies.</summary>
    private static SafeMigrationProviderAnalysis AnalyzeDropColumn(
        SqliteCatalogSnapshot snapshot,
        DropColumnIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return Missing(postconditionSatisfied: true);
        }

        if (!table.Columns.ContainsKey(intent.Name))
        {
            return Missing(postconditionSatisfied: true);
        }

        var hasSurvivingGeneratedColumn = false;
        foreach (var candidate in table.Columns.Values)
        {
            if (!s_identifierComparer.Equals(candidate.Name, intent.Name)
                && (candidate.GeneratedSql is not null || candidate.HiddenKind is 2 or 3))
            {
                hasSurvivingGeneratedColumn = true;
                break;
            }
        }

        return Matching(
            postconditionSatisfied: false,
            matchedObjectName: intent.Name,
            dropLeavesNoGeneratedColumns: !hasSurvivingGeneratedColumn);
    }

    /// <summary>Classifies a column rename against its physical source and target.</summary>
    private static SafeMigrationProviderAnalysis AnalyzeRenameColumn(
        SqliteCatalogSnapshot snapshot,
        RenameColumnIntent intent
    )
    {
        if (!snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return Missing(postconditionSatisfied: true)
                .WithRenameTargetExists(false);
        }

        var sourceExists = table.Columns.ContainsKey(intent.Name);
        var targetExists = table.Columns.ContainsKey(intent.NewName);

        // WHY: Missing source is an idempotent no-op even when a later writer
        // removed the destination too. Its postcondition proves source absence,
        // not a destination definition; occupancy remains independent evidence.
        var analysis = sourceExists switch
        {
            true when !targetExists => Matching(postconditionSatisfied: false, matchedObjectName: intent.Name),
            false => Missing(postconditionSatisfied: true),
            _ => Different("rename_target", "missing", "existing")
        };

        return analysis.WithRenameTargetExists(targetExists);
    }

    /// <summary>Identifies column facets SQLite cannot represent or compare safely.</summary>
    private string? ColumnUnsupportedFeature(
        ExpectedColumnDefinition definition
    )
    {
        if (!CapturedSourceDialectCompatible(definition.CapturedComputedSql))
        {
            return "computed_expression";
        }

        if (definition.Comment is not null)
        {
            return "column_comments";
        }

        if (definition.IsRowVersion)
        {
            return "row_version";
        }

        if (definition.Collation?.Schema is not null)
        {
            return "schema_qualified_collation";
        }

        if (definition.ProviderAnnotations.Any(static annotation => annotation.Name != "Sqlite:Autoincrement"))
        {
            return "column_provider_annotation";
        }

        if (definition.DefaultValue.StructuredExpression is not null)
        {
            var unsupported = _expressionRenderer.GetUnsupportedFeature(definition.DefaultValue.StructuredExpression);

            if (unsupported is not null)
            {
                return unsupported;
            }
        }

        return definition.ComputedExpression is not null
            ? _expressionRenderer.GetUnsupportedFeature(definition.ComputedExpression)
            : null;
    }

    /// <summary>Compares every supported physical column facet with the expected definition.</summary>
    private bool ColumnMatches(
        SqliteColumnSnapshot actual,
        ExpectedColumnDefinition expected
    )
    {
        var expectedStoreType = ExpectedStoreType(expected);
        var expectedDefault = ExpectedDefaultSql(expected);
        var expectedComputed = expected.CapturedComputedSql
            ?? (expected.ComputedExpression is null
                ? expected.ComputedColumnSql
                : _expressionRenderer.Render(expected.ComputedExpression));

        var expectedAutoincrement = expected.ProviderAnnotations
                .FirstOrDefault(static annotation => annotation.Name == "Sqlite:Autoincrement")
                ?.Value as bool?
            ?? false;

        var unsupportedAnnotation = expected.ProviderAnnotations.Any(static annotation =>
            annotation.Name != "Sqlite:Autoincrement");

        return !unsupportedAnnotation
            && expected.Comment is null
            && !expected.IsRowVersion
            && s_identifierComparer.Equals(actual.Name, expected.Name)
            && StringComparer.Ordinal.Equals(
                NormalizeStoreType(actual.StoreType),
                NormalizeStoreType(expectedStoreType))
            && actual.IsNullable == expected.IsNullable
            && SqlEquivalent(actual.DefaultSql, expectedDefault)
            && ExpressionMatches(actual.GeneratedSql, expectedComputed, expected.ComputedExpression)
            && (expectedComputed is null || actual.IsStored == (expected.IsStored ?? false))
            && CollationMatches(actual.Collation, expected.Collation)
            && actual.AutoIncrement == expectedAutoincrement;
    }

    /// <summary>Checks a retained physical column against its target-model rebuild definition.</summary>
    private bool ModelColumnMatches(
        SqliteColumnSnapshot actual,
        IColumn expected
    )
    {
        var expectedDefault = expected.DefaultValueSql;
        if (expectedDefault is null
            && expected.TryGetDefaultValue(out var defaultValue))
        {
            expectedDefault = defaultValue is null
                ? "NULL"
                : expected.StoreTypeMapping.GenerateSqlLiteral(defaultValue);
        }

        var expectedAutoincrement = expected.FindAnnotation("Sqlite:Autoincrement")
                ?.Value as bool?
            ?? false;

        // WHY: Preserving existing generated source verbatim needs no grammar
        // expansion. Nonidentical source still requires the bounded dialect
        // and semantic proofs; other facets and CHECK contracts remain separate.
        return s_identifierComparer.Equals(actual.Name, expected.Name)
            && StringComparer.Ordinal.Equals(
                NormalizeStoreType(actual.StoreType),
                NormalizeStoreType(expected.StoreType))
            && actual.IsNullable == expected.IsNullable
            && ModelDefaultMatches(actual, expected, expectedDefault)
            && (StringComparer.Ordinal.Equals(actual.GeneratedSql, expected.ComputedColumnSql)
                || ModelExpressionMatches(actual.GeneratedSql, expected.ComputedColumnSql))
            && (expected.ComputedColumnSql is null || actual.IsStored == (expected.IsStored ?? false))
            && s_identifierComparer.Equals(actual.Collation ?? "BINARY", expected.Collation ?? "BINARY")
            && actual.AutoIncrement == expectedAutoincrement;
    }

    /// <summary>Compares the model default while preserving EF's explicit backfill compatibility.</summary>
    private bool ModelDefaultMatches(
        SqliteColumnSnapshot actual,
        IColumn expected,
        string? expectedDefault
    )
    {
        if (SqlEquivalent(actual.DefaultSql, expectedDefault))
        {
            return true;
        }

        if (expectedDefault is not null
            || expected.IsNullable
            || actual.DefaultSql is null
            || !TryCreateEfBackfillDefault(expected, out var backfillDefault))
        {
            return false;
        }

        // WHY: EF scaffolds a transient CLR-default literal when a required
        // column is added to a populated SQLite table. The model intentionally
        // omits that default, and a later rebuild is the point where EF removes it.

        return SqlEquivalent(actual.DefaultSql, backfillDefault);
    }

    /// <summary>Obtains only EF's supported implicit backfill literal for a required model column.</summary>
    private bool TryCreateEfBackfillDefault(
        IColumn expected,
        [NotNullWhen(true)] out string? sql
    )
    {
        var providerClrType = expected.StoreTypeMapping.Converter?.ProviderClrType ?? expected.StoreTypeMapping.ClrType;

        var underlyingType = Nullable.GetUnderlyingType(providerClrType) ?? providerClrType;
        object? value;
        if (underlyingType == typeof(string))
        {
            value = string.Empty;
        }
        else if (underlyingType == typeof(byte[]))
        {
            value = Array.Empty<byte>();
        }
        else
        {
            value = underlyingType.IsValueType ? Activator.CreateInstance(underlyingType) : null;
        }

        if (value is null)
        {
            sql = null;

            return false;
        }

        var literalMapping = _typeMappingSource.FindMapping(value.GetType(), expected.StoreType);
        if (literalMapping is null)
        {
            sql = null;

            return false;
        }

        sql = literalMapping.GenerateSqlLiteral(value);

        return true;
    }

    /// <summary>Resolves the authored store type or the provider's CLR type mapping.</summary>
    private string ExpectedStoreType(
        ExpectedColumnDefinition definition
    ) => definition.StoreType
        ?? _typeMappingSource.FindMapping(
                definition.ClrType,
                storeTypeName: null,
                keyOrIndex: false,
                unicode: definition.IsUnicode,
                size: definition.MaxLength,
                rowVersion: definition.IsRowVersion,
                fixedLength: definition.IsFixedLength,
                precision: definition.Precision,
                scale: definition.Scale)
            ?.StoreType
        ?? throw new NotSupportedException($"SQLite has no type mapping for column '{definition.Name}'.");

    /// <summary>Renders the exact supported default contract for catalog comparison.</summary>
    private string? ExpectedDefaultSql(
        ExpectedColumnDefinition definition
    ) => definition.DefaultValue.Kind switch
    {
        SafeMigrationDefaultValueKind.None => null,
        SafeMigrationDefaultValueKind.Sql when definition.DefaultValue.StructuredExpression is not null =>
            _expressionRenderer.Render(definition.DefaultValue.StructuredExpression),
        SafeMigrationDefaultValueKind.Sql => definition.DefaultValue.SqlExpression,
        SafeMigrationDefaultValueKind.Literal => GenerateLiteral(
            definition.DefaultValue.GetLiteralValue(),
            definition.StoreType),
        _ => throw new UnreachableException(),
    };

    /// <summary>Uses the provider type mapping to render a literal without ad hoc conversion.</summary>
    private string GenerateLiteral(
        object? value,
        string? storeType
    )
    {
        if (value is null)
        {
            return "NULL";
        }

        var mapping = _typeMappingSource.FindMapping(value.GetType(), storeType)
            ?? _typeMappingSource.FindMapping(value.GetType())
            ?? throw new NotSupportedException(
                $"SQLite has no type mapping for default value '{value.GetType().FullName}'.");

        return mapping.GenerateSqlLiteral(value);
    }

    /// <summary>Builds a differing-column result with all observable facet diagnostics.</summary>
    private SafeMigrationProviderAnalysis ColumnDifferent(
        SqliteColumnSnapshot actual,
        ExpectedColumnDefinition expected
    ) => new(
        SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "classified_different",
        SafeMigrationOperationalImpact.NotApplicable,
        ColumnDifferences(actual, expected))
    {
        MatchedObjectName = actual.Name,
    };

    /// <summary>Collects physical column differences without replacing provider-specific proof rules.</summary>
    private ReadOnlyCollection<SafeMigrationFacetDifference> ColumnDifferences(
        SqliteColumnSnapshot actual,
        ExpectedColumnDefinition expected
    )
    {
        var result = new List<SafeMigrationFacetDifference>();
        var expectedStoreType = ExpectedStoreType(expected);
        if (!StringComparer.Ordinal.Equals(NormalizeStoreType(actual.StoreType), NormalizeStoreType(expectedStoreType)))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_store_type",
                    Display(expectedStoreType),
                    Display(actual.StoreType)));
        }

        if (actual.IsNullable != expected.IsNullable)
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_nullability",
                    expected.IsNullable ? "nullable" : "required",
                    actual.IsNullable ? "nullable" : "required"));
        }

        var expectedDefault = ExpectedDefaultSql(expected);
        if (!SqlEquivalent(actual.DefaultSql, expectedDefault))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_default",
                    Bound(expectedDefault ?? "none"),
                    Bound(actual.DefaultSql ?? "none")));
        }

        var expectedComputed = expected.CapturedComputedSql
            ?? (expected.ComputedExpression is null
                ? expected.ComputedColumnSql
                : _expressionRenderer.Render(expected.ComputedExpression));

        if (!ExpressionMatches(actual.GeneratedSql, expectedComputed, expected.ComputedExpression))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_generated_expression",
                    Bound(expectedComputed ?? "none"),
                    Bound(actual.GeneratedSql ?? "none")));
        }

        if (expectedComputed is not null
            && actual.IsStored != (expected.IsStored ?? false))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_generated_storage",
                    expected.IsStored == true ? "stored" : "virtual",
                    actual.IsStored ? "stored" : "virtual"));
        }

        if (!CollationMatches(actual.Collation, expected.Collation))
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_collation",
                    Bound(expected.Collation?.Name ?? "BINARY"),
                    Bound(actual.Collation ?? "BINARY")));
        }

        var expectedAutoincrement = expected.ProviderAnnotations
                .FirstOrDefault(static annotation => annotation.Name == "Sqlite:Autoincrement")
                ?.Value as bool?
            ?? false;

        if (actual.AutoIncrement != expectedAutoincrement)
        {
            result.Add(
                new SafeMigrationFacetDifference(
                    "column_autoincrement",
                    expectedAutoincrement ? "enabled" : "disabled",
                    actual.AutoIncrement ? "enabled" : "disabled"));
        }

        if (result.Count == 0)
        {
            result.Add(new SafeMigrationFacetDifference("column_definition", "matching", "different"));
        }

        return result.AsReadOnly();
    }

    /// <summary>Compares an explicit collation or SQLite's implicit BINARY collation.</summary>
    private static bool CollationMatches(
        string? actual,
        SafeMigrationCollationIdentifier? expected
    )
    {
        if (expected?.Schema is not null)
        {
            return false;
        }

        var actualName = actual ?? "BINARY";
        var expectedName = expected?.Name ?? "BINARY";

        return s_identifierComparer.Equals(actualName, expectedName);
    }
}
