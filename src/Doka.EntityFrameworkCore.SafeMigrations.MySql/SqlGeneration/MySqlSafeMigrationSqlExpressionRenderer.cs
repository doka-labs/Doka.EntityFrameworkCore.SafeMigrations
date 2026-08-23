namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlSafeMigrationSqlExpressionRenderer
{
    private const string ProviderId = "doka_mysql";
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public MySqlSafeMigrationSqlExpressionRenderer(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
    }

    public string Render(
        SafeMigrationSqlExpression expression
    )
    {
        ArgumentNullException.ThrowIfNull(expression);

        var builder = new StringBuilder();
        Append(builder, expression);
        return builder.ToString();
    }

    private void Append(
        StringBuilder builder,
        SafeMigrationSqlExpression expression
    )
    {
        switch (expression)
        {
            case SafeMigrationSqlIdentifierExpression value:
                builder.AppendJoin('.', value.Parts.Select(_sqlGenerationHelper.DelimitIdentifier));
                break;
            case SafeMigrationSqlLiteralExpression value:
                AppendLiteral(builder, value.Value, value.StoreType);
                break;
            case SafeMigrationSqlUnaryExpression value:
                builder.Append(value.Operator == SafeMigrationSqlUnaryOperator.Not ? "(NOT " : "(-");
                Append(builder, value.Operand);
                builder.Append(')');
                break;
            case SafeMigrationSqlBinaryExpression value:
                builder.Append('(');
                Append(builder, value.Left);
                builder
                    .Append(' ')
                    .Append(BinaryOperator(value.Operator))
                    .Append(' ');
                Append(builder, value.Right);
                builder.Append(')');
                break;
            case SafeMigrationSqlNullTestExpression value:
                builder.Append('(');
                Append(builder, value.Operand);
                builder.Append(value.Negated ? " IS NOT NULL)" : " IS NULL)");
                break;
            case SafeMigrationSqlBetweenExpression value:
                builder.Append('(');
                Append(builder, value.Operand);
                builder.Append(value.Negated ? " NOT BETWEEN " : " BETWEEN ");
                Append(builder, value.Lower);
                builder.Append(" AND ");
                Append(builder, value.Upper);
                builder.Append(')');
                break;
            case SafeMigrationSqlInExpression value:
                builder.Append('(');
                Append(builder, value.Operand);
                builder.Append(value.Negated ? " NOT IN (" : " IN (");
                AppendList(builder, value.Values);
                builder.Append("))");
                break;
            case SafeMigrationSqlFunctionExpression value:
                builder
                    .Append(value.Name.ToLowerInvariant())
                    .Append('(');
                AppendList(builder, value.Arguments);
                builder.Append(')');
                break;
            case SafeMigrationSqlCastExpression value:
                builder.Append("CAST(");
                Append(builder, value.Operand);
                builder
                    .Append(" AS ")
                    .Append(value.StoreType)
                    .Append(')');
                break;
            case SafeMigrationSqlCollateExpression value:
                if (value.Schema is not null)
                {
                    throw new NotSupportedException("MySQL and MariaDB collations are not schema-qualified.");
                }

                builder.Append('(');
                Append(builder, value.Operand);
                builder
                    .Append(" COLLATE ")
                    .Append(_sqlGenerationHelper.DelimitIdentifier(value.Name))
                    .Append(')');
                break;
            case SafeMigrationSqlCurrentValueExpression value:
                builder.Append(CurrentValue(value.Value));
                AppendPrecision(builder, value.Precision);
                break;
            case SafeMigrationSqlProviderFragmentExpression value:
                if (!StringComparer.Ordinal.Equals(value.ProviderId, ProviderId))
                {
                    throw new NotSupportedException(
                        $"SQL fragment provider '{value.ProviderId}' cannot be rendered by '{ProviderId}'.");
                }

                builder.Append(value.Sql);
                break;
            case SafeMigrationSqlOpaqueExpression value:
                builder.Append(value.Sql);
                break;
            default:
                throw new UnreachableException();
        }
    }

    private void AppendLiteral(
        StringBuilder builder,
        object? value,
        string? storeType
    )
    {
        if (value is null)
        {
            builder.Append("NULL");
            return;
        }

        var mapping = _typeMappingSource.FindMapping(value.GetType(), storeType)
            ?? throw new NotSupportedException(
                $"MySQL has no type mapping for structured literal '{value.GetType().FullName}'.");

        var literal = mapping.GenerateSqlLiteral(value);
        if (storeType is null)
        {
            builder.Append(literal);
        }
        else
        {
            builder
                .Append("CAST(")
                .Append(literal)
                .Append(" AS ")
                .Append(storeType)
                .Append(')');
        }
    }

    private void AppendList(
        StringBuilder builder,
        IReadOnlyList<SafeMigrationSqlExpression> values
    )
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            Append(builder, values[index]);
        }
    }

    private static string BinaryOperator(
        SafeMigrationSqlBinaryOperator value
    ) => value switch
    {
        SafeMigrationSqlBinaryOperator.And => "AND",
        SafeMigrationSqlBinaryOperator.Or => "OR",
        SafeMigrationSqlBinaryOperator.Equal => "=",
        SafeMigrationSqlBinaryOperator.NotEqual => "<>",
        SafeMigrationSqlBinaryOperator.LessThan => "<",
        SafeMigrationSqlBinaryOperator.LessThanOrEqual => "<=",
        SafeMigrationSqlBinaryOperator.GreaterThan => ">",
        SafeMigrationSqlBinaryOperator.GreaterThanOrEqual => ">=",
        SafeMigrationSqlBinaryOperator.Add => "+",
        SafeMigrationSqlBinaryOperator.Subtract => "-",
        SafeMigrationSqlBinaryOperator.Multiply => "*",
        SafeMigrationSqlBinaryOperator.Divide => "/",
        SafeMigrationSqlBinaryOperator.Modulo => "%",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string CurrentValue(
        SafeMigrationSqlCurrentValue value
    ) => value switch
    {
        SafeMigrationSqlCurrentValue.Date => "CURRENT_DATE",
        SafeMigrationSqlCurrentValue.Time => "CURRENT_TIME",
        SafeMigrationSqlCurrentValue.Timestamp => "CURRENT_TIMESTAMP",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static void AppendPrecision(
        StringBuilder builder,
        int? precision
    )
    {
        if (precision is not null)
        {
            builder
                .Append('(')
                .Append(precision.Value.ToString(CultureInfo.InvariantCulture))
                .Append(')');
        }
    }
}
