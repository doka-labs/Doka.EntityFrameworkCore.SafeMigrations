namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the complete provider-neutral definition of a column.
/// </summary>
public sealed class ExpectedColumnDefinition
{
    /// <summary>Initializes a complete expected column definition.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="clrType">The CLR type used for provider type mapping.</param>
    /// <param name="isNullable">Whether the column accepts null values.</param>
    /// <param name="storeType">The explicit store type, or null for provider inference.</param>
    /// <param name="isUnicode">The Unicode facet, or null when unspecified.</param>
    /// <param name="maxLength">The maximum-length facet, or null when unspecified.</param>
    /// <param name="isFixedLength">The fixed-length facet, or null when unspecified.</param>
    /// <param name="isRowVersion">Whether the column is a row-version column.</param>
    /// <param name="precision">The numeric precision, or null when unspecified.</param>
    /// <param name="scale">The numeric scale, or null when unspecified.</param>
    /// <param name="collation">The expected database collation, or null when unspecified.</param>
    /// <param name="comment">The expected database comment, or null when unspecified.</param>
    /// <param name="defaultValue">The provider-neutral default-value representation.</param>
    /// <param name="computedColumnSql">The computed-column SQL expression, or null when absent.</param>
    /// <param name="isStored">Whether the computed column is stored, or null when unspecified.</param>
    public ExpectedColumnDefinition(
        string name,
        Type clrType,
        bool isNullable,
        string? storeType = null,
        bool? isUnicode = null,
        int? maxLength = null,
        bool? isFixedLength = null,
        bool isRowVersion = false,
        int? precision = null,
        int? scale = null,
        string? collation = null,
        string? comment = null,
        SafeMigrationDefaultValue? defaultValue = null,
        string? computedColumnSql = null,
        bool? isStored = null
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        ArgumentNullException.ThrowIfNull(clrType);

        ClrType = clrType;
        StoreType = SafeMigrationDefinitionValidator.Optional(storeType, nameof(storeType));
        IsNullable = isNullable;
        IsUnicode = isUnicode;

        if (maxLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Maximum length must be positive.");
        }

        if (precision is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), "Precision must be positive.");
        }

        if (scale is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must not be negative.");
        }

        if (precision is not null
            && scale > precision)
        {
            throw new ArgumentException("Scale must not exceed precision.", nameof(scale));
        }

        MaxLength = maxLength;
        IsFixedLength = isFixedLength;
        IsRowVersion = isRowVersion;
        Precision = precision;
        Scale = scale;
        Collation = SafeMigrationDefinitionValidator.Optional(collation, nameof(collation));
        Comment = comment;
        DefaultValue = defaultValue ?? SafeMigrationDefaultValue.None;
        ComputedColumnSql = SafeMigrationDefinitionValidator.Optional(computedColumnSql, nameof(computedColumnSql));
        IsStored = isStored;

        if (ComputedColumnSql is not null
            && DefaultValue.Kind != SafeMigrationDefaultValueKind.None)
        {
            throw new ArgumentException("A computed column cannot also define a default value.", nameof(defaultValue));
        }

        if (DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal)
        {
            var literal = DefaultValue.GetLiteralValue();
            if (literal is null
                && !IsNullable)
            {
                throw new ArgumentException("A literal NULL default requires a nullable column.", nameof(defaultValue));
            }

            var targetType = Nullable.GetUnderlyingType(ClrType) ?? ClrType;
            if (literal is not null
                && !targetType.IsInstanceOfType(literal))
            {
                throw new ArgumentException(
                    "The literal default CLR type must match the column CLR type.",
                    nameof(defaultValue));
            }
        }

        if (ComputedColumnSql is null
            && IsStored is not null)
        {
            throw new ArgumentException(
                "Stored or virtual state requires a computed-column expression.",
                nameof(isStored));
        }
    }

    /// <summary>Gets the column name.</summary>
    public string Name { get; }

    /// <summary>Gets the CLR type used for provider type mapping.</summary>
    public Type ClrType { get; }

    /// <summary>Gets the explicit store type, or null for provider inference.</summary>
    public string? StoreType { get; }

    /// <summary>Gets whether the column accepts null values.</summary>
    public bool IsNullable { get; }

    /// <summary>Gets the Unicode facet when specified.</summary>
    public bool? IsUnicode { get; }

    /// <summary>Gets the maximum-length facet when specified.</summary>
    public int? MaxLength { get; }

    /// <summary>Gets the fixed-length facet when specified.</summary>
    public bool? IsFixedLength { get; }

    /// <summary>Gets whether the column is a row-version column.</summary>
    public bool IsRowVersion { get; }

    /// <summary>Gets the numeric precision when specified.</summary>
    public int? Precision { get; }

    /// <summary>Gets the numeric scale when specified.</summary>
    public int? Scale { get; }

    /// <summary>Gets the expected collation when specified.</summary>
    public string? Collation { get; }

    /// <summary>Gets the expected comment when specified.</summary>
    public string? Comment { get; }

    /// <summary>Gets the expected default-value representation.</summary>
    public SafeMigrationDefaultValue DefaultValue { get; }

    /// <summary>Gets the computed-column expression when specified.</summary>
    public string? ComputedColumnSql { get; }

    /// <summary>Gets whether a computed column is stored or virtual.</summary>
    public bool? IsStored { get; }
}
