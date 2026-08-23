namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly PostgreSqlSafeMigrationSqlExpressionRenderer _expressionRenderer;
    private readonly RelationalTypeMapping _stringMapping;
    private readonly Func<string, string> _literal;

    public PostgreSqlSafeMigrationCatalogSqlBuilder(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper,
        Func<string, string>? literal = null
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
        _expressionRenderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(typeMappingSource, sqlGenerationHelper);
        _stringMapping = typeMappingSource.FindMapping(typeof(string))
            ?? throw new InvalidOperationException("The PostgreSQL provider has no string type mapping.");

        _literal = literal ?? _stringMapping.GenerateSqlLiteral;
    }

    public PostgreSqlSafeMigrationRuntimePlan Build(
        SafeMigrationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        var unsupported = GetUnsupportedFeature(operation.Intent);
        if (unsupported is not null)
        {
            return Unsupported(unsupported);
        }

        var plan = operation.Intent switch
        {
            EnsureSchemaIntent value => BuildEnsureSchema(value),
            DropSchemaIntent value => BuildDropSchema(value),
            EnsureTableIntent value => BuildEnsureTable(value),
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
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation.Intent.GetType()
                    .FullName,
                "Unknown SafeMigrations intent type."),
        };

        return plan with { PrerequisiteExpression = BuildPrerequisiteExpression(operation.Intent) };
    }

    public string BuildPrerequisiteExpression(
        SafeMigrationIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (GetUnsupportedFeature(intent) is not null)
        {
            return "TRUE";
        }

        return intent switch
        {
            EnsureColumnIntent value => TableExists(value.Table, value.Schema),
            AlterColumnIntent value => TableExists(value.Table, value.Schema),
            EnsureIndexIntent value => TableExists(value.Definition.Table, value.Definition.Schema),
            EnsurePrimaryKeyIntent value => TableExists(value.Definition.Table, value.Definition.Schema),
            EnsureUniqueConstraintIntent value => TableExists(value.Definition.Table, value.Definition.Schema),
            EnsureCheckConstraintIntent value => TableExists(value.Definition.Table, value.Definition.Schema),
            EnsureForeignKeyIntent value => $"({TableExists(value.Definition.Table, value.Definition.Schema)}) "
                + $"AND ({TableExists(value.Definition.PrincipalTable, value.Definition.PrincipalSchema)})",
            _ => "TRUE",
        };
    }

    private string? GetUnsupportedFeature(
        SafeMigrationIntent intent
    ) => GetUnsupportedSqlExpressionFeature(intent)
        ?? GetUnsupportedColumnFeature(intent) ?? GetUnsupportedIndexFeature(intent);

    public string RenderExpression(
        SafeMigrationSqlExpression expression
    ) => _expressionRenderer.Render(expression);

    private static string? GetUnsupportedSqlExpressionFeature(
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

    private static PostgreSqlSafeMigrationRuntimePlan Plan(
        string stateExpression,
        string postcondition,
        SafeMigrationRepairCapability repair = SafeMigrationRepairCapability.None,
        string repairPrecondition = "FALSE"
    ) => new(stateExpression, postcondition, repair, repairPrecondition);

    private static PostgreSqlSafeMigrationRuntimePlan Unsupported(
        string code
    ) => new("'unsupported'", "FALSE", SafeMigrationRepairCapability.None, "FALSE", code);

    private string QualifiedRegclass(
        string table,
        string? schema
    ) => $"pg_catalog.to_regclass({Literal(Qualified(table, schema))})";

    private string Qualified(
        string name,
        string? schema
    ) => schema is null
        ? _sqlGenerationHelper.DelimitIdentifier(name)
        : _sqlGenerationHelper.DelimitIdentifier(name, schema);

    private string DelimitedPath(
        string value
    ) => string.Join(
        ".",
        value
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(_sqlGenerationHelper.DelimitIdentifier));

    private string SchemaExpression(
        string? schema
    ) => schema is null ? "current_schema()" : Literal(schema);

    private string Literal(
        string value
    ) => _literal(value);
}
