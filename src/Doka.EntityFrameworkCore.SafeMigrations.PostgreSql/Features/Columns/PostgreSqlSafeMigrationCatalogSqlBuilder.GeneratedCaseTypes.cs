namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Identifies finite generated additions that require accepted current source-type evidence.</summary>
    internal static bool RequiresGeneratedCaseInputCapture(SafeMigrationIntent intent)
        => intent is EnsureColumnIntent { Definition.ComputedExpression: { } expression } column
                && (ContainsGeneratedCase(expression) || RequiresCapturedOperandBinding(column.Definition))
            || intent is EnsureCheckConstraintIntent { Definition.Expression: { } check }
                && ContainsGeneratedCase(check);

    /// <summary>Rechecks accepted prefix types instead of retaining an earlier live source certificate.</summary>
    internal bool? ValidateProjectedGeneratedCaseBinding(
        SafeMigrationIntent intent,
        Func<string, string?, string, (long TypeOid, long CollationOid)?> sourceType)
    {
        intent = SafeMigrationCapturedExpressionNormalization.NormalizeIntent(intent, true);
        var expression = intent switch
        {
            EnsureColumnIntent column => column.Definition.ComputedExpression,
            EnsureCheckConstraintIntent check => check.Definition.Expression,
            _ => null,
        };

        if (expression is null || !RequiresGeneratedCaseInputCapture(intent))
        {
            return true;
        }

        var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(intent);
        var target = intent is EnsureColumnIntent added ? GeneratedCaseColumnCategory(added.Definition)
            : GeneratedCaseCategory.Boolean;
        var inputs = new GeneratedCaseInputs();
        if (!ValidateGeneratedCaseOperand(expression, target, owner.Table!, owner.Schema, null, inputs,
            capturedSource: HasCapturedSource(intent)))
        {
            return false;
        }

        var unproven = false;
        long? textCollation = null;
        foreach (var (name, category) in inputs.Columns)
        {
            var type = sourceType(owner.Table!, owner.Schema, name);
            if (type is null)
            {
                unproven = true;

                continue;
            }

            if (!GeneratedCaseTypeMatches(type.Value.TypeOid, category))
            {
                return false;
            }

            if (intent is EnsureColumnIntent captured && RequiresCapturedOperandBinding(captured.Definition)
                && !CapturedOperandInputMatchesAssignment(captured.Definition, type.Value.TypeOid))
            {
                return false;
            }

            if (category == GeneratedCaseCategory.Text)
            {
                if (textCollation is not null && textCollation != type.Value.CollationOid)
                {
                    return false;
                }

                textCollation = type.Value.CollationOid;
            }
        }

        if (inputs.Comparisons is not null)
        {
            foreach (var (left, right) in inputs.Comparisons)
            {
                var first = sourceType(owner.Table!, owner.Schema, left);
                var second = sourceType(owner.Table!, owner.Schema, right);
                if (first is null || second is null)
                {
                    unproven = true;

                    continue;
                }

                if (!CompatibleGeneratedCasePair(first.Value, second.Value))
                {
                    return false;
                }
            }
        }

        return unproven ? null : true;
    }

    /// <summary>Preserves prerequisite-only and full-plan agreement for statically invalid CASE contracts.</summary>
    private string? GetUnsupportedGeneratedCaseFeature(
        SafeMigrationIntent intent,
        IReadOnlyList<ExpectedColumnDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            if (definition.ComputedExpression is not { } expression
                || !ContainsGeneratedCase(expression) && !RequiresCapturedOperandBinding(definition))
            {
                continue;
            }

            var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(intent);
            var declared = intent is EnsureTableIntent table ? table.Definition.Columns : null;
            var inputs = new GeneratedCaseInputs();
            if (!ValidateGeneratedCaseOperand(expression, GeneratedCaseColumnCategory(definition),
                owner.Table!, owner.Schema, declared, inputs,
                capturedSource: definition.CapturedComputedSql is not null)
                || declared is not null && !HaveCompatibleGeneratedCaseCollations(declared, inputs))
            {
                return "generated_case_operand_type";
            }

            if (declared is not null && RequiresCapturedOperandBinding(definition)
                && CapturedOperandAssignmentType(definition) != 16
                && declared.Any(source => SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
                    expression, source.Name, StringComparer.Ordinal)
                    && (CapturedOperandAssignmentType(source) is not { } sourceType
                        || !CapturedOperandInputMatchesAssignment(definition, sourceType))))
            {
                return "generated_case_operand_type";
            }
        }

        return null;
    }

    /// <summary>Fences newly supported CASE bindings without preparing SQL or invoking application functions.</summary>
    private PostgreSqlSafeMigrationRuntimePlan QualifyGeneratedCaseBinding(
        PostgreSqlSafeMigrationRuntimePlan plan,
        SafeMigrationIntent intent)
    {
        if (intent is EnsureColumnIntent added
                && (added.Definition.ComputedExpression is not { } addedExpression
                    || !ContainsGeneratedCase(addedExpression) && !RequiresCapturedOperandBinding(added.Definition))
            || intent is AlterColumnIntent altered
                && (altered.Definition.ComputedExpression is not { } alteredExpression
                    || !ContainsGeneratedCase(alteredExpression))
            || intent is EnsureCheckConstraintIntent check
                && (check.Definition.Expression is not { } checkExpression || !ContainsGeneratedCase(checkExpression))
            || intent is not (EnsureTableIntent or EnsureColumnIntent or AlterColumnIntent
                or EnsureCheckConstraintIntent))
        {
            // WHY: Ordinary column and unrelated plans do not allocate a
            // temporary one-element definition array for this new capability.
            return plan;
        }

        List<string>? predicates = null;
        foreach (var (expression, target, captured) in GeneratedCaseBindings(intent))
        {
            var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(intent);
            var declared = intent is EnsureTableIntent table ? table.Definition.Columns : null;
            var inputs = new GeneratedCaseInputs();
            if (!ValidateGeneratedCaseOperand(expression, target, owner.Table!, owner.Schema,
                declared, inputs, captured))
            {
                return Unsupported(intent is EnsureCheckConstraintIntent ? "structured_check_operand_type"
                    : "generated_case_operand_type");
            }

            if (inputs.Columns.Count == 0)
            {
                continue;
            }

            if (declared is not null)
            {
                // WHY: A missing table has no physical attributes to query.
                // The complete authored declaration already proves this binding.
                continue;
            }

            predicates ??= [];
            foreach (var (name, category) in inputs.Columns)
            {
                // WHY: A standalone ADD cannot borrow the target model's
                // proposed source types. Capture the actual builtin OID while
                // the existing prerequisite fence proves those members exist.
                predicates.Add("EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a WHERE a.attrelid "
                    + "OPERATOR(pg_catalog.=) " + QualifiedRegclass(owner.Table!, owner.Schema)
                    + " AND a.attnum OPERATOR(pg_catalog.>) 0 AND NOT a.attisdropped "
                    + "AND a.attname OPERATOR(pg_catalog.=) " + Literal(name)
                    + " AND a.atttypid OPERATOR(pg_catalog.=) ANY(ARRAY["
                    + GeneratedCaseTypeOids(category) + "]::pg_catalog.oid[]))");

                if (intent is EnsureColumnIntent addedColumn && RequiresCapturedOperandBinding(addedColumn.Definition)
                    && CapturedOperandAssignmentType(addedColumn.Definition) is { } assignment && assignment != 16)
                {
                    // WHY: A numeric family alone does not prove a generated
                    // assignment preserves width. Reuse the same exact builtin
                    // OID for live classification and accepted sparse bindings.
                    predicates.Add("EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a WHERE a.attrelid "
                        + "OPERATOR(pg_catalog.=) " + QualifiedRegclass(owner.Table!, owner.Schema)
                        + " AND a.attname OPERATOR(pg_catalog.=) " + Literal(name)
                        + " AND a.atttypid OPERATOR(pg_catalog.=) "
                        + assignment.ToString(CultureInfo.InvariantCulture) + ")");
                }
            }

            if (inputs.Comparisons is not null)
            {
                foreach (var (left, right) in inputs.Comparisons)
                {
                    // WHY: Two unresolved identifiers have no literal which
                    // chooses an operator family. Prove the actual pair instead
                    // of independently accepting two unrelated builtin types.
                    predicates.Add("EXISTS (SELECT 1 FROM pg_catalog.pg_attribute l "
                        + "JOIN pg_catalog.pg_attribute r ON r.attrelid OPERATOR(pg_catalog.=) l.attrelid "
                        + "WHERE l.attrelid OPERATOR(pg_catalog.=) "
                        + QualifiedRegclass(owner.Table!, owner.Schema)
                        + " AND l.attnum OPERATOR(pg_catalog.>) 0 AND NOT l.attisdropped "
                        + "AND r.attnum OPERATOR(pg_catalog.>) 0 AND NOT r.attisdropped "
                        + "AND l.attname OPERATOR(pg_catalog.=) " + Literal(left)
                        + " AND r.attname OPERATOR(pg_catalog.=) " + Literal(right)
                        + " AND ((l.atttypid OPERATOR(pg_catalog.=) "
                        + "ANY(ARRAY[20,21,23,700,701,1700]::pg_catalog.oid[]) "
                        + "AND r.atttypid OPERATOR(pg_catalog.=) ANY(ARRAY[20,21,23,700,701,1700]::pg_catalog.oid[])) "
                        + "OR (l.atttypid OPERATOR(pg_catalog.=) ANY(ARRAY[25,1042,1043]::pg_catalog.oid[]) "
                        + "AND r.atttypid OPERATOR(pg_catalog.=) ANY(ARRAY[25,1042,1043]::pg_catalog.oid[]) "
                        + "AND l.attcollation OPERATOR(pg_catalog.=) r.attcollation) "
                        + "OR (l.atttypid OPERATOR(pg_catalog.=) r.atttypid "
                        + "AND l.atttypid OPERATOR(pg_catalog.=) ANY(ARRAY[16,2950]::pg_catalog.oid[]))))");
                }
            }

            var textInputs = inputs.Columns.Where(static item => item.Category == GeneratedCaseCategory.Text)
                .Select(static item => item.Name).Distinct(StringComparer.Ordinal).ToArray();

            if (textInputs.Length > 1)
            {
                // WHY: Equal TEXT OIDs do not resolve incompatible implicit
                // collations of CASE result branches used by a comparison.
                predicates.Add("(SELECT pg_catalog.count(DISTINCT a.attcollation) FROM pg_catalog.pg_attribute a "
                    + "WHERE a.attrelid OPERATOR(pg_catalog.=) " + QualifiedRegclass(owner.Table!, owner.Schema)
                    + " AND a.attnum OPERATOR(pg_catalog.>) 0 AND NOT a.attisdropped "
                    + "AND a.attname OPERATOR(pg_catalog.=) ANY(ARRAY["
                    + string.Join(", ", textInputs.Select(Literal))
                    + "]::pg_catalog.text[])) OPERATOR(pg_catalog.=) 1");
            }
        }

        if (predicates is null)
        {
            return plan;
        }

        if (intent is EnsureCheckConstraintIntent checkIntent)
        {
            // WHY: A CASE with a non-Boolean WHEN cannot even be planned as a
            // data predicate. The catalog-only preliminary phase must reject
            // its physical binding before parsing the data-bearing statement.
            return plan with
            {
                StateEvaluationGuardExpression = "(" + plan.StateEvaluationGuardExpression + ") AND ("
                    + string.Join(" AND ", predicates) + ")",
                StateEvaluationGuardFailureExpression = "CASE WHEN EXISTS (SELECT 1 "
                    + "FROM pg_catalog.pg_constraint co WHERE co.conrelid OPERATOR(pg_catalog.=) "
                    + QualifiedRegclass(checkIntent.Definition.Table, checkIntent.Definition.Schema)
                    + " AND co.conname OPERATOR(pg_catalog.=) " + Literal(checkIntent.Definition.Name)
                    + ") THEN 'different' ELSE 'unsupported' END",
                UnsupportedCode = "structured_check_operand_type",
            };
        }

        return plan with
        {
            StateExpression = "CASE WHEN NOT (" + string.Join(" AND ", predicates)
                + ") THEN 'unsupported' ELSE (" + plan.StateExpression + ") END",
            UnsupportedCode = "generated_case_operand_type",
        };
    }

    /// <summary>Includes CASE CHECKs in the same finite binder without materializing ordinary-column arrays.</summary>
    private IEnumerable<(SafeMigrationSqlExpression Expression, GeneratedCaseCategory Target, bool Captured)>
        GeneratedCaseBindings(
        SafeMigrationIntent intent)
    {
        var columns = intent switch
        {
            EnsureTableIntent table => table.Definition.Columns,
            EnsureColumnIntent column => [column.Definition],
            AlterColumnIntent column => [column.Definition],
            _ => Array.Empty<ExpectedColumnDefinition>(),
        };

        foreach (var column in columns)
        {
            if (column.ComputedExpression is { } expression
                && (ContainsGeneratedCase(expression) || RequiresCapturedOperandBinding(column)))
            {
                yield return (expression, GeneratedCaseColumnCategory(column), column.CapturedComputedSql is not null);
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
            if (check.Expression is { } expression && ContainsGeneratedCase(expression))
            {
                yield return (expression, GeneratedCaseCategory.Boolean, check.CapturedSql is not null);
            }
        }
    }

    /// <summary>Rejects known invalid CHECK CASE types in prerequisite and complete plans.</summary>
    private string? GetUnsupportedGeneratedCaseCheckFeature(SafeMigrationIntent intent)
    {
        if (intent is not (EnsureTableIntent or EnsureCheckConstraintIntent))
        {
            return null;
        }

        var checks = intent is EnsureTableIntent ownerTable ? ownerTable.Definition.CheckConstraints
            : [((EnsureCheckConstraintIntent)intent).Definition];

        foreach (var check in checks)
        {
            if (check.CapturedSql is { } source
                && !SafeMigrationSqlExpressionParser.IsCapturedSourceDialectCompatible(source, '"'))
            {
                return "opaque_sql_expression";
            }
        }

        var owner = PostgreSqlSafeMigrationSchemaIdentity.GetOwner(intent);
        var declared = intent is EnsureTableIntent table ? table.Definition.Columns : null;
        foreach (var (expression, target, captured) in GeneratedCaseBindings(intent))
        {
            var inputs = new GeneratedCaseInputs();
            if (!ValidateGeneratedCaseOperand(expression, target, owner.Table!, owner.Schema,
                declared, inputs, captured)
                || declared is not null && !HaveCompatibleGeneratedCaseCollations(declared, inputs))
            {
                return "structured_check_operand_type";
            }
        }

        return null;
    }

    /// <summary>Checks only the finite builtin CASE contract; unknown binding never becomes a certificate.</summary>
    private bool ValidateGeneratedCaseOperand(
        SafeMigrationSqlExpression expression,
        GeneratedCaseCategory expected,
        string table,
        string? schema,
        IReadOnlyList<ExpectedColumnDefinition>? declared,
        GeneratedCaseInputs inputs,
        bool capturedSource = false)
    {
        var pending = new SafeMigrationWorkStack<GeneratedCaseOperandFrame>();
        try
        {
            while (true)
            {
                if (!TryCreateGeneratedCaseOperandFrame(expression, expected, table, schema, declared,
                    inputs, capturedSource, out var frame))
                {
                    return false;
                }

                while (true)
                {
                    if (TryTakeGeneratedCaseOperand(ref frame, out var next, out var category))
                    {
                        // WHY: One cursor represents siblings regardless of their
                        // count; only the active expression depth consumes storage.
                        if (frame.CompareIdentifiers || PostgreSqlSafeMigrationExpressionWalker.TryGetChild(
                            frame.Expression, frame.NextChild, out _))
                        {
                            pending.Push(frame);
                        }

                        expression = next!;
                        expected = category;

                        break;
                    }

                    if (frame.CompareIdentifiers)
                    {
                        var pair = (SafeMigrationSqlBinaryExpression)frame.Expression;
                        var first = (SafeMigrationSqlIdentifierExpression)pair.Left;
                        var second = (SafeMigrationSqlIdentifierExpression)pair.Right;

                        (inputs.Comparisons ??= []).Add((first.Parts[^1], second.Parts[^1]));
                    }

                    if (!pending.TryPop(out frame))
                    {
                        return true;
                    }
                }
            }
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Validates a node before its ordered operands, retaining the original first-failure contract.</summary>
    private bool TryCreateGeneratedCaseOperandFrame(
        SafeMigrationSqlExpression expression,
        GeneratedCaseCategory expected,
        string table,
        string? schema,
        IReadOnlyList<ExpectedColumnDefinition>? declared,
        GeneratedCaseInputs inputs,
        bool capturedSource,
        out GeneratedCaseOperandFrame frame)
    {
        frame = new GeneratedCaseOperandFrame(expression, expected);
        if (expected == GeneratedCaseCategory.Unknown)
        {
            return false;
        }

        switch (expression)
        {
            case SafeMigrationSqlIdentifierExpression identifier:
                if (identifier.Parts.Count > 3
                    || identifier.Parts.Count >= 2 && identifier.Parts[^2] != table
                    || identifier.Parts.Count == 3 && identifier.Parts[0] != schema
                        && !(capturedSource && schema is null))
                {
                    return false;
                }

                // WHY: Only captured source has an independent native owner
                // binding fence. It proves an explicit schema against the
                // current implicit owner before this finite type proof is used.

                var name = identifier.Parts[^1];
                if (declared is not null)
                {
                    var column = declared.FirstOrDefault(item => item.Name == name);
                    if (expected == GeneratedCaseCategory.Text)
                    {
                        inputs.Columns.Add((name, expected));
                    }

                    return column is not null && GeneratedCaseCategoryMatches(
                        GeneratedCaseColumnCategory(column), expected);
                }

                inputs.Columns.Add((name, expected));

                return true;
            case SafeMigrationSqlLiteralExpression { Value: null, StoreType: null }:
                return true;
            case SafeMigrationSqlLiteralExpression literal:
                return GeneratedCaseCategoryMatches(InferGeneratedCaseCategory(literal, declared), expected);
            case SafeMigrationSqlCaseExpression:
                return true;
            case SafeMigrationSqlNullTestExpression nullTest:
                var nullOperand = InferGeneratedCaseCategory(nullTest.Operand, declared);
                frame = new GeneratedCaseOperandFrame(expression,
                    nullOperand == GeneratedCaseCategory.Unknown ? GeneratedCaseCategory.Any : nullOperand);

                // WHY: IS NULL accepts every supported scalar family. Keep
                // the live OID proof without inventing a particular source type.
                return expected == GeneratedCaseCategory.Boolean;
            case SafeMigrationSqlUnaryExpression unary:
                var unaryCategory = unary.Operator == SafeMigrationSqlUnaryOperator.Not
                    ? GeneratedCaseCategory.Boolean : InferGeneratedCaseCategory(unary.Operand, declared);
                frame = new GeneratedCaseOperandFrame(expression, unaryCategory);

                return (unary.Operator == SafeMigrationSqlUnaryOperator.Not
                        || unaryCategory is GeneratedCaseCategory.Integer or GeneratedCaseCategory.Number)
                    && GeneratedCaseCategoryMatches(unaryCategory, expected);
            case SafeMigrationSqlBinaryExpression binary:
                if (binary.Operator is SafeMigrationSqlBinaryOperator.And or SafeMigrationSqlBinaryOperator.Or)
                {
                    return expected == GeneratedCaseCategory.Boolean;
                }

                var left = InferGeneratedCaseCategory(binary.Left, declared);
                var right = InferGeneratedCaseCategory(binary.Right, declared);
                var common = left == GeneratedCaseCategory.Unknown ? right
                    : right == GeneratedCaseCategory.Unknown ? left
                    : left is GeneratedCaseCategory.Integer or GeneratedCaseCategory.Number
                        && right is GeneratedCaseCategory.Integer or GeneratedCaseCategory.Number
                        ? left == GeneratedCaseCategory.Number || right == GeneratedCaseCategory.Number
                            ? GeneratedCaseCategory.Number : GeneratedCaseCategory.Integer
                        : left == right ? left : GeneratedCaseCategory.Unknown;

                if (binary.Operator is >= SafeMigrationSqlBinaryOperator.Equal
                    and <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual)
                {
                    if ((left == GeneratedCaseCategory.Unknown || right == GeneratedCaseCategory.Unknown)
                        && common == GeneratedCaseCategory.Integer)
                    {
                        // WHY: An integer literal does not restrict a native
                        // comparison's other operand to integers; NUMERIC and
                        // floating-point operands have certified coercions too.
                        common = GeneratedCaseCategory.Number;
                    }

                    if (common == GeneratedCaseCategory.Unknown && declared is null
                        && binary.Left is SafeMigrationSqlIdentifierExpression first
                        && binary.Right is SafeMigrationSqlIdentifierExpression second)
                    {
                        frame = new GeneratedCaseOperandFrame(expression, GeneratedCaseCategory.Any,
                            compareIdentifiers: true);

                        return expected == GeneratedCaseCategory.Boolean;
                    }

                    frame = new GeneratedCaseOperandFrame(expression, common);

                    return expected == GeneratedCaseCategory.Boolean;
                }

                if (common == GeneratedCaseCategory.Unknown
                    && expected is GeneratedCaseCategory.Integer or GeneratedCaseCategory.Number)
                {
                    // WHY: An authored numeric result supplies the missing
                    // arithmetic context, but the native OID proof still checks
                    // both sources before their expression can reach DDL.
                    common = expected;
                }

                frame = new GeneratedCaseOperandFrame(expression, common);

                return common is GeneratedCaseCategory.Integer or GeneratedCaseCategory.Number
                    && (binary.Operator != SafeMigrationSqlBinaryOperator.Modulo
                        || common == GeneratedCaseCategory.Integer)
                    && GeneratedCaseCategoryMatches(common, expected);
            case SafeMigrationSqlCastExpression cast:
                var castCategory = GeneratedCaseStoreCategory(cast.StoreType);
                var source = InferGeneratedCaseCategory(cast.Operand, declared);
                frame = new GeneratedCaseOperandFrame(expression, source);

                return GeneratedCaseCategoryMatches(castCategory, expected)
                    && source is GeneratedCaseCategory.Integer or GeneratedCaseCategory.Number
                    && castCategory is GeneratedCaseCategory.Integer or GeneratedCaseCategory.Number;
            case SafeMigrationSqlFunctionExpression function
                when function.Name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase):
                return function.Arguments.Count != 0;
            case SafeMigrationSqlBetweenExpression between:
                var betweenCategory = InferGeneratedCaseCategory(between.Operand, declared);
                if (betweenCategory == GeneratedCaseCategory.Unknown)
                {
                    betweenCategory = InferGeneratedCaseCategory(between.Lower, declared);
                }

                frame = new GeneratedCaseOperandFrame(expression, betweenCategory);

                return expected == GeneratedCaseCategory.Boolean;
            case SafeMigrationSqlInExpression list:
                var listCategory = InferGeneratedCaseCategory(list.Operand, declared);
                if (listCategory == GeneratedCaseCategory.Unknown)
                {
                    foreach (var value in list.Values)
                    {
                        listCategory = InferGeneratedCaseCategory(value, declared);
                        if (listCategory != GeneratedCaseCategory.Unknown)
                        {
                            break;
                        }
                    }
                }

                frame = new GeneratedCaseOperandFrame(expression, listCategory);

                return expected == GeneratedCaseCategory.Boolean;
            default:
                return false;
        }
    }

    /// <summary>Visits CASE conditions before results and all other operands in their authored order.</summary>
    private static bool TryTakeGeneratedCaseOperand(
        ref GeneratedCaseOperandFrame frame,
        out SafeMigrationSqlExpression? operand,
        out GeneratedCaseCategory category)
    {
        category = frame.Category;
        if (!PostgreSqlSafeMigrationExpressionWalker.TryGetChild(frame.Expression, frame.NextChild, out operand))
        {
            return false;
        }

        if (frame.Expression is SafeMigrationSqlCaseExpression conditional
            && frame.NextChild < conditional.WhenClauses.Count * 2 && (frame.NextChild & 1) == 0)
        {
            category = GeneratedCaseCategory.Boolean;
        }

        frame.NextChild++;

        return true;
    }

    /// <summary>Keeps sibling position and contextual type without modifying immutable public expressions.</summary>
    private struct GeneratedCaseOperandFrame(
        SafeMigrationSqlExpression expression,
        GeneratedCaseCategory category,
        bool compareIdentifiers = false)
    {
        internal readonly SafeMigrationSqlExpression Expression = expression;
        internal readonly GeneratedCaseCategory Category = category;
        internal readonly bool CompareIdentifiers = compareIdentifiers;
        internal int NextChild;
    }

    /// <summary>Infers only intrinsic builtin families, never provider overloads or arbitrary SQL text.</summary>
    private GeneratedCaseCategory InferGeneratedCaseCategory(
        SafeMigrationSqlExpression expression,
        IReadOnlyList<ExpectedColumnDefinition>? declared)
    {
        var pending = new SafeMigrationWorkStack<GeneratedCaseCategoryFrame>();
        try
        {
            while (true)
            {
                var category = expression switch
                {
                    SafeMigrationSqlIdentifierExpression identifier when declared is not null
                        => declared.FirstOrDefault(item => item.Name == identifier.Parts[^1]) is { } column
                            ? GeneratedCaseColumnCategory(column) : GeneratedCaseCategory.Unknown,
                    SafeMigrationSqlLiteralExpression { StoreType: { } type } => GeneratedCaseStoreCategory(type),
                    SafeMigrationSqlLiteralExpression { Value: bool } => GeneratedCaseCategory.Boolean,
                    SafeMigrationSqlLiteralExpression
                        { Value: byte or sbyte or short or ushort or int or uint or long or ulong }
                        => GeneratedCaseCategory.Integer,
                    SafeMigrationSqlLiteralExpression { Value: decimal or float or double }
                        => GeneratedCaseCategory.Number,
                    SafeMigrationSqlLiteralExpression { Value: string } => GeneratedCaseCategory.Text,
                    SafeMigrationSqlLiteralExpression { Value: Guid } => GeneratedCaseCategory.Uuid,
                    SafeMigrationSqlNullTestExpression => GeneratedCaseCategory.Boolean,
                    SafeMigrationSqlUnaryExpression { Operator: SafeMigrationSqlUnaryOperator.Not }
                        => GeneratedCaseCategory.Boolean,
                    SafeMigrationSqlBinaryExpression binary when binary.Operator
                        is <= SafeMigrationSqlBinaryOperator.GreaterThanOrEqual => GeneratedCaseCategory.Boolean,
                    SafeMigrationSqlCastExpression cast => GeneratedCaseStoreCategory(cast.StoreType),
                    _ => GeneratedCaseCategory.Unknown,
                };

                if (category != GeneratedCaseCategory.Unknown)
                {
                    return category;
                }

                switch (expression)
                {
                    case SafeMigrationSqlUnaryExpression unary:
                        expression = unary.Operand;

                        continue;
                    case SafeMigrationSqlBinaryExpression binary:
                        // WHY: The old intrinsic proof preferred the right
                        // arithmetic operand and inspected the left only if unknown.
                        pending.Push(new GeneratedCaseCategoryFrame(binary.Left));
                        expression = binary.Right;

                        continue;
                    case SafeMigrationSqlCaseExpression conditional:
                        pending.Push(new GeneratedCaseCategoryFrame(conditional, 0));

                        break;
                }

                while (true)
                {
                    if (!pending.TryPop(out var frame))
                    {
                        return GeneratedCaseCategory.Unknown;
                    }

                    if (frame.NextResult < 0)
                    {
                        expression = frame.Expression;

                        break;
                    }

                    var conditional = (SafeMigrationSqlCaseExpression)frame.Expression;
                    if (frame.NextResult < conditional.WhenClauses.Count)
                    {
                        expression = conditional.WhenClauses[frame.NextResult].Result;
                        pending.Push(frame with { NextResult = frame.NextResult + 1 });

                        break;
                    }

                    if (conditional.ElseResult is { } otherwise)
                    {
                        expression = otherwise;

                        break;
                    }
                }
            }
        }
        finally
        {
            pending.Dispose();
        }
    }

    /// <summary>Retains first-known CASE result selection without allocating one frame per result branch.</summary>
    private readonly record struct GeneratedCaseCategoryFrame(
        SafeMigrationSqlExpression Expression, int NextResult = -1);

    /// <summary>Rejects unresolved competing declared collation identities without querying a missing table.</summary>
    private static bool HaveCompatibleGeneratedCaseCollations(
        IReadOnlyList<ExpectedColumnDefinition> declared,
        GeneratedCaseInputs inputs)
    {
        (string? Name, string? Schema)? first = null;
        foreach (var (name, category) in inputs.Columns)
        {
            if (category != GeneratedCaseCategory.Text)
            {
                continue;
            }

            var definition = declared.First(item => item.Name == name);
            var identity = (definition.Collation?.Name, definition.Collation?.Schema);
            if (first is not null && first != identity)
            {
                return false;
            }

            first = identity;
        }

        return true;
    }

    /// <summary>Respects inferred CLR mappings when public authoring omits an explicit store type.</summary>
    private GeneratedCaseCategory GeneratedCaseColumnCategory(ExpectedColumnDefinition definition)
        => GeneratedCaseStoreCategory(definition.StoreType
            ?? _typeMappingSource.FindMapping(definition.ClrType)?.StoreType);

    /// <summary>Maps only the native scalar families used by the newly certified CASE grammar.</summary>
    private GeneratedCaseCategory GeneratedCaseStoreCategory(string? storeType)
        => storeType is null ? GeneratedCaseCategory.Unknown
            : _typeMappingSource.FindMapping(storeType)?.StoreTypeNameBase switch
            {
                "bool" or "boolean" => GeneratedCaseCategory.Boolean,
                "int2" or "smallint" or "int4" or "integer" or "int8" or "bigint" => GeneratedCaseCategory.Integer,
                "numeric" or "decimal" or "real" or "double precision" => GeneratedCaseCategory.Number,
                "text" or "character varying" or "character" or "varchar" or "char" => GeneratedCaseCategory.Text,
                "uuid" => GeneratedCaseCategory.Uuid,
                _ => GeneratedCaseCategory.Unknown,
            };

    /// <summary>Allows integer promotion without certifying a narrowing numeric-to-integer result.</summary>
    private static bool GeneratedCaseCategoryMatches(GeneratedCaseCategory actual, GeneratedCaseCategory expected)
        => actual != GeneratedCaseCategory.Unknown && expected == GeneratedCaseCategory.Any
            || actual == expected
            || actual == GeneratedCaseCategory.Integer && expected == GeneratedCaseCategory.Number;

    /// <summary>Returns exact builtin identities; domains and extension types stay unsupported.</summary>
    private static string GeneratedCaseTypeOids(GeneratedCaseCategory category) => category switch
    {
        GeneratedCaseCategory.Boolean => "16",
        GeneratedCaseCategory.Integer => "20, 21, 23",
        GeneratedCaseCategory.Number => "20, 21, 23, 700, 701, 1700",
        GeneratedCaseCategory.Text => "25, 1042, 1043",
        GeneratedCaseCategory.Uuid => "2950",
        GeneratedCaseCategory.Any => "16, 20, 21, 23, 25, 700, 701, 1042, 1043, 1700, 2950",
        _ => throw new UnreachableException(),
    };

    /// <summary>Uses the same finite builtin identities for accepted projections and runtime guards.</summary>
    private static bool GeneratedCaseTypeMatches(long type, GeneratedCaseCategory category) => category switch
    {
        GeneratedCaseCategory.Boolean => type == 16,
        GeneratedCaseCategory.Integer => type is 20 or 21 or 23,
        GeneratedCaseCategory.Number => type is 20 or 21 or 23 or 700 or 701 or 1700,
        GeneratedCaseCategory.Text => type is 25 or 1042 or 1043,
        GeneratedCaseCategory.Uuid => type == 2950,
        GeneratedCaseCategory.Any => type is 16 or 20 or 21 or 23 or 25 or 700 or 701 or 1042 or 1043 or 1700 or 2950,
        _ => false,
    };

    /// <summary>Leaves preexisting non-CASE expression behavior outside this newly enabled capability.</summary>
    private static bool ContainsGeneratedCase(SafeMigrationSqlExpression expression)
    {
        var walker = new PostgreSqlSafeMigrationExpressionWalker(expression);
        try
        {
            while (walker.TryTake(out var node))
            {
                if (node is SafeMigrationSqlCaseExpression)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            walker.Dispose();
        }
    }

    private enum GeneratedCaseCategory { Unknown, Boolean, Integer, Number, Text, Uuid, Any }

    /// <summary>Allocates pairwise requirements only when no intrinsic operand supplies a native family.</summary>
    private sealed class GeneratedCaseInputs
    {
        internal HashSet<(string Name, GeneratedCaseCategory Category)> Columns { get; } = [];
        internal List<(string Left, string Right)>? Comparisons { get; set; }
    }

    /// <summary>Uses identical finite type and collation rules for live and accepted-prefix pairs.</summary>
    private static bool CompatibleGeneratedCasePair(
        (long TypeOid, long CollationOid) first,
        (long TypeOid, long CollationOid) second)
        => GeneratedCaseTypeMatches(first.TypeOid, GeneratedCaseCategory.Number)
                && GeneratedCaseTypeMatches(second.TypeOid, GeneratedCaseCategory.Number)
            || GeneratedCaseTypeMatches(first.TypeOid, GeneratedCaseCategory.Text)
                && GeneratedCaseTypeMatches(second.TypeOid, GeneratedCaseCategory.Text)
                && first.CollationOid == second.CollationOid
            || first.TypeOid == second.TypeOid && first.TypeOid is 16 or 2950;
}
