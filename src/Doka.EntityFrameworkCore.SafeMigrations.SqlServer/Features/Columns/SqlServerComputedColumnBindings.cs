namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Decodes bounded native source identities without approximating identifier collation.</summary>
internal static class SqlServerComputedColumnBindings
{
    // WHY: The native producer emits two nvarchar(12) integers, two separators
    // and one sysname (at most 128 UTF-16 code units) per reference.
    private const int MaximumEncodedReferenceLength = 12 + 12 + 2 + 128;

    /// <summary>Rebinds every native source to its immutable authored definition or unchanged live identity.</summary>
    /// <remarks>
    /// Each entry contains an authored ordinal, UTF-16 length and exact member text; names may contain colons.
    /// </remarks>
    /// <param name="encoded">The native invocation-owned length-prefixed source identities.</param>
    /// <param name="expectedReferences">The bounded reference count captured from the same expression.</param>
    /// <param name="authored">The exact immutable candidate definitions addressed by native ordinals.</param>
    /// <returns>The native physical source names and their immutable bindings.</returns>
    internal static IReadOnlyDictionary<string, ExpectedColumnDefinition?> Read(
        string encoded,
        int expectedReferences,
        ExpectedTableDefinition? authored
    )
    {
        if (expectedReferences is < 0 or > SafeMigrationSqlExpressionParser.MaximumTokens
            || encoded.Length > expectedReferences * MaximumEncodedReferenceLength)
        {
            throw InvalidBindings();
        }

        var result = new Dictionary<string, ExpectedColumnDefinition?>(StringComparer.Ordinal);
        var remaining = encoded.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            var ordinal = ReadNumber(ref remaining, allowNegative: true);
            var length = ReadNumber(ref remaining, allowNegative: false);
            if (length is < 1 or > 128 || remaining.Length < length
                || ordinal < -1 || ordinal >= (authored?.Columns.Count ?? 0))
            {
                throw InvalidBindings();
            }

            var nameSpan = remaining[..length];
            remaining = remaining[length..];
            var definition = ordinal < 0 ? null : authored!.Columns[ordinal];
            if (definition is not null && !nameSpan.SequenceEqual(definition.Name))
            {
                throw InvalidBindings();
            }

            var name = definition?.Name ?? new string(nameSpan);
            if (!result.TryAdd(name, definition)
                && !ReferenceEquals(result[name], definition))
            {
                throw InvalidBindings();
            }

            if (++count > expectedReferences)
            {
                throw InvalidBindings();
            }
        }

        if (count != expectedReferences)
        {
            throw InvalidBindings();
        }

        return result;
    }

    private static int ReadNumber(ref ReadOnlySpan<char> remaining, bool allowNegative)
    {
        var separator = remaining.IndexOf(':');
        if (separator < 1 || !int.TryParse(remaining[..separator],
            allowNegative ? NumberStyles.AllowLeadingSign : NumberStyles.None, CultureInfo.InvariantCulture,
            out var value))
        {
            throw InvalidBindings();
        }

        remaining = remaining[(separator + 1)..];

        return value;
    }

    private static InvalidOperationException InvalidBindings()
        => new("SQL Server returned invalid computed-column source bindings.");
}
