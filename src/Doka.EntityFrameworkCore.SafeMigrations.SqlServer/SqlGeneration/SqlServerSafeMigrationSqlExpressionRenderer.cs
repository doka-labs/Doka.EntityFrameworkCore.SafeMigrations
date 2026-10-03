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

        return expression switch
        {
            SafeMigrationSqlIdentifierExpression => null,
            SafeMigrationSqlLiteralExpression value => GetLiteralFeature(value),
            SafeMigrationSqlUnaryExpression value => GetUnsupportedFeature(value.Operand),
            SafeMigrationSqlBinaryExpression value =>
                GetUnsupportedFeature(value.Left) ?? GetUnsupportedFeature(value.Right),
            SafeMigrationSqlNullTestExpression value => GetUnsupportedFeature(value.Operand),
            SafeMigrationSqlBetweenExpression value =>
                GetUnsupportedFeature(value.Operand)
                ?? GetUnsupportedFeature(value.Lower)
                ?? GetUnsupportedFeature(value.Upper),
            SafeMigrationSqlInExpression value =>
                GetUnsupportedFeature(value.Operand)
                ?? value.Values.Select(GetUnsupportedFeature).FirstOrDefault(static feature => feature is not null),
            SafeMigrationSqlFunctionExpression value => GetFunctionFeature(value),
            SafeMigrationSqlCastExpression value => IsSupportedStoreType(value.StoreType)
                ? GetUnsupportedFeature(value.Operand)
                : "structured_cast_type",
            SafeMigrationSqlCollateExpression value => value.Schema is not null
                ? "structured_collation_schema"
                : IsSqlServerCollationName(value.Name)
                    ? GetUnsupportedFeature(value.Operand)
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

    private string? GetFunctionFeature(
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

        return valid
            ? expression.Arguments.Select(GetUnsupportedFeature).FirstOrDefault(static feature => feature is not null)
            : "structured_function_arity";
    }

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
        switch (expression)
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
                builder.Append(' ').Append(BinaryOperator(value.Operator)).Append(' ');
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
                builder.Append(value.Name.ToUpperInvariant()).Append('(');
                AppendList(builder, value.Arguments);
                builder.Append(')');
                break;

            case SafeMigrationSqlCastExpression value:
                builder.Append("CAST(");
                Append(builder, value.Operand);
                builder.Append(" AS ").Append(value.StoreType).Append(')');
                break;

            case SafeMigrationSqlCollateExpression value:
                builder.Append('(');
                Append(builder, value.Operand);
                // WHY: T-SQL COLLATE requires a literal collation token, not a
                // delimited object identifier. The name was validated above.
                builder.Append(" COLLATE ").Append(value.Name).Append(')');
                break;

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
