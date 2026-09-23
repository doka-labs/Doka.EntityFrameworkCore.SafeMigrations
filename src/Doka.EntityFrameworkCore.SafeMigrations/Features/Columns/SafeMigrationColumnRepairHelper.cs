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
    /// Determines whether a declared default is structurally guaranteed to
    /// produce a non-null value for an existing row.
    /// </summary>
    /// <param name="defaultValue">The captured default-value contract.</param>
    /// <returns><see langword="true" /> when null replacement is provable.</returns>
    internal static bool HasProvablyNonNullDefault(
        SafeMigrationDefaultValue defaultValue
    )
    {
        ArgumentNullException.ThrowIfNull(defaultValue);

        return defaultValue.Kind switch
        {
            SafeMigrationDefaultValueKind.None => false,
            SafeMigrationDefaultValueKind.Literal => !defaultValue.IsNullLiteral,
            SafeMigrationDefaultValueKind.Sql when defaultValue.StructuredExpression is not null =>
                IsProvablyNonNull(defaultValue.StructuredExpression),
            SafeMigrationDefaultValueKind.Sql => false,
            _ => throw new ArgumentOutOfRangeException(nameof(defaultValue)),
        };
    }

    private static bool IsProvablyNonNull(
        SafeMigrationSqlExpression expression
    ) => expression switch
    {
        SafeMigrationSqlLiteralExpression value => value.Value is not null,
        SafeMigrationSqlUnaryExpression value => IsProvablyNonNull(value.Operand),
        // WHY: Even non-null operands can yield NULL for division by zero.
        SafeMigrationSqlBinaryExpression => false,
        SafeMigrationSqlNullTestExpression => true,
        SafeMigrationSqlBetweenExpression value =>
            IsProvablyNonNull(value.Operand)
            && IsProvablyNonNull(value.Lower)
            && IsProvablyNonNull(value.Upper),
        SafeMigrationSqlInExpression value =>
            IsProvablyNonNull(value.Operand) && value.Values.All(IsProvablyNonNull),
        SafeMigrationSqlFunctionExpression value
            when StringComparer.OrdinalIgnoreCase.Equals(value.Name, "COALESCE") =>
                value.Arguments.Any(IsProvablyNonNull),
        // WHY: Cast behavior depends on the provider and SQL mode. An invalid
        // conversion must never authorize a nullability backfill.
        SafeMigrationSqlCastExpression => false,
        SafeMigrationSqlCollateExpression value => IsProvablyNonNull(value.Operand),
        SafeMigrationSqlCurrentValueExpression => true,
        SafeMigrationSqlIdentifierExpression
            or SafeMigrationSqlFunctionExpression
            or SafeMigrationSqlProviderFragmentExpression
            or SafeMigrationSqlOpaqueExpression => false,
        _ => throw new UnreachableException(),
    };

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
