namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Renders structured MySQL-family expressions without interpreting opaque SQL.</summary>
internal sealed class MySqlSafeMigrationSqlExpressionRenderer
{
    private const string ProviderId = "doka_mysql";
    private const string DefaultHexPrefix = "convert(0x";
    private const string DefaultStringSuffix = " using utf8mb4)";
    private const string DefaultBinarySuffix = " using binary)";
    private const int StackUtf8ByteLimit = 512;

    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    /// <summary>Initializes the renderer with the active provider's public mappings and identifier syntax.</summary>
    /// <param name="typeMappingSource">The provider's literal mapping source.</param>
    /// <param name="sqlGenerationHelper">The provider's identifier delimiter.</param>
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

    /// <summary>Renders an ordinary computed, check, or index expression using native literal spelling.</summary>
    /// <param name="expression">The structured expression to render.</param>
    /// <returns>The complete SQL expression.</returns>
    public string Render(
        SafeMigrationSqlExpression expression
    )
    {
        ArgumentNullException.ThrowIfNull(expression);

        var builder = new StringBuilder();
        Append(builder, expression);

        return builder.ToString();
    }

    /// <summary>Renders a default expression with SQL-mode-independent UTF-8 string leaves.</summary>
    /// <param name="expression">The authored default expression, including any nested structured nodes.</param>
    /// <returns>The complete default expression without rewriting opaque or provider-owned SQL.</returns>
    internal string RenderDefault(
        SafeMigrationSqlExpression expression
    )
    {
        ArgumentNullException.ThrowIfNull(expression);

        var builder = new StringBuilder();
        Append(builder, expression, defaultContext: true);

        return builder.ToString();
    }

    /// <summary>Renders one default string without depending on the session's backslash interpretation.</summary>
    /// <param name="value">The valid UTF-16 default text.</param>
    /// <returns>A canonical UTF-8 conversion, or the existing quoted spelling for an empty string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    /// <exception cref="EncoderFallbackException">The text contains an unpaired UTF-16 surrogate.</exception>
    internal static string RenderStringDefault(
        string value
    )
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            // WHY: Empty quoted text is mode-independent and retains the
            // existing catalog spelling without an ambiguous empty hex token.
            return "''";
        }

        var byteCount = s_strictUtf8.GetByteCount(value);
        var resultLength = checked(DefaultHexPrefix.Length + (byteCount * 2) + DefaultStringSuffix.Length);
        byte[]? rented = null;
        var bytes = byteCount <= StackUtf8ByteLimit
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);

        try
        {
            _ = s_strictUtf8.GetBytes(value.AsSpan(), bytes);

            // WHY: A synchronous ref-struct state avoids a captured closure and
            // intermediate byte/hex strings; only the complete SQL is allocated.
            return string.Create(
                resultLength,
                (ReadOnlySpan<byte>)bytes,
                static (destination, source) =>
                {
                    DefaultHexPrefix.AsSpan().CopyTo(destination);
                    var hex = destination.Slice(DefaultHexPrefix.Length, source.Length * 2);
                    if (!Convert.TryToHexStringLower(source, hex, out var written)
                        || written != hex.Length)
                    {
                        throw new InvalidOperationException("A default string could not be encoded as UTF-8 hex.");
                    }

                    DefaultStringSuffix.AsSpan().CopyTo(destination[(DefaultHexPrefix.Length + hex.Length)..]);
                });
        }
        finally
        {
            if (rented is not null)
            {
                // WHY: Default text must not remain visible to a later renter;
                // clear the complete rented array, including its spare capacity.
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    /// <summary>Renders a binary default without passing its bytes through server text-literal serialization.</summary>
    /// <param name="value">The exact authored bytes, including values that are not valid UTF-8.</param>
    /// <returns>A canonical binary conversion, or the native empty binary literal for an empty span.</returns>
    internal static string RenderBinaryDefault(
        ReadOnlySpan<byte> value
    )
    {
        if (value.IsEmpty)
        {
            return "X''";
        }

        // WHY: Older MariaDB catalog versions can reserialize raw hex defaults
        // as escaped text. A binary conversion preserves every byte after a
        // reopen without routing arbitrary binary data through an encoding.
        return string.Create(
            checked(DefaultHexPrefix.Length + (value.Length * 2) + DefaultBinarySuffix.Length),
            value,
            static (destination, source) =>
            {
                DefaultHexPrefix.AsSpan().CopyTo(destination);
                var hex = destination.Slice(DefaultHexPrefix.Length, source.Length * 2);
                if (!Convert.TryToHexStringLower(source, hex, out var written)
                    || written != hex.Length)
                {
                    throw new InvalidOperationException("A binary default could not be encoded as hexadecimal.");
                }

                DefaultBinarySuffix.AsSpan().CopyTo(destination[(DefaultHexPrefix.Length + hex.Length)..]);
            });
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
                MySqlSafeMigrationCastTypeRenderer.TryRender(value.StoreType, out _)
                    ? GetUnsupportedFeature(value.Operand)
                    : "structured_cast_type",
            SafeMigrationSqlCollateExpression { Schema: not null } => "schema_qualified_expression_collation",
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

    private void Append(
        StringBuilder builder,
        SafeMigrationSqlExpression expression,
        bool defaultContext = false
    )
    {
        switch (expression)
        {
            case SafeMigrationSqlIdentifierExpression value:
                builder.AppendJoin('.', value.Parts.Select(_sqlGenerationHelper.DelimitIdentifier));
                break;
            case SafeMigrationSqlLiteralExpression value:
                AppendLiteral(builder, value.Value, value.StoreType, defaultContext);
                break;
            case SafeMigrationSqlUnaryExpression value:
                builder.Append(value.Operator == SafeMigrationSqlUnaryOperator.Not ? "(NOT " : "(-");
                Append(builder, value.Operand, defaultContext);
                builder.Append(')');
                break;
            case SafeMigrationSqlBinaryExpression value:
                builder.Append('(');
                Append(builder, value.Left, defaultContext);
                builder
                    .Append(' ')
                    .Append(BinaryOperator(value.Operator))
                    .Append(' ');
                Append(builder, value.Right, defaultContext);
                builder.Append(')');
                break;
            case SafeMigrationSqlNullTestExpression value:
                builder.Append('(');
                Append(builder, value.Operand, defaultContext);
                builder.Append(value.Negated ? " IS NOT NULL)" : " IS NULL)");
                break;
            case SafeMigrationSqlBetweenExpression value:
                builder.Append('(');
                Append(builder, value.Operand, defaultContext);
                builder.Append(value.Negated ? " NOT BETWEEN " : " BETWEEN ");
                Append(builder, value.Lower, defaultContext);
                builder.Append(" AND ");
                Append(builder, value.Upper, defaultContext);
                builder.Append(')');
                break;
            case SafeMigrationSqlInExpression value:
                builder.Append('(');
                Append(builder, value.Operand, defaultContext);
                builder.Append(value.Negated ? " NOT IN (" : " IN (");
                AppendList(builder, value.Values, defaultContext);
                builder.Append("))");
                break;
            case SafeMigrationSqlFunctionExpression value:
                builder
                    .Append(value.Name.ToLowerInvariant())
                    .Append('(');
                AppendList(builder, value.Arguments, defaultContext);
                builder.Append(')');
                break;
            case SafeMigrationSqlCastExpression value:
                builder.Append("CAST(");
                Append(builder, value.Operand, defaultContext);
                builder
                    .Append(" AS ")
                    .Append(MySqlSafeMigrationCastTypeRenderer.Render(value.StoreType))
                    .Append(')');
                break;
            case SafeMigrationSqlCollateExpression value:
                if (value.Schema is not null)
                {
                    throw new NotSupportedException("MySQL and MariaDB collations are not schema-qualified.");
                }

                builder.Append('(');
                Append(builder, value.Operand, defaultContext);
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
        string? storeType,
        bool defaultContext
    )
    {
        var castType = storeType is null ? null : MySqlSafeMigrationCastTypeRenderer.Render(storeType);

        if (value is null)
        {
            if (castType is null)
            {
                builder.Append("NULL");
            }
            else
            {
                builder
                    .Append("CAST(NULL AS ")
                    .Append(castType)
                    .Append(')');
            }

            return;
        }

        var mapping = _typeMappingSource.FindMapping(value.GetType(), storeType)
            ?? _typeMappingSource.FindMapping(value.GetType())
            ?? throw new NotSupportedException(
                $"MySQL has no type mapping for structured literal '{value.GetType().FullName}'.");

        // WHY: Native introducer/quoted literals can be reserialized by the
        // server when stored as DEFAULT expressions. A conversion expression
        // retains the authored bytes after reopening under another SQL mode.
        var literal = defaultContext
            ? value switch
            {
                string text => RenderStringDefault(text),
                char character => RenderStringDefault(character.ToString()),
                _ => mapping.GenerateSqlLiteral(value),
            }
            : mapping.GenerateSqlLiteral(value);

        if (castType is null)
        {
            builder.Append(literal);
        }
        else
        {
            builder
                .Append("CAST(")
                .Append(literal)
                .Append(" AS ")
                .Append(castType)
                .Append(')');
        }
    }

    private string? GetUnsupportedLiteralFeature(
        SafeMigrationSqlLiteralExpression expression
    )
    {
        if (expression.StoreType is not null
            && !MySqlSafeMigrationCastTypeRenderer.TryRender(expression.StoreType, out _))
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

    private void AppendList(
        StringBuilder builder,
        IReadOnlyList<SafeMigrationSqlExpression> values,
        bool defaultContext
    )
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                // WHY: Server DEFAULT expression metadata uses compact argument
                // lists. Ordinary expression output retains its established form.
                builder.Append(defaultContext ? "," : ", ");
            }

            Append(builder, values[index], defaultContext);
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
