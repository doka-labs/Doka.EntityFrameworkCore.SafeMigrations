namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Renders bounded structured expressions through SQLite type mappings.</summary>
internal sealed class SqliteSafeMigrationSqlExpressionRenderer
{
    internal const string ProviderId = "microsoft_sqlite";

    private static readonly Regex s_storeTypePattern = new(
        "^[A-Za-z][A-Za-z0-9_ ]*(?:\\([0-9]+(?:\\s*,\\s*[0-9]+)?\\))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    /// <summary>Initializes the SQLite structured-expression renderer.</summary>
    public SqliteSafeMigrationSqlExpressionRenderer(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
    }

    /// <summary>Renders a validated structured expression as SQLite SQL.</summary>
    public string Render(
        SafeMigrationSqlExpression expression
    )
    {
        ArgumentNullException.ThrowIfNull(expression);

        var builder = new StringBuilder();
        Append(builder, expression);

        return builder.ToString();
    }

    /// <summary>Gets the first provider boundary that prevents safe rendering.</summary>
    public string? GetUnsupportedFeature(
        SafeMigrationSqlExpression expression
    )
    {
        ArgumentNullException.ThrowIfNull(expression);

        return expression switch
        {
            SafeMigrationSqlIdentifierExpression => null,
            SafeMigrationSqlLiteralExpression value => LiteralUnsupportedFeature(value),
            SafeMigrationSqlUnaryExpression value => GetUnsupportedFeature(value.Operand),
            SafeMigrationSqlBinaryExpression value =>
                GetUnsupportedFeature(value.Left) ?? GetUnsupportedFeature(value.Right),
            SafeMigrationSqlNullTestExpression value => GetUnsupportedFeature(value.Operand),
            SafeMigrationSqlBetweenExpression value => GetUnsupportedFeature(value.Operand)
                ?? GetUnsupportedFeature(value.Lower) ?? GetUnsupportedFeature(value.Upper),
            SafeMigrationSqlInExpression value => GetUnsupportedFeature(value.Operand)
                ?? value
                    .Values
                    .Select(GetUnsupportedFeature)
                    .FirstOrDefault(static result => result is not null),
            SafeMigrationSqlFunctionExpression value => value
                .Arguments
                .Select(GetUnsupportedFeature)
                .FirstOrDefault(static result => result is not null),
            SafeMigrationSqlCastExpression value =>
                IsStoreType(value.StoreType) ? GetUnsupportedFeature(value.Operand) : "structured_cast_type",
            SafeMigrationSqlCollateExpression { Schema: not null } => "schema_qualified_expression_collation",
            SafeMigrationSqlCollateExpression value => GetUnsupportedFeature(value.Operand),
            SafeMigrationSqlCurrentValueExpression { Precision: not null } => "current_value_precision",
            SafeMigrationSqlCurrentValueExpression => null,
            SafeMigrationSqlProviderFragmentExpression value =>
                StringComparer.Ordinal.Equals(value.ProviderId, ProviderId) ? null : "provider_fragment_mismatch",
            SafeMigrationSqlOpaqueExpression => null,
            _ => throw new UnreachableException(),
        };
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
                AppendLiteral(builder, value);
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
                    .Append(value.Name)
                    .Append('(');
                AppendList(builder, value.Arguments);
                builder.Append(')');
                break;
            case SafeMigrationSqlCastExpression value:
                AppendCast(builder, value.Operand, value.StoreType);
                break;
            case SafeMigrationSqlCollateExpression value:
                if (value.Schema is not null)
                {
                    throw new NotSupportedException("SQLite collations are not schema-qualified.");
                }

                builder.Append('(');
                Append(builder, value.Operand);
                builder
                    .Append(" COLLATE ")
                    .Append(_sqlGenerationHelper.DelimitIdentifier(value.Name))
                    .Append(')');
                break;
            case SafeMigrationSqlCurrentValueExpression value:
                if (value.Precision is not null)
                {
                    throw new NotSupportedException("SQLite current-value expressions do not accept precision.");
                }

                builder.Append(
                    value.Value switch
                    {
                        SafeMigrationSqlCurrentValue.Date => "CURRENT_DATE",
                        SafeMigrationSqlCurrentValue.Time => "CURRENT_TIME",
                        SafeMigrationSqlCurrentValue.Timestamp => "CURRENT_TIMESTAMP",
                        _ => throw new UnreachableException(),
                    });
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
        SafeMigrationSqlLiteralExpression expression
    )
    {
        if (expression.StoreType is not null
            && !IsStoreType(expression.StoreType))
        {
            throw new NotSupportedException($"SQLite store type '{expression.StoreType}' is not safe to render.");
        }

        var mapping = expression.Value is null
            ? null
            : _typeMappingSource.FindMapping(expression.Value.GetType(), expression.StoreType)
            ?? _typeMappingSource.FindMapping(expression.Value.GetType());

        var literal = expression.Value is null
            ? "NULL"
            : mapping?.GenerateSqlLiteral(expression.Value)
            ?? throw new NotSupportedException(
                $"SQLite has no type mapping for structured literal '{expression.Value.GetType().FullName}'.");

        if (expression.StoreType is null)
        {
            builder.Append(literal);
            return;
        }

        builder
            .Append("CAST(")
            .Append(literal)
            .Append(" AS ")
            .Append(expression.StoreType)
            .Append(')');
    }

    private void AppendCast(
        StringBuilder builder,
        SafeMigrationSqlExpression operand,
        string storeType
    )
    {
        if (!IsStoreType(storeType))
        {
            throw new NotSupportedException($"SQLite store type '{storeType}' is not safe to render.");
        }

        builder.Append("CAST(");
        Append(builder, operand);
        builder
            .Append(" AS ")
            .Append(storeType)
            .Append(')');
    }

    private string? LiteralUnsupportedFeature(
        SafeMigrationSqlLiteralExpression expression
    ) => expression.StoreType is not null && !IsStoreType(expression.StoreType)
        ? "structured_cast_type"
        : expression.Value is not null
        && _typeMappingSource.FindMapping(expression.Value.GetType(), expression.StoreType) is null
        && _typeMappingSource.FindMapping(expression.Value.GetType()) is null
            ? "structured_literal_mapping"
            : null;

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

    private static bool IsStoreType(
        string storeType
    ) => s_storeTypePattern.IsMatch(storeType);

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
}
