namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationColumnRepairHelper
{
    public static bool CanSafelyAddMissingColumn(
        ExpectedColumnDefinition expected
    )
    {
        ArgumentNullException.ThrowIfNull(expected);

        return expected.IsNullable
            || expected.DefaultValue.Kind != SafeMigrationDefaultValueKind.None
            || expected.ComputedColumnSql is not null
            || expected.ComputedExpression is not null;
    }

    public static bool CanSafelyAlterColumn(
        ExpectedColumnDefinition oldDefinition,
        ExpectedColumnDefinition targetDefinition
    )
    {
        ArgumentNullException.ThrowIfNull(oldDefinition);
        ArgumentNullException.ThrowIfNull(targetDefinition);

        return StringComparer.Ordinal.Equals(oldDefinition.Name, targetDefinition.Name)
            && oldDefinition.ClrType == targetDefinition.ClrType
            && StringComparer.Ordinal.Equals(oldDefinition.StoreType, targetDefinition.StoreType)
            && oldDefinition.IsUnicode == targetDefinition.IsUnicode
            && oldDefinition.MaxLength == targetDefinition.MaxLength
            && oldDefinition.IsFixedLength == targetDefinition.IsFixedLength
            && oldDefinition.IsRowVersion == targetDefinition.IsRowVersion
            && oldDefinition.Precision == targetDefinition.Precision
            && oldDefinition.Scale == targetDefinition.Scale
            && Equals(oldDefinition.Collation, targetDefinition.Collation)
            && StringComparer.Ordinal.Equals(oldDefinition.ComputedColumnSql, targetDefinition.ComputedColumnSql)
            && SafeMigrationSqlExpressionContract.Equivalent(
                oldDefinition.ComputedExpression,
                targetDefinition.ComputedExpression)
            && oldDefinition.IsStored == targetDefinition.IsStored;
    }

    public static bool CanSafelyConvergeExistingColumn(
        ExpectedColumnDefinition targetDefinition
    )
    {
        ArgumentNullException.ThrowIfNull(targetDefinition);

        return HasRepairableIntrinsicShape(targetDefinition)
            && targetDefinition.ProviderAnnotations.Count == 0;
    }

    /// <summary>
    /// Determines whether provider-neutral column facets can be converged after
    /// the active provider has independently validated its own metadata.
    /// </summary>
    /// <param name="targetDefinition">The immutable expected column definition.</param>
    /// <returns><see langword="true" /> when the provider-neutral shape is repairable.</returns>
    internal static bool HasRepairableIntrinsicShape(
        ExpectedColumnDefinition targetDefinition
    )
    {
        ArgumentNullException.ThrowIfNull(targetDefinition);

        // Provider metadata is deliberately excluded here. Only the owning
        // provider can prove whether its annotations affect physical DDL.
        return targetDefinition is
        {
            IsRowVersion: false,
            ComputedColumnSql: null,
            ComputedExpression: null,
            IsStored: null,
        };
    }
}
