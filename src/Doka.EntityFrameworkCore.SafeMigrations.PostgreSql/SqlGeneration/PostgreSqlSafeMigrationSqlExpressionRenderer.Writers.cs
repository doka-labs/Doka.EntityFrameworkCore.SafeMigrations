namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationSqlExpressionRenderer
{
    /// <summary>Shares one ordered traversal between execution text and bounded catalog-candidate writers.</summary>
    private interface IExpressionTextWriter
    {
        /// <summary>Whether collation nodes keep authored parentheses rather than PostgreSQL's pretty form.</summary>
        bool ParenthesizeCollations { get; }

        /// <summary>Whether this catalog candidate uses native literal and SQL special-form spelling.</summary>
        bool NativeLiteralForms { get; }

        /// <summary>Gets or sets the nesting local to this one render, never shared across callers.</summary>
        int CaseDepth { get; set; }

        /// <summary>Appends the server's typed implicit ELSE NULL for this ordered result set.</summary>
        void AppendImplicitCaseNull(
            SafeMigrationSqlCaseExpression expression
        );

        /// <summary>Removes only the immediately preceding emitted separator space.</summary>
        void TrimTrailingSpace();

        /// <summary>
        /// Appends a native constant without converting its captured decimal token through CLR floats.
        /// </summary>
        bool TryAppendCapturedNumeric(
            SafeMigrationSqlLiteralExpression literal,
            bool negate = false
        );

        /// <summary>Appends an UNKNOWN string input under a native relation-column type.</summary>
        /// <param name="literal">The uncast literal retaining its original value.</param>
        /// <param name="column">The directly compared structured column identifier.</param>
        /// <returns>Whether a context-scoped native candidate was appended.</returns>
        bool TryAppendNativeUnknownLiteral(
            SafeMigrationSqlLiteralExpression literal,
            SafeMigrationSqlIdentifierExpression column
        );

        /// <summary>Appends the exact native VARCHAR-to-TEXT comparison conversion, when applicable.</summary>
        bool TryAppendNativeTextColumn(
            SafeMigrationSqlIdentifierExpression column,
            SafeMigrationSqlIdentifierExpression? other = null
        );

        /// <summary>
        /// Appends only a native builtin LOWER operand whose VARCHAR conversion is independently resolved.
        /// </summary>
        /// <param name="function">The structured single-argument function call.</param>
        /// <returns>Whether the owning native type and builtin signature qualify this exact operand variant.</returns>
        bool TryAppendNativeLowerOperand(
            SafeMigrationSqlFunctionExpression function
        );

        /// <summary>Deparses a captured native column only after proving all explicit owner roles.</summary>
        bool TryAppendCapturedColumn(
            SafeMigrationSqlIdentifierExpression column
        );

        /// <summary>Appends one authored SQL character in traversal order.</summary>
        /// <param name="value">The next character.</param>
        void Append(
            char value
        );

        /// <summary>Appends authored SQL text in traversal order.</summary>
        /// <param name="value">The next text segment.</param>
        void Append(
            string value
        );

        /// <summary>Appends an immutable captured numeric token without copying its source string.</summary>
        void Append(
            ReadOnlySpan<char> value
        );

        /// <summary>Appends one identifier through the writer's authoritative quoting path.</summary>
        /// <param name="identifier">The exact identifier spelling.</param>
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

    /// <summary>Produces execution text with native delimiting and no catalog-only proof substitutions.</summary>
    private sealed class DelimitedExpressionTextWriter : IExpressionTextWriter
    {
        private readonly StringBuilder _builder = new();
        private readonly ISqlGenerationHelper _sqlGenerationHelper;

        /// <inheritdoc />
        public bool ParenthesizeCollations => true;

        /// <inheritdoc />
        public bool NativeLiteralForms => false;

        /// <inheritdoc />
        public int CaseDepth { get; set; }

        /// <inheritdoc />
        public void AppendImplicitCaseNull(
            SafeMigrationSqlCaseExpression expression
        ) => _builder.Append("NULL");

        /// <inheritdoc />
        public bool TryAppendCapturedNumeric(
            SafeMigrationSqlLiteralExpression literal,
            bool negate = false
        ) => false;

        /// <inheritdoc />
        public void TrimTrailingSpace()
        {
            if (_builder.Length > 0
                && _builder[^1] == ' ')
            {
                _builder.Length--;
            }
        }

        /// <inheritdoc />
        public bool TryAppendNativeUnknownLiteral(
            SafeMigrationSqlLiteralExpression literal,
            SafeMigrationSqlIdentifierExpression column
        ) => false;

        /// <inheritdoc />
        public bool TryAppendNativeTextColumn(
            SafeMigrationSqlIdentifierExpression column,
            SafeMigrationSqlIdentifierExpression? other = null
        ) => false;

        /// <inheritdoc />
        public bool TryAppendNativeLowerOperand(
            SafeMigrationSqlFunctionExpression function
        ) => false;

        /// <inheritdoc />
        public bool TryAppendCapturedColumn(
            SafeMigrationSqlIdentifierExpression column
        ) => false;

        /// <summary>Owns one render-local output buffer and the active native delimiter.</summary>
        /// <param name="sqlGenerationHelper">The provider's identifier delimiter.</param>
        public DelimitedExpressionTextWriter(
            ISqlGenerationHelper sqlGenerationHelper
        )
        {
            _sqlGenerationHelper = sqlGenerationHelper;
        }

        /// <inheritdoc />
        public void Append(
            char value
        ) => _builder.Append(value);

        /// <inheritdoc />
        public void Append(
            string value
        ) => _builder.Append(value);

        /// <inheritdoc />
        public void Append(
            ReadOnlySpan<char> value
        ) => _builder.Append(value);

        /// <inheritdoc />
        public void AppendIdentifier(
            string identifier
        ) => _builder.Append(_sqlGenerationHelper.DelimitIdentifier(identifier));

        /// <inheritdoc />
        public void AppendCollation(
            string name,
            string? schema
        ) => _builder.Append(_sqlGenerationHelper.DelimitIdentifier(name, schema));

        /// <inheritdoc />
        public override string ToString() => _builder.ToString();
    }

    /// <summary>
    /// Combines literal segments and server-resolved physical identity without evaluating source rows.
    /// </summary>
    private sealed class CatalogExpressionTextWriter : IExpressionTextWriter
    {
        private readonly List<string> _parts = [];
        private readonly Func<string, string> _literal;
        private readonly StringBuilder _text = new();
        private readonly string? _relationOid;
        private readonly bool _capturedSourceRoles;
        private Dictionary<SafeMigrationSqlCaseExpression, string?>? _caseResultTypes;

        /// <inheritdoc />
        public bool ParenthesizeCollations { get; }

        /// <inheritdoc />
        public bool NativeLiteralForms { get; }

        /// <inheritdoc />
        public int CaseDepth { get; set; }

        /// <summary>Shares the same native operand type proof without rendering or evaluating the expression.</summary>
        internal string? GetNativeResultType(
            SafeMigrationSqlExpression expression
        ) => ResultType(expression);

        /// <summary>Resolves native builtin numeric operand casts without invoking application operators.</summary>
        internal bool TryScheduleNativeNumericOperand(
            SafeMigrationSqlExpression operand,
            SafeMigrationSqlExpression other,
            ref SafeMigrationWorkStack<RenderStep> pending
        )
        {
            var input = ResultType(operand);
            var counterpart = ResultType(other);
            if (input is null
                || counterpart is null)
            {
                return false;
            }

            string? cast = null;
            var staticInput = int.TryParse(input, CultureInfo.InvariantCulture, out var first);
            var staticCounterpart = int.TryParse(counterpart, CultureInfo.InvariantCulture, out var second);
            if ((staticInput && first is not (20 or 21 or 23 or 700 or 701 or 1700))
                || (staticCounterpart && second is not (20 or 21 or 23 or 700 or 701 or 1700)))
            {
                return false;
            }

            if (staticInput && staticCounterpart)
            {
                cast = first is 20 or 21 or 23 or 1700 && second is 700 or 701
                    ? "double precision"
                    : first is 20 or 21 or 23 && second == 1700
                        ? "numeric"
                        : null;

                if (cast is null)
                {
                    return false;
                }
            }

            if (operand is SafeMigrationSqlCaseExpression)
            {
                TrimTrailingSpace();
            }

            var child = new CatalogExpressionTextWriter(
                _literal,
                ParenthesizeCollations,
                nativeLiteralForms: true,
                _relationOid,
                _capturedSourceRoles)
            {
                CaseDepth = CaseDepth,
                _caseResultTypes = _caseResultTypes,
            };

            pending.Push(
                new RenderStep(
                    RenderStepKind.NumericComplete,
                    child,
                    operand,
                    Text: cast,
                    Parent: this,
                    Input: input,
                    Common: counterpart));
            pending.Push(new RenderStep(RenderStepKind.Expression, child, operand));

            return true;
        }

        /// <summary>
        /// Completes native coercion only after its child writer has produced the original ordered text.
        /// </summary>
        internal void CompleteNativeNumericOperand(
            RenderStep step
        )
        {
            var rendered = ((CatalogExpressionTextWriter)step.Writer).BuildSqlExpression();
            FlushText();
            if (step.Text is not null)
            {
                _parts.Add("('(' || " + rendered + " || " + _literal(")::" + step.Text) + ")");
            }
            else
            {
                // WHY: Native integer cross-operators retain their input
                // widths; integer/NUMERIC and non-float/float pairs instead
                // insert these exact casts. Keep each metadata/type/text
                // candidate once so nested CASE SQL does not grow by copies.
                _parts.Add(
                    "(SELECT CASE WHEN v.input_type IN (20,21,23,1700) "
                    + "AND v.other_type IN (700,701) THEN '(' || v.input_sql || ')::double precision' "
                    + "WHEN v.input_type IN (20,21,23) AND v.other_type = 1700 "
                    + "THEN '(' || v.input_sql || ')::numeric' "
                    + "WHEN v.input_type = 1043 AND v.other_type = 1042 THEN '(' || v.input_sql || ')::bpchar' "
                    + "WHEN v.input_type = 1043 OR (v.input_type = 1042 AND v.other_type = 25) "
                    + "THEN '(' || v.input_sql || ')::text' ELSE v.input_sql END FROM (VALUES (("
                    + step.Input
                    + ")::pg_catalog.oid,("
                    + step.Common
                    + ")::pg_catalog.oid,"
                    + rendered
                    + ")) AS v(input_type,other_type,input_sql))");
            }
        }

        /// <inheritdoc />
        public bool TryAppendCapturedNumeric(
            SafeMigrationSqlLiteralExpression literal,
            bool negate = false
        )
        {
            if (literal.NumericSourceToken.IsEmpty)
            {
                return false;
            }

            var token = literal.NumericSourceToken.Span;
            var type = CapturedNumericType(literal, negate);
            var source = negate ? "-" + token.ToString() : token.ToString();
            var nativeType = type switch
            {
                "integer" => "int4", "bigint" => "int8", _ => type
            };
            var text = "(" + _literal(source) + "::pg_catalog." + nativeType + ")::pg_catalog.text";
            FlushText();
            if (type == "integer")
            {
                _parts.Add(
                    negate
                        ? "(CASE WHEN pg_catalog.left("
                        + text
                        + ", 1) = '-' THEN pg_catalog.quote_literal("
                        + text
                        + ") || '::integer' ELSE "
                        + text
                        + " END)"
                        : text);
            }
            else if (type == "numeric")
            {
                // WHY: ruleutils quotes a NUMERIC constant whose normalized
                // spelling has no decimal point, preserving its native type.
                // Let PostgreSQL normalize the exact token and scale rather
                // than round-trip its potentially approximate CLR value.
                _parts.Add(
                    "(CASE WHEN pg_catalog.strpos("
                    + text
                    + ", '.') > 0 AND pg_catalog.left("
                    + text
                    + ", 1) <> '-' THEN "
                    + text
                    + " ELSE pg_catalog.quote_literal("
                    + text
                    + ") || '::numeric' END)");
            }
            else
            {
                _parts.Add("(pg_catalog.quote_literal(" + text + ") || " + _literal("::" + type) + ")");
            }

            return true;
        }

        /// <summary>Preserves native integer boundary folding and decimal/exponent token domains.</summary>
        private static string CapturedNumericType(
            SafeMigrationSqlLiteralExpression literal,
            bool negate = false
        )
        {
            if (literal.NumericSourceToken.Span.ContainsAny('.', 'e', 'E'))
            {
                return "numeric";
            }

            return literal.Value switch
            {
                int => "integer",
                long number when negate && number == 2_147_483_648L => "integer",
                long => "bigint",
                decimal number when negate && number == 9_223_372_036_854_775_808m => "bigint",
                _ => "numeric",
            };
        }

        /// <inheritdoc />
        public void TrimTrailingSpace()
        {
            if (_text.Length > 0
                && _text[^1] == ' ')
            {
                _text.Length--;
            }
        }

        /// <inheritdoc />
        public void AppendImplicitCaseNull(
            SafeMigrationSqlCaseExpression expression
        )
        {
            var type = CaseResultType(expression);
            FlushText();
            _parts.Add(
                type is null ? "NULL" : "('NULL::' || pg_catalog.format_type((" + type + ")::pg_catalog.oid, NULL))");
        }

        /// <summary>Matches native implicit branch casts using only captured builtin type identities.</summary>
        internal bool TryScheduleCaseResult(
            SafeMigrationSqlExpression result,
            SafeMigrationSqlCaseExpression expression,
            ref SafeMigrationWorkStack<RenderStep> pending
        )
        {
            if (result is SafeMigrationSqlLiteralExpression { Value: null })
            {
                AppendImplicitCaseNull(expression);

                return true;
            }

            var common = CaseResultType(expression);
            var input = ResultType(result);
            if (common is null
                || input is null
                || StringComparer.Ordinal.Equals(common, input))
            {
                return false;
            }

            if (result is SafeMigrationSqlLiteralExpression { Value: string text, StoreType: null })
            {
                FlushText();
                _parts.Add(
                    "("
                    + _literal("'" + text.Replace("'", "''", StringComparison.Ordinal) + "'::")
                    + " || pg_catalog.format_type(("
                    + common
                    + ")::pg_catalog.oid, NULL))");

                return true;
            }

            if (result is SafeMigrationSqlCaseExpression)
            {
                TrimTrailingSpace();
            }

            var child = new CatalogExpressionTextWriter(
                _literal,
                ParenthesizeCollations,
                nativeLiteralForms: true,
                _relationOid,
                _capturedSourceRoles)
            {
                CaseDepth = CaseDepth,
                _caseResultTypes = _caseResultTypes,
            };

            pending.Push(
                new RenderStep(
                    RenderStepKind.CaseResultComplete,
                    child,
                    result,
                    Parent: this,
                    Input: input,
                    Common: common));
            pending.Push(new RenderStep(RenderStepKind.Expression, child, result));

            return true;
        }

        /// <summary>Appends one completed native CASE branch without recursively rendering nested branches.</summary>
        internal void CompleteCaseResult(
            RenderStep step
        )
        {
            var rendered = ((CatalogExpressionTextWriter)step.Writer).BuildSqlExpression();
            FlushText();

            // WHY: Native ruleutils retains an implicit widening cast, unlike
            // a folded explicit literal CAST. Match only this exact typed
            // branch representation, never strip casts from the stored SQL.
            _parts.Add(
                "(CASE WHEN ("
                + step.Common
                + ") = ("
                + step.Input
                + ") THEN "
                + rendered
                + " ELSE '(' || "
                + rendered
                + " || ')::' || pg_catalog.format_type(("
                + step.Common
                + ")::pg_catalog.oid, NULL) END)");
        }

        /// <summary>Resolves native built-in CASE result types without evaluating application rows.</summary>
        private string? CaseResultType(
            SafeMigrationSqlCaseExpression expression
        )
        {
            if (_caseResultTypes is not null
                && _caseResultTypes.TryGetValue(expression, out var captured))
            {
                return captured;
            }

            return ResultType(expression);
        }

        /// <summary>Preserves type roles for typed leaves and the supported native scalar result families.</summary>
        private string? ResultType(
            SafeMigrationSqlExpression expression
        )
        {
            var pending = new SafeMigrationWorkStack<ResultTypeFrame>();
            try
            {
                while (true)
                {
                    string? result;
                    switch (expression)
                    {
                        case SafeMigrationSqlUnaryExpression unary
                            when unary.Operator != SafeMigrationSqlUnaryOperator.Not
                            && !TryGetCapturedNumericUnary(unary, out _, out _):
                            expression = unary.Operand;
                            while (expression is SafeMigrationSqlUnaryExpression
                                   {
                                       Operator: SafeMigrationSqlUnaryOperator.Negate
                                   } nested)
                            {
                                expression = nested.Operand;
                            }

                            continue;
                        case SafeMigrationSqlCollateExpression collate:
                            expression = collate.Operand;
                            continue;
                        case SafeMigrationSqlBinaryExpression
                        {
                            Operator: not (>= SafeMigrationSqlBinaryOperator.And
                            and <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual)
                        } binary:
                            pending.Push(new ResultTypeFrame(binary));
                            expression = binary.Left;
                            continue;
                        case SafeMigrationSqlCaseExpression conditional:
                            if (_caseResultTypes is not null
                                && _caseResultTypes.TryGetValue(conditional, out result))
                            {
                                break;
                            }

                            pending.Push(
                                new ResultTypeFrame(
                                    conditional,
                                    conditional.ElseResult is null ? 0 : -1,
                                    Types: new List<string>(conditional.WhenClauses.Count + 1)));
                            expression = conditional.ElseResult ?? conditional.WhenClauses[0].Result;
                            continue;
                        case SafeMigrationSqlFunctionExpression function when function.Name.Equals(
                            "COALESCE",
                            StringComparison.OrdinalIgnoreCase):
                            if (function.Arguments.Count == 0)
                            {
                                result = null;
                                break;
                            }

                            pending.Push(
                                new ResultTypeFrame(function, Types: new List<string>(function.Arguments.Count)));
                            expression = function.Arguments[0];
                            continue;
                        default:
                            result = LeafResultType(expression);
                            break;
                    }

                    while (pending.TryPop(out var frame))
                    {
                        if (frame.Expression is SafeMigrationSqlBinaryExpression binary)
                        {
                            if (frame.Index == 0)
                            {
                                // WHY: The original numeric resolver inspected
                                // both sides even when the left was unknown.
                                pending.Push(
                                    frame with
                                    {
                                        Index = 1,
                                        Left = result
                                    });
                                expression = binary.Right;
                                goto NextExpression;
                            }

                            result = NumericBinaryResultType(frame.Left, result);
                            continue;
                        }

                        if (result is not null)
                        {
                            frame.Types!.Add(result);
                            var next = frame.Index + 1;
                            if (frame.Expression is SafeMigrationSqlCaseExpression conditional
                                && next < conditional.WhenClauses.Count)
                            {
                                pending.Push(frame with { Index = next });
                                expression = conditional.WhenClauses[next].Result;
                                goto NextExpression;
                            }

                            if (frame.Expression is SafeMigrationSqlFunctionExpression function
                                && next < function.Arguments.Count)
                            {
                                pending.Push(frame with { Index = next });
                                expression = function.Arguments[next];
                                goto NextExpression;
                            }

                            result = CommonResultType(frame.Types);
                        }

                        if (frame.Expression is SafeMigrationSqlCaseExpression completed)
                        {
                            // WHY: Keep the existing per-render CASE cache and
                            // its ELSE-before-WHEN callback/short-circuit order.
                            (_caseResultTypes ??= []).Add(completed, result);
                        }
                    }

                    return result;
                    NextExpression:
                    continue;
                }
            }
            finally
            {
                pending.Dispose();
            }
        }

        /// <summary>Resolves leaves without recursively reentering CASE or arithmetic inference.</summary>
        private string? LeafResultType(
            SafeMigrationSqlExpression expression
        ) => expression switch
        {
            SafeMigrationSqlLiteralExpression { NumericSourceToken.IsEmpty: false } value => CapturedNumericType(value)
                switch
                {
                    "integer" => "23", "bigint" => "20", _ => "1700"
                },
            SafeMigrationSqlLiteralExpression { StoreType: { } storeType } =>
                "pg_catalog.to_regtype(" + _literal(storeType) + ")::pg_catalog.oid",
            SafeMigrationSqlLiteralExpression { Value: null or string } => "705",
            SafeMigrationSqlLiteralExpression { Value: bool } => "16",
            SafeMigrationSqlLiteralExpression { Value: sbyte or byte or short or ushort or int } => "23",
            SafeMigrationSqlLiteralExpression { Value: uint or long } => "20",
            SafeMigrationSqlLiteralExpression { Value: decimal or ulong } => "1700",
            SafeMigrationSqlLiteralExpression { Value: float } => "700",
            SafeMigrationSqlLiteralExpression { Value: double } => "701",
            SafeMigrationSqlIdentifierExpression column when _relationOid is not null =>
                "(SELECT a.atttypid FROM pg_catalog.pg_attribute a WHERE a.attrelid OPERATOR(pg_catalog.=) "
                + _relationOid
                + " AND a.attname OPERATOR(pg_catalog.=) "
                + _literal(column.Parts[^1])
                + " AND a.attnum OPERATOR(pg_catalog.>) 0 AND NOT a.attisdropped"
                + (_capturedSourceRoles ? " AND " + CapturedOwnerProof(column) : string.Empty)
                + ")",
            SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not } => "16",
            SafeMigrationSqlUnaryExpression unary when TryGetCapturedNumericUnary(
                unary,
                out var literal,
                out var negate) => CapturedNumericType(literal, negate) switch
            {
                "integer" => "23", "bigint" => "20", _ => "1700"
            },
            SafeMigrationSqlBinaryExpression
            {
                Operator: >= SafeMigrationSqlBinaryOperator.And
                and <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
            } => "16",
            SafeMigrationSqlNullTestExpression or SafeMigrationSqlBetweenExpression or SafeMigrationSqlInExpression =>
                "16",
            SafeMigrationSqlCastExpression cast => "pg_catalog.to_regtype("
                + _literal(cast.StoreType)
                + ")::pg_catalog.oid",
            _ => null,
        };

        /// <summary>Preserves native numeric operator result types rather than CASE's common-input selection.</summary>
        private static string? NumericBinaryResultType(
            string? left,
            string? right
        )
        {
            if (left is null
                || right is null)
            {
                return null;
            }

            // WHY: REAL combined with an integer/NUMERIC operand selects a
            // DOUBLE PRECISION operator, unlike two REAL inputs. A generic
            // common-type rank cannot represent that native operator rule.
            return "(SELECT CASE WHEN l = 700 AND r = 700 THEN 700 "
                + "WHEN l IN (700,701) OR r IN (700,701) THEN 701 "
                + "WHEN l = 1700 OR r = 1700 THEN 1700 WHEN l = 20 OR r = 20 THEN 20 "
                + "WHEN l = 23 OR r = 23 THEN 23 WHEN l = 21 AND r = 21 THEN 21 ELSE NULL END "
                + "FROM (VALUES (("
                + left
                + ")::pg_catalog.oid,("
                + right
                + ")::pg_catalog.oid)) AS t(l,r))";
        }

        /// <summary>Retains one continuation per active type dependency, not per list member.</summary>
        private readonly record struct ResultTypeFrame(
            SafeMigrationSqlExpression Expression,
            int Index = 0,
            string? Left = null,
            List<string>? Types = null
        );

        /// <summary>Applies native builtin common-type selection conservatively to a bounded result list.</summary>
        private static string CommonResultType(
            List<string> types
        )
        {
            var first = types[0];
            var allEqual = true;
            // WHY: Compare the remaining result types with the already captured first type.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 1; index < types.Count; index++)
            {
                if (!StringComparer.Ordinal.Equals(types[index], first))
                {
                    allEqual = false;

                    break;
                }
            }

            if (allEqual)
            {
                return first == "705" ? "25" : first;
            }

            var rows = string.Join(
                ", ",
                types.Select((
                    type,
                    index
                ) => "(" + index.ToString(CultureInfo.InvariantCulture) + ", (" + type + ")::pg_catalog.oid)"));

            // WHY: UNKNOWN literals do not select a type. Numeric builtins
            // widen in the native implicit-cast order; other families retain
            // the first typed result, with ELSE considered before WHENs.
            return "(SELECT CASE WHEN COUNT(*) FILTER (WHERE t <> 705) = 0 THEN 25::pg_catalog.oid "
                + "WHEN BOOL_AND(t IN (20, 21, 23, 700, 701, 1700, 705)) THEN "
                + "(ARRAY_AGG(t ORDER BY CASE t WHEN 701 THEN 6 WHEN 700 THEN 5 WHEN 1700 THEN 4 "
                + "WHEN 20 THEN 3 WHEN 23 THEN 2 WHEN 21 THEN 1 ELSE 0 END DESC))[1] "
                + "ELSE (ARRAY_AGG(t ORDER BY i) FILTER (WHERE t <> 705))[1] END "
                + "FROM (VALUES "
                + rows
                + ") result_types(i,t))";
        }

        /// <summary>Owns one candidate's ordered segments and native proof context.</summary>
        /// <param name="literal">The current builder's SQL literal encoder.</param>
        /// <param name="parenthesizeCollations">Whether to retain authored collation parentheses.</param>
        /// <param name="nativeLiteralForms">Whether to use the native deparser's literal forms.</param>
        /// <param name="relationOid">The independently resolved physical source owner, when present.</param>
        /// <param name="capturedSourceRoles">Whether all authored source qualifiers have been certified.</param>
        public CatalogExpressionTextWriter(
            Func<string, string> literal,
            bool parenthesizeCollations = true,
            bool nativeLiteralForms = false,
            string? relationOid = null,
            bool capturedSourceRoles = false
        )
        {
            _literal = literal;
            // WHY: The pretty deparser drops parentheses around COLLATE inside function arguments.
            // Keep both bounded catalog candidates; execution always retains authored parentheses.
            ParenthesizeCollations = parenthesizeCollations;
            NativeLiteralForms = nativeLiteralForms;
            _relationOid = relationOid;
            _capturedSourceRoles = capturedSourceRoles;
        }

        /// <inheritdoc />
        public bool TryAppendCapturedColumn(
            SafeMigrationSqlIdentifierExpression column
        )
        {
            if (!_capturedSourceRoles)
            {
                return false;
            }

            FlushText();
            _parts.Add(NativeColumnText(column));

            return true;
        }

        /// <summary>Strips only a complete independently proven native owner, never arbitrary qualifier text.</summary>
        private string NativeColumnText(
            SafeMigrationSqlIdentifierExpression column
        )
        {
            var name = "pg_catalog.quote_ident(" + _literal(column.Parts[^1]) + ")";
            if (!_capturedSourceRoles
                || column.Parts.Count == 1)
            {
                return _capturedSourceRoles
                    ? name
                    : string.Join(
                        " || '.' || ",
                        column.Parts.Select(part => "pg_catalog.quote_ident(" + _literal(part) + ")"));
            }

            // WHY: pg_get_expr omits a relation's native qualifier. That spelling
            // is valid only for the same OID and complete namespace/table path;
            // an unrelated terminal member cannot acquire this alternate.
            return "(CASE WHEN " + CapturedOwnerProof(column) + " THEN " + name + " ELSE NULL END)";
        }

        /// <summary>Certifies all original owner parts against the physical expression's relation.</summary>
        private string CapturedOwnerProof(
            SafeMigrationSqlIdentifierExpression column
        ) => column.Parts.Count == 1
            ? "TRUE"
            : _relationOid is null || column.Parts.Count > 3
                ? "FALSE"
                : "EXISTS (SELECT 1 FROM pg_catalog.pg_class own JOIN pg_catalog.pg_namespace ns "
                + "ON ns.oid OPERATOR(pg_catalog.=) own.relnamespace WHERE own.oid OPERATOR(pg_catalog.=) "
                + _relationOid
                + " AND own.relname OPERATOR(pg_catalog.=) "
                + _literal(column.Parts[^2])
                + (column.Parts.Count == 3
                    ? " AND ns.nspname OPERATOR(pg_catalog.=) " + _literal(column.Parts[0])
                    : string.Empty)
                + ")";

        /// <inheritdoc />
        public bool TryAppendNativeUnknownLiteral(
            SafeMigrationSqlLiteralExpression literal,
            SafeMigrationSqlIdentifierExpression column
        )
        {
            if (!NativeLiteralForms
                || _relationOid is null
                || literal is not { Value: string value, StoreType: null })
            {
                return false;
            }

            var cast = new SafeMigrationSqlCastExpression(literal, "bigint");
            var integerInput = TryGetNativeIntegerLiteralValue(cast, "bigint", out var integer);
            var text = integer.ToString(CultureInfo.InvariantCulture);
            var target = ResultType(column)!;

            var arms = new StringBuilder("CASE ").Append(target);
            if (integerInput && integer is >= int.MinValue and <= int.MaxValue)
            {
                arms
                    .Append(" WHEN 23 THEN ")
                    .Append(_literal(text));
            }

            if (integerInput && integer is >= short.MinValue and <= short.MaxValue)
            {
                arms
                    .Append(" WHEN 21 THEN ")
                    .Append(_literal("'" + text + "'::smallint"));
            }

            if (integerInput)
            {
                arms
                    .Append(" WHEN 20 THEN ")
                    .Append(_literal("'" + text + "'::bigint"));
            }

            arms
                .Append(" WHEN 25 THEN ")
                .Append(_literal("'" + value.Replace("'", "''", StringComparison.Ordinal) + "'::text"));
            arms
                .Append(" WHEN 1043 THEN ")
                .Append(_literal("'" + value.Replace("'", "''", StringComparison.Ordinal) + "'::text"));
            arms
                .Append(" WHEN 1042 THEN ")
                .Append(_literal("'" + value.Replace("'", "''", StringComparison.Ordinal) + "'::bpchar"))
                .Append(" ELSE NULL END");

            // WHY: An unknown '1' is folded differently for INTEGER and TEXT.
            // The native owning attribute OID selects the exact representation;
            // no generic cast stripping, user input function or app-row scan is used.
            FlushText();
            _parts.Add("(" + arms + ")");

            return true;
        }

        /// <inheritdoc />
        public bool TryAppendNativeTextColumn(
            SafeMigrationSqlIdentifierExpression column,
            SafeMigrationSqlIdentifierExpression? other = null
        )
        {
            if (!NativeLiteralForms
                || _relationOid is null)
            {
                return false;
            }

            var identifier = NativeColumnText(column);

            var type = ResultType(column)!;
            var counterpart = other is null ? null : ResultType(other);
            var text = "'(' || " + identifier + " || ')::text'";
            var bpchar = "'(' || " + identifier + " || ')::bpchar'";

            // WHY: VARCHAR has no comparison operator of its own. Native
            // binding selects BPCHAR against BPCHAR, otherwise TEXT; BPCHAR
            // itself converts to TEXT only against an actual TEXT operand.
            // Retain that finite native matrix and every identifier part.
            FlushText();
            _parts.Add(
                counterpart is null
                    ? "(CASE WHEN (" + type + ") = 1043 THEN " + text + " ELSE " + identifier + " END)"
                    : "(CASE WHEN ("
                    + type
                    + ") = 1043 AND ("
                    + counterpart
                    + ") = 1042 THEN "
                    + bpchar
                    + " WHEN ("
                    + type
                    + ") = 1043 OR (("
                    + type
                    + ") = 1042 AND ("
                    + counterpart
                    + ") = 25) THEN "
                    + text
                    + " ELSE "
                    + identifier
                    + " END)");

            return true;
        }

        /// <inheritdoc />
        public bool TryAppendNativeLowerOperand(
            SafeMigrationSqlFunctionExpression function
        )
        {
            if (!NativeLiteralForms
                || _relationOid is null
                || !function.Name.Equals("LOWER", StringComparison.OrdinalIgnoreCase)
                || function.Arguments.Count != 1
                || function.Arguments[0] is not SafeMigrationSqlIdentifierExpression column)
            {
                return false;
            }

            var identifier = NativeColumnText(column);

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
                + _relationOid
                + " AND a.attname = "
                + _literal(column.Parts[^1])
                + " AND a.attnum > 0 AND NOT a.attisdropped AND a.atttypid = 1043)";

            // WHY: Native LOWER(VARCHAR) records its implicit TEXT conversion.
            // Use the actual owning attribute and visible builtin signature;
            // a custom exact or scalar-polymorphic overload must not acquire this candidate.
            FlushText();
            _parts.Add(
                "(CASE WHEN "
                + varchar
                + " AND "
                + nativeBuiltin
                + " AND NOT "
                + competingOverload
                + " THEN '(' || "
                + identifier
                + " || ')::text' ELSE "
                + identifier
                + " END)");

            return true;
        }

        /// <inheritdoc />
        public void Append(
            char value
        ) => _text.Append(value);

        /// <inheritdoc />
        public void Append(
            string value
        ) => _text.Append(value);

        /// <inheritdoc />
        public void Append(
            ReadOnlySpan<char> value
        ) => _text.Append(value);

        /// <inheritdoc />
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

        /// <summary>Flushes the final ordered segment and returns the candidate-producing SQL expression.</summary>
        /// <returns>
        /// The single segment or ordered concatenation, retaining the original literal callback order.
        /// </returns>
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
