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
    ) => StringComparer.Ordinal.Equals(left.Name, right.Name)
        && left.ClrType == right.ClrType
        && left.IsNullable == right.IsNullable
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
        && left.IsStored == right.IsStored
        && ProviderAnnotations(left.ProviderAnnotations, right.ProviderAnnotations);

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
