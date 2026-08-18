namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
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
        && StringComparer.Ordinal.Equals(left.Collation, right.Collation)
        && StringComparer.Ordinal.Equals(left.Comment, right.Comment)
        && DefaultValue(left.DefaultValue, right.DefaultValue)
        && StringComparer.Ordinal.Equals(left.ComputedColumnSql, right.ComputedColumnSql)
        && left.IsStored == right.IsStored;

    private static bool DefaultValue(
        SafeMigrationDefaultValue left,
        SafeMigrationDefaultValue right
    )
    {
        if (left.Kind != right.Kind
            || !StringComparer.Ordinal.Equals(left.SqlExpression, right.SqlExpression))
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
