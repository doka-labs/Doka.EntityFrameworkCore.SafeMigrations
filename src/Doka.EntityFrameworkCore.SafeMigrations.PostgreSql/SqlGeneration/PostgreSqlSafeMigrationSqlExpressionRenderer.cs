namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed class PostgreSqlSafeMigrationSqlExpressionRenderer
{
    private const string ProviderId = "npgsql_postgresql";
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public PostgreSqlSafeMigrationSqlExpressionRenderer(
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

        var writer = new DelimitedExpressionTextWriter(_sqlGenerationHelper);
        Append(writer, expression, catalogShape: false);

        return writer.ToString();
    }

    public string RenderCatalogCandidateSql(
        SafeMigrationSqlExpression expression,
        Func<string, string> literal
    )
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(literal);

        var writer = new CatalogExpressionTextWriter(literal);
        Append(writer, expression, catalogShape: true);

        return writer.BuildSqlExpression();
    }

    public string RenderCatalogDeparsedCandidateSql(
        SafeMigrationSqlExpression expression,
        Func<string, string> literal
    )
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(literal);

        var writer = new CatalogExpressionTextWriter(literal);
        AppendDeparsed(writer, expression);

        return writer.BuildSqlExpression();
    }

    private void AppendDeparsed(
        IExpressionTextWriter writer,
        SafeMigrationSqlExpression expression
    )
    {
        if (expression is not SafeMigrationSqlBinaryExpression binary)
        {
            Append(writer, expression, catalogShape: true);
            return;
        }

        AppendDeparsedBinary(writer, binary, includeParentheses: true);
    }

    private void AppendDeparsedBinary(
        IExpressionTextWriter writer,
        SafeMigrationSqlBinaryExpression expression,
        bool includeParentheses
    )
    {
        if (includeParentheses)
        {
            writer.Append('(');
        }

        AppendDeparsedBinaryOperand(writer, expression.Left, expression.Operator, isRightOperand: false);
        writer.Append(' ');
        writer.Append(BinaryOperator(expression.Operator));
        writer.Append(' ');
        AppendDeparsedBinaryOperand(writer, expression.Right, expression.Operator, isRightOperand: true);
        if (includeParentheses)
        {
            writer.Append(')');
        }
    }

    private void AppendDeparsedBinaryOperand(
        IExpressionTextWriter writer,
        SafeMigrationSqlExpression operand,
        SafeMigrationSqlBinaryOperator parentOperator,
        bool isRightOperand
    )
    {
        if (operand is not SafeMigrationSqlBinaryExpression binary)
        {
            Append(writer, operand, catalogShape: true);
            return;
        }

        var childPrecedence = BinaryPrecedence(binary.Operator);
        var parentPrecedence = BinaryPrecedence(parentOperator);
        var requiresParentheses = childPrecedence < parentPrecedence
            || (isRightOperand && childPrecedence <= parentPrecedence);

        AppendDeparsedBinary(writer, binary, requiresParentheses);
    }

    private void Append(
        IExpressionTextWriter writer,
        SafeMigrationSqlExpression expression,
        bool catalogShape
    )
    {
        switch (expression)
        {
            case SafeMigrationSqlIdentifierExpression value:
                for (var index = 0; index < value.Parts.Count; index++)
                {
                    if (index > 0)
                    {
                        writer.Append('.');
                    }

                    writer.AppendIdentifier(value.Parts[index]);
                }

                break;
            case SafeMigrationSqlLiteralExpression value:
                AppendLiteral(writer, value.Value, value.StoreType);
                break;
            case SafeMigrationSqlUnaryExpression value:
                writer.Append(value.Operator == SafeMigrationSqlUnaryOperator.Not ? "(NOT " : "(-");
                Append(writer, value.Operand, catalogShape);
                writer.Append(')');
                break;
            case SafeMigrationSqlBinaryExpression value:
                writer.Append('(');
                Append(writer, value.Left, catalogShape);
                writer.Append(' ');
                writer.Append(BinaryOperator(value.Operator));
                writer.Append(' ');
                Append(writer, value.Right, catalogShape);
                writer.Append(')');
                break;
            case SafeMigrationSqlNullTestExpression value:
                writer.Append('(');
                Append(writer, value.Operand, catalogShape);
                writer.Append(value.Negated ? " IS NOT NULL)" : " IS NULL)");
                break;
            case SafeMigrationSqlBetweenExpression value:
                if (catalogShape)
                {
                    writer.Append("((");
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(value.Negated ? " < " : " >= ");
                    Append(writer, value.Lower, catalogShape);
                    writer.Append(value.Negated ? ") OR (" : ") AND (");
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(value.Negated ? " > " : " <= ");
                    Append(writer, value.Upper, catalogShape);
                    writer.Append("))");
                }
                else
                {
                    writer.Append('(');
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(value.Negated ? " NOT BETWEEN " : " BETWEEN ");
                    Append(writer, value.Lower, catalogShape);
                    writer.Append(" AND ");
                    Append(writer, value.Upper, catalogShape);
                    writer.Append(')');
                }

                break;
            case SafeMigrationSqlInExpression value:
                writer.Append('(');
                if (catalogShape)
                {
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(value.Negated ? " <> ALL (ARRAY[" : " = ANY (ARRAY[");
                    AppendList(writer, value.Values, catalogShape);
                    writer.Append("]))");
                }
                else
                {
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(value.Negated ? " NOT IN (" : " IN (");
                    AppendList(writer, value.Values, catalogShape);
                    writer.Append("))");
                }

                break;
            case SafeMigrationSqlFunctionExpression value:
                writer.Append(value.Name.ToLowerInvariant());
                writer.Append('(');
                AppendList(writer, value.Arguments, catalogShape);
                writer.Append(')');
                break;
            case SafeMigrationSqlCastExpression value:
                if (catalogShape)
                {
                    writer.Append('(');
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(")::");
                    writer.Append(value.StoreType);
                }
                else
                {
                    writer.Append("CAST(");
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(" AS ");
                    writer.Append(value.StoreType);
                    writer.Append(')');
                }

                break;
            case SafeMigrationSqlCollateExpression value:
                writer.Append('(');
                Append(writer, value.Operand, catalogShape);
                writer.Append(" COLLATE ");
                if (value.Schema is not null)
                {
                    writer.AppendIdentifier(value.Schema);
                    writer.Append('.');
                }

                writer.AppendIdentifier(value.Name);
                writer.Append(')');
                break;
            case SafeMigrationSqlCurrentValueExpression value:
                writer.Append(CurrentValue(value.Value));
                AppendPrecision(writer, value.Precision);
                break;
            case SafeMigrationSqlProviderFragmentExpression value:
                if (!StringComparer.Ordinal.Equals(value.ProviderId, ProviderId))
                {
                    throw new NotSupportedException(
                        $"SQL fragment provider '{value.ProviderId}' cannot be rendered by '{ProviderId}'.");
                }

                writer.Append(value.Sql);
                break;
            case SafeMigrationSqlOpaqueExpression value:
                writer.Append(value.Sql);
                break;
            default:
                throw new UnreachableException();
        }
    }

    private void AppendLiteral(
        IExpressionTextWriter writer,
        object? value,
        string? storeType
    )
    {
        if (value is null)
        {
            writer.Append("NULL");
            return;
        }

        var mapping = _typeMappingSource.FindMapping(value.GetType(), storeType)
            ?? throw new NotSupportedException(
                $"PostgreSQL has no type mapping for structured literal '{value.GetType().FullName}'.");

        writer.Append(mapping.GenerateSqlLiteral(value));
        if (storeType is not null)
        {
            writer.Append("::");
            writer.Append(storeType);
        }
    }

    private void AppendList(
        IExpressionTextWriter writer,
        IReadOnlyList<SafeMigrationSqlExpression> values,
        bool catalogShape
    )
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                writer.Append(", ");
            }

            Append(writer, values[index], catalogShape);
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

    private static int BinaryPrecedence(
        SafeMigrationSqlBinaryOperator value
    ) => value switch
    {
        SafeMigrationSqlBinaryOperator.Or => 1,
        SafeMigrationSqlBinaryOperator.And => 2,
        SafeMigrationSqlBinaryOperator.Equal
            or SafeMigrationSqlBinaryOperator.NotEqual
            or SafeMigrationSqlBinaryOperator.LessThan
            or SafeMigrationSqlBinaryOperator.LessThanOrEqual
            or SafeMigrationSqlBinaryOperator.GreaterThan
            or SafeMigrationSqlBinaryOperator.GreaterThanOrEqual => 3,
        SafeMigrationSqlBinaryOperator.Add or SafeMigrationSqlBinaryOperator.Subtract => 4,
        SafeMigrationSqlBinaryOperator.Multiply
            or SafeMigrationSqlBinaryOperator.Divide
            or SafeMigrationSqlBinaryOperator.Modulo => 5,
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
        IExpressionTextWriter writer,
        int? precision
    )
    {
        if (precision is not null)
        {
            writer.Append('(');
            writer.Append(precision.Value.ToString(CultureInfo.InvariantCulture));
            writer.Append(')');
        }
    }

    private interface IExpressionTextWriter
    {
        void Append(
            char value
        );

        void Append(
            string value
        );

        void AppendIdentifier(
            string identifier
        );
    }

    private sealed class DelimitedExpressionTextWriter : IExpressionTextWriter
    {
        private readonly StringBuilder _builder = new();
        private readonly ISqlGenerationHelper _sqlGenerationHelper;

        public DelimitedExpressionTextWriter(
            ISqlGenerationHelper sqlGenerationHelper
        )
        {
            _sqlGenerationHelper = sqlGenerationHelper;
        }

        public void Append(
            char value
        ) => _builder.Append(value);

        public void Append(
            string value
        ) => _builder.Append(value);

        public void AppendIdentifier(
            string identifier
        ) => _builder.Append(_sqlGenerationHelper.DelimitIdentifier(identifier));

        public override string ToString() => _builder.ToString();
    }

    private sealed class CatalogExpressionTextWriter : IExpressionTextWriter
    {
        private readonly List<string> _parts = [];
        private readonly Func<string, string> _literal;
        private readonly StringBuilder _text = new();

        public CatalogExpressionTextWriter(
            Func<string, string> literal
        )
        {
            _literal = literal;
        }

        public void Append(
            char value
        ) => _text.Append(value);

        public void Append(
            string value
        ) => _text.Append(value);

        public void AppendIdentifier(
            string identifier
        )
        {
            FlushText();
            _parts.Add($"pg_catalog.quote_ident({_literal(identifier)})");
        }

        public string BuildSqlExpression()
        {
            FlushText();

            return _parts.Count == 1 ? _parts[0] : $"({string.Join(" || ", _parts)})";
        }

        private void FlushText()
        {
            if (_text.Length == 0)
            {
                return;
            }

            _parts.Add(_literal(_text.ToString()));
            _text.Clear();
        }
    }
}
