namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationColumnRepairHelper
{
    public static bool CanSafelyAddMissingColumn(ExpectedColumnDefinition expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        return expected.IsNullable
            || expected.DefaultValueLiteral is not null
            || expected.DefaultValueJson is not null
            || expected.DefaultValueSql is not null
            || expected.ComputedColumnSql is not null;
    }
}
