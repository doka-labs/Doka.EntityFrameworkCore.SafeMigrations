namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>Shares exact native index-input expressions only within one catalog-only prerequisite statement.</summary>
internal sealed class PostgreSqlIndexPrerequisiteCertificates
{
    private readonly Dictionary<ExpectedIndexDefinition, string> _references = new(InputContractComparer.Instance);
    private readonly List<(ExpectedIndexDefinition Input, string Definition, int Bytes)> _definitions = [];
    private readonly IReadOnlyList<string>? _names;

    /// <summary>Uses the invocation's collision-free names without retaining its owner-name inventory.</summary>
    /// <param name="names">The bounded immutable alias palette, or null for isolated catalog-only use.</param>
    internal PostgreSqlIndexPrerequisiteCertificates(IReadOnlyList<string>? names = null) => _names = names;

    /// <summary>Gets the number of independently retained native input contracts.</summary>
    internal int Count => _definitions.Count;

    /// <summary>Gets every retained WITH prefix byte, including separators and its trailing newline.</summary>
    internal int Utf8PrefixBytes { get; private set; }

    /// <summary>Returns a lazy scalar reference for an exactly identical, already parameter-bound expression.</summary>
    /// <param name="input">The immutable physical/native input contract, without operation-specific name state.</param>
    /// <param name="builder">The current statement's parameter-bound catalog renderer.</param>
    /// <returns>The scalar subquery consumed only by its operation's existing Missing branch.</returns>
    internal string Intern(ExpectedIndexDefinition input, PostgreSqlSafeMigrationCatalogSqlBuilder builder)
    {
        if (_references.TryGetValue(input, out var existing))
        {
            return existing;
        }

        var expression = builder.IndexInputEligibility(input);
        var name = _names is null ? "\"_doka_sm_index_input_" + Count.ToString(CultureInfo.InvariantCulture) + "\""
            : _names[Count];
        var definition = name + " AS MATERIALIZED (SELECT (" + expression + ") AS eligible)";
        var reference = "(SELECT eligible FROM " + name + ")";
        var bytes = Encoding.UTF8.GetByteCount(definition) + (Count == 0 ? 6 : 2);

        // WHY: PostgreSQL initializes planned expression copies before CASE
        // chooses a branch; repeated unconsumed catalog SQL can trigger costly
        // JIT. One MATERIALIZED SELECT shares that exact plan without eagerly
        // reading it. Per-operation owner/name/alias guards stay outside it.
        _references.Add(input, reference);
        _definitions.Add((input, definition, bytes));
        Utf8PrefixBytes += bytes;

        return reference;
    }

    /// <summary>Restores the exact certificate prefix alongside its provider-parameter checkpoint.</summary>
    /// <param name="count">The certificate count before the candidate operation was constructed.</param>
    internal void Rollback(int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)Count);
        while (_definitions.Count > count)
        {
            var removed = _definitions[^1];
            _references.Remove(removed.Input);
            _definitions.RemoveAt(_definitions.Count - 1);
            Utf8PrefixBytes -= removed.Bytes;
        }
    }

    /// <summary>Emits only retained catalog certificates; a rolled-back empty prefix emits no WITH.</summary>
    internal string RenderPrefix()
        => Count == 0 ? string.Empty : "WITH "
            + string.Join(", ", _definitions.Select(static value => value.Definition)) + "\n";

    /// <summary>Reserves exact unqualified zero-row owners once, then retains only one statement's aliases.</summary>
    /// <param name="operations">The actual authored stream; reservation is never a structural certificate.</param>
    /// <returns>A collision-free palette under the existing per-statement operation bound.</returns>
    internal static string[] ReserveNames(IReadOnlyList<SafeMigrationOperation> operations)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (operation?.Intent is EnsureIndexIntent { Definition.Schema: null } index)
            {
                owners.Add(index.Definition.Table);
            }
        }

        // WHY: Independent expression-index matching can bind a zero-row
        // application owner even though native input proof itself is catalog-
        // only. A later owner must not become an earlier WITH alias. One scan
        // of immutable names prevents shadowing without SQL rewriting or a
        // repeated full-stream scan per statement; only 32 aliases survive.
        var names = new string[SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement];
        var candidate = 0;
        for (var ordinal = 0; ordinal < names.Length; ordinal++)
        {
            string name;
            do
            {
                name = "_doka_sm_index_input_" + candidate.ToString(CultureInfo.InvariantCulture);
                candidate++;
            }
            while (owners.Contains(name));

            names[ordinal] = "\"" + name + "\"";
        }

        return names;
    }

    /// <summary>Compares only the exact facts consumed by native input eligibility, never SQL literals.</summary>
    private sealed class InputContractComparer : IEqualityComparer<ExpectedIndexDefinition>
    {
        internal static readonly InputContractComparer Instance = new();

        /// <inheritdoc />
        public bool Equals(ExpectedIndexDefinition? first, ExpectedIndexDefinition? second)
        {
            if (ReferenceEquals(first, second))
            {
                return true;
            }

            if (first is null || second is null
                || !StringComparer.Ordinal.Equals(first.Table, second.Table)
                || !StringComparer.Ordinal.Equals(first.Schema, second.Schema)
                || !StringComparer.Ordinal.Equals(first.Method ?? "btree", second.Method ?? "btree")
                || PostgreSqlSafeMigrationCatalogSqlBuilder.IndexRequiredFlags(first)
                    != PostgreSqlSafeMigrationCatalogSqlBuilder.IndexRequiredFlags(second))
            {
                return false;
            }

            var firstOrdinal = 0;
            var secondOrdinal = 0;
            while (true)
            {
                var firstMember = Next(first, ref firstOrdinal);
                var secondMember = Next(second, ref secondOrdinal);
                if (firstMember is null || secondMember is null)
                {
                    return firstMember is null && secondMember is null;
                }

                if (firstMember != secondMember
                    || !StringComparer.Ordinal.Equals(first.Keys[firstOrdinal - 1].OperatorClass,
                        second.Keys[secondOrdinal - 1].OperatorClass))
                {
                    return false;
                }
            }
        }

        /// <inheritdoc />
        public int GetHashCode(ExpectedIndexDefinition input)
        {
            var hash = new HashCode();
            hash.Add(input.Table, StringComparer.Ordinal);
            hash.Add(input.Schema, StringComparer.Ordinal);
            hash.Add(input.Method ?? "btree", StringComparer.Ordinal);
            hash.Add(PostgreSqlSafeMigrationCatalogSqlBuilder.IndexRequiredFlags(input));
            for (var ordinal = 0; ordinal < input.Keys.Count; ordinal++)
            {
                var key = input.Keys[ordinal];
                if (PostgreSqlSafeMigrationCatalogSqlBuilder.IndexPhysicalMember(key) is not { } member)
                {
                    continue;
                }

                hash.Add(member);
                hash.Add(key.OperatorClass, StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }

        private static PostgreSqlIndexPhysicalMember? Next(ExpectedIndexDefinition input, ref int ordinal)
        {
            while (ordinal < input.Keys.Count)
            {
                if (PostgreSqlSafeMigrationCatalogSqlBuilder.IndexPhysicalMember(input.Keys[ordinal++]) is { } member)
                {
                    return member;
                }
            }

            return null;
        }
    }
}
