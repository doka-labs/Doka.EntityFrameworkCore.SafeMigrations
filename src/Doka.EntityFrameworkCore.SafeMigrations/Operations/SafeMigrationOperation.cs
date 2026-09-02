namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Wraps one closed SafeMigrations intent so an unregistered provider cannot
/// interpret it as a standard EF Core migration operation.
/// </summary>
public sealed class SafeMigrationOperation : MigrationOperation
{
    /// <summary>Initializes a safe migration operation.</summary>
    /// <param name="intent">The immutable operation intent.</param>
    /// <param name="policy">The conflict policy.</param>
    public SafeMigrationOperation(
        SafeMigrationIntent intent,
        SafeMigrationPolicy policy
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (intent is ModelManagedDataIntent
            && policy != SafeMigrationPolicy.ThrowIfDifferent)
        {
            throw new ArgumentException(
                "Model-managed data operations require the fixed ThrowIfDifferent policy.",
                nameof(policy));
        }

        Intent = intent;
        Policy = policy;
    }

    /// <summary>Gets the immutable operation intent.</summary>
    public SafeMigrationIntent Intent { get; }

    /// <summary>Gets the conflict policy.</summary>
    public SafeMigrationPolicy Policy { get; }
}
