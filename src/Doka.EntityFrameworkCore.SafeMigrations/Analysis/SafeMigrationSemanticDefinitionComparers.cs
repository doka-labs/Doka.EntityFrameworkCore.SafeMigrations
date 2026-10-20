namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationSemanticDefinitionComparers
{
    // WHY: Semantic aliases are resolved on the per-operation hot path. These
    // comparers keep lookup proportional to definition arity; hash equality
    // only narrows candidates, while full semantic equality remains decisive.
    public static IEqualityComparer<ExpectedCheckConstraintDefinition> CheckConstraint { get; } =
        new SemanticComparer<ExpectedCheckConstraintDefinition>(
            SafeMigrationDefinitionEquivalence.CheckConstraintSemantics,
            CheckConstraintHash);

    public static IEqualityComparer<ExpectedForeignKeyDefinition> ForeignKey { get; } =
        new SemanticComparer<ExpectedForeignKeyDefinition>(
            SafeMigrationDefinitionEquivalence.ForeignKeySemantics,
            ForeignKeyHash);

    public static IEqualityComparer<ExpectedIndexDefinition> Index { get; } =
        new SemanticComparer<ExpectedIndexDefinition>(
            SafeMigrationDefinitionEquivalence.IndexSemantics,
            IndexHash);

    public static IEqualityComparer<ExpectedUniqueConstraintDefinition> UniqueConstraint { get; } =
        new SemanticComparer<ExpectedUniqueConstraintDefinition>(
            SafeMigrationDefinitionEquivalence.UniqueConstraintSemantics,
            UniqueConstraintHash);

    // WHY: Built-in provider comparers are shared so each projected owner needs
    // neither a comparer allocation nor a capturing equality/hash delegate.
    private static readonly IEqualityComparer<ExpectedIndexDefinition> s_caseInsensitiveColumnIndex =
        new ColumnAwareIndexComparer(StringComparer.OrdinalIgnoreCase);

    private static readonly IEqualityComparer<ExpectedUniqueConstraintDefinition> s_caseInsensitiveColumnUnique =
        new ColumnAwareUniqueComparer(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets equality and hashing that share the exact same typed index-column identity contract.</summary>
    /// <param name="columns">The separately certified column comparer.</param>
    public static IEqualityComparer<ExpectedIndexDefinition> IndexForColumns(StringComparer columns) =>
        ReferenceEquals(columns, StringComparer.Ordinal) ? Index
            : ReferenceEquals(columns, StringComparer.OrdinalIgnoreCase) ? s_caseInsensitiveColumnIndex
            : new ColumnAwareIndexComparer(columns);

    /// <summary>Gets equality and hashing with one certified typed candidate-key column identity contract.</summary>
    /// <param name="columns">The separately certified column comparer.</param>
    public static IEqualityComparer<ExpectedUniqueConstraintDefinition> UniqueForColumns(StringComparer columns) =>
        ReferenceEquals(columns, StringComparer.Ordinal) ? UniqueConstraint
            : ReferenceEquals(columns, StringComparer.OrdinalIgnoreCase) ? s_caseInsensitiveColumnUnique
            : new ColumnAwareUniqueComparer(columns);

    private static int CheckConstraintHash(
        ExpectedCheckConstraintDefinition definition
    )
    {
        var hash = new HashCode();

        AddIdentity(ref hash, definition.Table, definition.Schema);
        hash.Add(definition.Sql, StringComparer.Ordinal);
        hash.Add(definition.CapturedSql, StringComparer.Ordinal);
        AddExpression(ref hash, definition.Expression);

        return hash.ToHashCode();
    }

    private static int ForeignKeyHash(
        ExpectedForeignKeyDefinition definition
    )
    {
        var hash = new HashCode();

        AddIdentity(ref hash, definition.Table, definition.Schema);
        AddStrings(ref hash, definition.Columns);
        AddIdentity(ref hash, definition.PrincipalTable, definition.PrincipalSchema);
        AddStrings(ref hash, definition.PrincipalColumns);
        hash.Add((int)definition.OnUpdate);
        hash.Add((int)definition.OnDelete);

        return hash.ToHashCode();
    }

    private static int IndexHash(
        ExpectedIndexDefinition definition
    ) => IndexHash(definition, StringComparer.Ordinal);

    private static int IndexHash(ExpectedIndexDefinition definition, StringComparer columns)
    {
        var hash = new HashCode();

        AddIdentity(ref hash, definition.Table, definition.Schema);
        hash.Add(definition.Unique);
        hash.Add(definition.Filter, StringComparer.Ordinal);
        AddExpression(ref hash, definition.StructuredFilter);
        hash.Add(definition.Method, StringComparer.Ordinal);
        hash.Add(definition.NullsDistinct);
        hash.Add(definition.Keys.Count);
        foreach (var key in definition.Keys)
        {
            AddIndexKey(ref hash, key, columns);
        }

        AddStrings(ref hash, definition.IncludedColumns, columns);

        return hash.ToHashCode();
    }

    private static int UniqueConstraintHash(
        ExpectedUniqueConstraintDefinition definition
    ) => UniqueConstraintHash(definition, StringComparer.Ordinal);

    private static int UniqueConstraintHash(ExpectedUniqueConstraintDefinition definition, StringComparer columns)
    {
        var hash = new HashCode();

        AddIdentity(ref hash, definition.Table, definition.Schema);
        AddStrings(ref hash, definition.Columns, columns);

        return hash.ToHashCode();
    }

    private static void AddIdentity(
        ref HashCode hash,
        string table,
        string? schema
    )
    {
        hash.Add(table, StringComparer.Ordinal);
        hash.Add(schema, StringComparer.Ordinal);
    }

    private static void AddIndexKey(
        ref HashCode hash,
        ExpectedIndexKeyDefinition key,
        StringComparer columns
    )
    {
        hash.Add(key.Column, columns);
        hash.Add(key.Expression, StringComparer.Ordinal);
        AddExpression(ref hash, key.StructuredExpression);
        hash.Add((int)key.SortOrder);
        hash.Add((int)key.NullOrder);
        hash.Add(key.PrefixLength);
        hash.Add(key.Collation);
        hash.Add(key.OperatorClass, StringComparer.Ordinal);
    }

    private static void AddExpression(
        ref HashCode hash,
        SafeMigrationSqlExpression? expression
    )
    {
        var traversal = new SafeMigrationSqlExpressionTraversal(expression);

        try
        {
            while (traversal.TryTake(out expression, out var collectionCount))
            {
                if (collectionCount >= 0)
                {
                    hash.Add(collectionCount);
                    continue;
                }

                if (expression is null)
                {
                    hash.Add(0);
                    continue;
                }

                switch (expression)
                {
                    case SafeMigrationSqlIdentifierExpression value:
                        hash.Add(1);
                        AddStrings(ref hash, value.Parts);
                        break;
                    case SafeMigrationSqlLiteralExpression value:
                        hash.Add(value.NumericSourceToken.IsEmpty ? 2 : 16);
                        if (!value.NumericSourceToken.IsEmpty)
                        {
                            hash.Add(string.GetHashCode(value.NumericSourceToken.Span, StringComparison.Ordinal));
                        }

                        hash.Add(value.StoreType, StringComparer.Ordinal);
                        AddLiteral(ref hash, value.Value);
                        break;
                    case SafeMigrationSqlUnaryExpression value:
                        hash.Add(3);
                        hash.Add((int)value.Operator);
                        break;
                    case SafeMigrationSqlBinaryExpression value:
                        hash.Add(4);
                        hash.Add((int)value.Operator);
                        break;
                    case SafeMigrationSqlNullTestExpression value:
                        hash.Add(5);
                        hash.Add(value.Negated);
                        break;
                    case SafeMigrationSqlBetweenExpression value:
                        hash.Add(6);
                        hash.Add(value.Negated);
                        break;
                    case SafeMigrationSqlInExpression value:
                        hash.Add(7);
                        hash.Add(value.Negated);
                        break;
                    case SafeMigrationSqlFunctionExpression value:
                        hash.Add(8);
                        hash.Add(value.Name, StringComparer.Ordinal);
                        break;
                    case SafeMigrationSqlCastExpression value:
                        hash.Add(value.UsesSqlServerConvert ? 15 : 9);
                        hash.Add(value.StoreType, StringComparer.Ordinal);
                        break;
                    case SafeMigrationSqlCollateExpression value:
                        hash.Add(10);
                        hash.Add(value.Name, StringComparer.Ordinal);
                        hash.Add(value.Schema, StringComparer.Ordinal);
                        break;
                    case SafeMigrationSqlCurrentValueExpression value:
                        hash.Add(11);
                        hash.Add((int)value.Value);
                        hash.Add(value.Precision);
                        break;
                    case SafeMigrationSqlProviderFragmentExpression value:
                        hash.Add(12);
                        hash.Add(value.ProviderId, StringComparer.Ordinal);
                        hash.Add(value.Sql, StringComparer.Ordinal);
                        break;
                    case SafeMigrationSqlOpaqueExpression value:
                        hash.Add(13);
                        hash.Add(value.FollowsIdentifierRename);
                        hash.Add(value.Sql, StringComparer.Ordinal);
                        break;
                    case SafeMigrationSqlCaseExpression:
                        hash.Add(14);
                        break;
                    default:
                        throw new UnreachableException();
                }
            }
        }
        finally
        {
            traversal.Dispose();
        }
    }

    private static void AddLiteral(
        ref HashCode hash,
        object? value
    )
    {
        if (value is byte[] bytes)
        {
            hash.Add(typeof(byte[]));
            hash.Add(bytes.Length);
            foreach (var item in bytes)
            {
                hash.Add(item);
            }

            return;
        }

        hash.Add(value?.GetType());
        hash.Add(value);
    }

    private static void AddStrings(
        ref HashCode hash,
        IReadOnlyList<string> values,
        StringComparer? comparer = null
    )
    {
        hash.Add(values.Count);
        foreach (var value in values)
        {
            hash.Add(value, comparer ?? StringComparer.Ordinal);
        }
    }

    /// <summary>Preserves O(arity) hashing without normalizing or allocating SQL/expression text.</summary>
    private sealed class ColumnAwareIndexComparer(StringComparer columns) : IEqualityComparer<ExpectedIndexDefinition>
    {
        /// <inheritdoc />
        public bool Equals(ExpectedIndexDefinition? left, ExpectedIndexDefinition? right) =>
            ReferenceEquals(left, right)
            || (left is not null && right is not null
                && SafeMigrationDefinitionEquivalence.IndexSemantics(left, right, columns));

        /// <inheritdoc />
        public int GetHashCode(ExpectedIndexDefinition value) => IndexHash(value, columns);
    }

    /// <summary>Shares candidate-key member hashing with the same column comparer used by equality.</summary>
    private sealed class ColumnAwareUniqueComparer(StringComparer columns)
        : IEqualityComparer<ExpectedUniqueConstraintDefinition>
    {
        /// <inheritdoc />
        public bool Equals(ExpectedUniqueConstraintDefinition? left, ExpectedUniqueConstraintDefinition? right) =>
            ReferenceEquals(left, right)
            || (left is not null && right is not null
                && SafeMigrationDefinitionEquivalence.UniqueConstraintSemantics(left, right, columns));

        /// <inheritdoc />
        public int GetHashCode(ExpectedUniqueConstraintDefinition value) => UniqueConstraintHash(value, columns);
    }

    private sealed class SemanticComparer<T>(
        Func<T, T, bool> equals,
        Func<T, int> hash
    ) : IEqualityComparer<T>
        where T : class
    {
        public bool Equals(
            T? left,
            T? right
        ) => ReferenceEquals(left, right)
            || (left is not null && right is not null && equals(left, right));

        public int GetHashCode(
            T value
        ) => hash(value);
    }
}
