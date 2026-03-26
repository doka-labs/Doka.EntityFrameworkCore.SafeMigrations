namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationSqlHelper
{
    public static void ApplyCommonAnnotations(
        MigrationOperation operation,
        SafeMigrationStrictMode strictMode,
        object? expectedDefinition,
        ExistenceCheck existenceCheck = ExistenceCheck.None)
    {
        switch (existenceCheck)
        {
            case ExistenceCheck.IfExists:
                operation[SafeMigrationAnnotationNames.IfExists] = true;
                break;
            case ExistenceCheck.IfNotExists:
                operation[SafeMigrationAnnotationNames.IfNotExists] = true;
                break;
            case ExistenceCheck.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(existenceCheck), existenceCheck, null);
        }

        operation[SafeMigrationAnnotationNames.StrictMode] = strictMode;

        if (expectedDefinition is not null)
        {
            operation[SafeMigrationAnnotationNames.ExpectedDefinition] = SafeMigrationDefinitionSerializer.Serialize(expectedDefinition);
        }
    }
}
