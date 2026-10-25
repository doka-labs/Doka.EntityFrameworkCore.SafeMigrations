namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Renders structured expressions through SQLite type mappings without recursive traversal.</summary>
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

        var pending = default(SafeMigrationWorkStack<ExpressionFrame>);

        try
        {
            while (true)
            {
                var feature = LocalUnsupportedFeature(expression);
                if (feature is not null)
                {
                    return feature;
                }

                var child = GetChild(expression, 0);
                if (child is not null)
                {
                    // WHY: Retain only the sibling cursor; wide lists must not rent one frame per item.
                    if (GetChild(expression, 1) is not null)
                    {
                        pending.Push(new(expression, 1));
                    }

                    expression = child;
                    continue;
                }

                if (!pending.TryPop(out var frame))
                {
                    return null;
                }

                expression = GetChild(frame.Expression, frame.Position)!;
                frame.Position++;
                if (GetChild(frame.Expression, frame.Position) is not null)
                {
                    pending.Push(frame);
                }
            }
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Renders one child at a time, preserving the original left-to-right mapping order.</summary>
    private void Append(
        StringBuilder builder,
        SafeMigrationSqlExpression expression
    )
    {
        var pending = default(SafeMigrationWorkStack<ExpressionFrame>);
        var frame = new ExpressionFrame(expression, 0);

        try
        {
            while (true)
            {
                if (frame.Position == 0)
                {
                    AppendPrefix(builder, frame.Expression);
                }

                var child = GetChild(frame.Expression, frame.Position);
                if (child is not null)
                {
                    AppendChildSeparator(builder, frame.Expression, frame.Position);
                    frame.Position++;
                    pending.Push(frame);
                    frame = new(child, 0);
                    continue;
                }

                AppendSuffix(builder, frame.Expression);
                if (!pending.TryPop(out frame))
                {
                    return;
                }
            }
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Writes node prefixes and validates node-local boundaries before visiting any operands.</summary>
    private void AppendPrefix(StringBuilder builder, SafeMigrationSqlExpression expression)
    {
        switch (expression)
        {
            case SafeMigrationSqlIdentifierExpression value:
                builder.AppendJoin('.', value.Parts.Select(_sqlGenerationHelper.DelimitIdentifier));
                break;
            case SafeMigrationSqlLiteralExpression value:
                if (!value.NumericSourceToken.IsEmpty)
                {
                    builder.Append(value.NumericSourceToken.Span);
                }
                else
                {
                    AppendLiteral(builder, value);
                }

                break;
            case SafeMigrationSqlUnaryExpression value:
                builder.Append(value.Operator == SafeMigrationSqlUnaryOperator.Not ? "(NOT " : "(-");
                break;
            case SafeMigrationSqlBinaryExpression or SafeMigrationSqlNullTestExpression
                or SafeMigrationSqlBetweenExpression or SafeMigrationSqlInExpression:
                builder.Append('(');
                break;
            case SafeMigrationSqlFunctionExpression value:
                builder
                    .Append(value.Name)
                    .Append('(');
                break;
            case SafeMigrationSqlCastExpression value:
                if (value.UsesSqlServerConvert)
                {
                    throw new NotSupportedException("Type-first CONVERT syntax requires SQL Server.");
                }

                if (!IsStoreType(value.StoreType))
                {
                    throw new NotSupportedException($"SQLite store type '{value.StoreType}' is not safe to render.");
                }

                builder.Append("CAST(");
                break;
            case SafeMigrationSqlCaseExpression:
                builder.Append("CASE");
                break;
            case SafeMigrationSqlCollateExpression value:
                if (value.Schema is not null)
                {
                    throw new NotSupportedException("SQLite collations are not schema-qualified.");
                }

                builder.Append('(');
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

    /// <summary>Writes separators exactly when their preceding operand has completed.</summary>
    private static void AppendChildSeparator(
        StringBuilder builder,
        SafeMigrationSqlExpression expression,
        int position
    )
    {
        switch (expression)
        {
            case SafeMigrationSqlBinaryExpression value when position == 1:
                builder.Append(' ').Append(BinaryOperator(value.Operator)).Append(' ');
                break;
            case SafeMigrationSqlBetweenExpression value when position == 1:
                builder.Append(value.Negated ? " NOT BETWEEN " : " BETWEEN ");
                break;
            case SafeMigrationSqlBetweenExpression when position == 2:
                builder.Append(" AND ");
                break;
            case SafeMigrationSqlInExpression value when position == 1:
                builder.Append(value.Negated ? " NOT IN (" : " IN (");
                break;
            case SafeMigrationSqlInExpression when position > 1:
            case SafeMigrationSqlFunctionExpression when position > 0:
                builder.Append(", ");
                break;
            case SafeMigrationSqlCaseExpression value:
                builder.Append(position / 2 == value.WhenClauses.Count
                    ? " ELSE "
                    : position % 2 == 0 ? " WHEN " : " THEN ");
                break;
        }
    }

    /// <summary>Writes suffixes after every child without changing parenthesization or identifier quoting.</summary>
    private void AppendSuffix(StringBuilder builder, SafeMigrationSqlExpression expression)
    {
        switch (expression)
        {
            case SafeMigrationSqlUnaryExpression or SafeMigrationSqlBinaryExpression
                or SafeMigrationSqlBetweenExpression or SafeMigrationSqlFunctionExpression:
                builder.Append(')');
                break;
            case SafeMigrationSqlNullTestExpression value:
                builder.Append(value.Negated ? " IS NOT NULL)" : " IS NULL)");
                break;
            case SafeMigrationSqlInExpression:
                builder.Append("))");
                break;
            case SafeMigrationSqlCastExpression value:
                builder.Append(" AS ").Append(value.StoreType).Append(')');
                break;
            case SafeMigrationSqlCaseExpression:
                builder.Append(" END");
                break;
            case SafeMigrationSqlCollateExpression value:
                builder.Append(" COLLATE ").Append(_sqlGenerationHelper.DelimitIdentifier(value.Name)).Append(')');
                break;
        }
    }

    /// <summary>Enumerates one immutable child by ordinal without allocating or flattening sibling lists.</summary>
    private static SafeMigrationSqlExpression? GetChild(SafeMigrationSqlExpression expression, int position) =>
        expression switch
        {
            SafeMigrationSqlUnaryExpression value when position == 0 => value.Operand,
            SafeMigrationSqlBinaryExpression value when position == 0 => value.Left,
            SafeMigrationSqlBinaryExpression value when position == 1 => value.Right,
            SafeMigrationSqlNullTestExpression value when position == 0 => value.Operand,
            SafeMigrationSqlBetweenExpression value when position == 0 => value.Operand,
            SafeMigrationSqlBetweenExpression value when position == 1 => value.Lower,
            SafeMigrationSqlBetweenExpression value when position == 2 => value.Upper,
            SafeMigrationSqlInExpression value when position == 0 => value.Operand,
            SafeMigrationSqlInExpression value when position <= value.Values.Count => value.Values[position - 1],
            SafeMigrationSqlFunctionExpression value when position < value.Arguments.Count => value.Arguments[position],
            SafeMigrationSqlCastExpression value when position == 0 => value.Operand,
            SafeMigrationSqlCollateExpression value when position == 0 => value.Operand,
            SafeMigrationSqlCaseExpression value when position / 2 < value.WhenClauses.Count => position % 2 == 0
                ? value.WhenClauses[position / 2].Condition
                : value.WhenClauses[position / 2].Result,
            SafeMigrationSqlCaseExpression value when position % 2 == 0
                && position / 2 == value.WhenClauses.Count => value.ElseResult,
            _ => null,
        };

    /// <summary>Checks only the current node so validation retains its original first-failure behavior.</summary>
    private string? LocalUnsupportedFeature(SafeMigrationSqlExpression expression) => expression switch
    {
        SafeMigrationSqlLiteralExpression value => LiteralUnsupportedFeature(value),
        SafeMigrationSqlCastExpression { UsesSqlServerConvert: true } => "structured_convert_dialect",
        SafeMigrationSqlCastExpression value when !IsStoreType(value.StoreType) => "structured_cast_type",
        SafeMigrationSqlCollateExpression { Schema: not null } => "schema_qualified_expression_collation",
        SafeMigrationSqlCurrentValueExpression { Precision: not null } => "current_value_precision",
        SafeMigrationSqlProviderFragmentExpression value =>
            StringComparer.Ordinal.Equals(value.ProviderId, ProviderId) ? null : "provider_fragment_mismatch",
        SafeMigrationSqlIdentifierExpression or SafeMigrationSqlUnaryExpression or SafeMigrationSqlBinaryExpression
            or SafeMigrationSqlNullTestExpression or SafeMigrationSqlBetweenExpression or SafeMigrationSqlInExpression
            or SafeMigrationSqlFunctionExpression or SafeMigrationSqlCastExpression or SafeMigrationSqlCollateExpression
            or SafeMigrationSqlCaseExpression or SafeMigrationSqlCurrentValueExpression
            or SafeMigrationSqlOpaqueExpression => null,
        _ => throw new UnreachableException(),
    };

    /// <summary>Retains the next child so traversal storage grows with depth rather than list width.</summary>
    private struct ExpressionFrame(SafeMigrationSqlExpression expression, int position)
    {
        /// <summary>Gets the node whose ordered children are being visited.</summary>
        public SafeMigrationSqlExpression Expression { get; } = expression;

        /// <summary>Gets or sets the next child ordinal.</summary>
        public int Position { get; set; } = position;
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

    private string? LiteralUnsupportedFeature(
        SafeMigrationSqlLiteralExpression expression
    ) => expression.StoreType is not null && !IsStoreType(expression.StoreType)
        ? "structured_cast_type"
        : expression.Value is not null
        && _typeMappingSource.FindMapping(expression.Value.GetType(), expression.StoreType) is null
        && _typeMappingSource.FindMapping(expression.Value.GetType()) is null
            ? "structured_literal_mapping"
            : null;

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
