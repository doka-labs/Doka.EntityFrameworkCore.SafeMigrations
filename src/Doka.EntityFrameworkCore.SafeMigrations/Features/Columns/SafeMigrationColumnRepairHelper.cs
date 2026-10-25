namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationColumnRepairHelper
{
    /// <summary>Checks whether an authored add needs no separate proof that existing rows can be populated.</summary>
    /// <param name="expected">The immutable target column contract.</param>
    /// <returns>Whether the target retains the provider-neutral existing-row additive contract.</returns>
    /// <remarks>Unknown SQL defaults still require provider assignment and atomic-DDL qualification.</remarks>
    public static bool CanSafelyAddMissingColumn(
        ExpectedColumnDefinition expected
    )
    {
        ArgumentNullException.ThrowIfNull(expected);

        // WHY: A known SQL NULL supplies no replacement for a required row.
        // Unknown and non-null SQL defaults retain their native provider contract.
        return expected.IsNullable
            || (expected.DefaultValue.Kind != SafeMigrationDefaultValueKind.None
                && !(expected.DefaultValue is
                {
                    Kind: SafeMigrationDefaultValueKind.Sql,
                    StructuredExpression: SafeMigrationSqlLiteralExpression { Value: null }
                }))
            || expected.ComputedColumnSql is not null
            || expected.ComputedExpression is not null;
    }

    public static bool CanSafelyAlterColumn(
        ExpectedColumnDefinition oldDefinition,
        ExpectedColumnDefinition targetDefinition
    ) => CanSafelyAlterColumn(oldDefinition, targetDefinition, nameComparer: null);

    /// <summary>Checks exact alter facets after a provider resolves the physical column-member identity.</summary>
    /// <param name="oldDefinition">The accepted source column.</param>
    /// <param name="targetDefinition">The immutable authored target.</param>
    /// <param name="nameComparer">The provider member comparer, or null for exact ordinal names.</param>
    /// <returns>Whether only supported non-storage facets can differ on this same physical member.</returns>
    public static bool CanSafelyAlterColumn(
        ExpectedColumnDefinition oldDefinition,
        ExpectedColumnDefinition targetDefinition,
        StringComparer? nameComparer
    )
    {
        ArgumentNullException.ThrowIfNull(oldDefinition);
        ArgumentNullException.ThrowIfNull(targetDefinition);

        return (nameComparer ?? StringComparer.Ordinal).Equals(oldDefinition.Name, targetDefinition.Name)
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
            && StringComparer.Ordinal.Equals(oldDefinition.CapturedComputedSql, targetDefinition.CapturedComputedSql)
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

        return HasRepairableIntrinsicShape(targetDefinition) && targetDefinition.ProviderAnnotations.Count == 0;
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
    )
    {
        var parents = default(SafeMigrationWorkStack<NonNullProofFrame>);
        var current = expression;

        try
        {
            while (true)
            {
                if (current is SafeMigrationSqlUnaryExpression unary)
                {
                    current = unary.Operand;
                    continue;
                }

                if (current is SafeMigrationSqlCollateExpression collate)
                {
                    current = collate.Operand;
                    continue;
                }

                var any = current is SafeMigrationSqlFunctionExpression function
                    && StringComparer.OrdinalIgnoreCase.Equals(function.Name, "COALESCE");
                if (current is SafeMigrationSqlBetweenExpression or SafeMigrationSqlInExpression || any)
                {
                    var first = NonNullProofChild(current, 0);
                    if (first is not null)
                    {
                        if (NonNullProofChild(current, 1) is not null)
                        {
                            parents.Push(new NonNullProofFrame(current, 1, any));
                        }

                        current = first;
                        continue;
                    }
                }

                var result = current switch
                {
                    SafeMigrationSqlLiteralExpression value => value.Value is not null,
                    // WHY: Even non-null operands can yield NULL for division by zero.
                    SafeMigrationSqlBinaryExpression => false,
                    SafeMigrationSqlNullTestExpression => true,
                    // WHY: Conversion and CASE result rules are provider contracts,
                    // not a provider-neutral proof that authorizes backfilling rows.
                    SafeMigrationSqlCastExpression or SafeMigrationSqlCaseExpression => false,
                    SafeMigrationSqlCurrentValueExpression => true,
                    SafeMigrationSqlIdentifierExpression
                        or SafeMigrationSqlFunctionExpression
                        or SafeMigrationSqlProviderFragmentExpression
                        or SafeMigrationSqlOpaqueExpression => false,
                    _ => throw new UnreachableException(),
                };

                while (true)
                {
                    if (!parents.TryPop(out var parent))
                    {
                        return result;
                    }

                    // WHY: Preserve Any/All short-circuiting. A decisive child
                    // must not inspect an unsupported or unknown later subtree.
                    if (result == parent.Any)
                    {
                        continue;
                    }

                    current = NonNullProofChild(parent.Expression, parent.Ordinal)!;
                    if (NonNullProofChild(parent.Expression, parent.Ordinal + 1) is not null)
                    {
                        parents.Push(parent with { Ordinal = parent.Ordinal + 1 });
                    }

                    break;
                }
            }
        }
        finally
        {
            parents.Dispose();
        }
    }

    /// <summary>Identifies only the children that the existing structural proof is permitted to inspect.</summary>
    private static SafeMigrationSqlExpression? NonNullProofChild(
        SafeMigrationSqlExpression expression,
        long ordinal
    ) => expression switch
    {
        SafeMigrationSqlBetweenExpression value when ordinal == 0 => value.Operand,
        SafeMigrationSqlBetweenExpression value when ordinal == 1 => value.Lower,
        SafeMigrationSqlBetweenExpression value when ordinal == 2 => value.Upper,
        SafeMigrationSqlInExpression value when ordinal == 0 => value.Operand,
        SafeMigrationSqlInExpression value when ordinal <= value.Values.Count => value.Values[(int)(ordinal - 1)],
        SafeMigrationSqlFunctionExpression value when ordinal < value.Arguments.Count => value.Arguments[(int)ordinal],
        _ => null,
    };

    /// <summary>Resumes an ordered conjunction or disjunction without materializing all its pending siblings.</summary>
    private readonly record struct NonNullProofFrame(
        SafeMigrationSqlExpression Expression,
        long Ordinal,
        bool Any
    );

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
            IsRowVersion: false, ComputedColumnSql: null, ComputedExpression: null, IsStored: null,
        };
    }
}
