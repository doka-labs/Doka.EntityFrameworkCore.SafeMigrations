namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Validates and renders provider-neutral expressions for SQL Server.</summary>
internal sealed class SqlServerSafeMigrationSqlExpressionRenderer
{
    private const string ProviderId = "efcore_sqlserver";

    private static readonly HashSet<string> s_supportedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABS", "COALESCE", "CONCAT", "LEN", "LOWER", "NULLIF", "UPPER",
    };

    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;

    /// <summary>Initializes the provider's structured expression renderer.</summary>
    /// <param name="typeMappingSource">The canonical store-type and literal mappings.</param>
    /// <param name="sqlGenerationHelper">The identifier delimiter.</param>
    public SqlServerSafeMigrationSqlExpressionRenderer(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
    }

    /// <summary>Finds the first expression feature without a proven SQL Server contract.</summary>
    /// <param name="expression">The immutable expression tree.</param>
    /// <returns>A stable rejection code, or null when every node is supported.</returns>
    public string? GetUnsupportedFeature(
        SafeMigrationSqlExpression expression
    )
    {
        ArgumentNullException.ThrowIfNull(expression);

        var pending = default(SafeMigrationWorkStack<ValidationFrame>);
        var current = new ValidationFrame(expression, 0);
        try
        {
            do
            {
                var feature = current.Index == 0 ? GetNodeFeature(current.Expression, current.CaseDepth) : null;
                if (feature is not null)
                {
                    return feature;
                }

                // WHY: Resume lists one child at a time. A public expression can
                // be arbitrarily deep or wide without consuming the call stack
                // or reserving a second array for all its siblings.
                if (TryGetValidationChild(current, out var child))
                {
                    var continuation = current with { Index = current.Index + 1 };
                    if (TryGetValidationChild(continuation, out _))
                    {
                        pending.Push(continuation);
                    }

                    current = child;
                    continue;
                }

                if (!pending.TryPop(out current))
                {
                    return null;
                }
            }
            while (true);
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Retains SQL Server's ten-level CASE limit across every nested expression role.</summary>
    private string? GetNodeFeature(
        SafeMigrationSqlExpression expression,
        int caseDepth
    )
    {
        if (caseDepth > 10)
        {
            return "structured_case_nesting";
        }

        return expression switch
        {
            SafeMigrationSqlIdentifierExpression => null,
            SafeMigrationSqlLiteralExpression value => GetLiteralFeature(value),
            SafeMigrationSqlUnaryExpression or SafeMigrationSqlBinaryExpression
                or SafeMigrationSqlNullTestExpression or SafeMigrationSqlBetweenExpression
                or SafeMigrationSqlInExpression => null,
            SafeMigrationSqlFunctionExpression value => GetFunctionFeature(value),
            SafeMigrationSqlCaseExpression => caseDepth >= 10 ? "structured_case_nesting" : null,
            SafeMigrationSqlCastExpression value => IsSupportedStoreType(value.StoreType)
                ? null : "structured_cast_type",
            SafeMigrationSqlCollateExpression value => value.Schema is not null
                ? "structured_collation_schema"
                : IsSqlServerCollationName(value.Name)
                    ? null
                    : "structured_collation_name",
            SafeMigrationSqlCurrentValueExpression => null,
            SafeMigrationSqlProviderFragmentExpression value =>
                StringComparer.Ordinal.Equals(value.ProviderId, ProviderId)
                    ? null
                    : "provider_fragment_mismatch",
            SafeMigrationSqlOpaqueExpression => "opaque_sql_expression",
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>Recognizes SQL Server's documented zero-argument system-time functions.</summary>
    /// <param name="name">The unqualified function name.</param>
    /// <returns>True only for a supported current temporal builtin.</returns>
    internal static bool IsCurrentTemporalFunction(
        string name
    ) => name.ToUpperInvariant() is "GETDATE" or "GETUTCDATE" or "SYSDATETIME"
        or "SYSUTCDATETIME" or "SYSDATETIMEOFFSET";

    private static string? GetFunctionFeature(
        SafeMigrationSqlFunctionExpression expression
    )
    {
        var temporal = IsCurrentTemporalFunction(expression.Name);
        if (!temporal && !s_supportedFunctions.Contains(expression.Name))
        {
            return "structured_function";
        }

        var count = expression.Arguments.Count;
        var valid = temporal ? count == 0 : expression.Name.ToUpperInvariant() switch
        {
            "ABS" or "LEN" or "LOWER" or "UPPER" => count == 1,
            "NULLIF" => count == 2,
            "COALESCE" => count >= 2,
            "CONCAT" => count is >= 2 and <= 254,
            _ => false,
        };

        return valid ? null : "structured_function_arity";
    }

    /// <summary>Enumerates native roles in their original left-to-right validation order.</summary>
    private static bool TryGetValidationChild(
        ValidationFrame frame,
        out ValidationFrame child
    )
    {
        var expression = frame.Expression;
        var index = frame.Index;
        var next = expression switch
        {
            SafeMigrationSqlUnaryExpression value when index == 0 => value.Operand,
            SafeMigrationSqlBinaryExpression value when index < 2 => index == 0 ? value.Left : value.Right,
            SafeMigrationSqlNullTestExpression value when index == 0 => value.Operand,
            SafeMigrationSqlBetweenExpression value when index < 3
                => index == 0 ? value.Operand : index == 1 ? value.Lower : value.Upper,
            SafeMigrationSqlInExpression value when index == 0 => value.Operand,
            SafeMigrationSqlInExpression value when index <= value.Values.Count => value.Values[index - 1],
            SafeMigrationSqlFunctionExpression value when index < value.Arguments.Count => value.Arguments[index],
            SafeMigrationSqlCastExpression value when index == 0 => value.Operand,
            SafeMigrationSqlCollateExpression value when index == 0 => value.Operand,
            SafeMigrationSqlCaseExpression value when index / 2 < value.WhenClauses.Count
                => index % 2 == 0 ? value.WhenClauses[index / 2].Condition : value.WhenClauses[index / 2].Result,
            SafeMigrationSqlCaseExpression value when index == value.WhenClauses.Count * 2 => value.ElseResult,
            _ => null,
        };

        if (next is null)
        {
            child = default;

            return false;
        }

        var depth = frame.CaseDepth;
        if (expression is SafeMigrationSqlCaseExpression @case)
        {
            var result = index % 2 != 0 || index == @case.WhenClauses.Count * 2;
            depth += 1 + (result && IsPredicate(next) ? 1 : 0);
        }

        child = new ValidationFrame(next, depth);

        return true;
    }

    /// <summary>Retains one pending child and the native CASE depth without recursive calls.</summary>
    private readonly record struct ValidationFrame(SafeMigrationSqlExpression Expression, int CaseDepth, int Index = 0);

    /// <summary>Validates and renders a supported expression without untrusted SQL substitution.</summary>
    /// <param name="expression">The immutable expression tree.</param>
    /// <returns>The SQL Server expression text.</returns>
    public string Render(
        SafeMigrationSqlExpression expression
    )
    {
        ArgumentNullException.ThrowIfNull(expression);

        var feature = GetUnsupportedFeature(expression);
        if (feature is not null)
        {
            throw new NotSupportedException($"SQL Server cannot render expression feature '{feature}'.");
        }

        var builder = new StringBuilder();
        Append(builder, expression);

        return builder.ToString();
    }

    private string? GetLiteralFeature(
        SafeMigrationSqlLiteralExpression expression
    )
    {
        if (expression.StoreType is not null && !IsSupportedStoreType(expression.StoreType))
        {
            return "structured_cast_type";
        }

        if (expression.Value is not null
            && (!SqlServerSafeMigrationCatalogSqlBuilder.IsSupportedScalarClrType(expression.Value.GetType())
                || _typeMappingSource.FindMapping(expression.Value.GetType()) is null))
        {
            return "structured_literal_mapping";
        }

        return null;
    }

    private bool IsSupportedStoreType(
        string storeType
    )
    {
        // WHY: Provider mappings can preserve caller-supplied store-type text.
        // Restrict a CAST target to one built-in type token with a simple facet
        // clause before embedding it in generated SQL.
        var opening = storeType.IndexOf('(');
        var name = (opening < 0 ? storeType : storeType[..opening]).Trim();
        if (!IsBuiltInStoreType(name))
        {
            return false;
        }

        if (opening >= 0)
        {
            var facet = storeType.AsSpan(opening);
            if (facet.Length < 3 || facet[0] != '(' || facet[^1] != ')')
            {
                return false;
            }

            var content = facet[1..^1].Trim();
            if (!content.Equals("max", StringComparison.OrdinalIgnoreCase)
                && (content.Length == 0
                    || content.IndexOfAnyExceptInRange('0', '9') >= 0
                    && !IsDecimalFacet(content)))
            {
                return false;
            }
        }

        return _typeMappingSource.FindMapping(storeType) is not null;
    }

    private static bool IsDecimalFacet(
        ReadOnlySpan<char> content
    )
    {
        var comma = content.IndexOf(',');

        return comma > 0
            && comma < content.Length - 1
            && content[..comma].Trim().IndexOfAnyExceptInRange('0', '9') < 0
            && content[(comma + 1)..].Trim().IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool IsBuiltInStoreType(
        string name
    ) => name.ToLowerInvariant() is
        "bigint" or "binary" or "bit" or "char" or "date" or "datetime" or "datetime2"
        or "datetimeoffset" or "decimal" or "float" or "int" or "money" or "nchar"
        or "numeric" or "nvarchar" or "real" or "smalldatetime" or "smallint"
        or "smallmoney" or "time" or "tinyint" or "uniqueidentifier" or "varbinary"
        or "varchar";

    private static bool IsSqlServerCollationName(
        string name
    )
    {
        if (name.Length is 0 or > 128 || !char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        for (var index = 1; index < name.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(name[index]) && name[index] != '_')
            {
                return false;
            }
        }

        return true;
    }

    private void Append(
        StringBuilder builder,
        SafeMigrationSqlExpression expression
    )
    {
        var pending = default(SafeMigrationWorkStack<RenderFrame>);
        var current = new RenderFrame(expression);
        try
        {
            do
            {
                if (current.Text is { } text)
                {
                    builder.Append(text);
                }
                else if (current.Role == RenderRole.List)
                {
                    var values = current.Expression is SafeMigrationSqlInExpression predicate
                        ? predicate.Values : ((SafeMigrationSqlFunctionExpression)current.Expression!).Arguments;

                    if (current.Index < values.Count)
                    {
                        if (current.Index > 0)
                        {
                            builder.Append(", ");
                        }

                        pending.Push(current with { Index = current.Index + 1 });
                        current = new RenderFrame(values[current.Index]);
                        continue;
                    }
                }
                else if (current.Role == RenderRole.Case)
                {
                    var conditional = (SafeMigrationSqlCaseExpression)current.Expression!;
                    if (current.Index < conditional.WhenClauses.Count)
                    {
                        var clause = conditional.WhenClauses[current.Index];
                        pending.Push(current with { Index = current.Index + 1 });
                        pending.Push(new RenderFrame(clause.Result, Role: RenderRole.Scalar));
                        pending.Push(new RenderFrame(Text: " THEN "));
                        builder.Append(" WHEN ");
                        current = new RenderFrame(clause.Condition, Role: RenderRole.Predicate);
                        continue;
                    }

                    pending.Push(new RenderFrame(Text: " END"));
                    if (conditional.ElseResult is { } alternative)
                    {
                        builder.Append(" ELSE ");
                        current = new RenderFrame(alternative, Role: RenderRole.Scalar);
                        continue;
                    }
                }
                else if (current.Role == RenderRole.Predicate)
                {
                    if (current.Expression is SafeMigrationSqlUnaryExpression
                        { Operator: SafeMigrationSqlUnaryOperator.Not } unary)
                    {
                        builder.Append("(NOT ");
                        pending.Push(new RenderFrame(Text: ")"));
                        current = new RenderFrame(unary.Operand, Role: RenderRole.Predicate);
                        continue;
                    }

                    if (current.Expression is SafeMigrationSqlBinaryExpression
                        { Operator: SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or } binary)
                    {
                        builder.Append('(');
                        pending.Push(new RenderFrame(Text: ")"));
                        pending.Push(new RenderFrame(binary.Right, Role: RenderRole.Predicate));
                        pending.Push(new RenderFrame(Text: " "));
                        pending.Push(new RenderFrame(Text: BinaryOperator(binary.Operator)));
                        pending.Push(new RenderFrame(Text: " "));
                        current = new RenderFrame(binary.Left, Role: RenderRole.Predicate);
                        continue;
                    }

                    if (!IsPredicate(current.Expression!))
                    {
                        builder.Append('(');
                        pending.Push(new RenderFrame(Text: " = CAST(1 AS bit))"));
                    }

                    current = current with { Role = RenderRole.Node };
                    continue;
                }
                else if (current.Role == RenderRole.Scalar)
                {
                    if (IsPredicate(current.Expression!))
                    {
                        // WHY: T-SQL predicates require a scalar CASE conversion.
                        // Render each occurrence independently, retaining UNKNOWN
                        // and the original traversal and literal-mapping order.
                        builder.Append("CASE WHEN ");
                        pending.Push(new RenderFrame(Text: " THEN CAST(0 AS bit) ELSE NULL END"));
                        pending.Push(new RenderFrame(current.Expression, Role: RenderRole.Predicate));
                        pending.Push(new RenderFrame(Text: " THEN CAST(1 AS bit) WHEN NOT "));
                        current = current with { Role = RenderRole.Predicate };
                        continue;
                    }

                    current = current with { Role = RenderRole.Node };
                    continue;
                }
                else
                {
                    switch (current.Expression)
                    {
                        case SafeMigrationSqlIdentifierExpression value:
                            for (var index = 0; index < value.Parts.Count; index++)
                            {
                                if (index > 0)
                                {
                                    builder.Append('.');
                                }

                                builder.Append(_sqlGenerationHelper.DelimitIdentifier(value.Parts[index]));
                            }

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
                            pending.Push(new RenderFrame(Text: ")"));
                            current = new RenderFrame(value.Operand);
                            continue;

                        case SafeMigrationSqlBinaryExpression value:
                            builder.Append('(');
                            pending.Push(new RenderFrame(Text: ")"));
                            pending.Push(new RenderFrame(value.Right));
                            pending.Push(new RenderFrame(Text: " "));
                            pending.Push(new RenderFrame(Text: BinaryOperator(value.Operator)));
                            pending.Push(new RenderFrame(Text: " "));
                            current = new RenderFrame(value.Left);
                            continue;

                        case SafeMigrationSqlNullTestExpression value:
                            builder.Append('(');
                            pending.Push(new RenderFrame(Text: value.Negated ? " IS NOT NULL)" : " IS NULL)"));
                            current = new RenderFrame(value.Operand);
                            continue;

                        case SafeMigrationSqlBetweenExpression value:
                            builder.Append('(');
                            pending.Push(new RenderFrame(Text: ")"));
                            pending.Push(new RenderFrame(value.Upper));
                            pending.Push(new RenderFrame(Text: " AND "));
                            pending.Push(new RenderFrame(value.Lower));
                            pending.Push(new RenderFrame(Text: value.Negated ? " NOT BETWEEN " : " BETWEEN "));
                            current = new RenderFrame(value.Operand);
                            continue;

                        case SafeMigrationSqlInExpression value:
                            builder.Append('(');
                            pending.Push(new RenderFrame(Text: "))"));
                            pending.Push(new RenderFrame(value, Role: RenderRole.List));
                            pending.Push(new RenderFrame(Text: value.Negated ? " NOT IN (" : " IN ("));
                            current = new RenderFrame(value.Operand);
                            continue;

                        case SafeMigrationSqlFunctionExpression value:
                            builder.Append(value.Name.ToUpperInvariant()).Append('(');
                            pending.Push(new RenderFrame(Text: ")"));
                            current = new RenderFrame(value, Role: RenderRole.List);
                            continue;

                        case SafeMigrationSqlCaseExpression value:
                            builder.Append("CASE");
                            current = new RenderFrame(value, Role: RenderRole.Case);
                            continue;

                        case SafeMigrationSqlCastExpression value:
                            builder.Append("CAST(");
                            pending.Push(new RenderFrame(Text: ")"));
                            pending.Push(new RenderFrame(Text: value.StoreType));
                            pending.Push(new RenderFrame(Text: " AS "));
                            current = new RenderFrame(value.Operand);
                            continue;

                        case SafeMigrationSqlCollateExpression value:
                            builder.Append('(');
                            // WHY: COLLATE is a validated literal token, not a
                            // delimited identifier in the T-SQL grammar.
                            pending.Push(new RenderFrame(Text: ")"));
                            pending.Push(new RenderFrame(Text: value.Name));
                            pending.Push(new RenderFrame(Text: " COLLATE "));
                            current = new RenderFrame(value.Operand);
                            continue;

                        case SafeMigrationSqlCurrentValueExpression value:
                            AppendCurrentValue(builder, value);
                            break;

                        case SafeMigrationSqlProviderFragmentExpression value:
                            builder.Append(value.Sql);
                            break;

                        default:
                            throw new UnreachableException();
                    }
                }
                if (!pending.TryPop(out current))
                {
                    break;
                }
            }
            while (true);
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Distinguishes resumable native scalar, predicate, and collection rendering roles.</summary>
    private enum RenderRole
    {
        Node,
        Predicate,
        Scalar,
        List,
        Case,
    }

    /// <summary>Keeps one output continuation without allocating a delegate or per-node object.</summary>
    private readonly record struct RenderFrame(
        SafeMigrationSqlExpression? Expression = null,
        string? Text = null,
        RenderRole Role = RenderRole.Node,
        int Index = 0
    );

    /// <summary>Distinguishes T-SQL search conditions from scalar values.</summary>
    private static bool IsPredicate(
        SafeMigrationSqlExpression expression
    ) => expression is SafeMigrationSqlNullTestExpression or SafeMigrationSqlBetweenExpression
        or SafeMigrationSqlInExpression
        or SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not }
        or SafeMigrationSqlBinaryExpression
        {
            Operator: >= SafeMigrationSqlBinaryOperator.And and <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
        };


    private void AppendLiteral(
        StringBuilder builder,
        SafeMigrationSqlLiteralExpression expression
    )
    {
        if (expression.StoreType is not null)
        {
            builder.Append("CAST(");
        }

        if (expression.Value is null)
        {
            builder.Append("NULL");
        }
        else
        {
            // WHY: StoreType is an explicit SQL CAST, not the CLR input type.
            // Asking EF for incompatible CLR/store pairs can invoke collection
            // mapping rather than return null. Bind the natural input literal
            // independently of the already validated SQL destination type.
            var mapping = _typeMappingSource.FindMapping(expression.Value.GetType())
                ?? throw new NotSupportedException("SQL Server cannot map the structured literal.");

            var temporal = expression.Value is DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan;
            if (temporal)
            {
                // WHY: Temporal CLR literals must retain their natural source
                // type. Legacy datetime text parsing rejects seven fractional
                // digits and offsets that typed temporal conversion supports.
                builder.Append("CAST(");
            }

            builder.Append(mapping.GenerateSqlLiteral(expression.Value));
            if (temporal)
            {
                builder.Append(" AS ").Append(mapping.StoreType).Append(')');
            }
        }

        if (expression.StoreType is not null)
        {
            builder.Append(" AS ").Append(expression.StoreType).Append(')');
        }
    }

    private static void AppendCurrentValue(
        StringBuilder builder,
        SafeMigrationSqlCurrentValueExpression expression
    )
    {
        var precision = expression.Precision ?? 6;
        switch (expression.Value)
        {
            case SafeMigrationSqlCurrentValue.Date:
                builder.Append("CONVERT(date, SYSDATETIME())");
                break;

            case SafeMigrationSqlCurrentValue.Time:
                builder.Append("CONVERT(time(").Append(precision).Append("), SYSDATETIME())");
                break;

            case SafeMigrationSqlCurrentValue.Timestamp:
                builder.Append("CONVERT(datetime2(").Append(precision).Append("), SYSDATETIME())");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(expression));
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
}
