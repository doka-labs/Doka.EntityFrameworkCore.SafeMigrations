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
    ) => normalizer is null ? schema : normalizer.NormalizeSchema(schema);

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

    private sealed class TableKeyComparer(ISafeMigrationProviderObjectIdentityNormalizer? normalizer)
        : IEqualityComparer<TableKey>
    {
        public bool Equals(
            TableKey left,
            TableKey right
        ) => IdentifierEquals(normalizer, left.Table, right.Table)
            && StringComparer.Ordinal.Equals(Normalize(left.Schema), Normalize(right.Schema));

        public int GetHashCode(
            TableKey value
        ) => HashCode.Combine(
            IdentifierHashCode(normalizer, value.Table),
            Normalize(value.Schema) is { } schema ? StringComparer.Ordinal.GetHashCode(schema) : 0);

        private string? Normalize(
            string? schema
        ) => NormalizeSchema(normalizer, schema);
    }

    private sealed class IndexKeyComparer(ISafeMigrationProviderObjectIdentityNormalizer? normalizer)
        : IEqualityComparer<IndexKey>
    {
        private readonly TableKeyComparer _tableComparer = new(normalizer);
        private readonly StringComparer _nameComparer = normalizer?.IndexIdentifierComparer ?? StringComparer.Ordinal;

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

    private sealed class IdentifierComparer(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        StringComparer? memberComparer = null
    ) : IEqualityComparer<string>
    {
        private readonly StringComparer _comparer = memberComparer
            ?? normalizer?.IdentifierComparer ?? StringComparer.Ordinal;

        public bool Equals(
            string? left,
            string? right
        ) => ReferenceEquals(left, right) || (left is not null && right is not null && _comparer.Equals(left, right));

        public int GetHashCode(
            string value
        ) => _comparer.GetHashCode(value);
    }
}
