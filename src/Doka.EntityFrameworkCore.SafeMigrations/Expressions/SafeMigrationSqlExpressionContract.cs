namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationSqlExpressionContract
{
    public static bool Equivalent(
        SafeMigrationSqlExpression? left,
        SafeMigrationSqlExpression? right
    )
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null
            || right is null
            || left.GetType() != right.GetType())
        {
            return false;
        }

        return (left, right) switch
        {
            (SafeMigrationSqlIdentifierExpression first, SafeMigrationSqlIdentifierExpression second) =>
                first.Parts.SequenceEqual(second.Parts, StringComparer.Ordinal),
            (SafeMigrationSqlLiteralExpression first, SafeMigrationSqlLiteralExpression second) =>
                StringComparer.Ordinal.Equals(first.StoreType, second.StoreType)
                && LiteralEquivalent(first.Value, second.Value),
            (SafeMigrationSqlUnaryExpression first, SafeMigrationSqlUnaryExpression second) =>
                first.Operator == second.Operator && Equivalent(first.Operand, second.Operand),
            (SafeMigrationSqlBinaryExpression first, SafeMigrationSqlBinaryExpression second) =>
                first.Operator == second.Operator
                && Equivalent(first.Left, second.Left)
                && Equivalent(first.Right, second.Right),
            (SafeMigrationSqlNullTestExpression first, SafeMigrationSqlNullTestExpression second) =>
                first.Negated == second.Negated && Equivalent(first.Operand, second.Operand),
            (SafeMigrationSqlBetweenExpression first, SafeMigrationSqlBetweenExpression second) =>
                first.Negated == second.Negated
                && Equivalent(first.Operand, second.Operand)
                && Equivalent(first.Lower, second.Lower)
                && Equivalent(first.Upper, second.Upper),
            (SafeMigrationSqlInExpression first, SafeMigrationSqlInExpression second) =>
                first.Negated == second.Negated
                && Equivalent(first.Operand, second.Operand)
                && SequenceEquivalent(first.Values, second.Values),
            (SafeMigrationSqlFunctionExpression first, SafeMigrationSqlFunctionExpression second) =>
                StringComparer.Ordinal.Equals(first.Name, second.Name)
                && SequenceEquivalent(first.Arguments, second.Arguments),
            (SafeMigrationSqlCastExpression first, SafeMigrationSqlCastExpression second) =>
                StringComparer.Ordinal.Equals(first.StoreType, second.StoreType)
                && Equivalent(first.Operand, second.Operand),
            (SafeMigrationSqlCollateExpression first, SafeMigrationSqlCollateExpression second) =>
                StringComparer.Ordinal.Equals(first.Name, second.Name)
                && StringComparer.Ordinal.Equals(first.Schema, second.Schema)
                && Equivalent(first.Operand, second.Operand),
            (SafeMigrationSqlCurrentValueExpression first, SafeMigrationSqlCurrentValueExpression second) =>
                first.Value == second.Value && first.Precision == second.Precision,
            (SafeMigrationSqlProviderFragmentExpression first, SafeMigrationSqlProviderFragmentExpression second) =>
                StringComparer.Ordinal.Equals(first.ProviderId, second.ProviderId)
                && StringComparer.Ordinal.Equals(first.Sql, second.Sql),
            (SafeMigrationSqlOpaqueExpression first, SafeMigrationSqlOpaqueExpression second) =>
                first.FollowsIdentifierRename == second.FollowsIdentifierRename
                && StringComparer.Ordinal.Equals(first.Sql, second.Sql),
            _ => throw new UnreachableException(),
        };
    }

    public static void Write(
        CanonicalHashWriter writer,
        SafeMigrationSqlExpression? expression
    )
    {
        while (true)
        {
            writer.Add(expression is not null);
            if (expression is null)
            {
                return;
            }

            switch (expression)
            {
                case SafeMigrationSqlIdentifierExpression value:
                    writer.Add("identifier");
                    WriteStrings(writer, value.Parts);
                    break;
                case SafeMigrationSqlLiteralExpression value:
                    writer.Add("literal");
                    writer.Add(value.StoreType);
                    WriteLiteral(writer, value.Value);
                    break;
                case SafeMigrationSqlUnaryExpression value:
                    writer.Add("unary");
                    writer.Add((int)value.Operator);
                    expression = value.Operand;
                    continue;
                case SafeMigrationSqlBinaryExpression value:
                    writer.Add("binary");
                    writer.Add((int)value.Operator);
                    Write(writer, value.Left);
                    expression = value.Right;
                    continue;
                case SafeMigrationSqlNullTestExpression value:
                    writer.Add("null_test");
                    writer.Add(value.Negated);
                    expression = value.Operand;
                    continue;
                case SafeMigrationSqlBetweenExpression value:
                    writer.Add("between");
                    writer.Add(value.Negated);
                    Write(writer, value.Operand);
                    Write(writer, value.Lower);
                    expression = value.Upper;
                    continue;
                case SafeMigrationSqlInExpression value:
                    writer.Add("in");
                    writer.Add(value.Negated);
                    Write(writer, value.Operand);
                    writer.Add(value.Values.Count);
                    foreach (var item in value.Values)
                    {
                        Write(writer, item);
                    }

                    break;
                case SafeMigrationSqlFunctionExpression value:
                    writer.Add("function");
                    writer.Add(value.Name);
                    writer.Add(value.Arguments.Count);
                    foreach (var item in value.Arguments)
                    {
                        Write(writer, item);
                    }

                    break;
                case SafeMigrationSqlCastExpression value:
                    writer.Add("cast");
                    writer.Add(value.StoreType);
                    expression = value.Operand;
                    continue;
                case SafeMigrationSqlCollateExpression value:
                    writer.Add("collate");
                    writer.Add(value.Schema);
                    writer.Add(value.Name);
                    expression = value.Operand;
                    continue;
                case SafeMigrationSqlCurrentValueExpression value:
                    writer.Add("current");
                    writer.Add((int)value.Value);
                    writer.Add(value.Precision);
                    break;
                case SafeMigrationSqlProviderFragmentExpression value:
                    writer.Add("provider_fragment");
                    writer.Add(value.ProviderId);
                    writer.Add(value.Sql);
                    break;
                case SafeMigrationSqlOpaqueExpression value:
                    writer.Add("opaque");
                    writer.Add(value.FollowsIdentifierRename);
                    writer.Add(value.Sql);
                    break;
                default:
                    throw new UnreachableException();
            }

            break;
        }
    }

    private static bool SequenceEquivalent(
        IReadOnlyList<SafeMigrationSqlExpression> left,
        IReadOnlyList<SafeMigrationSqlExpression> right
    ) => left.Count == right.Count
        && left
            .Zip(right)
            .All(pair => Equivalent(pair.First, pair.Second));

    private static bool LiteralEquivalent(
        object? left,
        object? right
    ) => left is byte[] leftBytes && right is byte[] rightBytes
        ? leftBytes
            .AsSpan()
            .SequenceEqual(rightBytes)
        : Equals(left, right);

    private static void WriteStrings(
        CanonicalHashWriter writer,
        IReadOnlyList<string> values
    )
    {
        writer.Add(values.Count);
        foreach (var value in values)
        {
            writer.Add(value);
        }
    }

    private static void WriteLiteral(
        CanonicalHashWriter writer,
        object? value
    )
    {
        if (value is null)
        {
            writer.Add("null");
            return;
        }

        writer.Add(value.GetType().FullName ?? value.GetType().Name);
        if (value is byte[] bytes)
        {
            writer.Add(bytes);
            return;
        }

        writer.Add(
            value switch
            {
                float number => number.ToString("R", CultureInfo.InvariantCulture),
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
                DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
                TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
                DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
                TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
                Guid guid => guid.ToString("D"),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            });
    }
}
