namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Identifies the expected default-value representation of a column.
/// </summary>
public enum SafeMigrationDefaultValueKind
{
    /// <summary>The column has no default.</summary>
    None = 0,

    /// <summary>The column has a typed literal default, including a literal null.</summary>
    Literal = 1,

    /// <summary>The column has a SQL expression default.</summary>
    Sql = 2,
}

/// <summary>
/// Represents no default, a typed literal default or a SQL expression without
/// serializing CLR type names into migration metadata.
/// </summary>
public sealed class SafeMigrationDefaultValue
{
    private readonly object? _literalValue;

    private SafeMigrationDefaultValue(
        SafeMigrationDefaultValueKind kind,
        object? literalValue,
        string? sqlExpression,
        SafeMigrationSqlExpression? structuredExpression = null
    )
    {
        Kind = kind;
        _literalValue = CloneLiteral(literalValue);
        SqlExpression = sqlExpression;
        StructuredExpression = structuredExpression;
    }

    /// <summary>Gets the shared representation for a column without a default.</summary>
    public static SafeMigrationDefaultValue None { get; } = new(
        SafeMigrationDefaultValueKind.None,
        literalValue: null,
        sqlExpression: null);

    /// <summary>Gets the representation kind.</summary>
    public SafeMigrationDefaultValueKind Kind { get; }

    /// <summary>
    /// Gets a defensive copy of the literal value when <see cref="Kind"/> is
    /// <see cref="SafeMigrationDefaultValueKind.Literal"/>.
    /// </summary>
    public object? LiteralValue => CloneLiteral(_literalValue);

    /// <summary>
    /// Gets the SQL expression when <see cref="Kind"/> is
    /// <see cref="SafeMigrationDefaultValueKind.Sql"/>.
    /// </summary>
    public string? SqlExpression { get; }

    /// <summary>Gets the structured SQL expression when one was supplied.</summary>
    public SafeMigrationSqlExpression? StructuredExpression { get; }

    /// <summary>Creates a typed literal default.</summary>
    /// <param name="value">The literal value. A null value means SQL NULL.</param>
    /// <returns>An immutable default-value representation.</returns>
    public static SafeMigrationDefaultValue Literal(
        object? value
    )
    {
        if (!IsSupportedLiteral(value))
        {
            throw new ArgumentException(
                $"Default values of CLR type '{value!.GetType().FullName}' are not supported.",
                nameof(value));
        }

        return new SafeMigrationDefaultValue(SafeMigrationDefaultValueKind.Literal, value, sqlExpression: null);
    }

    /// <summary>Creates a SQL expression default.</summary>
    /// <param name="sqlExpression">The non-empty SQL expression.</param>
    /// <returns>An immutable default-value representation.</returns>
    public static SafeMigrationDefaultValue Sql(
        string sqlExpression
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlExpression);

        return new SafeMigrationDefaultValue(SafeMigrationDefaultValueKind.Sql, literalValue: null, sqlExpression);
    }

    /// <summary>Creates a structurally comparable SQL expression default.</summary>
    /// <param name="expression">The typed SQL expression.</param>
    /// <returns>An immutable default-value representation.</returns>
    public static SafeMigrationDefaultValue Sql(
        SafeMigrationSqlExpression expression
    )
    {
        ArgumentNullException.ThrowIfNull(expression);

        return new SafeMigrationDefaultValue(
            SafeMigrationDefaultValueKind.Sql,
            literalValue: null,
            sqlExpression: null,
            expression);
    }

    internal object? GetLiteralValue() => CloneLiteral(_literalValue);

    internal bool IsNullLiteral => Kind == SafeMigrationDefaultValueKind.Literal && _literalValue is null;

    private static bool IsSupportedLiteral(
        object? value
    ) => value is null
            or bool
            or byte
            or sbyte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong
            or decimal
            or float
            or double
            or string
            or char
            or byte[]
            or Guid
            or DateOnly
            or TimeOnly
            or DateTime
            or DateTimeOffset
            or TimeSpan
        || value.GetType()
            .IsEnum;

    private static object? CloneLiteral(
        object? value
    ) => value is byte[] bytes ? bytes.ToArray() : value;
}
