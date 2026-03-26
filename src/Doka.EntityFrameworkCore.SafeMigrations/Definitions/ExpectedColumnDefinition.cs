namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the expected shape of a column for safe comparison.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="StoreType">The expected store type.</param>
/// <param name="IsNullable">Whether the column is expected to be nullable.</param>
/// <param name="DefaultValueLiteral">The expected literal default value, if any.</param>
/// <param name="DefaultValueSql">The expected SQL default expression, if any.</param>
/// <param name="DefaultValueTypeName">The CLR type name for the literal default value, if any.</param>
/// <param name="DefaultValueJson">The serialized literal default value, if any.</param>
/// <param name="ComputedColumnSql">The expected computed-column expression, if any.</param>
/// <param name="Precision">The expected precision, if any.</param>
/// <param name="Scale">The expected scale, if any.</param>
/// <param name="Collation">The expected collation, if any.</param>
/// <param name="IsStored">Whether a computed column is expected to be stored, if applicable.</param>
/// <remarks>
/// <para>
/// The default-value fields use the following priority order during comparison:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <strong>Typed JSON path</strong> (<see cref="DefaultValueTypeName"/> + <see cref="DefaultValueJson"/>):
///       the value is deserialized from JSON using the stored CLR type name and compared by value.
///       This is the primary path for all default values captured via EF Core's typed API.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Legacy literal fallback</strong> (<see cref="DefaultValueLiteral"/>):
///       used when type deserialization fails — either because the CLR type has been renamed or
///       removed, or because the JSON cannot be round-tripped. Comparison falls back to the
///       serialized string representation produced by <c>SafeMigrationDefaultValueSerializer.ToLegacyLiteral</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>SQL expression path</strong> (<see cref="DefaultValueSql"/>):
///       compared independently of the literal/JSON paths; represents an inline SQL default
///       expression (e.g. <c>CURRENT_TIMESTAMP</c>) rather than a CLR value.
///     </description>
///   </item>
/// </list>
/// <para>
/// <see cref="DefaultValueTypeName"/> and <see cref="DefaultValueJson"/> are populated together
/// by <c>SafeMigrationDefaultValueSerializer.Capture</c>. If the CLR type is later renamed or
/// removed, deserialization returns <see langword="false"/> and comparison falls back to
/// <see cref="DefaultValueLiteral"/>. This means migrations that were generated against a now-removed
/// CLR type will continue to match using the legacy literal representation.
/// </para>
/// </remarks>
public sealed record ExpectedColumnDefinition
(
    string Name,
    string? StoreType,
    bool IsNullable,
    string? DefaultValueLiteral = null,
    string? DefaultValueSql = null,
    string? DefaultValueTypeName = null,
    string? DefaultValueJson = null,
    string? ComputedColumnSql = null,
    int? Precision = null,
    int? Scale = null,
    string? Collation = null,
    bool? IsStored = null
);
