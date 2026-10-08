namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Builds bounded SQL Server catalog and guarded execution contracts.</summary>
internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Proves both permissions required by the protected expression-dependency view.</summary>
    internal const string ExpressionDependencyReadPermission =
        "COALESCE(HAS_PERMS_BY_NAME(DB_NAME(),N'DATABASE',N'VIEW DEFINITION'),0)=1 "
        + "AND COALESCE(HAS_PERMS_BY_NAME(N'sys.sql_expression_dependencies',N'OBJECT',N'SELECT'),0)=1";

    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly Func<string, string> _literal;
    private readonly Func<object?, string, string> _valueLiteral;
    private readonly Func<object, bool, string>? _sourceParameter;
    private readonly SqlServerSafeMigrationSqlExpressionRenderer _expressionRenderer;

    /// <summary>Initializes the catalog builder with the active provider services.</summary>
    /// <param name="typeMappingSource">The provider's canonical store-type and literal mappings.</param>
    /// <param name="sqlGenerationHelper">The provider's identifier delimiter.</param>
    /// <param name="literal">An optional string-literal renderer for contract probes.</param>
    /// <param name="valueLiteral">An optional typed-literal renderer for contract probes.</param>
    /// <param name="sourceParameter">An analysis-only lossless scalar binding, before target conversion.</param>
    public SqlServerSafeMigrationCatalogSqlBuilder(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper,
        Func<string, string>? literal = null,
        Func<object?, string, string>? valueLiteral = null,
        Func<object, bool, string>? sourceParameter = null
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
        _expressionRenderer = new SqlServerSafeMigrationSqlExpressionRenderer(typeMappingSource, sqlGenerationHelper);
        _literal = literal ?? RenderStringLiteral;
        _valueLiteral = valueLiteral ?? RenderValueLiteral;
        _sourceParameter = sourceParameter;
    }

    /// <summary>Captures one operation's catalog, prerequisite, and execution predicates.</summary>
    /// <param name="operation">The immutable safe operation.</param>
    /// <param name="expectedTableConstraints">The ordered stream's allowed intermediate table constraints.</param>
    /// <param name="targetTableKnownAbsent">Whether an analysis-only occupancy probe proved target absence.</param>
    /// <returns>A plan retaining unsupported boundaries before SQL generation.</returns>
    public SqlServerSafeMigrationRuntimePlan Build(
        SafeMigrationOperation operation,
        SafeMigrationExpectedTableConstraints? expectedTableConstraints = null,
        bool targetTableKnownAbsent = false
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        var unsupported = GetUnsupportedFeature(operation);
        if (unsupported is not null)
        {
            return Unsupported(unsupported);
        }

        var plan = operation.Intent switch
        {
            EnsureSchemaIntent value => BuildEnsureSchema(value),
            DropSchemaIntent value => BuildDropSchema(value),
            EnsureTableIntent value => BuildEnsureTable(value, expectedTableConstraints, targetTableKnownAbsent),
            DropTableIntent value => BuildDropTable(value),
            RenameTableIntent value => BuildRenameTable(value),
            EnsureColumnIntent value => BuildEnsureColumn(value),
            DropColumnIntent value => BuildDropColumn(value),
            RenameColumnIntent value => BuildRenameColumn(value),
            AlterColumnIntent value => BuildAlterColumn(value),
            EnsureIndexIntent value => BuildEnsureIndex(value),
            DropIndexIntent value => BuildDropIndex(value),
            RenameIndexIntent value => BuildRenameIndex(value),
            EnsurePrimaryKeyIntent value => BuildEnsurePrimaryKey(value),
            DropPrimaryKeyIntent value => BuildDropPrimaryKey(value),
            EnsureUniqueConstraintIntent value => BuildEnsureUnique(value),
            DropUniqueConstraintIntent value => BuildDropUnique(value),
            EnsureCheckConstraintIntent value => BuildEnsureCheck(value),
            DropCheckConstraintIntent value => BuildDropCheck(value),
            EnsureForeignKeyIntent value => BuildEnsureForeignKey(value),
            DropForeignKeyIntent value => BuildDropForeignKey(value),
            EnsureModelManagedDataIntent value => BuildEnsureModelManagedData(value),
            UpdateModelManagedDataIntent value => BuildUpdateModelManagedData(value),
            DeleteModelManagedDataIntent value => BuildDeleteModelManagedData(value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation.Intent.GetType().FullName,
                "Unknown SafeMigrations intent type."),
        };

        var defaultSupport = BuildDefaultValueSupportExpression(operation.Intent);
        var columnCollationSupport = BuildColumnCollationSupportExpression(operation.Intent);
        var delayedDefault = defaultSupport is not null && columnCollationSupport is not null;
        var prerequisite = BuildPrerequisiteExpression(operation.Intent);
        if (plan.PrerequisiteExpression != "1")
        {
            // WHY: A feature can derive additional prerequisites from authored
            // expressions. Replacing them with the common table gate could bind
            // a row expression before its physical columns have been proved.
            prerequisite = Bit($"({prerequisite}) = 1 AND ({plan.PrerequisiteExpression}) = 1");
        }

        return QualifyExpressionDependencyRead(plan with
        {
            PhysicalTableSupportExpression = BuildPhysicalTableSupportExpression(operation.Intent),
            DefaultValueSupportExpression = defaultSupport,
            ColumnCollationSupportExpression = columnCollationSupport,
            DefaultValueSupportRequiresDelayedBinding = delayedDefault,
            RequiresDelayedBinding = plan.RequiresDelayedBinding || delayedDefault,
            PrerequisiteExpression = prerequisite,
        });
    }

    /// <summary>Keeps a denied dependency-catalog read outside the dynamically compiled classifier.</summary>
    /// <param name="plan">The feature-owned classifier and its independent admission gates.</param>
    /// <returns>A delayed permission-qualified plan, or the unchanged unrelated plan.</returns>
    internal static SqlServerSafeMigrationRuntimePlan QualifyExpressionDependencyRead(
        SqlServerSafeMigrationRuntimePlan plan
    )
    {
        if (!plan.RequiresExpressionDependencyRead)
        {
            return plan;
        }

        // WHY: VIEW DEFINITION and table SELECT do not grant SELECT on this
        // system view. A CASE around an inaccessible view still compiles its
        // reference; the outer permission proof must precede sp_executesql.
        return plan with
        {
            RequiresDelayedBinding = true,
            StateEvaluationGuardExpression = Bit($"({ExpressionDependencyReadPermission}) "
                + $"AND ({plan.StateEvaluationGuardExpression})=1"),
            StateEvaluationGuardFailureExpression = "N'unsupported'",
            AnalysisOuterStateGuardExpression = plan.AnalysisOuterStateGuardExpression is null ? null
                : Bit($"({ExpressionDependencyReadPermission}) AND ({plan.AnalysisOuterStateGuardExpression})=1"),
            PrerequisiteFailureCodeExpression = $"CASE WHEN NOT ({ExpressionDependencyReadPermission}) "
                + "THEN N'dependency_catalog_permission' "
                + $"ELSE ({plan.PrerequisiteFailureCodeExpression ?? "CONVERT(nvarchar(128),NULL)"}) END",
            ClassificationCodeExpression = $"CASE WHEN NOT ({ExpressionDependencyReadPermission}) "
                + "THEN N'dependency_catalog_permission' "
                + $"ELSE ({plan.ClassificationCodeExpression ?? "NULL"}) END",
        };
    }

    /// <summary>Builds catalog-only prerequisites before SQL binds row-reading expressions.</summary>
    /// <param name="intent">The operation whose physical prerequisites are required.</param>
    /// <returns>A scalar Boolean expression suitable for a delayed execution gate.</returns>
    public string BuildPrerequisiteExpression(
        SafeMigrationIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        var predicate = intent switch
        {
            EnsureTableIntent value => SchemaExists(EffectiveSchema(value.Definition.Schema)),
            RenameTableIntent value => SchemaExists(EffectiveSchema(value.NewSchema ?? value.Schema)),
            EnsureColumnIntent value => $"{TableAndColumnsExist(value.Table, value.Schema, [])} "
                + $"AND ({IdentitySlotIsAvailable(value)})",
            AlterColumnIntent value => TableAndColumnsExist(value.Table, value.Schema, [value.Definition.Name]),
            EnsureIndexIntent value => TableAndColumnsExist(
                value.Definition.Table,
                value.Definition.Schema,
                value.Definition.Keys.Select(static key => key.Column!).Concat(value.Definition.IncludedColumns)
                    .Concat(IndexFilterColumns(value.Definition)).Distinct(StringComparer.Ordinal).ToArray()),
            EnsurePrimaryKeyIntent value => TableAndColumnsExist(
                value.Definition.Table,
                value.Definition.Schema,
                value.Definition.Columns),
            EnsureUniqueConstraintIntent value => TableAndColumnsExist(
                value.Definition.Table,
                value.Definition.Schema,
                value.Definition.Columns),
            EnsureCheckConstraintIntent value
                => TableAndColumnsExist(value.Definition.Table, value.Definition.Schema,
                    SafeMigrationPrerequisiteColumns.Local(value)),
            EnsureForeignKeyIntent value => ForeignKeyPrerequisites(value.Definition),
            ModelManagedDataIntent value => BuildModelManagedDataPrerequisite(value),
            _ => "1 = 1",
        };

        return Bit(predicate);
    }

    /// <summary>Renders one provider-qualified structured expression.</summary>
    /// <param name="expression">The immutable expression to validate and render.</param>
    /// <returns>The SQL Server expression text.</returns>
    public string RenderExpression(
        SafeMigrationSqlExpression expression
    )
        => _expressionRenderer.Render(expression);

    private string? GetUnsupportedFeature(
        SafeMigrationOperation operation
    )
    {
        if (operation.GetAnnotations().Any())
        {
            return "operation_annotation";
        }

        if (operation.Intent is DropSchemaIntent dropSchema
            && IsReservedSchema(dropSchema.Name))
        {
            return "reserved_schema";
        }

        if (operation.Intent is EnsureTableIntent table)
        {
            if (table.Definition.Columns.Count > 1024)
            {
                return "table_column_limit";
            }

            if (table.Definition.Comment is not null)
            {
                return "table_unproven_facet";
            }

            if (table.Definition.PrimaryKey is { } primaryKey
                && table.Definition.Columns.Any(column => column.IsNullable
                    && primaryKey.Columns.Contains(column.Name)))
            {
                return "primary_key_nullable_column";
            }

            foreach (var column in table.Definition.Columns)
            {
                if (GetUnsupportedColumnFeature(column) is { } code)
                {
                    return code;
                }
            }

            if (table.Definition.Columns.Count(static column
                => TryGetIdentity(column, out var identity, out _, out _) && identity) > 1)
            {
                return "identity_multiple_columns";
            }

            if (!TableFixedLayoutIsSupported(table.Definition))
            {
                return "table_fixed_row_limit";
            }

            if (table.Definition.ForeignKeys.Any(foreignKey =>
                !InlineForeignKeyPhysicalWidthIsSupported(table.Definition, foreignKey)))
            {
                return "foreign_key_unproven_width";
            }

            if ((table.Definition.PrimaryKey is { } inlinePrimaryKey
                    && !InlineKeyWidthIsSupported(table.Definition, inlinePrimaryKey.Columns, 900))
                || table.Definition.UniqueConstraints.Any(unique =>
                    !InlineKeyWidthIsSupported(table.Definition, unique.Columns, 1700)))
            {
                return "key_unproven_width";
            }

            foreach (var check in table.Definition.CheckConstraints)
            {
                if (check.Expression is { } expression
                    && _expressionRenderer.GetUnsupportedFeature(expression) is not null)
                {
                    return "check_expression_unproven";
                }
            }
        }

        if (operation.Intent is EnsureColumnIntent columnIntent)
        {
            return GetUnsupportedColumnFeature(columnIntent.Definition);
        }

        if (operation.Intent is EnsureForeignKeyIntent foreignKeyIntent
            && foreignKeyIntent.Definition.Columns.Count > 32)
        {
            return "foreign_key_unproven_width";
        }

        if (operation.Intent is AlterColumnIntent alterIntent)
        {
            return GetUnsupportedColumnFeature(alterIntent.Definition)
                ?? (alterIntent.OldDefinition is null
                    ? null
                    : GetUnsupportedColumnFeature(alterIntent.OldDefinition));
        }

        if (operation.Intent is EnsureIndexIntent indexIntent)
        {
            var index = indexIntent.Definition;
            if (index.NullsDistinct is not null
                || index.Method is not null
                || index.Keys.Count > 32
                || index.Keys.Any(static key => key.Column is null
                    || key.PrefixLength is not null
                    || key.NullOrder != SafeMigrationIndexNullOrder.ProviderDefault
                    || key.Collation is not null
                    || key.OperatorClass is not null))
            {
                return "index_unproven_facet";
            }

            if (HasIndexFilter(index))
            {
                var filter = GetStructuredIndexFilter(index);
                if (filter is null || !IsSupportedIndexFilter(filter)
                    || _expressionRenderer.GetUnsupportedFeature(filter) is not null)
                {
                    return "index_filter_unproven";
                }
            }
        }

        if (operation.Intent is EnsureCheckConstraintIntent checkIntent
            && checkIntent.Definition.Expression is { } checkExpression
            && _expressionRenderer.GetUnsupportedFeature(checkExpression) is not null)
        {
            return "check_expression_unproven";
        }

        if (operation.Intent is ModelManagedDataIntent modelData)
        {
            return GetUnsupportedModelManagedDataFeature(modelData);
        }

        return null;
    }

    private static string ContractFingerprint(
        string kind,
        string? schema,
        string table,
        string name,
        string expression
    )
    {
        var input = string.Join("\u001e", kind, schema ?? "dbo", table, name, expression);
        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexString(digest);
    }

    private bool TryGetColumnType(
        ExpectedColumnDefinition definition,
        out SqlServerColumnType type
    )
    {
        // WHY: The Core baseline forwards these facets to EF's column
        // operation. Catalog inference must resolve the identical mapping.
        var mapping = FindColumnTypeMapping(definition);
        if (mapping is null)
        {
            type = default;

            return false;
        }

        return TryParseStoreType(definition.StoreType ?? mapping.StoreType, out type);
    }

    private RelationalTypeMapping? FindColumnTypeMapping(
        ExpectedColumnDefinition definition
    )
    {
        // WHY: An authored CLR column type is not an explicit SQL constant
        // cast. Reject incompatible scalar families before EF can dispatch a
        // string/binary pair into its collection-mapping inference path.
        var clrType = Nullable.GetUnderlyingType(definition.ClrType) ?? definition.ClrType;
        if (!IsSupportedScalarClrType(clrType)
            || definition.StoreType is { } storeType
                && (!TryParseStoreType(storeType, out var target)
                    || !ColumnClrTypeMatchesScalarFamily(clrType, target)))
        {
            return null;
        }

        return _typeMappingSource.FindMapping(
            definition.ClrType,
            definition.StoreType,
            unicode: definition.IsUnicode,
            size: definition.MaxLength,
            rowVersion: definition.IsRowVersion,
            fixedLength: definition.IsFixedLength,
            precision: definition.Precision,
            scale: definition.Scale);
    }

    /// <summary>Recognizes CLR scalar operands without provider collection or opaque converter inference.</summary>
    /// <param name="type">The non-nullable scalar CLR type.</param>
    /// <returns>True for built-in scalar values represented by the provider's literal contract.</returns>
    internal static bool IsSupportedScalarClrType(
        Type type
    ) => type == typeof(string) || type == typeof(char) || type == typeof(byte[]) || type == typeof(Guid)
        || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)
        || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(float) || type == typeof(double)
        || type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short)
        || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long)
        || type == typeof(ulong) || type == typeof(decimal) || type.IsEnum;

    private static bool ColumnClrTypeMatchesScalarFamily(
        Type type,
        SqlServerColumnType target
    )
    {
        if (type == typeof(string) || type == typeof(char))
        {
            return target.IsCharacter;
        }

        if (type == typeof(byte[]))
        {
            return target.Name is "binary" or "varbinary" or "timestamp";
        }

        if (type == typeof(Guid))
        {
            return target.Name == "uniqueidentifier";
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly))
        {
            return target.Name is "date" or "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime";
        }

        if (type == typeof(TimeOnly) || type == typeof(TimeSpan))
        {
            return target.Name == "time";
        }

        return target.Name is "bigint" or "bit" or "decimal" or "float" or "int" or "money" or "numeric"
            or "real" or "smallint" or "smallmoney" or "tinyint";
    }

    /// <summary>Parses supported physical scalar facets without materializing numeric argument tokens.</summary>
    private static bool TryParseStoreType(
        string value,
        out SqlServerColumnType type
    )
    {
        var storeType = value.Trim().ToLowerInvariant();
        var opening = storeType.IndexOf('(');
        var name = opening < 0 ? storeType : storeType[..opening].TrimEnd();
        // WHY: Canonical type names remain owned strings, but numeric arguments
        // are consumed synchronously and need no substring or split-array snapshot.
        var hasArguments = opening >= 0 && storeType.EndsWith(')');
        var arguments = hasArguments ? storeType.AsSpan(opening + 1, storeType.Length - opening - 2) : default;

        if (opening >= 0 && !hasArguments)
        {
            type = default;

            return false;
        }

        if (name is not ("bigint" or "binary" or "bit" or "char" or "date" or "datetime" or "datetime2"
            or "datetimeoffset" or "decimal" or "float" or "int" or "money" or "nchar" or "numeric"
            or "nvarchar" or "real" or "rowversion" or "smalldatetime" or "smallint" or "smallmoney"
            or "time" or "timestamp" or "tinyint" or "uniqueidentifier" or "varbinary" or "varchar"))
        {
            type = default;

            return false;
        }

        var isCharacter = name is "char" or "nchar" or "varchar" or "nvarchar";
        var length = default(short?);
        var precision = default(byte?);
        var scale = default(byte?);

        if (name is "char" or "nchar" or "varchar" or "nvarchar" or "binary" or "varbinary")
        {
            if (!hasArguments)
            {
                type = default;

                return false;
            }

            if (arguments.SequenceEqual("max"))
            {
                if (name is not ("varchar" or "nvarchar" or "varbinary"))
                {
                    type = default;

                    return false;
                }

                length = -1;
            }
            else if (short.TryParse(arguments, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0
                && parsed <= (name is "nchar" or "nvarchar" ? 4000 : 8000))
            {
                var bytes = name is "nchar" or "nvarchar" ? parsed * 2 : parsed;
                if (bytes > short.MaxValue)
                {
                    type = default;

                    return false;
                }

                length = (short)bytes;
            }
            else
            {
                type = default;

                return false;
            }
        }

        if (name is "decimal" or "numeric")
        {
            var separator = arguments.IndexOf(',');
            if (separator < 0 || arguments[(separator + 1)..].Contains(',')
                || !byte.TryParse(
                    arguments[..separator].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p)
                || !byte.TryParse(
                    arguments[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s))
            {
                type = default;

                return false;
            }

            precision = p;
            scale = s;
            if (p is < 1 or > 38 || s > p)
            {
                type = default;

                return false;
            }
        }

        if (name is "datetime2" or "datetimeoffset" or "time")
        {
            if (!hasArguments)
            {
                scale = 7;
            }
            else if (byte.TryParse(arguments, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedScale))
            {
                scale = parsedScale;
                if (parsedScale > 7)
                {
                    type = default;

                    return false;
                }
            }
            else
            {
                type = default;

                return false;
            }
        }

        if (hasArguments
            && name is not ("char" or "nchar" or "varchar" or "nvarchar" or "binary" or "varbinary"
                or "decimal" or "numeric" or "datetime2" or "datetimeoffset" or "time"))
        {
            type = default;

            return false;
        }

        type = new SqlServerColumnType(name == "rowversion" ? "timestamp" : name,
            length, precision, scale, isCharacter);

        return true;
    }

    private string TableAndColumnsExist(
        string table,
        string? schema,
        IReadOnlyList<string> columns
    )
    {
        var id = TableId(table, schema);
        if (columns.Count == 0)
        {
            return TableExists(table, schema);
        }

        var values = string.Join(", ", columns.Distinct(StringComparer.Ordinal)
            .Select((column, index) => $"({index + 1}, {Literal(column)})"));

        return $"({TableExists(table, schema)}) AND NOT EXISTS (SELECT 1 FROM (VALUES {values}) "
            + $"expected(ordinal, name) WHERE NOT EXISTS (SELECT 1 FROM sys.columns c "
            + $"WHERE c.object_id = {id} AND c.name = expected.name))";
    }

    private string TableExists(
        string table,
        string? schema
    )
        => $"EXISTS (SELECT 1 FROM sys.tables t WHERE t.object_id = {TableId(table, schema)})";

    private string ObjectExists(
        string table,
        string? schema
    )
        => $"EXISTS (SELECT 1 FROM sys.objects o WHERE o.schema_id = SCHEMA_ID({Literal(EffectiveSchema(schema))}) "
            + $"AND o.name = {Literal(table)})";

    private string ColumnExists(
        string table,
        string? schema,
        string column
    )
        => $"EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = {TableId(table, schema)} "
            + $"AND c.name = {Literal(column)})";

    private string ConstraintNameExists(
        string table,
        string? schema,
        string name
    )
        => $"EXISTS (SELECT 1 FROM sys.objects o WHERE o.parent_object_id = {TableId(table, schema)} "
            + $"AND o.name = {Literal(name)} AND o.type IN ('PK', 'UQ', 'C', 'F'))";

    private string ConstraintNameOccupiedInSchema(
        string? schema,
        string name
    )
        => $"EXISTS (SELECT 1 FROM sys.objects o WHERE o.schema_id = SCHEMA_ID({Literal(EffectiveSchema(schema))}) "
            + $"AND o.name = {Literal(name)})";

    private string TableId(
        string table,
        string? schema
    )
        => $"OBJECT_ID({Literal(_sqlGenerationHelper.DelimitIdentifier(table, EffectiveSchema(schema)))}, 'U')";

    private static string EffectiveSchema(
        string? schema
    ) => schema ?? "dbo";

    private string Literal(
        string value
    ) => _literal(value);

    private string RenderStringLiteral(
        string value
    )
        => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private string RenderValueLiteral(
        object? value,
        string storeType
    )
    {
        var mapping = value is null
            ? _typeMappingSource.FindMapping(storeType)
            : _typeMappingSource.FindMapping(value.GetType(), storeType);

        return (mapping
                ?? throw new NotSupportedException($"SQL Server has no type mapping for '{storeType}'."))
            .GenerateSqlLiteral(value);
    }

    private static string Bit(
        string predicate
    ) => $"CASE WHEN {predicate} THEN 1 ELSE 0 END";

    private static SqlServerSafeMigrationRuntimePlan Plan(
        string stateExpression,
        string postcondition,
        SafeMigrationRepairCapability repair = SafeMigrationRepairCapability.None,
        string repairPrecondition = "0"
    )
        => new(stateExpression, postcondition, repair, repairPrecondition);

    private static SqlServerSafeMigrationRuntimePlan Unsupported(
        string code
    )
        => new("N'unsupported'", "0", SafeMigrationRepairCapability.None, "0", code)
        {
            IsStaticallyUnsupported = true,
        };

    private readonly record struct SqlServerColumnType(
        string Name,
        short? Length,
        byte? Precision,
        byte? Scale,
        bool IsCharacter
    );
}
