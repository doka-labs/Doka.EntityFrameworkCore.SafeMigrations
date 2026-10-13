namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    /// <summary>Compares every provider-neutral and provider-owned column facet.</summary>
    /// <param name="left">The first immutable column definition.</param>
    /// <param name="right">The second immutable column definition.</param>
    /// <returns>True when both definitions describe the same complete column contract.</returns>
    public static bool Column(
        ExpectedColumnDefinition left,
        ExpectedColumnDefinition right
    ) => Column(left, right, nameComparer: null);

    /// <summary>Compares exact column facets after provider-qualified member identity resolves the lookup.</summary>
    /// <param name="left">The accepted immutable column definition.</param>
    /// <param name="right">The authored immutable target definition.</param>
    /// <param name="nameComparer">The provider's column-member comparer, or null for exact ordinal names.</param>
    /// <returns>True only when all non-name facets retain complete exact equality.</returns>
    public static bool Column(
        ExpectedColumnDefinition left,
        ExpectedColumnDefinition right,
        StringComparer? nameComparer
    ) => left.IsNullable == right.IsNullable
        && ColumnIntrinsicExceptNullability(left, right, nameComparer)
        && ProviderAnnotations(left.ProviderAnnotations, right.ProviderAnnotations);

    /// <summary>Compares every column facet except the explicitly reviewed nullability transition.</summary>
    /// <param name="left">The immutable source column definition.</param>
    /// <param name="right">The immutable target column definition.</param>
    /// <returns>True only when all storage, generation, default, comment and provider facets agree.</returns>
    public static bool ColumnExceptNullability(
        ExpectedColumnDefinition left,
        ExpectedColumnDefinition right
    ) => ColumnIntrinsicExceptNullability(left, right)
        && ProviderAnnotations(left.ProviderAnnotations, right.ProviderAnnotations);

    /// <summary>Compares provider-neutral column facets except nullability for provider-owned qualification.</summary>
    /// <param name="left">The immutable source column definition.</param>
    /// <param name="right">The immutable target column definition.</param>
    /// <param name="nameComparer">A provider-qualified column comparer, or null for exact ordinal names.</param>
    /// <returns>True only when every intrinsic storage, generation, default and comment facet agrees.</returns>
    /// <remarks>The provider must separately validate and compare all of its captured annotations.</remarks>
    public static bool ColumnIntrinsicExceptNullability(
        ExpectedColumnDefinition left,
        ExpectedColumnDefinition right,
        StringComparer? nameComparer = null
    ) => (nameComparer ?? StringComparer.Ordinal).Equals(left.Name, right.Name)
        && left.ClrType == right.ClrType
        && StringComparer.Ordinal.Equals(left.StoreType, right.StoreType)
        && left.IsUnicode == right.IsUnicode
        && left.MaxLength == right.MaxLength
        && left.IsFixedLength == right.IsFixedLength
        && left.IsRowVersion == right.IsRowVersion
        && left.Precision == right.Precision
        && left.Scale == right.Scale
        && Equals(left.Collation, right.Collation)
        && StringComparer.Ordinal.Equals(left.Comment, right.Comment)
        && DefaultValue(left.DefaultValue, right.DefaultValue)
        && StringComparer.Ordinal.Equals(left.ComputedColumnSql, right.ComputedColumnSql)
        && SafeMigrationSqlExpressionContract.Equivalent(left.ComputedExpression, right.ComputedExpression)
        && left.IsStored == right.IsStored;

    // WHY: Foreign-key columns may legitimately differ in nullability and value
    // generation. Referential compatibility depends on their physical storage
    // shape, while defaults and comments do not alter the referenced value.
    public static bool ForeignKeyColumnStorage(
        ExpectedColumnDefinition dependent,
        ExpectedColumnDefinition principal
    ) => StringComparer.Ordinal.Equals(dependent.StoreType, principal.StoreType)
        && dependent.IsUnicode == principal.IsUnicode
        && dependent.MaxLength == principal.MaxLength
        && dependent.IsFixedLength == principal.IsFixedLength
        && dependent.Precision == principal.Precision
        && dependent.Scale == principal.Scale
        && Equals(dependent.Collation, principal.Collation)
        && dependent.ComputedColumnSql is null
        && dependent.ComputedExpression is null
        && principal.ComputedColumnSql is null
        && principal.ComputedExpression is null;

    private static bool ProviderAnnotations(
        IReadOnlyList<SafeMigrationProviderAnnotation> left,
        IReadOnlyList<SafeMigrationProviderAnnotation> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!StringComparer.Ordinal.Equals(left[index].Name, right[index].Name)
                || !StringComparer.Ordinal.Equals(left[index].Fingerprint, right[index].Fingerprint))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DefaultValue(
        SafeMigrationDefaultValue left,
        SafeMigrationDefaultValue right
    )
    {
        if (left.Kind != right.Kind
            || !StringComparer.Ordinal.Equals(left.SqlExpression, right.SqlExpression)
            || !SafeMigrationSqlExpressionContract.Equivalent(left.StructuredExpression, right.StructuredExpression))
        {
            return false;
        }

        var leftValue = left.GetLiteralValue();
        var rightValue = right.GetLiteralValue();
        if (leftValue is byte[] leftBytes
            && rightValue is byte[] rightBytes)
        {
            return leftBytes
                .AsSpan()
                .SequenceEqual(rightBytes);
        }

        return Equals(leftValue, rightValue);
    }
}
