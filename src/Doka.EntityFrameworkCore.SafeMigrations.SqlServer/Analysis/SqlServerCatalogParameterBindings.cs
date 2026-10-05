namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Captures lossless source values independently of their guarded destination types.</summary>
internal sealed class SqlServerCatalogParameterBindings
{
    /// <summary>Leaves room below SQL Server's 2,100-parameter RPC limit.</summary>
    internal const int MaximumParameters = 2_000;

    private readonly IRelationalTypeMappingSource _mappings;
    private readonly string _externalPrefix;
    private readonly Dictionary<(object Value, string StoreType), SqlServerCatalogParameterValue> _names
        = new(SourceIdentityComparer.Instance);

    private readonly List<SqlServerCatalogParameterValue> _values = [];

    /// <summary>Creates one operation-local binding capture.</summary>
    /// <param name="mappings">The active SQL Server source mappings.</param>
    /// <param name="ordinal">The original operation ordinal owning the outer bindings.</param>
    public SqlServerCatalogParameterBindings(IRelationalTypeMappingSource mappings, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        _mappings = mappings;
        _externalPrefix = "@doka_source" + ordinal.ToString(CultureInfo.InvariantCulture) + "_";
    }

    /// <summary>Gets the captured bindings, which are never used as a SQL-template result-cache key.</summary>
    public IReadOnlyList<SqlServerCatalogParameterValue> Values => _values;

    /// <summary>Registers a lossless source scalar and returns its stable local SQL marker.</summary>
    /// <param name="value">The non-null authored scalar before destination conversion.</param>
    /// <param name="external">Whether the marker is used before the delayed classifier binds.</param>
    /// <returns>A stable local name or its uniquely owned outer transport marker.</returns>
    public string Add(object value, bool external = false)
    {
        ArgumentNullException.ThrowIfNull(value);

        var mapping = SourceMapping(value);
        var identity = (value, mapping.StoreType);
        if (_names.TryGetValue(identity, out var existing))
        {
            return external ? existing.ExternalName : existing.Name;
        }

        var name = "@doka_value" + _values.Count.ToString(CultureInfo.InvariantCulture);
        var externalName = _externalPrefix + _values.Count.ToString(CultureInfo.InvariantCulture);
        var captured = new SqlServerCatalogParameterValue(name, externalName, value, mapping, PayloadBytes(value));
        _values.Add(captured);
        _names.Add(identity, captured);

        return external ? externalName : name;
    }

    private RelationalTypeMapping SourceMapping(object value)
    {
        // WHY: A target varchar/decimal/datetime parameter can lose information before TRY_CAST
        // or ANSI round-trip validation. Wide Unicode/binary, full temporal precision and the
        // actual decimal scale preserve every authored source bit before those existing guards.
        var storeType = value switch
        {
            string => "nvarchar(max)",
            byte[] => "varbinary(max)",
            DateTime => "datetime2(7)",
            DateTimeOffset => "datetimeoffset(7)",
            TimeOnly or TimeSpan => "time(7)",
            decimal number => "decimal(38," + DecimalScale(number).ToString(CultureInfo.InvariantCulture) + ")",
            _ => null,
        };

        return (storeType is null ? _mappings.FindMapping(value.GetType())
                : _mappings.FindMapping(value.GetType(), storeType))
            ?? throw new NotSupportedException("SQL Server has no lossless catalog source mapping.");
    }

    private static int DecimalScale(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);

        return (bits[3] >> 16) & 0xff;
    }

    private static int PayloadBytes(object value) => value switch
    {
        string text => checked(Math.Max(Encoding.UTF8.GetByteCount(text), text.Length * 2) + 64),
        byte[] bytes => checked(bytes.Length + 64),
        _ => 128,
    };

    /// <summary>Retains physical scalar identity rather than only CLR semantic equality.</summary>
    private sealed class SourceIdentityComparer : IEqualityComparer<(object Value, string StoreType)>
    {
        /// <summary>Gets the allocation-free shared comparer for operation-local dictionaries.</summary>
        internal static readonly SourceIdentityComparer Instance = new();

        /// <inheritdoc />
        public bool Equals((object Value, string StoreType) left, (object Value, string StoreType) right)
        {
            if (!StringComparer.Ordinal.Equals(left.StoreType, right.StoreType))
            {
                return false;
            }

            // WHY: DateTimeOffset.Equals merges equal instants with different stored offsets,
            // and floating-point equality merges signed zero. Managed row matching retains
            // physical bytes, so neither equality can authorize sharing an authored scalar.
            return (left.Value, right.Value) switch
            {
                (DateTimeOffset first, DateTimeOffset second) => first.EqualsExact(second),
                (double first, double second) => BitConverter.DoubleToInt64Bits(first)
                    == BitConverter.DoubleToInt64Bits(second),
                (float first, float second) => BitConverter.SingleToInt32Bits(first)
                    == BitConverter.SingleToInt32Bits(second),
                _ => object.Equals(left.Value, right.Value),
            };
        }

        /// <inheritdoc />
        public int GetHashCode((object Value, string StoreType) identity)
        {
            var sourceHash = identity.Value switch
            {
                DateTimeOffset value => HashCode.Combine(value.Ticks, value.Offset.Ticks),
                double value => BitConverter.DoubleToInt64Bits(value).GetHashCode(),
                float value => BitConverter.SingleToInt32Bits(value),
                _ => identity.Value.GetHashCode(),
            };

            return HashCode.Combine(sourceHash, StringComparer.Ordinal.GetHashCode(identity.StoreType));
        }
    }
}

/// <summary>Retains a parameter's source mapping and bounded wire-payload estimate.</summary>
/// <param name="Name">The local parameter name inside a delayed classifier.</param>
/// <param name="ExternalName">The unique outer marker also used by conversion guards.</param>
/// <param name="Value">The complete authored source value.</param>
/// <param name="Mapping">The provider mapping before target conversion.</param>
/// <param name="PayloadBytes">A conservative source-value payload including scalar metadata.</param>
internal sealed record SqlServerCatalogParameterValue(
    string Name,
    string ExternalName,
    object Value,
    RelationalTypeMapping Mapping,
    int PayloadBytes
);
