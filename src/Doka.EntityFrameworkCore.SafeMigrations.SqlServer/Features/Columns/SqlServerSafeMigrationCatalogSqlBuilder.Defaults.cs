namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private string? GetUnsupportedDefaultFeature(
        ExpectedColumnDefinition definition,
        string storeType
    )
    {
        if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None)
        {
            return null;
        }

        if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal)
        {
            return IsDefaultValueRepresentable(definition.DefaultValue.LiteralValue, storeType, definition.IsNullable)
                ? null : SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode;
        }

        var expression = GetDefaultSqlExpression(definition);
        if (expression is null || _expressionRenderer.GetUnsupportedFeature(expression) is not null)
        {
            return "default_expression_unproven";
        }

        return GetDefaultExpressionFeature(expression, storeType, definition.IsNullable);
    }

    private static SafeMigrationSqlExpression? GetDefaultSqlExpression(
        ExpectedColumnDefinition definition
    )
    {
        if (definition.DefaultValue.StructuredExpression is { } expression)
        {
            return expression;
        }

        return definition.DefaultValue.SqlExpression is { } sql
            && SafeMigrationSqlExpressionParser.TryParse(sql, out var parsed) ? parsed : null;
    }

    private string? GetDefaultExpressionFeature(
        SafeMigrationSqlExpression expression,
        string storeType,
        bool nullable
    )
    {
        switch (expression)
        {
            case SafeMigrationSqlLiteralExpression literal:
                return (literal.StoreType is null
                        || IsDefaultValueRepresentable(literal.Value, literal.StoreType, nullable))
                    && IsDefaultValueRepresentable(literal.Value, storeType, nullable)
                        ? null : SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode;

            case SafeMigrationSqlCastExpression { Operand: SafeMigrationSqlLiteralExpression literal } cast:
                // WHY: A single typed constant cast has a bounded input domain.
                // Chained/computed casts need a separate intermediate-value
                // proof; checking only the original CLR value is insufficient.
                if (literal.StoreType is not null
                    && !literal.StoreType.Equals(cast.StoreType, StringComparison.OrdinalIgnoreCase))
                {
                    return "default_expression_unproven";
                }

                return GetDefaultExpressionFeature(literal, cast.StoreType, nullable)
                    ?? GetDefaultExpressionFeature(literal, storeType, nullable);

            case SafeMigrationSqlFunctionExpression function
                when function.Name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase):
                if (function.Arguments.Count < 2 || !function.Arguments.Any(IsNonNullDefaultExpression))
                {
                    return "default_expression_unproven";
                }

                // WHY: COALESCE chooses the highest-precedence argument type,
                // not the column type. Prove every branch in one compatible
                // scalar family before evaluating the expression itself.
                return function.Arguments.Select(argument
                    => GetDefaultExpressionFeature(argument, storeType, nullable: true))
                    .FirstOrDefault(static feature => feature is not null);

            case SafeMigrationSqlCurrentValueExpression current:
                return IsCompatibleCurrentTemporalType(current.Value, storeType) ? null : "default_expression_unproven";

            case SafeMigrationSqlFunctionExpression function
                when SqlServerSafeMigrationSqlExpressionRenderer.IsCurrentTemporalFunction(function.Name):
                return function.Arguments.Count == 0
                    && IsCompatibleCurrentTemporalType(SafeMigrationSqlCurrentValue.Timestamp, storeType)
                        ? null : "default_expression_unproven";

            default:
                return "default_expression_unproven";
        }
    }

    private static bool IsNonNullDefaultExpression(
        SafeMigrationSqlExpression expression
    ) => expression switch
    {
        SafeMigrationSqlLiteralExpression literal => literal.Value is not null,
        SafeMigrationSqlCastExpression { Operand: SafeMigrationSqlLiteralExpression literal }
            => literal.Value is not null,
        SafeMigrationSqlCurrentValueExpression => true,
        SafeMigrationSqlFunctionExpression function
            when SqlServerSafeMigrationSqlExpressionRenderer.IsCurrentTemporalFunction(function.Name) => true,
        SafeMigrationSqlFunctionExpression function
            when function.Name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)
            => function.Arguments.Any(IsNonNullDefaultExpression),
        _ => false,
    };

    private static bool IsCompatibleCurrentTemporalType(
        SafeMigrationSqlCurrentValue value,
        string storeType
    )
    {
        if (!TryParseStoreType(storeType, out var target))
        {
            return false;
        }

        return value == SafeMigrationSqlCurrentValue.Time
            ? target.Name == "time"
            : target.Name is "date" or "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime"
                || value == SafeMigrationSqlCurrentValue.Timestamp && target.Name == "time";
    }

    private bool IsDefaultValueRepresentable(
        object? value,
        string storeType,
        bool nullable
    )
    {
        if (value is null)
        {
            return nullable;
        }

        if (!TryParseStoreType(storeType, out var target)
            || !IsManagedValueRepresentable(value, storeType)
            || !FitsManagedValueSize(value, storeType)
            || !IsSupportedScalarClrType(value.GetType())
            || _typeMappingSource.FindMapping(value.GetType()) is null)
        {
            return false;
        }

        // WHY: The shared domain check disproves out-of-range values but does
        // not authorize text parsing or cross-family conversions on its own.

        return value switch
        {
            string or char => target.IsCharacter,
            byte[] => target.Name is "binary" or "varbinary",
            Guid => target.Name == "uniqueidentifier",
            DateTime or DateOnly or DateTimeOffset => target.Name is "date" or "datetime" or "datetime2"
                or "datetimeoffset" or "smalldatetime",
            TimeOnly or TimeSpan => target.Name == "time",
            float or double => target.Name is "float" or "real",
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal or Enum
                => target.Name is "bigint" or "bit" or "decimal" or "float" or "int" or "money" or "numeric"
                    or "real" or "smallint" or "smallmoney" or "tinyint",
            _ => false,
        };
    }

    private string? BuildDefaultValueSupportExpression(
        SafeMigrationIntent intent
    )
    {
        var definitions = GetAuthoredColumnDefinitions(intent);
        var predicates = new List<string>();
        foreach (var definition in definitions)
        {
            if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None)
            {
                continue;
            }

            predicates.Add(BuildColumnDefaultSupportPredicate(definition));
        }

        return predicates.Count == 0 ? null : Bit(string.Join(" AND ", predicates));
    }

    private static IReadOnlyList<ExpectedColumnDefinition> GetAuthoredColumnDefinitions(
        SafeMigrationIntent intent
    ) => intent switch
        {
            EnsureTableIntent table => table.Definition.Columns,
            EnsureColumnIntent column => [column.Definition],
            AlterColumnIntent column => column.OldDefinition is null
                ? [column.Definition] : new[] { column.Definition, column.OldDefinition },
            _ => Array.Empty<ExpectedColumnDefinition>(),
        };

    private string? BuildColumnCollationSupportExpression(
        SafeMigrationIntent intent
    )
    {
        var names = GetAuthoredColumnDefinitions(intent).Select(static column => column.Collation)
            .OfType<SafeMigrationCollationIdentifier>().Select(static collation => collation.Name)
            .Distinct(StringComparer.Ordinal).ToArray();

        if (names.Length == 0)
        {
            return null;
        }

        return Bit("NOT EXISTS (SELECT 1 FROM (VALUES "
            + string.Join(", ", names.Select(name => "(" + Literal(name) + ")"))
            + ") expected(name) WHERE NOT EXISTS (SELECT 1 FROM sys.fn_helpcollations() installed "
            + "WHERE installed.name = expected.name))");
    }

    private string BuildColumnDefaultSupportPredicate(
        ExpectedColumnDefinition definition
    )
    {
        var mapping = FindColumnTypeMapping(definition)
            ?? throw new NotSupportedException("SQL Server has no default-value type mapping.");

        var storeType = definition.StoreType ?? mapping.StoreType;
        if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal)
        {
            return BuildDefaultLiteralSupportPredicate(definition.DefaultValue.LiteralValue, storeType,
                definition.Collation?.Name);
        }

        var expression = GetDefaultSqlExpression(definition)
            ?? throw new NotSupportedException("SQL Server has no proven default expression.");

        return BuildDefaultExpressionSupportPredicate(expression, storeType, definition.IsNullable,
            definition.Collation?.Name);
    }

    /// <summary>Retains provider conversion authority for a required omitted or projected default.</summary>
    /// <param name="definition">The exact authored column contract.</param>
    /// <returns>A non-null scalar proof, or null when the default cannot be proven.</returns>
    internal string? BuildNonNullDefaultSupportExpression(
        ExpectedColumnDefinition definition
    )
    {
        if (GetUnsupportedColumnFeature(definition) is not null)
        {
            return null;
        }

        if (definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal)
        {
            return definition.DefaultValue.LiteralValue is null ? null
                : Bit(BuildColumnDefaultSupportPredicate(definition));
        }

        var expression = GetDefaultSqlExpression(definition);

        return expression is not null && IsNonNullDefaultExpression(expression)
            ? Bit(BuildColumnDefaultSupportPredicate(definition)) : null;
    }

    private string BuildDefaultLiteralSupportPredicate(
        object? value,
        string storeType,
        string? collation
    )
    {
        var scalar = ManagedValueRepresentationGuard(value, storeType);
        var encoded = ManagedAnsiValueRepresentationGuard(value, storeType, collation);

        // WHY: EF's ANSI DEFAULT literal is parsed under the database code
        // page before conversion to the column collation. Prove both stages;
        // a destination-only roundtrip cannot recover an already lost glyph.

        return $"({scalar}) AND ({encoded})"
            + (collation is null ? string.Empty : $" AND ({ManagedAnsiValueRepresentationGuard(value, storeType)})");
    }

    private string BuildDefaultExpressionSupportPredicate(
        SafeMigrationSqlExpression expression,
        string storeType,
        bool nullable,
        string? collation
    )
    {
        if (expression is SafeMigrationSqlFunctionExpression function
            && function.Name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase))
        {
            return "(" + string.Join(" AND ", function.Arguments.Select(argument =>
                BuildDefaultExpressionSupportPredicate(argument, storeType, nullable: true, collation))) + ")";
        }

        if (expression is SafeMigrationSqlLiteralExpression { Value: null }
            || expression is SafeMigrationSqlCastExpression
                { Operand: SafeMigrationSqlLiteralExpression { Value: null } })
        {
            return nullable ? "1 = 1" : "1 = 0";
        }

        // WHY: Each inner conversion must itself be non-throwing. Wrapping a
        // target-typed literal or CAST in an outer TRY_CAST does not protect
        // SQL Server from an exception raised while evaluating that operand.
        var safeOperand = expression switch
        {
            SafeMigrationSqlLiteralExpression literal => NonThrowingDefaultLiteral(literal, storeType),
            SafeMigrationSqlCastExpression { Operand: SafeMigrationSqlLiteralExpression literal } cast
                => NonThrowingScalarConversion(NonThrowingDefaultLiteral(literal, cast.StoreType),
                    literal.Value, cast.StoreType),
            SafeMigrationSqlCurrentValueExpression current => "TRY_CAST(SYSDATETIME() AS " + (current.Value switch
            {
                SafeMigrationSqlCurrentValue.Date => "date",
                SafeMigrationSqlCurrentValue.Time
                    => $"time({(current.Precision ?? 6).ToString(CultureInfo.InvariantCulture)})",
                SafeMigrationSqlCurrentValue.Timestamp
                    => $"datetime2({(current.Precision ?? 6).ToString(CultureInfo.InvariantCulture)})",
                _ => throw new ArgumentOutOfRangeException(nameof(expression)),
            }) + ")",
            SafeMigrationSqlFunctionExpression => _expressionRenderer.Render(expression),
            _ => throw new NotSupportedException("SQL Server has no proven default conversion operand."),
        };

        var literalValue = expression switch
        {
            SafeMigrationSqlLiteralExpression literal => literal.Value,
            SafeMigrationSqlCastExpression { Operand: SafeMigrationSqlLiteralExpression literal } => literal.Value,
            _ => null,
        };

        var conversion = NonThrowingScalarConversion(safeOperand, literalValue, storeType) + " IS NOT NULL";
        var intermediate = expression switch
        {
            SafeMigrationSqlLiteralExpression { StoreType: not null } literal
                => BuildDefaultLiteralSupportPredicate(literal.Value, literal.StoreType, collation: null),
            SafeMigrationSqlCastExpression { Operand: SafeMigrationSqlLiteralExpression literal } cast
                => BuildDefaultLiteralSupportPredicate(literal.Value, cast.StoreType, collation: null),
            _ => "1 = 1",
        };

        // WHY: A typed ANSI intermediate can truncate or lose a glyph before
        // conversion to a Unicode destination. Prove its database input code
        // page and encoded capacity independently of the final column type.

        return $"({conversion}) AND ({intermediate}) "
            + $"AND ({BuildDefaultLiteralSupportPredicate(literalValue, storeType, collation)})";
    }

    private string NonThrowingDefaultLiteral(
        SafeMigrationSqlLiteralExpression expression,
        string fallbackStoreType
    )
    {
        var raw = ManagedValueLiteral(expression.Value, expression.StoreType ?? fallbackStoreType);

        return expression.StoreType is null ? raw
            : NonThrowingScalarConversion(raw, expression.Value, expression.StoreType);
    }

    private static string NonThrowingScalarConversion(
        string operand,
        object? value,
        string storeType
    )
    {
        // WHY: TRY_CAST has a documented large-input limitation. Intrinsic
        // same-family LOB conversion does not parse values or overflow; the
        // separate capacity and encoding predicates still prove exact storage.
        var intrinsic = value is string { Length: > 4_000 } && IsCharacterManagedStoreType(storeType)
            || value is byte[] { Length: > 8_000 }
                && storeType.Trim().Equals("varbinary(max)", StringComparison.OrdinalIgnoreCase);

        return intrinsic ? $"CONVERT({storeType}, ({operand}))" : $"TRY_CAST(({operand}) AS {storeType})";
    }
}
