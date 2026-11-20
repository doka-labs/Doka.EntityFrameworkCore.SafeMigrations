namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Specifies a standard current date or time value.</summary>
[PublicAPI]
public enum SafeMigrationSqlCurrentValue
{
    /// <summary>The current date.</summary>
    Date = 0,

    /// <summary>The provider's current time value.</summary>
    Time = 1,

    /// <summary>The provider's current timestamp value.</summary>
    Timestamp = 2,
}
