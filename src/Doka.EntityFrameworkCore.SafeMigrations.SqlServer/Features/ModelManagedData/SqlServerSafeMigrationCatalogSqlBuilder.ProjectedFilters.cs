namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Enumerates validated filtered-index columns and their optional constant operands.</summary>
    /// <param name="expression">A predicate already accepted by the filtered-index grammar.</param>
    /// <returns>Each NULL-test column or column/constant comparison in immutable predicate order.</returns>
    internal static IEnumerable<(string Column, SafeMigrationSqlLiteralExpression? Literal)> GetIndexFilterComparisons(
        SafeMigrationSqlExpression expression
    )
    {
        switch (expression)
        {
            case SafeMigrationSqlNullTestExpression { Operand: SafeMigrationSqlIdentifierExpression identifier }:
                yield return (identifier.Parts[0], null);
                break;
            case SafeMigrationSqlBinaryExpression { Operator: SafeMigrationSqlBinaryOperator.And } conjunction:
                foreach (var comparison in GetIndexFilterComparisons(conjunction.Left))
                {
                    yield return comparison;
                }

                foreach (var comparison in GetIndexFilterComparisons(conjunction.Right))
                {
                    yield return comparison;
                }

                break;
            case SafeMigrationSqlBinaryExpression
            {
                Left: SafeMigrationSqlIdentifierExpression identifier,
                Right: SafeMigrationSqlLiteralExpression literal,
            }:
                yield return (identifier.Parts[0], literal);
                break;
            case SafeMigrationSqlInExpression { Operand: SafeMigrationSqlIdentifierExpression identifier } predicate:
                foreach (var value in predicate.Values)
                {
                    yield return (identifier.Parts[0], (SafeMigrationSqlLiteralExpression)value);
                }

                break;
            default:
                throw new ArgumentException(
                    "A filtered-index predicate must be validated before extracting comparisons.",
                    nameof(expression));
        }
    }

    /// <summary>Rejects filtered predicates requiring a forbidden conversion of the destination column.</summary>
    /// <param name="literal">The actual emitted right-hand constant.</param>
    /// <param name="storeType">The canonical destination column type.</param>
    /// <returns>True when SQL Server's documented type precedence converts only the right-hand constant.</returns>
    internal bool IsIndexFilterLiteralCompatible(
        SafeMigrationSqlLiteralExpression literal,
        string storeType
    )
    {
        var target = _typeMappingSource.FindMapping(storeType);
        if (target is null || _expressionRenderer.GetUnsupportedFeature(literal) is not null)
        {
            return false;
        }

        // WHY: A SELECT conversion proof is insufficient for CREATE INDEX.
        // SQL Server forbids implicit conversions on the predicate's column
        // side; only constant-side conversion can preserve the authored filter.
        var sourceRank = FilterTypePrecedence(EmittedIndexFilterLiteralStoreType(literal));
        var targetRank = FilterTypePrecedence(target.StoreTypeNameBase);

        return sourceRank != 0 && targetRank != 0 && targetRank >= sourceRank;
    }

    /// <summary>Gets the built-in scalar type of the actual emitted right-hand SQL literal.</summary>
    /// <param name="literal">The immutable constant already validated by the expression renderer.</param>
    /// <returns>The SQL literal's built-in type name, or an empty string for an unmapped literal.</returns>
    internal string EmittedIndexFilterLiteralStoreType(SafeMigrationSqlLiteralExpression literal)
    {
        if (literal.StoreType is { } explicitType)
        {
            return _typeMappingSource.FindMapping(explicitType)?.StoreTypeNameBase ?? string.Empty;
        }

        // WHY: CLR mapping types are not necessarily SQL literal types. GUID
        // mappings emit quoted varchar constants, while integral constants
        // outside int range bind as decimal rather than bigint. Temporal
        // literals retain an explicit natural source conversion.
        var rendered = _expressionRenderer.Render(literal);
        if (rendered.StartsWith("N'", StringComparison.Ordinal))
        {
            return "nvarchar";
        }

        if (rendered.StartsWith('\''))
        {
            return "varchar";
        }

        if (rendered.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return "varbinary";
        }

        if (rendered.StartsWith("CAST(", StringComparison.OrdinalIgnoreCase)
            || rendered.StartsWith("TRY_CAST(", StringComparison.OrdinalIgnoreCase))
        {
            return literal.Value is null ? string.Empty
                : _typeMappingSource.FindMapping(literal.Value.GetType())?.StoreTypeNameBase ?? string.Empty;
        }

        if (rendered.Contains('E') || rendered.Contains('e'))
        {
            return "float";
        }

        return rendered.Contains('.') || !int.TryParse(rendered,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? "decimal" : "int";
    }

    /// <summary>Qualifies constant conversions for exactly captured authored filtered-index columns.</summary>
    /// <param name="filter">The immutable supported filtered predicate.</param>
    /// <param name="columns">The authored column references represented by this proof.</param>
    /// <returns>A bounded constant-only scalar query, or null when the authored contract is not provable.</returns>
    internal string? BuildProjectedIndexFilterProofSql(
        SafeMigrationSqlExpression filter,
        IReadOnlyList<ExpectedColumnDefinition> columns
    )
    {
        if (ProjectedSeedFilterIdentity(filter) is null)
        {
            return null;
        }

        var definitions = columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
        var predicates = new HashSet<string>(StringComparer.Ordinal);
        var collations = new HashSet<string>(StringComparer.Ordinal);
        var bytes = 2048;
        foreach (var (name, literal) in GetIndexFilterComparisons(filter))
        {
            if (!definitions.TryGetValue(name, out var column))
            {
                // WHY: Untouched live columns retain the original catalog's
                // conversion proof. This query binds only authored definitions;
                // the caller must separately prove every live reference exists.
                continue;
            }

            var mapping = _typeMappingSource.FindMapping(
                column.ClrType, column.StoreType, unicode: column.IsUnicode,
                size: column.MaxLength, rowVersion: column.IsRowVersion,
                fixedLength: column.IsFixedLength, precision: column.Precision, scale: column.Scale);

            if (mapping is null || !IsSupportedManagedStoreType(mapping.StoreType)
                || column.ComputedExpression is not null || column.ComputedColumnSql is not null
                || column.Collation is { } collation && _expressionRenderer.GetUnsupportedFeature(
                    new SafeMigrationSqlCollateExpression(SafeMigrationSql.Identifier(name),
                        collation.Name, collation.Schema)) is not null)
            {
                return null;
            }

            if (column.Collation is { } installed)
            {
                collations.Add(installed.Name);
            }

            if (literal is null)
            {
                continue;
            }

            if (!IsIndexFilterLiteralCompatible(literal, mapping.StoreType))
            {
                return null;
            }

            var scalar = ManagedValueRepresentationGuard(literal.Value, mapping.StoreType);
            if (!AddPredicate(scalar) || !AddPredicate(ManagedAnsiValueRepresentationGuard(
                literal.Value, mapping.StoreType, column.Collation?.Name)))
            {
                return null;
            }

            if (literal.StoreType is { } emittedType)
            {
                if (!AddPredicate(ManagedValueRepresentationGuard(literal.Value, emittedType))
                    || !AddPredicate(ManagedAnsiValueRepresentationGuard(literal.Value, emittedType)))
                {
                    return null;
                }

                // WHY: The authored predicate contains this CAST before its
                // implicit target conversion. Prove the intermediate value,
                // rather than proving only the original CLR literal.
                var intermediate = _expressionRenderer.Render(literal);
                if (!AddPredicate($"TRY_CAST({intermediate} AS {mapping.StoreType}) IS NOT NULL"))
                {
                    return null;
                }
            }
        }

        var condition = predicates.Count == 0 ? "1 = 1"
            : string.Join(" AND ", predicates.Select(static predicate => $"({predicate})"));

        var body = $"SELECT CASE WHEN {condition} THEN 1 ELSE 0 END;";
        var installedGuard = collations.Count == 0 ? "1 = 1"
            : string.Join(" AND ", collations.Select(collation =>
                $"EXISTS (SELECT 1 FROM sys.fn_helpcollations() WHERE name = {Literal(collation)})"));

        // WHY: Bind constant-only SQL only after installed-collation validation.
        // Neither missing destinations nor empty row sets can authorize an
        // invalid filter conversion, and malformed constants never touch rows.
        var sql = $"IF {installedGuard} BEGIN TRY EXEC sys.sp_executesql {Literal(body)}; "
            + "END TRY BEGIN CATCH IF ERROR_NUMBER() IN (241, 242, 245, 248, 529, 8114, 8115) "
            + "SELECT 0; ELSE THROW; END CATCH ELSE SELECT 0;";

        return Encoding.UTF8.GetByteCount(sql) <= SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes ? sql : null;

        bool AddPredicate(string predicate)
        {
            if (!predicates.Add(predicate))
            {
                return true;
            }

            var size = Encoding.UTF8.GetByteCount(predicate)
                + predicate.Count(static character => character == '\'') + 8;

            if (size > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes - bytes)
            {
                return false;
            }

            bytes += size;

            return true;
        }
    }

    private static int FilterTypePrecedence(string storeType)
        => storeType.ToLowerInvariant() switch
        {
            "datetimeoffset" => 27,
            "datetime2" => 26,
            "datetime" => 25,
            "smalldatetime" => 24,
            "date" => 23,
            "time" => 22,
            "float" => 21,
            "real" => 20,
            "decimal" or "numeric" => 19,
            "money" => 18,
            "smallmoney" => 17,
            "bigint" => 16,
            "int" => 15,
            "smallint" => 14,
            "tinyint" => 13,
            "bit" => 12,
            "uniqueidentifier" => 7,
            "nvarchar" => 6,
            "nchar" => 5,
            "varchar" => 4,
            "char" => 3,
            "varbinary" => 2,
            "binary" => 1,
            _ => 0,
        };

    /// <summary>Gets a stable structured predicate identity for invocation-local proof memoization.</summary>
    /// <param name="filter">The immutable structured filter.</param>
    /// <returns>The canonical rendered predicate, or null when it is not supported.</returns>
    internal string? ProjectedSeedFilterIdentity(
        SafeMigrationSqlExpression filter
    )
        => IsSupportedIndexFilter(filter) && _expressionRenderer.GetUnsupportedFeature(filter) is null
            ? _expressionRenderer.Render(filter) : null;

    /// <summary>
    /// Validates SQL Server's filtered-index predicate grammar, independently of general SQL expressions.
    /// </summary>
    /// <param name="expression">The immutable structured filter.</param>
    /// <returns>True only for AND-connected constant comparisons, NULL tests, and non-negated IN predicates.</returns>
    internal static bool IsSupportedIndexFilter(
        SafeMigrationSqlExpression expression
    )
        => expression switch
        {
            SafeMigrationSqlNullTestExpression
                { Operand: SafeMigrationSqlIdentifierExpression { Parts.Count: 1 } } => true,
            SafeMigrationSqlBinaryExpression { Operator: SafeMigrationSqlBinaryOperator.And } conjunction
                => IsSupportedIndexFilter(conjunction.Left) && IsSupportedIndexFilter(conjunction.Right),
            SafeMigrationSqlBinaryExpression
            {
                Left: SafeMigrationSqlIdentifierExpression { Parts.Count: 1 },
                Right: SafeMigrationSqlLiteralExpression { Value: not null } literal,
            } comparison when comparison.Operator is SafeMigrationSqlBinaryOperator.Equal
                or SafeMigrationSqlBinaryOperator.NotEqual or SafeMigrationSqlBinaryOperator.LessThan
                or SafeMigrationSqlBinaryOperator.LessThanOrEqual or SafeMigrationSqlBinaryOperator.GreaterThan
                or SafeMigrationSqlBinaryOperator.GreaterThanOrEqual
                => literal.StoreType is null || IsManagedValueRepresentable(literal.Value, literal.StoreType),
            SafeMigrationSqlInExpression
            {
                Operand: SafeMigrationSqlIdentifierExpression { Parts.Count: 1 },
                Negated: false,
            } predicate => predicate.Values.All(static value => value is SafeMigrationSqlLiteralExpression
                { Value: not null } literal
                && (literal.StoreType is null || IsManagedValueRepresentable(literal.Value, literal.StoreType))),
            _ => false,
        };

    private static SafeMigrationSqlExpression? BindProjectedSeedFilter(
        SafeMigrationSqlExpression expression,
        Dictionary<string, ExpectedColumnDefinition> columns,
        Dictionary<string, string> aliases
    )
    {
        // WHY: Bind immutable identifier nodes to typed row aliases rather than
        // replacing SQL text. Opaque fragments and qualified paths cannot prove
        // which destination column is being filtered and remain unproven.
        switch (expression)
        {
            case SafeMigrationSqlIdentifierExpression identifier
                when identifier.Parts.Count == 1 && columns.ContainsKey(identifier.Parts[0]):
                var name = identifier.Parts[0];
                if (!aliases.TryGetValue(name, out var alias))
                {
                    alias = "f" + aliases.Count.ToString(CultureInfo.InvariantCulture);
                    aliases.Add(name, alias);
                }

                return new SafeMigrationSqlIdentifierExpression([alias]);
            case SafeMigrationSqlLiteralExpression literal:
                return literal;
            case SafeMigrationSqlNullTestExpression test:
                var tested = BindProjectedSeedFilter(test.Operand, columns, aliases);

                return tested is null ? null : new SafeMigrationSqlNullTestExpression(tested, test.Negated);
            case SafeMigrationSqlBinaryExpression binary
                when binary.Operator is SafeMigrationSqlBinaryOperator.And
                    or SafeMigrationSqlBinaryOperator.Equal or SafeMigrationSqlBinaryOperator.NotEqual
                    or SafeMigrationSqlBinaryOperator.LessThan or SafeMigrationSqlBinaryOperator.LessThanOrEqual
                    or SafeMigrationSqlBinaryOperator.GreaterThan or SafeMigrationSqlBinaryOperator.GreaterThanOrEqual:
                var left = BindProjectedSeedFilter(binary.Left, columns, aliases);
                var right = BindProjectedSeedFilter(binary.Right, columns, aliases);

                return left is null || right is null ? null
                    : new SafeMigrationSqlBinaryExpression(left, binary.Operator, right);
            case SafeMigrationSqlInExpression predicate:
                var operand = BindProjectedSeedFilter(predicate.Operand, columns, aliases);
                var values = predicate.Values.Select(value => BindProjectedSeedFilter(value, columns, aliases))
                    .ToArray();

                return operand is null || values.Any(static value => value is null) ? null
                    : new SafeMigrationSqlInExpression(
                        operand, values.Select(static value => value!), predicate.Negated);
            default:
                return null;
        }
    }
}
