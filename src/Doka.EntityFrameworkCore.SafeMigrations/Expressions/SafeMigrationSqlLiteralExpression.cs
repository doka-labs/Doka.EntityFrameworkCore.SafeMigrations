namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents a typed literal value.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlLiteralExpression : SafeMigrationSqlExpression
{
    private readonly object? _value;

    /// <summary>Initializes a typed literal.</summary>
    /// <param name="value">The CLR value, including null.</param>
    /// <param name="storeType">The optional provider store type used for an explicit cast.</param>
    public SafeMigrationSqlLiteralExpression(
        object? value,
        string? storeType = null
    ) : this(value, storeType, numericSourceToken: default) { }

    /// <summary>Retains a parsed numeric lexeme without allocating a copied numeric string.</summary>
    internal SafeMigrationSqlLiteralExpression(
        object? value,
        string? storeType,
        ReadOnlyMemory<char> numericSourceToken
    )
    {
        // WHY: A literal is part of an immutable definition and its fingerprint.
        // Snapshot caller-owned bytes once; internal read-only consumers borrow
        // this snapshot instead of allocating another array for every traversal.
        // ReSharper disable once UseCollectionExpression
        _value = value is byte[] bytes
            ? bytes.ToArray()
            : value;

        StoreType = SafeMigrationDefinitionValidator.Optional(storeType, nameof(storeType));
        NumericSourceToken = numericSourceToken;
    }

    /// <summary>Gets the CLR value, returning a defensive copy for a binary literal.</summary>

    // WHY: Keep the explicit array copy protecting the retained immutable binary snapshot.
    // ReSharper disable once UseCollectionExpression
    public object? Value => _value is byte[] bytes
        ? bytes.ToArray()
        : _value;

    /// <summary>Gets the owned literal snapshot for trusted read-only comparison and rendering.</summary>
    /// <remarks>The borrowed binary value must never be mutated or exposed to a caller.</remarks>

    // WHY: Both accessors share the same defensive snapshot; the public accessor must not expose its backing array.
    // ReSharper disable once ConvertToAutoPropertyWhenPossible
    internal object? BorrowedValue => _value;

    /// <summary>Gets the explicit provider store type when specified.</summary>
    public string? StoreType { get; }

    /// <summary>Gets the original numeric token, or empty memory for an explicitly typed public literal.</summary>
    internal ReadOnlyMemory<char> NumericSourceToken { get; }
}
