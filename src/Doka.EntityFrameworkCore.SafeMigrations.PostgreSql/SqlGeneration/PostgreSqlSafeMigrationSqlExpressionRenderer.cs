namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationSqlExpressionRenderer
{
    private const string ProviderId = "npgsql_postgresql";
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    /// <summary>Uses the active provider's mapping and identifier services for every output surface.</summary>
    /// <param name="typeMappingSource">The native PostgreSQL type mapping source.</param>
    /// <param name="sqlGenerationHelper">The native identifier delimiter.</param>
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

    /// <summary>Renders the authored execution expression without catalog-only normalization.</summary>
    /// <param name="expression">The immutable authored expression.</param>
    /// <returns>The executable PostgreSQL SQL text.</returns>
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

        var traversal = new PostgreSqlSafeMigrationExpressionWalker(expression);
        try
        {
            while (traversal.TryTake(out var node))
            {
                var feature = node switch
                {
                    SafeMigrationSqlLiteralExpression value => GetUnsupportedLiteralFeature(value),
                    SafeMigrationSqlCastExpression { UsesSqlServerConvert: true } => "structured_convert_dialect",
                    SafeMigrationSqlCastExpression value when !TryFindCanonicalStoreType(value.StoreType, out _)
                        => "structured_cast_type",
                    SafeMigrationSqlProviderFragmentExpression value
                        when !StringComparer.Ordinal.Equals(value.ProviderId, ProviderId)
                        => "provider_fragment_mismatch",
                    SafeMigrationSqlIdentifierExpression
                        or SafeMigrationSqlUnaryExpression or SafeMigrationSqlBinaryExpression
                        or SafeMigrationSqlNullTestExpression or SafeMigrationSqlBetweenExpression
                        or SafeMigrationSqlInExpression or SafeMigrationSqlFunctionExpression
                        or SafeMigrationSqlCaseExpression or SafeMigrationSqlCastExpression
                        or SafeMigrationSqlCollateExpression or SafeMigrationSqlCurrentValueExpression
                        or SafeMigrationSqlProviderFragmentExpression or SafeMigrationSqlOpaqueExpression => null,
                    _ => throw new UnreachableException(),
                };

                if (feature is not null)
                {
                    return feature;
                }
            }

            return null;
        }
        finally
        {
            traversal.Dispose();
        }
    }

    /// <summary>Builds a server-quoted catalog candidate using the existing canonical comparison shape.</summary>
    /// <param name="expression">The immutable authored expression.</param>
    /// <param name="literal">The current builder's SQL literal encoder.</param>
    /// <returns>A SQL expression producing the expected catalog text.</returns>
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

    /// <summary>Builds the independently checked pretty-deparser spelling for the same authored expression.</summary>
    /// <param name="expression">The immutable authored expression.</param>
    /// <param name="literal">The current builder's SQL literal encoder.</param>
    /// <returns>A SQL expression producing the expected native-deparsed text.</returns>
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

    /// <summary>Retains native numeric token roles when matching validated captured provider SQL.</summary>
    /// <param name="expression">The tree parsed with its original numeric source tokens.</param>
    /// <param name="literal">The SQL literal encoder for candidate text.</param>
    /// <param name="relationOid">The native owning relation expression, or null without column type context.</param>
    /// <returns>
    /// The exact native candidate; unknown bindings propagate SQL null and cannot establish equality.
    /// </returns>
    internal string RenderCatalogCapturedNativeDeparsedCandidateSql(
        SafeMigrationSqlExpression expression,
        Func<string, string> literal,
        string? relationOid = null
    )
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(literal);
        var writer = new CatalogExpressionTextWriter(literal,
            parenthesizeCollations: false, nativeLiteralForms: true, relationOid: relationOid,
            capturedSourceRoles: true);

        // WHY: A validated source literal can be NUMERIC even when its CLR
        // inspection value is double. Only the native captured writer may
        // certify that source; typed-AST alternatives could change its value.
        Append(writer, expression, catalogShape: true);

        return writer.BuildSqlExpression();
    }

    /// <summary>
    /// Shares the bounded native operand type resolver with metadata-only captured function qualification.
    /// </summary>
    /// <param name="expression">The immutable native-bound operand tree.</param>
    /// <param name="literal">The SQL literal encoder.</param>
    /// <param name="relationOid">The owning relation expression, or null without physical attribute context.</param>
    /// <returns>The native type-OID SQL expression, or null when its exact builtin type is unproven.</returns>
    internal static string? RenderNativeResultTypeSql(
        SafeMigrationSqlExpression expression,
        Func<string, string> literal,
        string? relationOid = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(literal);
        var writer = new CatalogExpressionTextWriter(literal,
            parenthesizeCollations: false, nativeLiteralForms: true, relationOid: relationOid,
            capturedSourceRoles: true);

        return writer.GetNativeResultType(expression);
    }

    private void AppendDeparsed(
        IExpressionTextWriter writer,
        SafeMigrationSqlExpression expression
    )
    {
        AppendIterative(writer, expression, catalogShape: true, deparsed: true);
    }

    /// <summary>Renders every accepted tree without placing its depth on the process call stack.</summary>
    private void Append(
        IExpressionTextWriter writer,
        SafeMigrationSqlExpression expression,
        bool catalogShape,
        bool functionArgument = false
    ) => AppendIterative(writer, expression, catalogShape, functionArgument);

    /// <summary>Writes only scalar leaves; structured continuations are owned by the iterative renderer.</summary>
    private void AppendLeaf(IExpressionTextWriter writer, SafeMigrationSqlExpression expression, bool catalogShape)
    {
        switch (expression)
        {
            case SafeMigrationSqlIdentifierExpression value:
                if (writer.TryAppendCapturedColumn(value))
                {
                    break;
                }

                // WHY: Place separators only between the ordered identifier parts.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
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
                if (!writer.NativeLiteralForms && !value.NumericSourceToken.IsEmpty)
                {
                    writer.Append(value.NumericSourceToken.Span);
                    break;
                }

                if (writer.NativeLiteralForms && writer.TryAppendCapturedNumeric(value))
                {
                    break;
                }

                if (writer.NativeLiteralForms && value.Value is bool boolean
                    && (value.StoreType is null || FindCanonicalStoreType(value.StoreType) == "boolean"))
                {
                    writer.Append(boolean ? "true" : "false");
                    break;
                }

                if (writer.NativeLiteralForms && value.StoreType is null
                    && value.Value is long number && number is < int.MinValue or > int.MaxValue
                    && TryAppendNativeIntegerConstant(writer,
                        new SafeMigrationSqlCastExpression(value, "bigint"), "bigint"))
                {
                    break;
                }

                if (writer.NativeLiteralForms && value.Value is not null && value.StoreType is not null
                    && FindCanonicalStoreType(value.StoreType) is "smallint" or "integer" or "bigint")
                {
                    var target = FindCanonicalStoreType(value.StoreType);
                    var uncast = new SafeMigrationSqlLiteralExpression(value.Value);
                    if (!TryAppendNativeIntegerConstant(writer,
                        new SafeMigrationSqlCastExpression(uncast, target), target))
                    {
                        writer.Append('(');
                        Append(writer, uncast, catalogShape);
                        writer.Append(")::");
                        writer.Append(target);
                    }

                    break;
                }

                AppendLiteral(writer, value.Value, value.StoreType
                    ?? (writer.NativeLiteralForms && value.Value is string ? "text" : null));
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

    /// <summary>Restricts text member coercion to native literal or same-relation column comparisons.</summary>
    private static bool TryAppendNativeTextComparisonMember(
        IExpressionTextWriter writer,
        SafeMigrationSqlExpression operand,
        SafeMigrationSqlExpression other,
        SafeMigrationSqlBinaryOperator operation
    ) => operand is SafeMigrationSqlIdentifierExpression column
        && other is SafeMigrationSqlLiteralExpression { Value: string }
        && operation is >= SafeMigrationSqlBinaryOperator.Equal
            and <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual
        && writer.TryAppendNativeTextColumn(column, other as SafeMigrationSqlIdentifierExpression);

    /// <summary>
    /// Retains native folding of directly signed numeric tokens without evaluating arbitrary arithmetic.
    /// </summary>
    private static bool TryAppendCapturedNumericUnary(
        IExpressionTextWriter writer,
        SafeMigrationSqlUnaryExpression expression
    ) => TryGetCapturedNumericUnary(expression, out var literal, out var negate)
        && writer.TryAppendCapturedNumeric(literal, negate);

    /// <summary>Shares signed constant token roles with native CASE result-type selection.</summary>
    private static bool TryGetCapturedNumericUnary(
        SafeMigrationSqlUnaryExpression expression,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SafeMigrationSqlLiteralExpression? literal,
        out bool negate
    )
    {
        SafeMigrationSqlExpression operand = expression;
        negate = false;
        while (operand is SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Negate } unary)
        {
            negate = !negate;
            operand = unary.Operand;
        }

        literal = operand as SafeMigrationSqlLiteralExpression;

        return literal is not null && !literal.NumericSourceToken.IsEmpty;
    }

    /// <summary>Writes native CASE indentation without allocating whitespace strings per branch.</summary>
    private static void AppendCaseLine(IExpressionTextWriter writer, int indentation)
    {
        writer.Append('\n');
        // WHY: Emit exactly the requested indentation width without a temporary string.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < indentation; index++)
        {
            writer.Append(' ');
        }
    }

    /// <summary>Recognizes structured forms which differ from the ordinary catalog candidate.</summary>
    private static bool RequiresNativeDeparsedVariant(SafeMigrationSqlExpression expression)
    {
        var traversal = new PostgreSqlSafeMigrationExpressionWalker(expression);
        try
        {
            while (traversal.TryTake(out var node))
            {
                var required = node switch
                {
                    SafeMigrationSqlLiteralExpression { Value: string } => true,
                    SafeMigrationSqlLiteralExpression { StoreType: { } type, Value: not null }
                        => TryGetIntegerLiteralSource(type, out var source) && source.Length > 0,
                    SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Negate } => true,
                    SafeMigrationSqlBinaryExpression value => HasFlattenedBooleanOperands(value),
                    SafeMigrationSqlFunctionExpression value => IsNativeSpecialForm(value.Name),
                    // WHY: CASE result coercion can alter native literal spelling.
                    SafeMigrationSqlCaseExpression => true,
                    SafeMigrationSqlCastExpression value => value.UsesSqlServerConvert
                        || value.Operand is SafeMigrationSqlLiteralExpression,
                    _ => false,
                };

                if (required)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            traversal.Dispose();
        }
    }

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
        if (target is not ("smallint" or "integer" or "bigint"))
        {
            return false;
        }

        if (expression.Operand is SafeMigrationSqlLiteralExpression { StoreType: { } sourceType } typed)
        {
            if (FindCanonicalStoreType(sourceType) != target)
            {
                return false;
            }

            Append(writer, typed, catalogShape: true);

            return true;
        }

        if (!TryGetNativeIntegerLiteralValue(expression, target, out var integer))
        {
            return false;
        }

        if (expression.Operand is SafeMigrationSqlLiteralExpression { Value: not string })
        {
            var source = integer is >= int.MinValue and <= int.MaxValue ? "integer" : "bigint";
            if (source != target)
            {
                // WHY: Integer-to-integer casts are retained native cast
                // nodes, whereas UNKNOWN string input is folded to a typed
                // constant. Treating both as folded breaks nested CASE SQL.
                return false;
            }
        }

        // WHY: UNKNOWN string input and no-op integer casts can become typed
        // constants. Value proof alone never authorizes dropping another CAST.
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
}
