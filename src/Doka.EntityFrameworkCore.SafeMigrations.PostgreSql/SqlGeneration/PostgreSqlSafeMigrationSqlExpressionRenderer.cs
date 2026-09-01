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

    /// <summary>Returns the first provider-specific incompatibility in an expression tree.</summary>
    /// <param name="expression">The structured expression to validate.</param>
    /// <returns>A stable unsupported-feature code, or <see langword="null" /> when rendering is supported.</returns>
    public string? GetUnsupportedFeature(
        SafeMigrationSqlExpression expression
    )
    {
        ArgumentNullException.ThrowIfNull(expression);

        return expression switch
        {
            SafeMigrationSqlIdentifierExpression => null,
            SafeMigrationSqlLiteralExpression value => GetUnsupportedLiteralFeature(value),
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
            SafeMigrationSqlFunctionExpression value =>
                value.Arguments.Select(GetUnsupportedFeature).FirstOrDefault(static feature => feature is not null),
            SafeMigrationSqlCastExpression value =>
                TryFindCanonicalStoreType(value.StoreType, out _)
                    ? GetUnsupportedFeature(value.Operand)
                    : "structured_cast_type",
            SafeMigrationSqlCollateExpression value => GetUnsupportedFeature(value.Operand),
            SafeMigrationSqlCurrentValueExpression => null,
            SafeMigrationSqlProviderFragmentExpression value =>
                StringComparer.Ordinal.Equals(value.ProviderId, ProviderId)
                    ? null
                    : "provider_fragment_mismatch",
            SafeMigrationSqlOpaqueExpression => null,
            _ => throw new UnreachableException(),
        };
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
                var castType = FindCanonicalStoreType(value.StoreType);
                if (catalogShape)
                {
                    writer.Append('(');
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(")::");
                    writer.Append(castType);
                }
                else
                {
                    writer.Append("CAST(");
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(" AS ");
                    writer.Append(castType);
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
        var castType = storeType is null ? null : FindCanonicalStoreType(storeType);

        if (value is null)
        {
            writer.Append("NULL");
            if (castType is not null)
            {
                writer.Append("::");
                writer.Append(castType);
            }

            return;
        }

        var mapping = _typeMappingSource.FindMapping(value.GetType(), storeType)
            ?? _typeMappingSource.FindMapping(value.GetType())
            ?? throw new NotSupportedException(
                $"PostgreSQL has no type mapping for structured literal '{value.GetType().FullName}'.");

        writer.Append(mapping.GenerateSqlLiteral(value));
        if (castType is not null)
        {
            writer.Append("::");
            writer.Append(castType);
        }
    }

    private string? GetUnsupportedLiteralFeature(
        SafeMigrationSqlLiteralExpression expression
    )
    {
        if (expression.StoreType is not null
            && !TryFindCanonicalStoreType(expression.StoreType, out _))
        {
            return "structured_cast_type";
        }

        if (expression.Value is not null
            && _typeMappingSource.FindMapping(expression.Value.GetType(), expression.StoreType) is null
            && _typeMappingSource.FindMapping(expression.Value.GetType()) is null)
        {
            return "structured_literal_mapping";
        }

        return null;
    }

    private string FindCanonicalStoreType(
        string storeType
    )
    {
        if (!TryFindCanonicalStoreType(storeType, out var canonicalStoreType))
        {
            throw new NotSupportedException(
                $"PostgreSQL has no type mapping for structured CAST target '{storeType}'.");
        }

        return canonicalStoreType;
    }

    private bool TryFindCanonicalStoreType(
        string storeType,
        out string canonicalStoreType
    )
    {
        if (!TryNormalizePreMappingAlias(storeType, out var mappingStoreType))
        {
            canonicalStoreType = string.Empty;
            return false;
        }

        var mapping = _typeMappingSource.FindMapping(mappingStoreType);
        if (mapping is null)
        {
            canonicalStoreType = string.Empty;
            return false;
        }

        // Npgsql validates the type but intentionally preserves caller aliases.
        // PostgreSQL's catalog deparser emits canonical built-in names, so
        // normalize documented aliases before building both DDL and candidates.
        canonicalStoreType = CanonicalizeBuiltInAliases(mapping.StoreType);
        return true;
    }

    private static bool TryNormalizePreMappingAlias(
        string storeType,
        out string normalizedStoreType
    )
    {
        var candidate = storeType.AsSpan().Trim();
        var scalarLength = candidate.Length;
        while (scalarLength >= 2
               && candidate[..scalarLength].EndsWith("[]", StringComparison.Ordinal))
        {
            scalarLength -= 2;
        }

        var scalarType = candidate[..scalarLength].TrimEnd();
        var arraySuffix = candidate[scalarLength..];
        if (!scalarType.StartsWith("float", StringComparison.OrdinalIgnoreCase))
        {
            normalizedStoreType = storeType;
            return true;
        }

        var precisionClause = scalarType["float".Length..].Trim();
        if (precisionClause.Length == 0)
        {
            normalizedStoreType = AppendArraySuffix("double precision", arraySuffix);
            return true;
        }

        // Preserve custom types that merely share the keyword prefix. Once an
        // opening parenthesis is present, however, the input claims PostgreSQL
        // FLOAT(p) grammar and must satisfy its documented binary-precision
        // range before reaching Npgsql.
        if (precisionClause[0] != '(')
        {
            normalizedStoreType = storeType;
            return true;
        }

        if (precisionClause[^1] != ')'
            || !int.TryParse(
                precisionClause[1..^1].Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var precision)
            || precision is < 1 or > 53)
        {
            normalizedStoreType = string.Empty;
            return false;
        }

        normalizedStoreType = AppendArraySuffix(
            precision <= 24 ? "real" : "double precision",
            arraySuffix);
        return true;
    }

    private static string AppendArraySuffix(
        string scalarType,
        ReadOnlySpan<char> arraySuffix
    ) => arraySuffix.Length == 0
        ? scalarType
        : string.Concat(scalarType.AsSpan(), arraySuffix);

    private static string CanonicalizeBuiltInAliases(
        string storeType
    )
    {
        var candidate = storeType.Trim();
        var scalarLength = candidate.Length;
        while (scalarLength >= 2
               && candidate
                   .AsSpan(0, scalarLength)
                   .EndsWith("[]", StringComparison.Ordinal))
        {
            scalarLength -= 2;
        }

        var scalarType = candidate[..scalarLength];
        var arraySuffix = candidate[scalarLength..];
        var canonicalScalarType = scalarType.ToLowerInvariant() switch
        {
            "int" or "int4" => "integer",
            "int2" => "smallint",
            "int8" => "bigint",
            "float4" => "real",
            "float8" => "double precision",
            "bool" => "boolean",
            _ => CanonicalizeParameterizedBuiltInAlias(scalarType),
        };

        return canonicalScalarType + arraySuffix;
    }

    private static string CanonicalizeParameterizedBuiltInAlias(
        string storeType
    )
    {
        if (TryReplaceAlias(storeType, "decimal", "numeric", out var canonical)
            || TryReplaceAlias(storeType, "varchar", "character varying", out canonical)
            || TryReplaceAlias(storeType, "bpchar", "character", out canonical)
            || TryReplaceAlias(storeType, "char", "character", out canonical)
            || TryReplaceAlias(storeType, "varbit", "bit varying", out canonical))
        {
            return canonical;
        }

        if (TryReplaceAlias(storeType, "timestamptz", "timestamp", out canonical)
            || TryReplaceAlias(storeType, "timetz", "time", out canonical))
        {
            return canonical + " with time zone";
        }

        if (TryReplaceAlias(storeType, "timestamp", "timestamp", out canonical)
            || TryReplaceAlias(storeType, "time", "time", out canonical))
        {
            return canonical + " without time zone";
        }

        return storeType;
    }

    private static bool TryReplaceAlias(
        string storeType,
        string alias,
        string canonicalName,
        out string canonical
    )
    {
        if (!storeType.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
        {
            canonical = string.Empty;
            return false;
        }

        var suffix = storeType[alias.Length..];
        if (suffix.Length > 0
            && (suffix[0] != '(' || suffix[^1] != ')'))
        {
            canonical = string.Empty;
            return false;
        }

        canonical = canonicalName + suffix;
        return true;
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
