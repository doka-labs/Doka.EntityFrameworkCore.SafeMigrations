namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Preserves finite PostgreSQL binding contracts without consuming the process call stack.</summary>
public sealed class PostgreSqlExpressionTypeTraversalTests
{
    private const int DeepExpressionLength = 12000;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Accepted logical spines and unary chains retain their original Boolean type.</summary>
    /// <param name="shape">The operand position that carries the unbounded public tree.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    public void DeepCheckResolutionRetainsNativeBooleanType(string shape)
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);
        var source = new RecordingColumns();
        var expression = SafeMigrationSql.IsNull(SafeMigrationSql.Literal("leaf"));
        var boolean = expression;
        for (var index = 0; index < DeepExpressionLength; index++)
        {
            expression = shape switch
            {
                "left" => SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.And, boolean),
                "right" => SafeMigrationSql.Binary(boolean, SafeMigrationSqlBinaryOperator.And, expression),
                _ => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression),
            };
        }

        // Act
        var result = Resolve(analyzer, expression, source);

        // Assert
        Assert.Equal(16, result.TypeOid);
        Assert.False(result.Incompatible);
        Assert.Empty(source.Reads);
    }

    /// <summary>Deep CASE results and function arguments do not add a new expression admission limit.</summary>
    /// <param name="shape">The nested structured result form.</param>
    [Theory]
    [InlineData("case")]
    [InlineData("function")]
    public void DeepCommonTypeResolutionKeepsUnknownLiteralRules(string shape)
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);
        var source = new RecordingColumns();
        var condition = SafeMigrationSql.IsNull(SafeMigrationSql.Literal(null));
        var expression = SafeMigrationSql.Literal("value");
        for (var index = 0; index < DeepExpressionLength; index++)
        {
            expression = shape == "case"
                ? SafeMigrationSql.Case([new SafeMigrationSqlCaseWhenClause(condition, expression)],
                    SafeMigrationSql.Literal(null))
                : SafeMigrationSql.Function("COALESCE", expression, SafeMigrationSql.Literal(null));
        }

        // Act
        var result = Resolve(analyzer, expression, source);

        // Assert
        Assert.Equal(25, result.TypeOid);
        Assert.False(result.Incompatible);
    }

    /// <summary>Native CASE considers ELSE first and retains the first coercible result type.</summary>
    [Fact]
    public void CaseResolutionPreservesElseFirstTypeAndMemberOrder()
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);
        SeedNativeFacts(analyzer);
        var source = new RecordingColumns(("fallback", typeof(long), "bigint"),
            ("condition", typeof(bool), "boolean"), ("result", typeof(int), "integer"));

        var expression = SafeMigrationSql.Case(
            [new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Identifier("condition"),
                SafeMigrationSql.Identifier("result"))], SafeMigrationSql.Identifier("fallback"));

        // Act
        var result = Resolve(analyzer, expression, source);

        // Assert
        Assert.Equal(20, result.TypeOid);
        Assert.False(result.Incompatible);
        Assert.Equal(["fallback", "condition", "result"], source.Reads);
    }

    /// <summary>A failed WHEN keeps its original priority and never inspects its result or later clauses.</summary>
    /// <param name="unknown">Whether the first condition is unresolved rather than positively incompatible.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaseResolutionRetainsConditionShortCircuit(bool unknown)
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);
        SeedNativeFacts(analyzer);
        var source = new RecordingColumns(("fallback", typeof(long), "bigint"),
            ("bad_condition", typeof(long), "bigint"), ("unvisited", typeof(int), "integer"));

        var condition = unknown ? SafeMigrationSql.Opaque("unresolved") : SafeMigrationSql.Identifier("bad_condition");
        var expression = SafeMigrationSql.Case(
            [new SafeMigrationSqlCaseWhenClause(condition, SafeMigrationSql.Identifier("unvisited")),
                new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Identifier("unvisited"),
                    SafeMigrationSql.Identifier("unvisited"))], SafeMigrationSql.Identifier("fallback"));

        // Act
        var result = Resolve(analyzer, expression, source);

        // Assert
        Assert.Null(result.TypeOid);
        Assert.Equal(!unknown, result.Incompatible);
        Assert.Equal(unknown ? 1 : 2, source.Reads.Count);
        Assert.Equal("fallback", source.Reads[0]);
        if (!unknown)
        {
            Assert.Equal("bad_condition", source.Reads[1]);
        }
    }

    /// <summary>An incompatible IN value prevents access to later operands, including deep unsupported trees.</summary>
    [Fact]
    public void InResolutionPreservesFirstUnprovenComparison()
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);
        SeedNativeFacts(analyzer);
        var source = new RecordingColumns(("subject", typeof(int), "integer"),
            ("unvisited", typeof(int), "integer"));

        var expression = SafeMigrationSql.In(SafeMigrationSql.Identifier("subject"),
            [SafeMigrationSql.Opaque("unresolved"), SafeMigrationSql.Identifier("unvisited")]);

        // Act
        var result = Resolve(analyzer, expression, source);

        // Assert
        Assert.Null(result.TypeOid);
        Assert.False(result.Incompatible);
        Assert.Equal(["subject"], source.Reads);
    }

    /// <summary>Leaf, unary and zero-argument forms retain their exact prior type and refusal outcomes.</summary>
    /// <param name="shape">The native inference form independent of rendered SQL.</param>
    /// <param name="expectedOid">The resolved native OID, or null for unresolved evidence.</param>
    /// <param name="incompatible">Whether native incompatibility is positively proven.</param>
    [Theory]
    [InlineData("null", 705L, false)]
    [InlineData("string", 705L, false)]
    [InlineData("integer", 23L, false)]
    [InlineData("typed-text", 25L, false)]
    [InlineData("bad-typed-integer", null, false)]
    [InlineData("cast-integer", 23L, false)]
    [InlineData("bad-cast-integer", null, false)]
    [InlineData("missing-collation", null, true)]
    [InlineData("coalesce-empty", null, true)]
    [InlineData("nullif-empty", null, true)]
    [InlineData("function-empty", null, false)]
    [InlineData("null-test", 16L, false)]
    [InlineData("unresolved-null-test", null, false)]
    [InlineData("opaque", null, false)]
    [InlineData("provider", null, false)]
    public void ShallowNativeInferenceRetainsGoldenContracts(string shape, long? expectedOid, bool incompatible)
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);
        SeedNativeFacts(analyzer);
        var expression = shape switch
        {
            "null" => SafeMigrationSql.Literal(null),
            "string" => SafeMigrationSql.Literal("value"),
            "integer" => SafeMigrationSql.Literal(1),
            "typed-text" => SafeMigrationSql.Literal("value", "text"),
            "bad-typed-integer" => SafeMigrationSql.Literal("not-an-integer", "integer"),
            "cast-integer" => SafeMigrationSql.Cast(SafeMigrationSql.Literal("1"), "integer"),
            "bad-cast-integer" => SafeMigrationSql.Cast(SafeMigrationSql.Literal("not-an-integer"), "integer"),
            "missing-collation" => SafeMigrationSql.Collate(SafeMigrationSql.Literal("value"), "missing"),
            "coalesce-empty" => SafeMigrationSql.Function("COALESCE"),
            "nullif-empty" => SafeMigrationSql.Function("NULLIF"),
            "function-empty" => SafeMigrationSql.Function("custom"),
            "null-test" => SafeMigrationSql.IsNull(SafeMigrationSql.Literal(null)),
            "unresolved-null-test" => SafeMigrationSql.IsNull(SafeMigrationSql.Opaque("unknown")),
            "provider" => SafeMigrationSql.ProviderFragment("npgsql_postgresql", "unknown"),
            _ => SafeMigrationSql.Opaque("unknown"),
        };

        // Act
        var result = Resolve(analyzer, expression, new RecordingColumns());

        // Assert
        Assert.Equal(expectedOid, result.TypeOid);
        Assert.Equal(incompatible, result.Incompatible);
    }

    /// <summary>Deep generated conditions retain the local source certificate and contextual Boolean proof.</summary>
    /// <param name="shape">The unbounded logical nesting shape.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    public void DeepGeneratedCaseBindingKeepsPhysicalSourceProof(string shape)
    {
        // Arrange
        using var context = CreateContext();
        var catalog = CreateCatalog(context);
        var condition = SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("source"));
        var expression = SafeMigrationSql.Case(
            [new SafeMigrationSqlCaseWhenClause(condition, SafeMigrationSql.Literal(true))],
            SafeMigrationSql.Literal(false));

        for (var index = 0; index < DeepExpressionLength; index++)
        {
            expression = shape switch
            {
                "left" => SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.And, condition),
                "right" => SafeMigrationSql.Binary(condition, SafeMigrationSqlBinaryOperator.And, expression),
                _ => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression),
            };
        }

        var definition = ExpectedCheckConstraintDefinition.FromExpression("ck_deep", "nodes", expression);
        var reads = new List<string>();

        // Act
        var result = catalog.ValidateProjectedGeneratedCaseBinding(new EnsureCheckConstraintIntent(definition),
            (_, _, name) => { reads.Add(name); return (23, 0); });

        // Assert
        Assert.True(result);
        Assert.Equal(["source"], reads);
    }

    /// <summary>Deep accepted syntax still distinguishes incompatible physical types from missing evidence.</summary>
    /// <param name="sourceOid">The actual physical source type, or null for absent evidence.</param>
    /// <param name="expected">The unchanged native binding verdict.</param>
    [Theory]
    [InlineData(null, null)]
    [InlineData(2950L, false)]
    [InlineData(23L, true)]
    public void DeepGeneratedCaseBindingRetainsNegativeTypeEvidence(long? sourceOid, bool? expected)
    {
        // Arrange
        using var context = CreateContext();
        var catalog = CreateCatalog(context);
        var condition = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("source"),
            SafeMigrationSqlBinaryOperator.GreaterThan, SafeMigrationSql.Literal(0));

        var expression = SafeMigrationSql.Case(
            [new SafeMigrationSqlCaseWhenClause(condition, SafeMigrationSql.Literal(true))],
            SafeMigrationSql.Literal(false));

        for (var index = 0; index < DeepExpressionLength; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression);
        }

        var definition = ExpectedCheckConstraintDefinition.FromExpression("ck_deep", "nodes", expression);

        // Act
        var result = catalog.ValidateProjectedGeneratedCaseBinding(new EnsureCheckConstraintIntent(definition),
            (_, _, _) => sourceOid is { } oid ? (oid, 0) : null);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>A deep non-CASE tree does not accidentally opt into the generated CASE capability.</summary>
    [Fact]
    public void DeepCaseDetectionKeepsOrdinaryExpressionsOutsideNewCapability()
    {
        // Arrange
        var expression = SafeMigrationSql.Literal(true);
        for (var index = 0; index < DeepExpressionLength; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression);
        }

        var intent = new EnsureCheckConstraintIntent(ExpectedCheckConstraintDefinition.FromExpression(
            "ck_ordinary", "nodes", expression));

        // Act
        var result = PostgreSqlSafeMigrationCatalogSqlBuilder.RequiresGeneratedCaseInputCapture(intent);

        // Assert
        Assert.False(result);
    }

    /// <summary>Generated inference retains right-first arithmetic and first-known THEN result rules.</summary>
    /// <param name="shape">The intrinsic inference form.</param>
    [Theory]
    [InlineData("right")]
    [InlineData("left")]
    [InlineData("unary")]
    [InlineData("case")]
    public void DeepGeneratedIntrinsicInferencePreservesIntegerCategory(string shape)
    {
        // Arrange
        using var context = CreateContext();
        var catalog = CreateCatalog(context);
        var expression = SafeMigrationSql.Literal(1);
        for (var index = 0; index < DeepExpressionLength; index++)
        {
            expression = shape switch
            {
                "right" => SafeMigrationSql.Binary(SafeMigrationSql.Opaque("unknown"),
                    SafeMigrationSqlBinaryOperator.Add, expression),
                "left" => SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Add,
                    SafeMigrationSql.Opaque("unknown")),
                "unary" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, expression),
                _ => SafeMigrationSql.Case(
                    [new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true), expression)],
                    SafeMigrationSql.Literal(false)),
            };
        }

        // Act
        var result = InferGeneratedCategory(catalog, expression);

        // Assert
        Assert.Equal("Integer", result);
    }

    /// <summary>Intrinsic inference short-circuits a known right operand and a known first THEN result.</summary>
    [Fact]
    public void GeneratedIntrinsicInferenceRetainsShallowPrecedence()
    {
        // Arrange
        using var context = CreateContext();
        var catalog = CreateCatalog(context);
        var arithmetic = SafeMigrationSql.Binary(SafeMigrationSql.Literal(true),
            SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(1));

        var conditional = SafeMigrationSql.Case(
            [new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true), SafeMigrationSql.Literal(1))],
            SafeMigrationSql.Literal(false));

        // Act
        var results = new[]
        {
            InferGeneratedCategory(catalog, arithmetic), InferGeneratedCategory(catalog, conditional),
        };

        // Assert
        Assert.Equal(["Integer", "Integer"], results);
    }

    /// <summary>Signature collection remains preorder and preserves deep repeated occurrences.</summary>
    [Fact]
    public void CheckNodeCollectionIsStackSafeAndDoesNotDeduplicateOccurrences()
    {
        // Arrange
        var expression = SafeMigrationSql.Identifier("leaf");
        for (var index = 0; index < DeepExpressionLength; index++)
        {
            expression = SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression);
        }

        var shared = expression;
        expression = SafeMigrationSql.Case(
            [new SafeMigrationSqlCaseWhenClause(shared, SafeMigrationSql.Literal(1))], shared);

        var method = typeof(PostgreSqlSafeMigrationProviderAnalyzer).GetMethod("CheckExpressionNodes",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        // Act
        var nodes = ((IEnumerable<SafeMigrationSqlExpression>)method.Invoke(null, [expression])!).ToArray();

        // Assert
        Assert.Equal(2 * DeepExpressionLength + 4, nodes.Length);
        Assert.Same(expression, nodes[0]);
        Assert.Same(shared, nodes[1]);
        Assert.IsType<SafeMigrationSqlLiteralExpression>(nodes[DeepExpressionLength + 2]);
        Assert.Same(shared, nodes[DeepExpressionLength + 3]);
    }

    /// <summary>Provides only provider services; none of these probes opens a database connection.</summary>
    private static SafeMigrationDbContext CreateContext() => new(
        "Host=127.0.0.1;Port=1;Database=type_traversal;Username=test;Password=test", registerSafeMigrations: false);

    /// <summary>Uses the production catalog inference boundary without exposing it as public API.</summary>
    private static PostgreSqlSafeMigrationCatalogSqlBuilder CreateCatalog(DbContext context) => new(
        context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Creates the real native binding implementation without a live environment.</summary>
    private static PostgreSqlSafeMigrationProviderAnalyzer CreateAnalyzer(DbContext context) => new(
        context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Reads the private result contract while retaining the production entry point.</summary>
    private static (long? TypeOid, bool Incompatible) Resolve(
        PostgreSqlSafeMigrationProviderAnalyzer analyzer,
        SafeMigrationSqlExpression expression,
        RecordingColumns source)
    {
        var owner = ExpectedCheckConstraintDefinition.FromExpression("ck", "nodes", expression);
        var method = typeof(PostgreSqlSafeMigrationProviderAnalyzer).GetMethod(
            "ResolveCheckExpression", PrivateInstance)!;
        var result = method.Invoke(analyzer, [expression, owner, source])!;
        var type = result.GetType();

        return ((long?)type.GetProperty("TypeOid")!.GetValue(result),
            (bool)type.GetProperty("Incompatible")!.GetValue(result)!);
    }

    /// <summary>Checks intrinsic inference independently of SQL rendering or expression parser limits.</summary>
    private static string InferGeneratedCategory(
        PostgreSqlSafeMigrationCatalogSqlBuilder catalog,
        SafeMigrationSqlExpression expression)
        => catalog.GetType().GetMethod("InferGeneratedCaseCategory", PrivateInstance)!
            .Invoke(catalog, [expression, null])!.ToString()!;

    /// <summary>Publishes only builtin OIDs and the native integer-widening edge used by these tests.</summary>
    private static void SeedNativeFacts(PostgreSqlSafeMigrationProviderAnalyzer analyzer)
    {
        var analyzerType = analyzer.GetType();
        var typesField = analyzerType.GetField("_livePrerequisiteTypes", PrivateInstance)!;
        var dictionary = (IDictionary)Activator.CreateInstance(typesField.FieldType)!;
        var arguments = typesField.FieldType.GetGenericArguments();
        foreach (var (name, oid) in new[] { ("integer", 23L), ("bigint", 20L), ("boolean", 16L), ("text", 25L) })
        {
            dictionary.Add(Activator.CreateInstance(arguments[0], [name, null, null])!,
                Activator.CreateInstance(arguments[1], [oid, 0L, true]));
        }

        typesField.SetValue(analyzer, dictionary);
        var factsField = analyzerType.GetField("_sparseCheckTypeFacts", PrivateInstance)!;
        var constructor = factsField.FieldType.GetConstructors().Single();
        var parameters = constructor.GetParameters();
        var checks = Activator.CreateInstance(parameters[0].ParameterType)!;
        var signatures = Activator.CreateInstance(parameters[1].ParameterType)!;
        var casts = Activator.CreateInstance(parameters[2].ParameterType)!;
        var edgeType = parameters[2].ParameterType.GetGenericArguments()[0];
        parameters[2].ParameterType.GetMethod("Add")!.Invoke(casts,
            [Activator.CreateInstance(edgeType, [23L, 20L])]);

        factsField.SetValue(analyzer, constructor.Invoke([checks, signatures, casts]));
    }

    /// <summary>Records actual member lookup order and never invents a missing source definition.</summary>
    private sealed class RecordingColumns(params (string Name, Type ClrType, string StoreType)[] columns)
        : ISafeMigrationProjectedColumnSource
    {
        internal List<string> Reads { get; } = [];

        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            Reads.Add(column);
            var found = Array.FindIndex(columns, item => item.Name == column);
            definition = found < 0 ? null : new ExpectedColumnDefinition(column,
                columns[found].ClrType, true, columns[found].StoreType);

            return definition is not null;
        }
    }
}
