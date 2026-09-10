namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Consumes the internal design-time-services guard when EF Core reuses the
/// runtime model differ to create migration-history SQL.
/// </summary>
internal sealed class MySqlSafeMigrationDesignTimeServicesGuardHandler : IMySqlMigrationOperationHandler
{
    private const string HandlerIdentifier = "Doka.EntityFrameworkCore.SafeMigrations.MySql.DesignTimeServicesGuard";

    /// <inheritdoc />
    public string HandlerId => HandlerIdentifier;

    /// <inheritdoc />
    public Type OperationType => typeof(SafeMigrationDesignTimeServicesRequiredOperation);

    /// <inheritdoc />
    public MySqlMigrationOperationResult Generate(
        MySqlMigrationOperationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Operation is not SafeMigrationDesignTimeServicesRequiredOperation)
        {
            throw new ArgumentException(
                "The MySQL SafeMigrations design-time guard handler received an unexpected operation type.",
                nameof(context));
        }

        if (context.OperationOrdinal != 0)
        {
            throw new InvalidOperationException(
                "The SafeMigrations design-time-services guard must be the first migration operation.");
        }

        // WHY: EF Core also uses the decorated runtime model differ for
        // migration-history DDL. The marker must survive until scaffolding,
        // while runtime SQL generation consumes it without a database effect.
        return MySqlMigrationOperationResult.Consumed("design_time_guard_consumed");
    }
}
