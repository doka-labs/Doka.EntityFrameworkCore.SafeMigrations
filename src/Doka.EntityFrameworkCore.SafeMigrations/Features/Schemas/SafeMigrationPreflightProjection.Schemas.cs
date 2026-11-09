namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    // WHY: These typed hooks keep schema-family dispatch explicit. Schemas
    // do not mutate Core's table projection; the provider owns their state.
    private static SafeMigrationProviderAnalysis Project(
        EnsureSchemaIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis;

    private static SafeMigrationProviderAnalysis Project(
        DropSchemaIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis;

    private static void Observe(
        EnsureSchemaIntent intent,
        SafeMigrationDecision decision
    ) { }

    private static void Observe(
        DropSchemaIntent intent,
        SafeMigrationDecision decision
    ) { }
}
