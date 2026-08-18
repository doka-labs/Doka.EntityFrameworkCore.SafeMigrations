namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
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

        return operation.Intent switch
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
    }

    private string? GetUnsupportedFeature(
        SafeMigrationIntent intent
    ) => GetUnsupportedColumnFeature(intent) ?? GetUnsupportedIndexFeature(intent);

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
