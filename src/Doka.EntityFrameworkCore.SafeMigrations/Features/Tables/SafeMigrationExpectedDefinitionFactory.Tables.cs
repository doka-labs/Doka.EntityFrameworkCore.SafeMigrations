namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedTableDefinition From(
        CreateTableOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.PrimaryKey?.GetAnnotations().Any() == true
            || operation.UniqueConstraints.Any(static value => value.GetAnnotations().Any())
            || operation.CheckConstraints.Any(static value => value.GetAnnotations().Any())
            || operation.ForeignKeys.Any(static value => value.GetAnnotations().Any()))
        {
            // WHY: Table capture must not discard nested provider metadata that
            // the immutable constraint definitions cannot preserve or compare.
            throw new NotSupportedException(
                "Provider-owned table constraint annotations have no complete safe contract.");
        }

        return new ExpectedTableDefinition(
            operation.Name,
            operation.Columns.Select(From),
            operation.Schema,
            operation.Comment,
            operation.PrimaryKey is null
                ? null
                : From(operation.PrimaryKey),
            operation.UniqueConstraints.Select(From),
            operation.CheckConstraints.Select(From),
            operation.ForeignKeys.Select(From))
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(operation),
        };
    }
}
