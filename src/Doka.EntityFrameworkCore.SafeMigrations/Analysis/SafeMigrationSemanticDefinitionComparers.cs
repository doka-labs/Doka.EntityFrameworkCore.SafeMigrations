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

    private static int CheckConstraintHash(
        ExpectedCheckConstraintDefinition definition
    )
    {
        var hash = new HashCode();

        AddIdentity(ref hash, definition.Table, definition.Schema);
        hash.Add(definition.Sql, StringComparer.Ordinal);
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
    )
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
            AddIndexKey(ref hash, key);
        }

        AddStrings(ref hash, definition.IncludedColumns);

        return hash.ToHashCode();
    }

    private static int UniqueConstraintHash(
        ExpectedUniqueConstraintDefinition definition
    )
    {
        var hash = new HashCode();

        AddIdentity(ref hash, definition.Table, definition.Schema);
        AddStrings(ref hash, definition.Columns);

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
        ExpectedIndexKeyDefinition key
    )
    {
        hash.Add(key.Column, StringComparer.Ordinal);
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
        if (expression is null)
        {
            hash.Add(0);
            return;
        }

        switch (expression)
        {
            case SafeMigrationSqlIdentifierExpression value:
                hash.Add(1);
                AddStrings(ref hash, value.Parts);
                break;
            case SafeMigrationSqlLiteralExpression value:
                hash.Add(2);
                hash.Add(value.StoreType, StringComparer.Ordinal);
                AddLiteral(ref hash, value.Value);
                break;
            case SafeMigrationSqlUnaryExpression value:
                hash.Add(3);
                hash.Add((int)value.Operator);
                AddExpression(ref hash, value.Operand);
                break;
            case SafeMigrationSqlBinaryExpression value:
                hash.Add(4);
                hash.Add((int)value.Operator);
                AddExpression(ref hash, value.Left);
                AddExpression(ref hash, value.Right);
                break;
            case SafeMigrationSqlNullTestExpression value:
                hash.Add(5);
                hash.Add(value.Negated);
                AddExpression(ref hash, value.Operand);
                break;
            case SafeMigrationSqlBetweenExpression value:
                hash.Add(6);
                hash.Add(value.Negated);
                AddExpression(ref hash, value.Operand);
                AddExpression(ref hash, value.Lower);
                AddExpression(ref hash, value.Upper);
                break;
            case SafeMigrationSqlInExpression value:
                hash.Add(7);
                hash.Add(value.Negated);
                AddExpression(ref hash, value.Operand);
                AddExpressions(ref hash, value.Values);
                break;
            case SafeMigrationSqlFunctionExpression value:
                hash.Add(8);
                hash.Add(value.Name, StringComparer.Ordinal);
                AddExpressions(ref hash, value.Arguments);
                break;
            case SafeMigrationSqlCastExpression value:
                hash.Add(9);
                hash.Add(value.StoreType, StringComparer.Ordinal);
                AddExpression(ref hash, value.Operand);
                break;
            case SafeMigrationSqlCollateExpression value:
                hash.Add(10);
                hash.Add(value.Name, StringComparer.Ordinal);
                hash.Add(value.Schema, StringComparer.Ordinal);
                AddExpression(ref hash, value.Operand);
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
            default:
                throw new UnreachableException();
        }
    }

    private static void AddExpressions(
        ref HashCode hash,
        IReadOnlyList<SafeMigrationSqlExpression> expressions
    )
    {
        hash.Add(expressions.Count);
        foreach (var expression in expressions)
        {
            AddExpression(ref hash, expression);
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
        IReadOnlyList<string> values
    )
    {
        hash.Add(values.Count);
        foreach (var value in values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }
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
