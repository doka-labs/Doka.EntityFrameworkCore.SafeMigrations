namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed record PostgreSqlSafeMigrationRuntimePlan(
    string StateExpression,
    string Postcondition,
    SafeMigrationRepairCapability RepairCapability,
    string RepairPrecondition,
    string? UnsupportedCode = null
);
