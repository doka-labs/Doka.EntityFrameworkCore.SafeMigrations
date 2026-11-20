namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private bool SameSchema(
        string? left,
        string? right
    ) => StringComparer.Ordinal.Equals(
        NormalizeSchema(_objectIdentityNormalizer, left),
        NormalizeSchema(_objectIdentityNormalizer, right));

    private static string? NormalizeSchema(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string? schema
    ) => normalizer is null
        ? schema
        : normalizer.NormalizeSchema(schema);

    private static bool IdentifierEquals(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string left,
        string right
    ) => normalizer is null
        ? StringComparer.Ordinal.Equals(left, right)
        : normalizer.IdentifierComparer.Equals(left, right);

    private static int IdentifierHashCode(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string identifier
    ) => normalizer is null
        ? StringComparer.Ordinal.GetHashCode(identifier)
        : normalizer.IdentifierComparer.GetHashCode(identifier);

    private readonly record struct TableKey(
        string Table,
        string? Schema
    );

    private readonly record struct IndexKey(
        string Table,
        string? Schema,
        string Name
    );

    private sealed class TableKeyComparer : IEqualityComparer<TableKey>
    {
        private readonly ISafeMigrationProviderObjectIdentityNormalizer? _normalizer;

        /// <summary>Initializes physical table identity with the provider's normalization contract.</summary>
        /// <param name="normalizer">The provider identity rules, or ordinal identity when absent.</param>
        public TableKeyComparer(
            ISafeMigrationProviderObjectIdentityNormalizer? normalizer
        )
        {
            _normalizer = normalizer;
        }

        public bool Equals(
            TableKey left,
            TableKey right
        ) => IdentifierEquals(_normalizer, left.Table, right.Table)
            && StringComparer.Ordinal.Equals(Normalize(left.Schema), Normalize(right.Schema));

        public int GetHashCode(
            TableKey value
        ) => HashCode.Combine(
            IdentifierHashCode(_normalizer, value.Table),
            Normalize(value.Schema) is { } schema
                ? StringComparer.Ordinal.GetHashCode(schema)
                : 0);

        private string? Normalize(
            string? schema
        ) => NormalizeSchema(_normalizer, schema);
    }

    private sealed class IndexKeyComparer : IEqualityComparer<IndexKey>
    {
        private readonly StringComparer _nameComparer;
        private readonly TableKeyComparer _tableComparer;

        /// <summary>Initializes index identity while retaining the provider's table and member comparers.</summary>
        /// <param name="normalizer">The provider identity rules, or ordinal identity when absent.</param>
        public IndexKeyComparer(
            ISafeMigrationProviderObjectIdentityNormalizer? normalizer
        )
        {
            _nameComparer = normalizer?.IndexIdentifierComparer ?? StringComparer.Ordinal;
            _tableComparer = new TableKeyComparer(normalizer);
        }

        public bool Equals(
            IndexKey left,
            IndexKey right
        ) => _tableComparer.Equals(new TableKey(left.Table, left.Schema), new TableKey(right.Table, right.Schema))
            && _nameComparer.Equals(left.Name, right.Name);

        public int GetHashCode(
            IndexKey value
        ) => HashCode.Combine(
            _tableComparer.GetHashCode(new TableKey(value.Table, value.Schema)),
            _nameComparer.GetHashCode(value.Name));
    }

    private sealed class IdentifierComparer : IEqualityComparer<string>
    {
        private readonly StringComparer _comparer;

        /// <summary>Initializes member identity with the most specific available provider comparer.</summary>
        /// <param name="normalizer">The provider identity rules, or ordinal identity when absent.</param>
        /// <param name="memberComparer">The optional member-specific comparer.</param>
        public IdentifierComparer(
            ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
            StringComparer? memberComparer = null
        )
        {
            _comparer = memberComparer ?? normalizer?.IdentifierComparer ?? StringComparer.Ordinal;
        }

        public bool Equals(
            string? left,
            string? right
        ) => ReferenceEquals(left, right) || (left is not null && right is not null && _comparer.Equals(left, right));

        public int GetHashCode(
            string value
        ) => _comparer.GetHashCode(value);
    }
}
