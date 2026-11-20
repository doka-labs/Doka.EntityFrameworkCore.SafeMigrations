namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents a standard current date or time value.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlCurrentValueExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a current-value expression.</summary>
    /// <param name="value">The current value to read.</param>
    /// <param name="precision">The optional fractional-second precision.</param>
    public SafeMigrationSqlCurrentValueExpression(
        SafeMigrationSqlCurrentValue value,
        int? precision = null
    )
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (precision is < 0 or > 6
            || (value == SafeMigrationSqlCurrentValue.Date && precision is not null))
        {
            throw new ArgumentOutOfRangeException(nameof(precision));
        }

        Value = value;
        Precision = precision;
    }

    /// <summary>Gets the current value to read.</summary>
    public SafeMigrationSqlCurrentValue Value { get; }

    /// <summary>Gets the fractional-second precision when specified.</summary>
    public int? Precision { get; }
}
