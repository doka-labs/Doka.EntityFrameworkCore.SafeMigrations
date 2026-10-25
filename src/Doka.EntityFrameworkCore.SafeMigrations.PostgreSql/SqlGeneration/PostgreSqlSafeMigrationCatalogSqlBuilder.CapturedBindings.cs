namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Fences native grammar and bindings before source-bearing SQL can be planned.</summary>
    private PostgreSqlSafeMigrationRuntimePlan QualifyCapturedSourceBindings(
        PostgreSqlSafeMigrationRuntimePlan plan, SafeMigrationIntent intent)
    {
        if (!HasCapturedSource(intent) || plan.IsStaticallyUnsupported)
        {
            return plan;
        }

        HashSet<string>? roots = null;
        HashSet<string>? casts = null;
        HashSet<(string Name, bool Unary)>? operators = null;
        List<string>? functionProofs = null;
        List<string>? ownerProofs = null;
        foreach (var (expression, source, computed) in CapturedExpressions(intent))
        {
            if (!SafeMigrationSqlExpressionParser.TryCollectUnquotedColumnRootIdentifiers(
                source, out var names, foldUnquotedIdentifiers: true))
            {
                return Unsupported("opaque_sql_expression");
            }

            if (names.Length != 0)
            {
                (roots ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(names);
            }

            CollectCapturedCastTypes(expression, ref casts);
            CollectCapturedFunctionProofs(expression, intent, computed, ref functionProofs, ref ownerProofs);
            CollectCapturedOperators(expression, ref operators);
        }

        if (roots is null && casts is null && functionProofs is null && operators is null && ownerProofs is null)
        {
            return plan;
        }

        // WHY: ColId accepts unreserved and column-name keywords, whereas
        // later ColLabel parts may be reserved. The shared parser supplies
        // only the actual first unquoted role, not text from literals or types.
        var identifiers = roots is null ? "TRUE" : "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_get_keywords() k "
            + "WHERE (" + string.Join(" OR ", roots.Select(name =>
                "k.word OPERATOR(pg_catalog.=) " + Literal(name)))
            + ") AND k.catcode OPERATOR(pg_catalog.<>) 'U' AND k.catcode OPERATOR(pg_catalog.<>) 'C')";
        if (ownerProofs is not null)
        {
            identifiers = "(" + identifiers + ") AND " + string.Join(" AND ", ownerProofs);
        }

        var types = casts is null ? "TRUE" : string.Join(" AND ", casts.Select(CapturedCastBinding));
        var functions = functionProofs is null ? "TRUE" : string.Join(" AND ", functionProofs);
        var operatorBindings = operators is null ? "TRUE" : string.Join(" AND ", operators.Select(item =>
            "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_operator op WHERE op.oprname OPERATOR(pg_catalog.=) "
            + Literal(item.Name) + " AND op.oprleft OPERATOR(pg_catalog."
            + (item.Unary ? "=" : "<>") + ") 0 AND op.oprnamespace OPERATOR(pg_catalog.<>) "
            + "'pg_catalog'::pg_catalog.regnamespace AND pg_catalog.pg_operator_is_visible(op.oid))"));

        var binding = "COALESCE((" + identifiers + "), FALSE) AND COALESCE((" + types
            + "), FALSE) AND COALESCE((" + functions + "), FALSE) AND COALESCE(("
            + operatorBindings + "), FALSE)";
        var previousFailure = plan.StateEvaluationGuardFailureExpression ?? "'unsupported'";
        var previousCode = plan.StateEvaluationGuardFailureCodeExpression
            ?? Literal(plan.UnsupportedCode ?? "state_evaluation_guard");

        // WHY: INT8 and other aliases can resolve to a user domain under the
        // active search path. Canonical CLR mappings alone cannot certify that
        // the exact original CAST binds the builtin whose deparse we compare.
        return plan with
        {
            StateEvaluationGuardExpression = "(" + binding + ") AND (" + plan.StateEvaluationGuardExpression + ")",
            StateEvaluationGuardFailureExpression = "CASE WHEN NOT (" + binding
                + ") THEN 'unsupported' ELSE (" + previousFailure + ") END",
            StateEvaluationGuardFailureCodeExpression = "CASE WHEN NOT COALESCE((" + identifiers
                + "), FALSE) THEN 'captured_sql_identifier' WHEN NOT COALESCE((" + types
                + "), FALSE) THEN 'captured_sql_type_binding' WHEN NOT COALESCE((" + functions
                + "), FALSE) THEN 'captured_sql_function_binding' WHEN NOT COALESCE((" + operatorBindings
                + "), FALSE) THEN 'captured_sql_operator_binding' ELSE (" + previousCode + ") END",
        };
    }

    /// <summary>Collects overloadable roles, including implicit comparisons without an operator token.</summary>
    private static void CollectCapturedOperators(
        SafeMigrationSqlExpression expression, ref HashSet<(string Name, bool Unary)>? operators)
    {
        var traversal = new PostgreSqlSafeMigrationExpressionWalker(expression);
        try
        {
            while (traversal.TryTake(out var node))
            {
                switch (node)
                {
                    case SafeMigrationSqlBinaryExpression binary when binary.Operator
                        is not (SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or):
                        (operators ??= []).Add((CapturedOperatorName(binary.Operator), false));
                        break;
                    case SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Negate }:
                        (operators ??= []).Add(("-", true));
                        break;
                    case SafeMigrationSqlBetweenExpression:
                        (operators ??= []).Add((">=", false));
                        operators.Add(("<=", false));
                        break;
                    case SafeMigrationSqlInExpression:
                    case SafeMigrationSqlFunctionExpression function
                        when function.Name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase):
                        (operators ??= []).Add(("=", false));
                        break;
                }
            }
        }
        finally
        {
            traversal.Dispose();
        }
    }

    /// <summary>Maps finite arithmetic and comparison operators without invoking native resolution.</summary>
    private static string CapturedOperatorName(SafeMigrationSqlBinaryOperator operation) => operation switch
    {
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
        _ => throw new UnreachableException(),
    };

    /// <summary>Fences captured calls through visible builtin signatures without evaluating arguments.</summary>
    private void CollectCapturedFunctionProofs(
        SafeMigrationSqlExpression expression, SafeMigrationIntent intent, bool computed,
        ref List<string>? proofs, ref List<string>? owners)
    {
        var traversal = new PostgreSqlSafeMigrationExpressionWalker(expression);
        try
        {
            while (traversal.TryTake(out var node))
            {
                switch (node)
                {
                    case SafeMigrationSqlFunctionExpression function
                        when !function.Name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)
                            && !function.Name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase):
                        (proofs ??= []).Add(CapturedFunctionBinding(function, intent, computed));
                        break;
                    case SafeMigrationSqlCurrentValueExpression when computed:
                        // WHY: Stable clocks are valid in CHECKs but not generated columns.
                        (proofs ??= []).Add("FALSE");
                        break;
                    case SafeMigrationSqlIdentifierExpression { Parts.Count: > 1 } identifier:
                        var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(intent);
                        if (identifier.Parts.Count > 3 || identifier.Parts[^2] != owner.Table)
                        {
                            (owners ??= []).Add("FALSE");
                        }
                        else if (identifier.Parts.Count == 3)
                        {
                            // WHY: A terminal member alone does not certify its authored namespace.
                            (owners ??= []).Add("(" + Literal(identifier.Parts[0]) + " OPERATOR(pg_catalog.=) "
                                + (owner.Schema is null ? "pg_catalog.current_schema()" : Literal(owner.Schema)) + ")");
                        }

                        break;
                }
            }
        }
        finally
        {
            traversal.Dispose();
        }
    }

    /// <summary>Certifies a unique builtin overload and excludes application overloads before row planning.</summary>
    private string CapturedFunctionBinding(
        SafeMigrationSqlFunctionExpression function, SafeMigrationIntent intent, bool computed)
    {
        var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(intent);
        var arguments = new List<string>(function.Arguments.Count);
        foreach (var argument in function.Arguments)
        {
            if (CapturedArgumentType(argument, intent, owner.Table!, owner.Schema) is not { } type)
            {
                return "FALSE";
            }

            arguments.Add(type);
        }

        var arity = arguments.Count.ToString(CultureInfo.InvariantCulture);
        var family = "p.proname OPERATOR(pg_catalog.=) " + Literal(function.Name.ToLowerInvariant())
            + " AND pg_catalog.pg_function_is_visible(p.oid) AND (p.pronargs OPERATOR(pg_catalog.-) "
            + "p.pronargdefaults) OPERATOR(pg_catalog.<=) " + arity
            + " AND (p.provariadic OPERATOR(pg_catalog.<>) 0 OR " + arity
            + " OPERATOR(pg_catalog.<=) p.pronargs)";

        var eligible = new List<string>(arguments.Count);
        var exact = new List<string>(arguments.Count);
        // WHY: Bind each argument to the same positional entry in the native signature.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < arguments.Count; index++)
        {
            var position = index.ToString(CultureInfo.InvariantCulture);
            var actual = "(" + arguments[index] + ")::pg_catalog.oid";
            var expected = "p.proargtypes[" + position + "]";
            exact.Add(actual + " OPERATOR(pg_catalog.=) " + expected);
            eligible.Add("EXISTS (SELECT 1 FROM pg_catalog.pg_type t WHERE t.oid OPERATOR(pg_catalog.=) " + expected
                + " AND t.typnamespace OPERATOR(pg_catalog.=) 'pg_catalog'::pg_catalog.regnamespace "
                + "AND t.typtype OPERATOR(pg_catalog.=) 'b') AND ("
                + actual + " OPERATOR(pg_catalog.=) 705 OR " + actual + " OPERATOR(pg_catalog.=) " + expected
                + " OR EXISTS (SELECT 1 FROM pg_catalog.pg_cast cv WHERE cv.castsource OPERATOR(pg_catalog.=) "
                + actual + " AND cv.casttarget OPERATOR(pg_catalog.=) " + expected
                + " AND cv.castcontext OPERATOR(pg_catalog.=) 'i'))");
        }

        var builtin = " FROM pg_catalog.pg_proc p WHERE " + family
            + " AND p.pronamespace OPERATOR(pg_catalog.=) 'pg_catalog'::pg_catalog.regnamespace "
            + "AND p.provariadic OPERATOR(pg_catalog.=) 0 AND p.prokind OPERATOR(pg_catalog.=) 'f' "
            + "AND p.pronargs OPERATOR(pg_catalog.=) " + arity;
        var exactSignature = exact.Count == 0 ? "TRUE" : string.Join(" AND ", exact);
        var immutable = computed ? " AND p.provolatile OPERATOR(pg_catalog.=) 'i'" : string.Empty;

        // WHY: Native resolution prefers an exact signature before considering
        // implicit alternatives. ABS(integer) must not become ambiguous merely
        // because integer can also reach the numeric and floating overloads.
        // A visible application overload could win native resolution even
        // when a builtin is also coercible. Unknown or competing bindings remain
        // unsupported; proving a signature must never execute an application function.
        return "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_proc p WHERE " + family
            + " AND p.pronamespace OPERATOR(pg_catalog.<>) 'pg_catalog'::pg_catalog.regnamespace) "
            + "AND (CASE WHEN EXISTS (SELECT 1"
            + builtin + " AND " + exactSignature + ") THEN (SELECT COUNT(*)" + builtin + " AND "
            + exactSignature + immutable + ") ELSE (SELECT COUNT(*)" + builtin + immutable
            + (eligible.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", eligible))
            + ") END) OPERATOR(pg_catalog.=) 1";
    }

    /// <summary>Reuses native OID inference with declared leaf identities for a missing owner.</summary>
    private string? CapturedArgumentType(
        SafeMigrationSqlExpression expression, SafeMigrationIntent intent, string table, string? schema)
    {
        if (expression is SafeMigrationSqlCastExpression cast)
        {
            return CapturedBuiltinTypeName(cast.StoreType) is { } builtin
                ? "pg_catalog.to_regtype(" + Literal("pg_catalog." + builtin) + ")::pg_catalog.oid" : null;
        }

        HashSet<string>? nestedCasts = null;
        CollectCapturedCastTypes(expression, ref nestedCasts);
        if (nestedCasts is not null)
        {
            // WHY: Native type lookup of an unresolved typmod may call its
            // input routine. Nested casts need a stronger shared certificate;
            // do not substitute a second recursive type binder here.
            return null;
        }

        if (intent is EnsureTableIntent declared && expression is SafeMigrationSqlIdentifierExpression identifier)
        {
            var column = declared.Definition.Columns.FirstOrDefault(item => item.Name == identifier.Parts[^1]);

            return column is not null && CapturedBuiltinTypeName(ResolveStoreType(column)) is { } builtin
                ? "pg_catalog.to_regtype(" + Literal("pg_catalog." + builtin) + ")::pg_catalog.oid" : null;
        }

        return PostgreSqlSafeMigrationSqlExpressionRenderer.RenderNativeResultTypeSql(
            expression, Literal, QualifiedRegclass(table, schema));
    }

    /// <summary>Keeps ordinary operations allocation-free when no newly captured SQL contract exists.</summary>
    private static bool HasCapturedSource(SafeMigrationIntent intent)
    {
        if (intent is EnsureTableIntent table)
        {
            // WHY: Inspect immutable column snapshots without an interface enumerator.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < table.Definition.Columns.Count; index++)
            {
                if (table.Definition.Columns[index].CapturedComputedSql is not null)
                {
                    return true;
                }
            }

            // WHY: Inspect immutable CHECK snapshots without an interface enumerator.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < table.Definition.CheckConstraints.Count; index++)
            {
                if (table.Definition.CheckConstraints[index].CapturedSql is not null)
                {
                    return true;
                }
            }

            return false;
        }

        return intent switch
        {
            EnsureColumnIntent column => column.Definition.CapturedComputedSql is not null,
            AlterColumnIntent column => column.Definition.CapturedComputedSql is not null
                || column.OldDefinition?.CapturedComputedSql is not null,
            EnsureCheckConstraintIntent check => check.Definition.CapturedSql is not null,
            _ => false,
        };
    }

    /// <summary>Enumerates captured expressions, never public typed or explicitly opaque contracts.</summary>
    private static IEnumerable<(SafeMigrationSqlExpression Expression, string Source, bool Computed)>
        CapturedExpressions(
        SafeMigrationIntent intent)
    {
        var columns = intent switch
        {
            EnsureTableIntent table => table.Definition.Columns,
            EnsureColumnIntent column => [column.Definition],
            AlterColumnIntent column => column.OldDefinition is null ? [column.Definition]
                : new[] { column.Definition, column.OldDefinition },
            _ => Array.Empty<ExpectedColumnDefinition>(),
        };

        foreach (var column in columns)
        {
            if (column is { CapturedComputedSql: { } source, ComputedExpression: { } expression })
            {
                yield return (expression, source, true);
            }
        }

        var checks = intent switch
        {
            EnsureTableIntent table => table.Definition.CheckConstraints,
            EnsureCheckConstraintIntent check => [check.Definition],
            _ => Array.Empty<ExpectedCheckConstraintDefinition>(),
        };

        foreach (var check in checks)
        {
            if (check is { CapturedSql: { } source, Expression: { } expression })
            {
                yield return (expression, source, false);
            }
        }
    }

    /// <summary>Maps builtin identities; custom/domain mappings cannot borrow a canonical alias proof.</summary>
    private string? CapturedBuiltinTypeName(string type)
        => _typeMappingSource.FindMapping(type)?.StoreTypeNameBase switch
        {
            "boolean" or "bool" => "bool",
            "smallint" or "int2" => "int2",
            "integer" or "int" or "int4" => "int4",
            "bigint" or "int8" => "int8",
            "numeric" or "decimal" => "numeric",
            "real" or "float4" => "float4",
            "double precision" or "float8" => "float8",
            "text" => "text",
            "character varying" or "varchar" => "varchar",
            "character" or "char" or "bpchar" => "bpchar",
            "uuid" => "uuid",
            "bytea" => "bytea",
            "date" => "date",
            "timestamp without time zone" or "timestamp" => "timestamp",
            "timestamp with time zone" or "timestamptz" => "timestamptz",
            "time without time zone" or "time" => "time",
            "time with time zone" or "timetz" => "timetz",
            "interval" => "interval",
            "json" => "json",
            "jsonb" => "jsonb",
            _ => null,
        };

    /// <summary>Resolves the bare original type without executing user typmod parsers through TO_REGTYPE.</summary>
    private string CapturedCastBinding(string type)
    {
        if (CapturedBuiltinTypeName(type) is not { } builtin)
        {
            return "FALSE";
        }

        var nativeBase = type;
        var open = type.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = type.IndexOf(')', open);
            if (close < 0 || !ValidCapturedTypeModifier(builtin, type.AsSpan(open + 1, close - open - 1)))
            {
                return "FALSE";
            }

            // WHY: Resolving a complete user-shadowed typmod can invoke its
            // typmod_in routine even during a metadata-only cast probe. Prove
            // the original base OID first, with builtin modifiers checked here.
            nativeBase = string.Concat(type.AsSpan(0, open), type.AsSpan(close + 1));
        }

        return "pg_catalog.to_regtype(" + Literal(nativeBase) + ") OPERATOR(pg_catalog.=) pg_catalog.to_regtype("
            + Literal("pg_catalog." + builtin) + ")";
    }

    /// <summary>Certifies only builtin modifiers shared by every supported PostgreSQL version.</summary>
    private static bool ValidCapturedTypeModifier(string builtin, ReadOnlySpan<char> modifier)
    {
        var comma = modifier.IndexOf(',');
        var first = comma < 0 ? modifier : modifier[..comma];
        if (!int.TryParse(first.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var precision))
        {
            return false;
        }

        if (builtin == "numeric")
        {
            return precision is >= 1 and <= 1000 && (comma < 0
                || int.TryParse(modifier[(comma + 1)..].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var scale) && scale >= 0 && scale <= precision);
        }

        return comma < 0 && (builtin is "varchar" or "bpchar" ? precision is >= 1 and <= 10_485_760
            : builtin is "time" or "timetz" or "timestamp" or "timestamptz" or "interval"
                && precision is >= 0 and <= 6);
    }

    /// <summary>Collects actual CAST nodes without recursion or changing IN's historical binding order.</summary>
    private static void CollectCapturedCastTypes(SafeMigrationSqlExpression expression, ref HashSet<string>? types)
    {
        var traversal = new PostgreSqlSafeMigrationExpressionWalker(expression, inValuesFirst: true);
        try
        {
            while (traversal.TryTake(out var node))
            {
                switch (node)
                {
                    case SafeMigrationSqlCastExpression cast:
                        (types ??= new HashSet<string>(StringComparer.Ordinal)).Add(cast.StoreType);
                        break;
                    case SafeMigrationSqlUnaryExpression or SafeMigrationSqlBinaryExpression
                        or SafeMigrationSqlNullTestExpression or SafeMigrationSqlCollateExpression
                        or SafeMigrationSqlBetweenExpression or SafeMigrationSqlInExpression
                        or SafeMigrationSqlFunctionExpression or SafeMigrationSqlCaseExpression
                        or SafeMigrationSqlLiteralExpression or SafeMigrationSqlIdentifierExpression
                        or SafeMigrationSqlCurrentValueExpression or SafeMigrationSqlProviderFragmentExpression
                        or SafeMigrationSqlOpaqueExpression:
                        break;
                    default:
                        throw new UnreachableException();
                }
            }
        }
        finally
        {
            traversal.Dispose();
        }
    }
}
