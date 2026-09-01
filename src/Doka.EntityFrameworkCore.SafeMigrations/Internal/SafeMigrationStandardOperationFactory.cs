namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Reconstructs ordinary EF Core operations from immutable SafeMigrations
/// intents for provider-owned SQL generation.
/// </summary>
internal static partial class SafeMigrationStandardOperationFactory
{
    /// <summary>Creates the ordinary EF Core operation represented by an intent.</summary>
    /// <param name="intent">The immutable provider-neutral operation intent.</param>
    /// <param name="renderExpression">The provider expression renderer, when expressions are present.</param>
    /// <param name="renderCollation">The provider collation renderer, when collations are present.</param>
    /// <returns>The equivalent ordinary EF Core migration operation.</returns>
    public static MigrationOperation Create(
        SafeMigrationIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression = null,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation = null
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        return intent switch
        {
            EnsureSchemaIntent value => CreateOperation(value),
            DropSchemaIntent value => CreateOperation(value),
            EnsureTableIntent value => CreateOperation(value, renderExpression, renderCollation),
            DropTableIntent value => CreateOperation(value),
            RenameTableIntent value => CreateOperation(value),
            EnsureColumnIntent value => CreateOperation(value, renderExpression, renderCollation),
            DropColumnIntent value => CreateOperation(value),
            RenameColumnIntent value => CreateOperation(value),
            AlterColumnIntent value => CreateOperation(value, renderExpression, renderCollation),
            EnsureIndexIntent value => CreateOperation(value, renderExpression),
            DropIndexIntent value => CreateOperation(value),
            RenameIndexIntent value => CreateOperation(value),
            EnsurePrimaryKeyIntent value => CreateOperation(value),
            DropPrimaryKeyIntent value => CreateOperation(value),
            EnsureUniqueConstraintIntent value => CreateOperation(value),
            DropUniqueConstraintIntent value => CreateOperation(value),
            EnsureCheckConstraintIntent value => CreateOperation(value, renderExpression),
            DropCheckConstraintIntent value => CreateOperation(value),
            EnsureForeignKeyIntent value => CreateOperation(value),
            DropForeignKeyIntent value => CreateOperation(value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                intent.GetType()
                    .FullName,
                "Unknown SafeMigrations intent type."),
        };
    }

    /// <summary>
    /// Creates an alter-column repair operation whose old definition exposes
    /// every safely mutable facet to the provider generator.
    /// </summary>
    /// <param name="intent">The ensure-column intent to repair.</param>
    /// <param name="renderExpression">The provider expression renderer, when expressions are present.</param>
    /// <param name="renderCollation">The provider collation renderer, when collations are present.</param>
    /// <param name="providerRepairValidator">
    /// The provider-owned metadata validator, or null when no provider annotations are permitted.
    /// </param>
    /// <returns>An ordinary EF Core alter-column operation.</returns>
    public static MigrationOperation CreateRepair(
        EnsureColumnIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression = null,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation = null,
        Func<ExpectedColumnDefinition, bool>? providerRepairValidator = null
    )
    {
        ValidateRepairIntent(intent, providerRepairValidator);

        return CreateRepairOperation(
            intent,
            renderExpression,
            renderCollation,
            declareNullabilityDifference: true);
    }

    /// <summary>
    /// Creates a complete-definition repair operation without declaring a
    /// nullability transition to the provider generator.
    /// </summary>
    /// <param name="intent">The ensure-column intent to repair.</param>
    /// <param name="renderExpression">The provider expression renderer, when expressions are present.</param>
    /// <param name="renderCollation">The provider collation renderer, when collations are present.</param>
    /// <param name="providerRepairValidator">
    /// The provider-owned metadata validator, or null when no provider annotations are permitted.
    /// </param>
    /// <returns>An ordinary EF Core alter-column operation.</returns>
    public static MigrationOperation CreateFullDefinitionRepair(
        EnsureColumnIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression = null,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation = null,
        Func<ExpectedColumnDefinition, bool>? providerRepairValidator = null
    )
    {
        ValidateRepairIntent(intent, providerRepairValidator);

        return CreateRepairOperation(
            intent,
            renderExpression,
            renderCollation,
            declareNullabilityDifference: false);
    }

    private static void ValidateRepairIntent(
        EnsureColumnIntent intent,
        Func<ExpectedColumnDefinition, bool>? providerRepairValidator
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        var providerMetadataIsRepairable = providerRepairValidator is null
            ? intent.Definition.ProviderAnnotations.Count == 0
            : providerRepairValidator(intent.Definition);

        // A provider may narrow repair eligibility for its own metadata, but
        // it cannot override Core's intrinsic exclusions for computed,
        // row-version, or otherwise incomplete replacement definitions.
        var isRepairable = SafeMigrationColumnRepairHelper.HasRepairableIntrinsicShape(intent.Definition)
            && providerMetadataIsRepairable;

        if (!isRepairable)
        {
            throw new NotSupportedException(
                "The ensure-column definition contains facets that cannot be repaired "
                + "without an explicit old definition.");
        }
    }

    private static string Render(
        SafeMigrationSqlExpression expression,
        Func<SafeMigrationSqlExpression, string>? renderExpression
    ) => renderExpression?.Invoke(expression)
        ?? throw new NotSupportedException("A structured SQL expression requires a provider-specific renderer.");

    private static string? Render(
        SafeMigrationCollationIdentifier? collation,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation
    )
    {
        if (collation is null)
        {
            return null;
        }

        if (renderCollation is null)
        {
            throw new NotSupportedException("A collation identity requires a provider-specific renderer.");
        }

        return renderCollation(collation);
    }
}
