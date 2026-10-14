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

        var writer = new CatalogExpressionTextWriter(literal, parenthesizeCollations: false);
        AppendDeparsed(writer, expression);

        return writer.BuildSqlExpression();
    }

    /// <summary>Preserves the native index-only pretty form of a cast over one physical identifier.</summary>
    /// <param name="expression">The explicitly authored stored expression.</param>
    /// <param name="literal">The SQL literal encoder for candidate text.</param>
    /// <returns>The exact pretty index candidate, or null outside this independently proven shape.</returns>
    internal string? RenderCatalogIndexCastCandidateSql(
        SafeMigrationSqlExpression expression,
        Func<string, string> literal)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(literal);
        if (expression is not SafeMigrationSqlCastExpression
            { Operand: SafeMigrationSqlIdentifierExpression { Parts.Count: 1 } member } cast)
        {
            return null;
        }

        // WHY: pg_get_indexdef(position, pretty=true) stores (member::type),
        // whereas pg_get_expr retains (member)::type. Only this native index
        // shape gets another candidate; CHECK rendering and SQL are unchanged.
        return literal("(") + " || " + RenderCatalogCandidateSql(member, literal)
            + " || " + literal("::" + FindCanonicalStoreType(cast.StoreType) + ")");
    }

    /// <summary>Builds a native literal candidate without rewriting identifiers or SQL fragments.</summary>
    /// <param name="expression">The same explicitly structured expression rendered for DDL.</param>
    /// <param name="literal">The SQL literal encoder for candidate text.</param>
    /// <param name="relationOid">The native owning relation expression, or null without column type context.</param>
    /// <returns>A native candidate, or null when the ordinary candidate already carries the relevant forms.</returns>
    public string? RenderCatalogNativeDeparsedCandidateSql(
        SafeMigrationSqlExpression expression,
        Func<string, string> literal,
        string? relationOid = null
    )
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(literal);
        if (!RequiresNativeDeparsedVariant(expression))
        {
            return null;
        }

        var writer = new CatalogExpressionTextWriter(literal,
            parenthesizeCollations: false, nativeLiteralForms: true, relationOid: relationOid);

        // WHY: The normal deparser keeps parentheses around Boolean comparison
        // operands. The pretty arithmetic candidate deliberately flattens those;
        // native literal/special forms must also retain this original tree shape.
        Append(writer, expression, catalogShape: true);

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
        bool catalogShape,
        bool functionArgument = false
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
                if (writer.NativeLiteralForms && value.StoreType is not null
                    && TryAppendNativeIntegerConstant(writer,
                        new SafeMigrationSqlCastExpression(value, value.StoreType),
                        FindCanonicalStoreType(value.StoreType)))
                {
                    break;
                }

                AppendLiteral(writer, value.Value, value.StoreType
                    ?? (writer.NativeLiteralForms && value.Value is string ? "text" : null));
                break;
            case SafeMigrationSqlUnaryExpression value:
                writer.Append(value.Operator == SafeMigrationSqlUnaryOperator.Not ? "(NOT "
                    : writer.NativeLiteralForms ? "(- " : "(-");
                Append(writer, value.Operand, catalogShape);
                writer.Append(')');
                break;
            case SafeMigrationSqlBinaryExpression value:
                if (writer.NativeLiteralForms
                    && value.Operator is SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or)
                {
                    // WHY: Native parsing flattens equal Boolean operators
                    // into one n-ary node. Preserve unlike nested operators;
                    // this is structured tree rendering, not a SQL text rewrite.
                    writer.Append('(');
                    AppendNativeBooleanOperands(writer, value, value.Operator, catalogShape);
                    writer.Append(')');

                    break;
                }

                writer.Append('(');
                if (value.Left is not SafeMigrationSqlLiteralExpression leftLiteral
                    || value.Right is not SafeMigrationSqlIdentifierExpression rightColumn
                    || !writer.TryAppendNativeUnknownIntegerLiteral(leftLiteral, rightColumn))
                {
                    Append(writer, value.Left, catalogShape);
                }

                writer.Append(' ');
                writer.Append(BinaryOperator(value.Operator));
                writer.Append(' ');
                if (value.Right is not SafeMigrationSqlLiteralExpression rightLiteral
                    || value.Left is not SafeMigrationSqlIdentifierExpression leftColumn
                    || !writer.TryAppendNativeUnknownIntegerLiteral(rightLiteral, leftColumn))
                {
                    Append(writer, value.Right, catalogShape);
                }

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
                if (catalogShape && writer.NativeLiteralForms && value.Values.Count == 1)
                {
                    // WHY: PostgreSQL represents a singleton IN as scalar
                    // equality, not an ANY array. Only the additional native
                    // catalog candidate uses this exact structured form.
                    Append(writer, new SafeMigrationSqlBinaryExpression(value.Operand,
                        value.Negated ? SafeMigrationSqlBinaryOperator.NotEqual : SafeMigrationSqlBinaryOperator.Equal,
                        value.Values[0]), catalogShape);

                    break;
                }

                writer.Append('(');
                if (catalogShape)
                {
                    Append(writer, value.Operand, catalogShape);
                    writer.Append(value.Negated ? " <> ALL (ARRAY[" : " = ANY (ARRAY[");
                    for (var index = 0; index < value.Values.Count; index++)
                    {
                        if (index > 0)
                        {
                            writer.Append(", ");
                        }

                        if (value.Values[index] is not SafeMigrationSqlLiteralExpression literal
                            || value.Operand is not SafeMigrationSqlIdentifierExpression column
                            || !writer.TryAppendNativeUnknownIntegerLiteral(literal, column))
                        {
                            Append(writer, value.Values[index], catalogShape);
                        }
                    }

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
                writer.Append(writer.NativeLiteralForms && IsNativeSpecialForm(value.Name)
                    ? value.Name.ToUpperInvariant() : value.Name.ToLowerInvariant());
                writer.Append('(');
                if (!writer.TryAppendNativeLowerOperand(value))
                {
                    AppendList(writer, value.Arguments, catalogShape, functionArgument: true);
                }

                writer.Append(')');
                break;
            case SafeMigrationSqlCastExpression value:
                var castType = FindCanonicalStoreType(value.StoreType);
                if (writer.NativeLiteralForms && TryAppendNativeIntegerConstant(writer, value, castType))
                {
                    break;
                }

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
                var collateParentheses = writer.ParenthesizeCollations
                    || writer.NativeLiteralForms && !functionArgument;

                if (collateParentheses)
                {
                    writer.Append('(');
                }

                Append(writer, value.Operand, catalogShape);
                writer.Append(" COLLATE ");
                writer.AppendCollation(value.Name, value.Schema);
                if (collateParentheses)
                {
                    writer.Append(')');
                }

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

    /// <summary>Recognizes structured forms which differ from the ordinary catalog candidate.</summary>
    private static bool RequiresNativeDeparsedVariant(SafeMigrationSqlExpression expression)
        => expression switch
        {
            SafeMigrationSqlLiteralExpression { Value: string } => true,
            SafeMigrationSqlLiteralExpression { StoreType: { } type, Value: not null }
                => TryGetIntegerLiteralSource(type, out var source) && source.Length > 0,
            SafeMigrationSqlUnaryExpression value => value.Operator == SafeMigrationSqlUnaryOperator.Negate
                || RequiresNativeDeparsedVariant(value.Operand),
            SafeMigrationSqlBinaryExpression value => HasFlattenedBooleanOperands(value)
                || RequiresNativeDeparsedVariant(value.Left)
                || RequiresNativeDeparsedVariant(value.Right),
            SafeMigrationSqlNullTestExpression value => RequiresNativeDeparsedVariant(value.Operand),
            SafeMigrationSqlBetweenExpression value => RequiresNativeDeparsedVariant(value.Operand)
                || RequiresNativeDeparsedVariant(value.Lower) || RequiresNativeDeparsedVariant(value.Upper),
            SafeMigrationSqlInExpression value => RequiresNativeDeparsedVariant(value.Operand)
                || value.Values.Any(RequiresNativeDeparsedVariant),
            SafeMigrationSqlFunctionExpression value => IsNativeSpecialForm(value.Name)
                || value.Arguments.Any(RequiresNativeDeparsedVariant),
            SafeMigrationSqlCastExpression value => value.Operand is SafeMigrationSqlLiteralExpression
                || RequiresNativeDeparsedVariant(value.Operand),
            SafeMigrationSqlCollateExpression value => RequiresNativeDeparsedVariant(value.Operand),
            _ => false,
        };

    /// <summary>Preserves uppercase native SQL special forms without touching ordinary function names.</summary>
    private static bool IsNativeSpecialForm(string name)
        => name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GREATEST", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LEAST", StringComparison.OrdinalIgnoreCase);

    /// <summary>Requests the native variant only when a binary Boolean tree has an n-ary stored form.</summary>
    private static bool HasFlattenedBooleanOperands(SafeMigrationSqlBinaryExpression value)
        => value.Operator is SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or
            && (value.Left is SafeMigrationSqlBinaryExpression left && left.Operator == value.Operator
                || value.Right is SafeMigrationSqlBinaryExpression right && right.Operator == value.Operator);

    /// <summary>Flattens only identical Boolean operators, retaining precedence-changing structured subtrees.</summary>
    private void AppendNativeBooleanOperands(
        IExpressionTextWriter writer,
        SafeMigrationSqlExpression expression,
        SafeMigrationSqlBinaryOperator operation,
        bool catalogShape)
    {
        if (expression is SafeMigrationSqlBinaryExpression binary && binary.Operator == operation)
        {
            AppendNativeBooleanOperands(writer, binary.Left, operation, catalogShape);
            writer.Append(' ');
            writer.Append(BinaryOperator(operation));
            writer.Append(' ');
            AppendNativeBooleanOperands(writer, binary.Right, operation, catalogShape);

            return;
        }

        Append(writer, expression, catalogShape);
    }

    /// <summary>Represents only validated builtin integer literal casts as the native constant deparser does.</summary>
    private void AppendNativeIntegerConstant(IExpressionTextWriter writer, long value, string target)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (target == "integer")
        {
            writer.Append(text);
        }
        else
        {
            AppendLiteral(writer, text, target);
        }
    }

    /// <summary>Folds no functions, arithmetic, operand casts or potentially lossy numeric conversion.</summary>
    private bool TryAppendNativeIntegerConstant(
        IExpressionTextWriter writer,
        SafeMigrationSqlCastExpression expression,
        string target
    )
    {
        if (!TryGetNativeIntegerLiteralValue(expression, target, out var integer))
        {
            return false;
        }

        // WHY: PostgreSQL folds builtin integer input constants in its parsed
        // expression tree. Only these checked integral conversions are represented;
        // arbitrary CASTs, SQL functions and opaque payloads are never evaluated.
        AppendNativeIntegerConstant(writer, integer, target);

        return true;
    }

    /// <summary>Shares builtin integer literal proof between deparse rendering and sparse binding.</summary>
    /// <param name="expression">The explicitly authored constant CAST.</param>
    /// <param name="target">The canonical builtin integer target.</param>
    /// <param name="integer">The exact value only when ASCII input syntax and native range are proven.</param>
    /// <returns>Whether no native input function needs to be invoked to establish the literal conversion.</returns>
    internal static bool TryGetNativeIntegerLiteralValue(
        SafeMigrationSqlCastExpression expression,
        string target,
        out long integer
    )
    {
        integer = 0;
        if (target is not ("smallint" or "integer" or "bigint")
            || expression.Operand is not SafeMigrationSqlLiteralExpression { Value: { } raw } literal
            || !TryGetIntegerLiteralSource(literal.StoreType, out var source))
        {
            return false;
        }

        var text = raw switch
        {
            string value => value,
            byte or sbyte or short or ushort or int or uint or long or ulong
                => Convert.ToString(raw, CultureInfo.InvariantCulture)!,
            _ => null,
        };

        if (text is null || text.Any(static character => character is not (>= '0' and <= '9')
                and not ('+' or '-' or ' ' or '\t' or '\n' or '\r' or '\v' or '\f'))
            || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)
            || target == "smallint" && integer is < short.MinValue or > short.MaxValue
            || target == "integer" && integer is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        if (source == "smallint" && integer is < short.MinValue or > short.MaxValue
            || source == "integer" && integer is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        return true;
    }

    /// <summary>Limits literal folding to uncast, unbounded TEXT or exact native integer inputs.</summary>
    private static bool TryGetIntegerLiteralSource(string? storeType, out string source)
    {
        source = storeType?.Trim().ToLowerInvariant() switch
        {
            null or "text" => string.Empty,
            "smallint" or "int2" => "smallint",
            "int" or "integer" or "int4" => "integer",
            "bigint" or "int8" => "bigint",
            _ => "unsupported",
        };

        // WHY: A VARCHAR typmod can truncate before CAST. Its source input
        // is not interchangeable with an unbounded TEXT/integer literal.
        return source != "unsupported";
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
        bool catalogShape,
        bool functionArgument = false
    )
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                writer.Append(", ");
            }

            Append(writer, values[index], catalogShape, functionArgument);
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
        /// <summary>Whether collation nodes keep authored parentheses rather than PostgreSQL's pretty form.</summary>
        bool ParenthesizeCollations { get; }

        /// <summary>Whether this catalog candidate uses native literal and SQL special-form spelling.</summary>
        bool NativeLiteralForms { get; }

        /// <summary>Appends a proven unknown integer input under a native relation-column type.</summary>
        /// <param name="literal">The uncast literal retaining its original value.</param>
        /// <param name="column">The directly compared structured column identifier.</param>
        /// <returns>Whether a context-scoped native candidate was appended.</returns>
        bool TryAppendNativeUnknownIntegerLiteral(
            SafeMigrationSqlLiteralExpression literal,
            SafeMigrationSqlIdentifierExpression column);

        /// <summary>
        /// Appends only a native builtin LOWER operand whose VARCHAR conversion is independently resolved.
        /// </summary>
        /// <param name="function">The structured single-argument function call.</param>
        /// <returns>Whether the owning native type and builtin signature qualify this exact operand variant.</returns>
        bool TryAppendNativeLowerOperand(SafeMigrationSqlFunctionExpression function);

        void Append(
            char value
        );

        void Append(
            string value
        );

        void AppendIdentifier(
            string identifier
        );

        /// <summary>Appends a collation using the identity representation of this output surface.</summary>
        /// <param name="name">The exact collation name.</param>
        /// <param name="schema">The exact namespace, or null for PostgreSQL's search path.</param>
        void AppendCollation(
            string name,
            string? schema
        );
    }

    private sealed class DelimitedExpressionTextWriter : IExpressionTextWriter
    {
        private readonly StringBuilder _builder = new();
        private readonly ISqlGenerationHelper _sqlGenerationHelper;

        /// <inheritdoc />
        public bool ParenthesizeCollations => true;

        /// <inheritdoc />
        public bool NativeLiteralForms => false;

        /// <inheritdoc />
        public bool TryAppendNativeUnknownIntegerLiteral(
            SafeMigrationSqlLiteralExpression literal,
            SafeMigrationSqlIdentifierExpression column) => false;

        /// <inheritdoc />
        public bool TryAppendNativeLowerOperand(SafeMigrationSqlFunctionExpression function) => false;

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

        /// <inheritdoc />
        public void AppendCollation(
            string name,
            string? schema
        ) => _builder.Append(_sqlGenerationHelper.DelimitIdentifier(name, schema));

        public override string ToString() => _builder.ToString();
    }

    private sealed class CatalogExpressionTextWriter : IExpressionTextWriter
    {
        private readonly List<string> _parts = [];
        private readonly Func<string, string> _literal;
        private readonly StringBuilder _text = new();
        private readonly string? _relationOid;

        /// <inheritdoc />
        public bool ParenthesizeCollations { get; }

        /// <inheritdoc />
        public bool NativeLiteralForms { get; }

        public CatalogExpressionTextWriter(
            Func<string, string> literal,
            bool parenthesizeCollations = true,
            bool nativeLiteralForms = false,
            string? relationOid = null
        )
        {
            _literal = literal;
            // WHY: The pretty deparser drops parentheses around COLLATE inside function arguments.
            // Keep both bounded catalog candidates; execution always retains authored parentheses.
            ParenthesizeCollations = parenthesizeCollations;
            NativeLiteralForms = nativeLiteralForms;
            _relationOid = relationOid;
        }

        /// <inheritdoc />
        public bool TryAppendNativeUnknownIntegerLiteral(
            SafeMigrationSqlLiteralExpression literal,
            SafeMigrationSqlIdentifierExpression column
        )
        {
            if (!NativeLiteralForms || _relationOid is null
                || literal is not { Value: string value, StoreType: null })
            {
                return false;
            }

            var cast = new SafeMigrationSqlCastExpression(literal, "bigint");
            if (!TryGetNativeIntegerLiteralValue(cast, "bigint", out var integer))
            {
                return false;
            }

            var text = integer.ToString(CultureInfo.InvariantCulture);
            var target = "(SELECT a.atttypid FROM pg_catalog.pg_attribute a WHERE a.attrelid = "
                + _relationOid + " AND a.attname = " + _literal(column.Parts[^1])
                + " AND a.attnum > 0 AND NOT a.attisdropped)";

            var arms = new StringBuilder("CASE ").Append(target);
            if (integer is >= int.MinValue and <= int.MaxValue)
            {
                arms.Append(" WHEN 23 THEN ").Append(_literal(text));
            }

            if (integer is >= short.MinValue and <= short.MaxValue)
            {
                arms.Append(" WHEN 21 THEN ").Append(_literal("'" + text + "'::smallint"));
            }

            arms.Append(" WHEN 20 THEN ").Append(_literal("'" + text + "'::bigint"));
            arms.Append(" WHEN 25 THEN ").Append(_literal("'" + value.Replace("'", "''", StringComparison.Ordinal)
                + "'::text")).Append(" ELSE NULL END");

            // WHY: An unknown '1' is folded differently for INTEGER and TEXT.
            // The native owning attribute OID selects the exact representation;
            // no generic cast stripping, user input function or app-row scan is used.
            FlushText();
            _parts.Add("(" + arms + ")");

            return true;
        }

        /// <inheritdoc />
        public bool TryAppendNativeLowerOperand(SafeMigrationSqlFunctionExpression function)
        {
            if (!NativeLiteralForms || _relationOid is null
                || !function.Name.Equals("LOWER", StringComparison.OrdinalIgnoreCase)
                || function.Arguments.Count != 1
                || function.Arguments[0] is not SafeMigrationSqlIdentifierExpression column)
            {
                return false;
            }

            var identifier = string.Join(" || '.' || ", column.Parts
                .Select(part => "pg_catalog.quote_ident(" + _literal(part) + ")"));

            var nativeBuiltin = "EXISTS (SELECT 1 FROM pg_catalog.pg_proc p "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace "
                + "WHERE n.nspname = 'pg_catalog' AND p.proname = 'lower' "
                + "AND p.pronargs = 1 AND p.proargtypes[0] = 25 AND p.prorettype = 25 "
                + "AND pg_catalog.pg_function_is_visible(p.oid))";

            var competingOverload = "EXISTS (SELECT 1 FROM pg_catalog.pg_proc p "
                + "WHERE p.proname = 'lower' AND p.pronargs >= 1 "
                + "AND p.pronargs - p.pronargdefaults <= 1 "
                + "AND p.proargtypes[0] IN (1043, 2283, 5077) "
                + "AND pg_catalog.pg_function_is_visible(p.oid))";

            var varchar = "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a WHERE a.attrelid = "
                + _relationOid + " AND a.attname = " + _literal(column.Parts[^1])
                + " AND a.attnum > 0 AND NOT a.attisdropped AND a.atttypid = 1043)";

            // WHY: Native LOWER(VARCHAR) records its implicit TEXT conversion.
            // Use the actual owning attribute and visible builtin signature;
            // a custom exact or scalar-polymorphic overload must not acquire this candidate.
            FlushText();
            _parts.Add("(CASE WHEN " + varchar + " AND " + nativeBuiltin + " AND NOT "
                + competingOverload + " THEN '(' || " + identifier + " || ')::text' ELSE "
                + identifier + " END)");

            return true;
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

        /// <inheritdoc />
        public void AppendCollation(
            string name,
            string? schema
        )
        {
            // WHY: PostgreSQL deparses a visible collation without its schema even when the
            // authored name was qualified. Resolve the exact identifier first, then use the
            // server's regcollation spelling; dropping a namespace in client text is ambiguous.
            FlushText();
            var identifier = schema is null
                ? $"pg_catalog.quote_ident({_literal(name)})"
                : $"pg_catalog.quote_ident({_literal(schema)}) || '.' || pg_catalog.quote_ident({_literal(name)})";

            _parts.Add($"(pg_catalog.to_regcollation({identifier})::text)");
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
