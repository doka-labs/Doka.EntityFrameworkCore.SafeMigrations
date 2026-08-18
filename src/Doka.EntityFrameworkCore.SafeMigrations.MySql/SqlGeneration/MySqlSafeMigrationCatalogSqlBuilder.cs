namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly RelationalTypeMapping _stringMapping;

    public MySqlSafeMigrationCatalogSqlBuilder(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);
        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
        _stringMapping = typeMappingSource.FindMapping(typeof(string))
            ?? throw new InvalidOperationException("The MySQL provider has no string type mapping.");
    }

    public MySqlSafeMigrationRuntimePlan Build(
        SafeMigrationOperation operation,
        MySqlMigrationOperationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var featureFailure = GetUnsupportedFeature(operation.Intent, context.Features, context.ServerVersion.IsMariaDb);
        if (featureFailure is not null)
        {
            return Unsupported(featureFailure);
        }

        return operation.Intent switch
        {
            EnsureSchemaIntent value => BuildEnsureSchema(value),
            DropSchemaIntent value => BuildDropSchema(value),
            EnsureTableIntent value => BuildEnsureTable(value, context.ServerVersion.IsMariaDb),
            DropTableIntent value => BuildDropTable(value),
            RenameTableIntent value => BuildRenameTable(value),
            EnsureColumnIntent value => BuildEnsureColumn(value, context.ServerVersion.IsMariaDb),
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
            EnsureCheckConstraintIntent value => BuildEnsureCheckConstraint(value),
            DropCheckConstraintIntent value => BuildDropCheckConstraint(value),
            EnsureForeignKeyIntent value => BuildEnsureForeignKey(value),
            DropForeignKeyIntent value => BuildDropForeignKey(value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation.Intent.GetType()
                    .FullName,
                "Unknown SafeMigrations intent type."),
        };
    }

    private string? GetUnsupportedFeature(
        SafeMigrationIntent intent,
        MySqlMigrationFeatureSet features,
        bool isMariaDb
    ) => GetUnsupportedColumnFeature(intent, features, isMariaDb)
        ?? GetUnsupportedTableFeature(intent, features)
        ?? GetUnsupportedSchemaFeature(intent)
        ?? GetUnsupportedIndexFeature(intent, features) ?? GetUnsupportedCheckConstraintFeature(intent, features);

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
    ) => new("'unsupported'", "FALSE", SafeMigrationRepairCapability.None, "FALSE", code);

    private string Literal(
        string value
    ) => _stringMapping.GenerateSqlLiteral(value);

    private string Delimited(
        string identifier
    ) => _sqlGenerationHelper.DelimitIdentifier(identifier);
}
