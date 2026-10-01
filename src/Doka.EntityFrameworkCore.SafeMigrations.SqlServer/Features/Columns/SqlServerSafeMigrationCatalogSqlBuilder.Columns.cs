namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private string? GetUnsupportedColumnFeature(ExpectedColumnDefinition definition)
    {
        if (definition.Collation is { } collation
            && (collation.Schema is not null || _expressionRenderer.GetUnsupportedFeature(
                new SafeMigrationSqlCollateExpression(SafeMigrationSql.Identifier(definition.Name),
                    collation.Name)) is not null))
        {
            return "column_collation_unproven";
        }

        if (!TryGetIdentity(definition, out var isIdentity, out var seed, out var increment))
        {
            return "provider_column_annotation";
        }

        if (definition.Comment is not null
            || definition.ComputedColumnSql is not null
            || definition.ComputedExpression is not null)
        {
            // WHY: computed columns and extended-property comments need
            // separately verified SQL Server object contracts.
            return "column_unproven_facet";
        }

        var mapping = FindColumnTypeMapping(definition);
        if (mapping is null || !TryParseStoreType(definition.StoreType ?? mapping.StoreType, out var type))
        {
            return "column_type_mapping";
        }

        if (definition.Collation is not null && !type.IsCharacter)
        {
            return "column_collation_unproven";
        }

        // WHY: Recognizing an EF identity annotation does not prove SQL Server
        // can create it. Identity is restricted to integral built-in columns,
        // cannot be nullable, and cannot coexist with any authored DEFAULT.
        if (isIdentity && (definition.IsNullable || definition.IsRowVersion
            || definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None
            || type.Name is not ("tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric")
            || type.Name is "decimal" or "numeric" && type.Scale != 0
            || !IdentityValueFitsType(seed, type) || increment == 0 || !IdentityValueFitsType(increment, type)))
        {
            return "identity_definition_unproven";
        }

        return GetUnsupportedDefaultFeature(definition, definition.StoreType ?? mapping.StoreType);
    }

    private SqlServerSafeMigrationRuntimePlan BuildEnsureColumn(EnsureColumnIntent intent)
    {
        var table = TableExists(intent.Table, intent.Schema);
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Definition.Name);
        var matching = ColumnMatches(intent.Table, intent.Schema, intent.Definition);
        var identityAvailable = IdentitySlotIsAvailable(intent);
        var layoutFailure = BuildColumnAdditionLayoutFailureExpression(intent);
        var rowCapacity = BuildColumnAdditionRowCapacityPredicate(intent);
        var identityFailure = identityAvailable == "1 = 1"
            ? "CONVERT(nvarchar(128), NULL)"
            : $"CASE WHEN {table} AND NOT ({identityAvailable}) "
                + "THEN N'identity_slot_occupied' ELSE NULL END";

        var identityGate = identityAvailable == "1 = 1" ? string.Empty
            : $"WHEN NOT ({identityAvailable}) THEN N'prerequisite_missing' ";

        var addIsSafe = SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition);
        var rowBindingRequired = !addIsSafe || rowCapacity is not null;
        // WHY: Nullable variables without a materialized default use only
        // metadata. Do not build unused row SQL or a permission predicate for
        // that common path; row-bound plans retain the same explicit gates.
        var canReadRows = rowBindingRequired
            ? $"COALESCE(HAS_PERMS_BY_NAME({Literal(QualifiedTable(intent.Table, intent.Schema))}, "
                + "N'OBJECT', N'SELECT'), 0) = 1"
            : "1 = 1";

        // WHY: SQL Server checks SELECT permission while binding even a row
        // subquery in a skipped CASE arm. An existing named column or a false
        // capacity predicate cannot authorize that row-bound statement.
        var metadataFailure = !rowBindingRequired ? identityFailure
            : $"CASE WHEN {table} AND NOT ({identityAvailable}) THEN N'identity_slot_occupied' "
                + $"WHEN {table} AND NOT ({canReadRows}) "
                + "THEN N'column_row_layout_unproven' ELSE NULL END";

        var hasRows = rowBindingRequired
            ? $"EXISTS (SELECT TOP (1) 1 FROM {QualifiedTable(intent.Table, intent.Schema)})"
            : "1 = 0";

        var unsafeAdd = addIsSafe ? "1 = 0" : hasRows;
        if (rowCapacity is not null)
        {
            unsafeAdd = $"({unsafeAdd} OR (({rowCapacity}) AND {hasRows}))";
        }

        var classification = rowCapacity is null ? identityFailure
            : $"CASE WHEN {table} AND NOT ({identityAvailable}) THEN N'identity_slot_occupied' "
                + $"WHEN {table} AND NOT {exists} AND ({rowCapacity}) AND {hasRows} "
                + "THEN N'column_row_layout_unproven' ELSE NULL END";

        // WHY: The matching predicate dominates plan text. One interpolation
        // avoids copying it into an intermediate CASE fragment for every
        // operation, while all catalog and data decisions remain unchanged.
        var state = $"""
            CASE WHEN NOT {table} THEN N'prerequisite_missing'
            {identityGate}WHEN NOT {exists} AND {unsafeAdd} THEN N'data_blocked'
            WHEN NOT {exists} THEN N'missing'
            WHEN {matching} THEN N'matching' ELSE N'different' END
            """;

        return Plan(state, Bit(matching)) with
        {
            RequiresDelayedBinding = rowBindingRequired,
            ColumnLayoutFailureExpression = layoutFailure,
            ClassificationCodeExpression = classification,
            PrerequisiteFailureCodeExpression = metadataFailure,
            StateEvaluationGuardExpression = rowBindingRequired ? Bit(canReadRows) : "1",
            StateEvaluationGuardFailureExpression = rowBindingRequired ? "N'unsupported'" : null,
            PostApplySql = intent.Definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None
                ? null
                : BuildDefaultStampSql(intent.Table, intent.Schema, intent.Definition),
        };
    }

    private SqlServerSafeMigrationRuntimePlan BuildDropColumn(DropColumnIntent intent)
    {
        var table = TableExists(intent.Table, intent.Schema);
        var occupied = ObjectExists(intent.Table, intent.Schema);
        var column = ColumnExists(intent.Table, intent.Schema, intent.Name);
        var dependent = ColumnHasDependencies(intent.Table, intent.Schema, intent.Name);

        return Plan(
            $"CASE WHEN NOT {occupied} THEN N'missing' WHEN NOT {table} THEN N'different' "
            + $"WHEN NOT {column} THEN N'missing' WHEN {dependent} THEN N'different' "
            + "ELSE N'matching' END",
            Bit($"NOT {column}"));
    }

    private SqlServerSafeMigrationRuntimePlan BuildRenameColumn(RenameColumnIntent intent)
    {
        var source = ColumnExists(intent.Table, intent.Schema, intent.Name);
        var target = ColumnExists(intent.Table, intent.Schema, intent.NewName);
        var tableId = TableId(intent.Table, intent.Schema);
        var columnId = $"(SELECT c.column_id FROM sys.columns c WHERE c.object_id = {tableId} "
            + $"AND c.name = {Literal(intent.Name)})";

        // WHY: sp_rename retains column/index/FK catalog identities, so physical
        // keys and statistics do not prevent a rename. Textual expressions are
        // different: their references are not automatically rewritten safely.
        var dependent = "EXISTS (SELECT 1 FROM sys.sql_expression_dependencies d "
            + $"WHERE d.referenced_id = {tableId} "
            + $"AND (d.referenced_minor_id = 0 OR d.referenced_minor_id = {columnId}) "
            + "AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.object_id = d.referencing_id))";

        return Plan(
            $"CASE WHEN NOT {source} THEN N'missing' WHEN {target} OR {dependent} THEN N'different' "
            + "ELSE N'matching' END",
            Bit($"NOT {source} AND {target}"));
    }

    private SqlServerSafeMigrationRuntimePlan BuildAlterColumn(AlterColumnIntent intent)
    {
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Definition.Name);
        var matching = ColumnMatches(intent.Table, intent.Schema, intent.Definition);
        if (intent.OldDefinition is null
            || !IsSupportedTextAlter(intent.OldDefinition, intent.Definition, out var targetLength))
        {
            return Plan(
                $"CASE WHEN NOT {exists} THEN N'prerequisite_missing' "
                + $"WHEN {matching} THEN N'matching' ELSE N'different' END",
                Bit(matching));
        }

        var oldMatches = ColumnMatches(intent.Table, intent.Schema, intent.OldDefinition);
        var dependencies = ColumnHasDependencies(intent.Table, intent.Schema, intent.Definition.Name,
            allowAutomaticStatistics: true);
        var column = Delimited(intent.Definition.Name);
        var overflow = targetLength == -1
            ? "1 = 0"
            : $"DATALENGTH({column}) > {targetLength.ToString(CultureInfo.InvariantCulture)}";

        var nullRows = intent.Definition.IsNullable ? "1 = 0" : $"{column} IS NULL";
        var unsafeRows = $"EXISTS (SELECT TOP (1) 1 FROM {QualifiedTable(intent.Table, intent.Schema)} "
            + $"WHERE {overflow} OR {nullRows})";

        var safeRepair = $"{oldMatches} AND NOT ({dependencies}) AND NOT {unsafeRows}";

        // WHY: SQL Server rejects ALTER COLUMN when a dependent object exists.
        // DATALENGTH proves the stored byte width, including trailing spaces,
        // before a bounded varchar/nvarchar contraction or NOT NULL change.

        return Plan(
            $"CASE WHEN NOT {exists} THEN N'prerequisite_missing' "
            + $"WHEN {matching} THEN N'matching' "
            + $"WHEN {oldMatches} AND NOT ({dependencies}) AND {unsafeRows} "
            + "THEN N'data_blocked' ELSE N'different' END",
            Bit(matching), SafeMigrationRepairCapability.Safe, Bit(safeRepair)) with
        {
            RequiresDelayedBinding = true,
            MayRequireNullabilityDataProof = !intent.Definition.IsNullable && intent.OldDefinition.IsNullable,
        };
    }

    private bool IsSupportedTextAlter(
        ExpectedColumnDefinition oldDefinition,
        ExpectedColumnDefinition targetDefinition,
        out short targetLength
    )
    {
        targetLength = 0;
        if (!TryGetColumnType(oldDefinition, out var oldType)
            || !TryGetColumnType(targetDefinition, out var targetType)
            || oldType.Name is not ("varchar" or "nvarchar")
            || oldType.Name != targetType.Name
            || oldType.Length is null
            || targetType.Length is null
            || oldDefinition.ClrType != targetDefinition.ClrType
            || oldDefinition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None
            || targetDefinition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None
            || !Equals(oldDefinition.Collation, targetDefinition.Collation)
            || oldDefinition.ProviderAnnotations.Count != 0
            || targetDefinition.ProviderAnnotations.Count != 0
            || oldDefinition.ComputedColumnSql is not null
            || targetDefinition.ComputedColumnSql is not null
            || oldDefinition.ComputedExpression is not null
            || targetDefinition.ComputedExpression is not null
            || oldDefinition.Comment is not null
            || targetDefinition.Comment is not null)
        {
            return false;
        }

        targetLength = targetType.Length.Value;

        return true;
    }

    private string ColumnMatches(
        string table,
        string? schema,
        ExpectedColumnDefinition definition
    )
    {
        if (!TryGetColumnType(definition, out var type))
        {
            return "1 = 0";
        }

        if (!TryGetIdentity(definition, out var isIdentity, out var seed, out var increment))
        {
            return "1 = 0";
        }

        var identityClause = string.Empty;
        if (isIdentity)
        {
            var seedLiteral = seed.ToString(CultureInfo.InvariantCulture);
            var incrementLiteral = increment.ToString(CultureInfo.InvariantCulture);

            identityClause = " AND EXISTS (SELECT 1 FROM sys.identity_columns identity_column "
                + "WHERE identity_column.object_id = c.object_id "
                + "AND identity_column.column_id = c.column_id "
                + "AND identity_column.is_not_for_replication = 0 "
                + $"AND TRY_CONVERT(decimal(38,0), identity_column.seed_value) = {seedLiteral} "
                + $"AND TRY_CONVERT(decimal(38,0), identity_column.increment_value) = {incrementLiteral})";
        }

        var lengthClause = type.Length is { } length
            ? $" AND c.max_length = {length.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        var precisionClause = type.Precision is { } precision
            ? $" AND c.precision = {precision.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        var scaleClause = type.Scale is { } scale
            ? $" AND c.scale = {scale.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        var collationClause = string.Empty;
        if (type.IsCharacter)
        {
            var collation = definition.Collation is null
                ? "CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))"
                : Literal(definition.Collation.Name);

            collationClause = $" AND c.collation_name = {collation}";
        }

        // WHY: This predicate is reused by state, postcondition, and table
        // matching. Render it once without a list, joined copy, and another
        // intermediate copy of every invariant physical facet.

        return $"""
            EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = {TableId(table, schema)} AND c.name = {Literal(definition.Name)}
            AND ty.name = {Literal(type.Name)} AND ty.is_user_defined = 0 AND ty.is_assembly_type = 0
            AND c.is_nullable = {(definition.IsNullable ? "1" : "0")} AND c.is_identity = {(isIdentity ? "1" : "0")}
            AND c.is_computed = 0 AND c.is_sparse = 0 AND c.is_column_set = 0 AND c.is_rowguidcol = 0
            AND c.is_filestream = 0 AND c.is_hidden = 0 AND c.generated_always_type = 0
            AND {DefaultMatches(table, schema, definition)}{identityClause}
            {lengthClause}{precisionClause}{scaleClause}{collationClause})
            """;
    }

    /// <summary>Reads only the supported immutable EF identity annotations.</summary>
    internal static bool TryGetIdentity(
        ExpectedColumnDefinition definition,
        out bool isIdentity,
        out long seed,
        out long increment
    )
    {
        isIdentity = false;
        seed = 1;
        increment = 1;

        foreach (var annotation in definition.ProviderAnnotations)
        {
            switch (annotation.Name)
            {
                case "SqlServer:ValueGenerationStrategy":
                    var strategy = annotation.Value?.ToString();
                    if (strategy is "IdentityColumn")
                    {
                        isIdentity = true;
                    }
                    else if (strategy is not ("None" or null))
                    {
                        return false;
                    }

                    break;
                case "SqlServer:Identity":
                    if (annotation.Value is not string identity)
                    {
                        return false;
                    }

                    var parts = identity.Split(',');
                    if (parts.Length != 2
                        || !long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out seed)
                        || !long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out increment))
                    {
                        return false;
                    }

                    isIdentity = true;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool IdentityValueFitsType(
        long value,
        SqlServerColumnType type
    )
    {
        if (type.Name is "decimal" or "numeric" && type.Precision is < 19)
        {
            var limit = 1L;
            for (var digit = 0; digit < type.Precision; digit++)
            {
                limit *= 10;
            }

            return value > -limit && value < limit;
        }

        return type.Name switch
        {
            "tinyint" => value is >= byte.MinValue and <= byte.MaxValue,
            "smallint" => value is >= short.MinValue and <= short.MaxValue,
            "int" => value is >= int.MinValue and <= int.MaxValue,
            "bigint" or "decimal" or "numeric" => true,
            _ => false,
        };
    }

    private string IdentitySlotIsAvailable(
        EnsureColumnIntent intent
    )
    {
        if (!TryGetIdentity(intent.Definition, out var isIdentity, out _, out _) || !isIdentity)
        {
            return "1 = 1";
        }

        // WHY: A named existing column must reach the normal exact-match check;
        // an absent column cannot consume a second table-wide identity slot.

        return $"({ColumnExists(intent.Table, intent.Schema, intent.Definition.Name)} OR NOT EXISTS "
            + "(SELECT 1 FROM sys.identity_columns identity_slot "
            + $"WHERE identity_slot.object_id = {TableId(intent.Table, intent.Schema)}))";
    }

    private string DefaultMatches(
        string table,
        string? schema,
        ExpectedColumnDefinition definition
    )
    {
        if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None)
        {
            // WHY: sys.columns exposes both inline and stand-alone bound
            // defaults. Its zero identity proves absence directly, avoiding
            // another catalog join while also rejecting legacy bound defaults.
            return "c.default_object_id = 0";
        }

        var query = "SELECT 1 FROM sys.default_constraints dc "
            + "JOIN sys.columns default_column ON default_column.object_id = dc.parent_object_id "
            + "AND default_column.column_id = dc.parent_column_id "
            + $"WHERE dc.parent_object_id = {TableId(table, schema)} "
            + $"AND default_column.name = {Literal(definition.Name)}";

        var fingerprint = ContractFingerprint("default", schema, table, definition.Name,
            DefaultExpression(definition));

        var stamped = "EXISTS (" + query
            + " AND EXISTS (SELECT 1 FROM sys.extended_properties ep "
            + "WHERE ep.class = 1 AND ep.major_id = dc.object_id AND ep.minor_id = 0 "
            + "AND ep.name = N'Doka:SafeMigrations:Contract' "
            + $"AND CONVERT(nvarchar(64), ep.value) = {Literal(fingerprint)} "
            + "COLLATE Latin1_General_100_BIN2))";

        return stamped;
    }

    private string BuildDefaultStampSql(
        string table,
        string? schema,
        ExpectedColumnDefinition definition
    )
    {
        var fingerprint = ContractFingerprint("default", schema, table, definition.Name,
            DefaultExpression(definition));

        var variable = "@doka_default_" + fingerprint;

        return $"DECLARE {variable} sysname = (SELECT dc.name FROM sys.default_constraints dc "
            + "JOIN sys.columns c ON c.object_id = dc.parent_object_id "
            + "AND c.column_id = dc.parent_column_id "
            + $"WHERE dc.parent_object_id = {TableId(table, schema)} "
            + $"AND c.name = {Literal(definition.Name)}); "
            + $"IF {variable} IS NULL THROW 51004, N'SafeMigrations default contract was not created', 1; "
            + "EXEC sys.sp_addextendedproperty "
            + "@name = N'Doka:SafeMigrations:Contract', "
            + $"@value = {Literal(fingerprint)}, "
            + "@level0type = N'SCHEMA', "
            + $"@level0name = {Literal(EffectiveSchema(schema))}, "
            + "@level1type = N'TABLE', "
            + $"@level1name = {Literal(table)}, "
            + "@level2type = N'CONSTRAINT', "
            + $"@level2name = {variable}";
    }

    private string DefaultExpression(ExpectedColumnDefinition definition)
        => definition.DefaultValue.Kind switch
        {
            SafeMigrationDefaultValueKind.Literal => RenderValueLiteral(
                definition.DefaultValue.LiteralValue,
                definition.StoreType ?? FindColumnTypeMapping(definition)?.StoreType
                    ?? throw new NotSupportedException("SQL Server has no default-value type mapping.")),
            SafeMigrationDefaultValueKind.Sql when definition.DefaultValue.StructuredExpression is { } expression
                => _expressionRenderer.Render(expression),
            SafeMigrationDefaultValueKind.Sql => definition.DefaultValue.SqlExpression
                ?? throw new InvalidOperationException("A SQL default has no expression."),
            _ => throw new InvalidOperationException("No default expression exists."),
        };

    private string ColumnStoreTypeMatches(
        string table,
        string? schema,
        string column,
        string storeType
    )
    {
        if (!TryParseStoreType(storeType, out var type))
        {
            return "1 = 0";
        }

        var conditions = new List<string>
        {
            $"ty.name = {Literal(type.Name)}",
            "ty.is_user_defined = 0",
            "ty.is_assembly_type = 0",
        };

        if (type.Length is { } length)
        {
            conditions.Add($"c.max_length = {length.ToString(CultureInfo.InvariantCulture)}");
        }

        if (type.Precision is { } precision)
        {
            conditions.Add($"c.precision = {precision.ToString(CultureInfo.InvariantCulture)}");
        }

        if (type.Scale is { } scale)
        {
            conditions.Add($"c.scale = {scale.ToString(CultureInfo.InvariantCulture)}");
        }

        return "EXISTS (SELECT 1 FROM sys.columns c "
            + "JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
            + $"WHERE c.object_id = {TableId(table, schema)} "
            + $"AND c.name = {Literal(column)} AND {string.Join(" AND ", conditions)})";
    }

    private string ColumnHasDependencies(
        string table,
        string? schema,
        string column,
        bool allowAutomaticStatistics = false
    )
    {
        var id = TableId(table, schema);
        var columnId = $"(SELECT c.column_id FROM sys.columns c WHERE c.object_id = {id} "
            + $"AND c.name = {Literal(column)})";

        // WHY: ALTER COLUMN removes optimizer-created statistics itself. Its
        // guarded row probe can create them during compilation; only authored
        // statistics must prevent a repair that preserves caller-owned objects.
        var statistics = allowAutomaticStatistics
            ? "JOIN sys.stats s ON s.object_id = sc.object_id AND s.stats_id = sc.stats_id "
                + "AND s.auto_created = 0 "
            : string.Empty;

        return $"EXISTS (SELECT 1 FROM sys.tables t WHERE t.object_id = {id} "
            + "AND (t.temporal_type <> 0 OR t.is_filetable = 1)) "
            + $"OR EXISTS (SELECT 1 FROM sys.default_constraints dc WHERE dc.parent_object_id = {id} "
            + $"AND dc.parent_column_id = {columnId}) "
            + $"OR EXISTS (SELECT 1 FROM sys.index_columns ic WHERE ic.object_id = {id} "
            + $"AND ic.column_id = {columnId}) "
            + $"OR EXISTS (SELECT 1 FROM sys.stats_columns sc {statistics}WHERE sc.object_id = {id} "
            + $"AND sc.column_id = {columnId}) "
            + $"OR EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc WHERE "
            + $"(fkc.parent_object_id = {id} AND fkc.parent_column_id = {columnId}) "
            + $"OR (fkc.referenced_object_id = {id} AND fkc.referenced_column_id = {columnId})) "
            + $"OR EXISTS (SELECT 1 FROM sys.sql_expression_dependencies d WHERE d.referenced_id = {id} "
            + $"AND d.referenced_minor_id = {columnId})";
    }
}
