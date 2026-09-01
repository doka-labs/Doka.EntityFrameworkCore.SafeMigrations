namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly MySqlSafeMigrationSqlExpressionRenderer _expressionRenderer;
    private Dictionary<string, string>? _parameterMarkers;
    private List<string>? _parameterValues;

    public MySqlSafeMigrationCatalogSqlBuilder(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
        _expressionRenderer = new MySqlSafeMigrationSqlExpressionRenderer(typeMappingSource, sqlGenerationHelper);
    }

    public MySqlSafeMigrationRuntimePlan Build(
        SafeMigrationOperation operation,
        MySqlMigrationOperationContext context,
        IReadOnlySet<string>? expectedUniqueIndexes = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (_parameterValues is not null)
        {
            throw new InvalidOperationException(
                "The MySQL catalog plan builder does not support reentrant generation.");
        }

        _parameterMarkers = new Dictionary<string, string>(StringComparer.Ordinal);
        _parameterValues = [];
        try
        {
            var featureFailure = operation.GetAnnotations().Any()
                ? "operation_annotation"
                : GetUnsupportedFeature(operation.Intent, context.Features, context.ServerVersion);

            var plan = featureFailure is not null
                ? Unsupported(featureFailure)
                : operation.Intent switch
                {
                    EnsureSchemaIntent value => BuildEnsureSchema(value),
                    DropSchemaIntent value => BuildDropSchema(value),
                    EnsureTableIntent value => BuildEnsureTable(
                        value,
                        context.ServerVersion.IsMariaDb,
                        expectedUniqueIndexes),
                    DropTableIntent value => BuildDropTable(value),
                    RenameTableIntent value => BuildRenameTable(value),
                    EnsureColumnIntent value => BuildEnsureColumn(
                        value,
                        context.ServerVersion.IsMariaDb,
                        operation.Policy == SafeMigrationPolicy.RepairIfSafe),
                    DropColumnIntent value => BuildDropColumn(value),
                    RenameColumnIntent value => BuildRenameColumn(value),
                    AlterColumnIntent value => BuildAlterColumn(value, context.ServerVersion.IsMariaDb),
                    EnsureIndexIntent value => BuildEnsureIndex(value, context.ServerVersion.IsMariaDb),
                    DropIndexIntent value => BuildDropIndex(value),
                    RenameIndexIntent value => BuildRenameIndex(value),
                    EnsurePrimaryKeyIntent value => BuildEnsurePrimaryKey(value),
                    DropPrimaryKeyIntent value => BuildDropPrimaryKey(value),
                    EnsureUniqueConstraintIntent value => BuildEnsureUniqueConstraint(value),
                    DropUniqueConstraintIntent value => BuildDropUniqueConstraint(value),
                    EnsureCheckConstraintIntent value => BuildEnsureCheckConstraint(
                        value,
                        context.ServerVersion.IsMariaDb),
                    DropCheckConstraintIntent value => BuildDropCheckConstraint(value),
                    EnsureForeignKeyIntent value => BuildEnsureForeignKey(value),
                    DropForeignKeyIntent value => BuildDropForeignKey(value),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(operation),
                        operation.Intent.GetType()
                            .FullName,
                        "Unknown SafeMigrations intent type."),
                };

            if (featureFailure is null)
            {
                plan = plan with
                {
                    PrerequisiteExpression = BuildPrerequisiteExpression(operation.Intent),
                    RequiresLazyStateEvaluation = RequiresLazyStateEvaluation(
                        operation.Intent,
                        plan.RepairCapability),
                };
            }

            return plan with { ParameterValues = _parameterValues.ToArray() };
        }
        finally
        {
            _parameterMarkers = null;
            _parameterValues = null;
        }
    }

    private string BuildPrerequisiteExpression(
        SafeMigrationIntent intent
    ) => intent switch
    {
        EnsureColumnIntent value => TableAndColumnsExist(value.Table, SafeMigrationPrerequisiteColumns.Local(value)),
        AlterColumnIntent value => TableAndColumnsExist(value.Table, SafeMigrationPrerequisiteColumns.Local(value)),
        EnsureIndexIntent value =>
            TableAndColumnsExist(value.Definition.Table, SafeMigrationPrerequisiteColumns.Local(value)),
        EnsurePrimaryKeyIntent value => TableAndColumnsExist(
            value.Definition.Table,
            SafeMigrationPrerequisiteColumns.Local(value)),
        EnsureUniqueConstraintIntent value => TableAndColumnsExist(
            value.Definition.Table,
            SafeMigrationPrerequisiteColumns.Local(value)),
        EnsureCheckConstraintIntent value => TableAndColumnsExist(
            value.Definition.Table,
            SafeMigrationPrerequisiteColumns.Local(value)),
        EnsureForeignKeyIntent value => $"({TableAndColumnsExist(
            value.Definition.Table,
            SafeMigrationPrerequisiteColumns.Local(value))}) "
            + $"AND ({TableAndColumnsExist(
                value.Definition.PrincipalTable,
                SafeMigrationPrerequisiteColumns.Principal(value))})",
        _ => "TRUE",
    };

    private string TableAndColumnsExist(
        string table,
        IReadOnlyList<string> columns
    )
    {
        var tableExists = BaseTableExists(table);
        if (columns.Count == 0)
        {
            return tableExists;
        }

        return $"({tableExists}) AND ((SELECT COUNT(DISTINCT c.COLUMN_NAME) "
            + "FROM INFORMATION_SCHEMA.COLUMNS c "
            + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
            + $"AND c.COLUMN_NAME IN ({string.Join(", ", columns.Select(Literal))})) "
            + $"= {columns.Count.ToString(CultureInfo.InvariantCulture)})";
    }

    private static bool RequiresLazyStateEvaluation(
        SafeMigrationIntent intent,
        SafeMigrationRepairCapability repairCapability
    ) => intent switch
    {
        EnsureColumnIntent value => !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(value.Definition)
            || (repairCapability == SafeMigrationRepairCapability.Safe && !value.Definition.IsNullable),
        AlterColumnIntent value => value.OldDefinition is not null
            && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(value.OldDefinition, value.Definition)
            && value.OldDefinition.IsNullable
            && !value.Definition.IsNullable,
        // Index classification now reads physical key widths for every index,
        // not only row data for unique indexes. Defer both probes until the
        // prerequisite guard has proved that every referenced column exists.
        EnsureIndexIntent => true,
        EnsurePrimaryKeyIntent => true,
        EnsureUniqueConstraintIntent => true,
        EnsureCheckConstraintIntent => true,
        EnsureForeignKeyIntent => true,
        _ => false,
    };

    private string? GetUnsupportedFeature(
        SafeMigrationIntent intent,
        MySqlMigrationFeatureSet features,
        MySqlServerVersion serverVersion
    ) => GetUnsupportedSqlExpressionFeature(intent)
        ?? GetUnsupportedColumnFeature(intent, features, serverVersion)
        ?? GetUnsupportedTableFeature(intent, features)
        ?? GetUnsupportedSchemaFeature(intent)
        ?? GetUnsupportedIndexFeature(intent, features) ?? GetUnsupportedCheckConstraintFeature(intent, features);

    public string RenderExpression(
        SafeMigrationSqlExpression expression
    ) => _expressionRenderer.Render(expression);

    private string? GetUnsupportedSqlExpressionFeature(
        SafeMigrationIntent intent
    )
    {
        var expressions = intent switch
        {
            EnsureTableIntent value => value
                .Definition
                .Columns
                .SelectMany(ColumnExpressions)
                .Concat(value.Definition.CheckConstraints.Select(CheckExpression)),
            EnsureColumnIntent value => ColumnExpressions(value.Definition),
            AlterColumnIntent value => ColumnExpressions(value.Definition)
                .Concat(value.OldDefinition is null ? [] : ColumnExpressions(value.OldDefinition)),
            EnsureIndexIntent value => IndexExpressions(value.Definition),
            EnsureCheckConstraintIntent value => [CheckExpression(value.Definition)],
            _ => [],
        };

        foreach (var expression in expressions)
        {
            if (expression is SafeMigrationSqlOpaqueExpression { FollowsIdentifierRename: true })
            {
                return "opaque_expression_rename_projection";
            }

            if (!SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression))
            {
                return "opaque_sql_expression";
            }

            var providerFeature = _expressionRenderer.GetUnsupportedFeature(expression);
            if (providerFeature is not null)
            {
                return providerFeature;
            }
        }

        return null;
    }

    private static IEnumerable<SafeMigrationSqlExpression> ColumnExpressions(
        ExpectedColumnDefinition definition
    )
    {
        if (definition.ComputedColumnSql is not null)
        {
            yield return SafeMigrationSql.Opaque(definition.ComputedColumnSql);
        }
        else if (definition.ComputedExpression is not null)
        {
            yield return definition.ComputedExpression;
        }

        if (definition.DefaultValue is { Kind: SafeMigrationDefaultValueKind.Sql } defaultValue)
        {
            yield return defaultValue.StructuredExpression ?? SafeMigrationSql.Opaque(defaultValue.SqlExpression!);
        }
    }

    private static IEnumerable<SafeMigrationSqlExpression> IndexExpressions(
        ExpectedIndexDefinition definition
    )
    {
        if (definition.Filter is not null)
        {
            yield return SafeMigrationSql.Opaque(definition.Filter);
        }
        else if (definition.StructuredFilter is not null)
        {
            yield return definition.StructuredFilter;
        }

        foreach (var key in definition.Keys)
        {
            if (key.Expression is not null)
            {
                yield return SafeMigrationSql.Opaque(key.Expression);
            }
            else if (key.StructuredExpression is not null)
            {
                yield return key.StructuredExpression;
            }
        }
    }

    private static SafeMigrationSqlExpression CheckExpression(
        ExpectedCheckConstraintDefinition definition
    ) => definition.Expression ?? SafeMigrationSql.Opaque(definition.Sql!);

    private static bool Supported(
        MySqlMigrationFeatureSet features,
        MySqlMigrationFeature feature
    ) => features.GetSupport(feature) != MySqlMigrationFeatureSupport.Unsupported;

    private static MySqlSafeMigrationRuntimePlan Plan(
        string stateExpression,
        string postcondition,
        SafeMigrationRepairCapability repairCapability = SafeMigrationRepairCapability.None,
        string repairPrecondition = "FALSE"
    ) => new(stateExpression, postcondition, repairCapability, repairPrecondition);

    private static MySqlSafeMigrationRuntimePlan Unsupported(
        string code
    ) => new("'unsupported'", "FALSE", SafeMigrationRepairCapability.None, "FALSE", code)
    {
        IsStaticallyUnsupported = true,
    };

    private string Literal(
        string value
    )
    {
        if (_parameterMarkers is null
            || _parameterValues is null)
        {
            throw new InvalidOperationException("No MySQL catalog plan generation is active.");
        }

        if (_parameterMarkers.TryGetValue(value, out var existingMarker))
        {
            return existingMarker;
        }

        var marker = MySqlCatalogSqlTemplate.Marker(_parameterValues.Count);
        _parameterValues.Add(value);
        _parameterMarkers.Add(value, marker);

        return marker;
    }

    private string Delimited(
        string identifier
    ) => _sqlGenerationHelper.DelimitIdentifier(identifier);
}
