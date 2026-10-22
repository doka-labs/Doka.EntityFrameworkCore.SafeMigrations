namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Compares probe-only construction with the complete native plan, not a second SQL implementation.</summary>
public sealed class PostgreSqlDataProbeConstructionTests
{
    private static readonly string[] s_caseIds =
    [
        "ensure-varchar", "ensure-required", "ensure-inferred", "ensure-qualified", "ensure-comment",
        "ensure-literal-default", "ensure-structured-default", "ensure-opaque-default", "ensure-collation",
        "ensure-none-annotation", "ensure-unknown-annotation", "ensure-operation-annotation",
        "ensure-integer", "ensure-text", "ensure-unmapped", "ensure-rowversion", "ensure-fixed",
        "alter-narrow", "alter-widen", "alter-unbounded", "alter-same-type", "alter-no-source",
        "alter-type-change", "alter-collation-change", "alter-comment", "alter-nullability",
        "alter-unknown-annotation", "alter-old-annotation", "alter-operation-annotation",
        "generated", "generated-case", "generated-old", "captured", "captured-old",
        "opaque-generated", "foreign-captured", "unrelated-drop",
    ];

    /// <summary>Uses only serializable case IDs, defined policies and evidence modes in xUnit discovery.</summary>
    public static IEnumerable<object[]> ProbeCases
    {
        get
        {
            foreach (var caseId in s_caseIds)
            {
                foreach (var policy in Enum.GetValues<SafeMigrationPolicy>())
                {
                    yield return [caseId, policy, false];
                    yield return [caseId, policy, true];
                }
            }
        }
    }

    /// <summary>Preserves all eight record fields, including absence and exact native transition SQL.</summary>
    /// <param name="caseId">The independent operation capability or old/target-contract axis.</param>
    /// <param name="policy">The original operation policy.</param>
    /// <param name="includeTransitionEvidence">Whether physical eligibility SQL belongs in the probe.</param>
    [Theory]
    [MemberData(nameof(ProbeCases))]
    public void ProbeOnlyConstruction_MatchesCompletePlan(
        string caseId,
        SafeMigrationPolicy policy,
        bool includeTransitionEvidence)
    {
        // Arrange
        using var context = OfflineContext();
        var builder = Builder(context);
        var operation = Operation(caseId, policy);
        var expected = builder.Build(operation, includeTransitionEvidence: includeTransitionEvidence).DataProbe;

        // Act
        var actual = builder.BuildDataProbe(operation, includeTransitionEvidence);

        // Assert
        Assert.Equal(expected, actual);
        if (caseId is "ensure-varchar" or "alter-narrow")
        {
            // WHY: Exact null equality alone would allow both paths to lose all
            // positive probe construction. These anchors keep the oracle non-vacuous.
            Assert.Equal(policy == SafeMigrationPolicy.RepairIfSafe, actual is not null);
        }
    }

    /// <summary>Ordinary discovery skips unrelated matching/comment SQL when obtaining a probe.</summary>
    /// <param name="caseId">The EnsureColumn or AlterColumn producer.</param>
    [Theory]
    [InlineData("ensure-comment")]
    [InlineData("alter-comment")]
    public void OrdinaryProbeConstruction_DoesNotRenderDiscardedClassifierFacets(string caseId)
    {
        // Arrange
        using var context = OfflineContext();
        var fullLiterals = new List<string>();
        var probeLiterals = new List<string>();
        var operation = Operation(caseId, SafeMigrationPolicy.RepairIfSafe);
        var expected = Builder(context, fullLiterals).Build(operation, includeTransitionEvidence: true).DataProbe;

        // Act
        var actual = Builder(context, probeLiterals).BuildDataProbe(operation, includeTransitionEvidence: true);

        // Assert
        Assert.NotNull(expected);
        Assert.Equal(expected, actual);
        Assert.Contains("discarded-classifier-facet", fullLiterals);
        Assert.DoesNotContain("discarded-classifier-facet", probeLiterals);
    }

    /// <summary>Captured/generated contracts retain the full binder's exact literal sequence.</summary>
    /// <param name="caseId">The source-bearing or generated operation boundary.</param>
    [Theory]
    [InlineData("generated")]
    [InlineData("generated-case")]
    [InlineData("generated-old")]
    [InlineData("captured")]
    [InlineData("captured-old")]
    [InlineData("opaque-generated")]
    [InlineData("foreign-captured")]
    public void SourceBearingProbeConstruction_RetainsCompleteBindingPath(string caseId)
    {
        // Arrange
        using var context = OfflineContext();
        var fullLiterals = new List<string>();
        var probeLiterals = new List<string>();
        var operation = Operation(caseId, SafeMigrationPolicy.RepairIfSafe);
        var expected = Builder(context, fullLiterals).Build(operation, includeTransitionEvidence: true).DataProbe;

        // Act
        var actual = Builder(context, probeLiterals).BuildDataProbe(operation, includeTransitionEvidence: true);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(fullLiterals, probeLiterals);
    }

    /// <summary>A missing operation retains the complete builder's argument rejection.</summary>
    [Fact]
    public void ProbeOnlyConstruction_RejectsNullOperation()
    {
        // Arrange
        using var context = OfflineContext();
        var builder = Builder(context);

        // Act
        var exception = Record.Exception(() => builder.BuildDataProbe(null!));

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>Constructs only provider services, never a server connection.</summary>
    private static SafeMigrationDbContext OfflineContext() =>
        new("Host=127.0.0.1;Database=unused;Username=unused", registerSafeMigrations: false);

    /// <summary>Optionally observes existing literal calls without replacing native mapping semantics.</summary>
    private static PostgreSqlSafeMigrationCatalogSqlBuilder Builder(DbContext context, List<string>? literals = null)
    {
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var mapping = mappings.FindMapping(typeof(string))!;

        return new PostgreSqlSafeMigrationCatalogSqlBuilder(mappings, context.GetService<ISqlGenerationHelper>(),
            literals is null ? null : value =>
            {
                literals.Add(value);

                return mapping.GenerateSqlLiteral(value);
            });
    }

    /// <summary>Retains explicit positive, negative and source-binding producer axes behind serializable IDs.</summary>
    private static SafeMigrationOperation Operation(string caseId, SafeMigrationPolicy policy)
    {
        var target = Definition(40);
        var source = (ExpectedColumnDefinition?)Definition(80);
        var schema = (string?)null;
        var table = "probe_rows";
        var alter = caseId.StartsWith("alter-", StringComparison.Ordinal);

        switch (caseId)
        {
            case "ensure-varchar":
            case "alter-narrow":
                break;
            case "ensure-required":
                target = Definition(40, nullable: false);
                break;
            case "ensure-inferred":
                target = new ExpectedColumnDefinition("value", typeof(string), true, maxLength: 40);
                break;
            case "ensure-qualified":
                table = "probe's_table";
                schema = "Case Schema";
                target = new ExpectedColumnDefinition("Value\"Column", typeof(string), true, "character varying(40)");
                break;
            case "ensure-comment":
            case "alter-comment":
                target = Definition(40, comment: "discarded-classifier-facet");
                break;
            case "ensure-literal-default":
                target = Definition(40, defaultValue: SafeMigrationDefaultValue.Literal("seed"));
                break;
            case "ensure-structured-default":
                target = Definition(40, defaultValue: SafeMigrationDefaultValue.Sql(
                    SafeMigrationSql.Function("lower", SafeMigrationSql.Literal("Seed"))));
                break;
            case "ensure-opaque-default":
                target = Definition(40, defaultValue: SafeMigrationDefaultValue.Sql("application_default()"));
                break;
            case "ensure-collation":
            case "alter-collation-change":
                target = new ExpectedColumnDefinition("value", typeof(string), true, "character varying(40)",
                    collation: new SafeMigrationCollationIdentifier("C"));
                break;
            case "ensure-none-annotation":
                target = AnnotatedDefinition("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.None);
                break;
            case "ensure-unknown-annotation":
            case "alter-unknown-annotation":
                target = AnnotatedDefinition("Probe:Unsupported", true);
                break;
            case "alter-old-annotation":
                source = AnnotatedDefinition("Probe:Unsupported", true, 80);
                break;
            case "ensure-operation-annotation":
            case "alter-operation-annotation":
                break;
            case "ensure-integer":
            case "alter-type-change":
                target = new ExpectedColumnDefinition("value", typeof(int), true, "integer");
                break;
            case "ensure-text":
                target = new ExpectedColumnDefinition("value", typeof(string), true, "text");
                break;
            case "ensure-unmapped":
                target = new ExpectedColumnDefinition("value", typeof(UnmappedProbeValue), true);
                break;
            case "ensure-rowversion":
                target = new ExpectedColumnDefinition("value", typeof(string), true, "character varying(40)",
                    isRowVersion: true);
                break;
            case "ensure-fixed":
                target = new ExpectedColumnDefinition("value", typeof(string), true, "character(40)",
                    isFixedLength: true);
                break;
            case "alter-widen":
                source = Definition(20);
                break;
            case "alter-unbounded":
                source = new ExpectedColumnDefinition("value", typeof(string), true, "character varying");
                break;
            case "alter-same-type":
                source = Definition(40);
                break;
            case "alter-no-source":
                source = null;
                break;
            case "alter-nullability":
                target = Definition(40, nullable: false);
                break;
            case "generated":
                target = GeneratedDefinition(SafeMigrationSql.Identifier("source"));
                break;
            case "generated-case":
                target = GeneratedDefinition(SafeMigrationSql.Case(
                    [new(SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("source")),
                        SafeMigrationSql.Literal("known"))], SafeMigrationSql.Literal("fallback")));
                break;
            case "generated-old":
                source = GeneratedDefinition(SafeMigrationSql.Identifier("source"), 80);
                alter = true;
                break;
            case "captured":
                target = CapturedDefinition("LOWER(source)");
                break;
            case "captured-old":
                source = CapturedDefinition("LOWER(source)", 80);
                alter = true;
                break;
            case "opaque-generated":
                target = new ExpectedColumnDefinition("value", typeof(string), true, "character varying(40)",
                    computedColumnSql: "source +", isStored: true);
                break;
            case "foreign-captured":
                target = CapturedDefinition("`source`");
                break;
            case "unrelated-drop":
                return new SafeMigrationOperation(new DropColumnIntent("value", table), policy);
            default:
                throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown data-probe case.");
        }

        var intent = alter ? (SafeMigrationIntent)new AlterColumnIntent(table, target, source, schema)
            : new EnsureColumnIntent(table, target, schema);
        var operation = new SafeMigrationOperation(intent, policy);
        if (caseId.EndsWith("operation-annotation", StringComparison.Ordinal))
        {
            operation.AddAnnotation("Probe:Unsupported", true);
        }

        return operation;
    }

    /// <summary>Creates a plain target/source while keeping old and new physical column identity equal.</summary>
    private static ExpectedColumnDefinition Definition(
        int length,
        bool nullable = true,
        string? comment = null,
        SafeMigrationDefaultValue? defaultValue = null) =>
        new("value", typeof(string), nullable, $"character varying({length})", comment: comment,
            defaultValue: defaultValue);

    /// <summary>Uses the real immutable annotation capture rather than synthesizing provider metadata.</summary>
    private static ExpectedColumnDefinition AnnotatedDefinition(string name, object value, int length = 40)
    {
        var operation = new AddColumnOperation();
        operation.AddAnnotation(name, value);

        return new ExpectedColumnDefinition("value", typeof(string), true, $"character varying({length})")
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(operation),
        };
    }

    /// <summary>Keeps generated assignment and source-binding ownership on the original complete plan.</summary>
    private static ExpectedColumnDefinition GeneratedDefinition(
        SafeMigrationSqlExpression expression,
        int length = 40) =>
        new("value", typeof(string), true, $"character varying({length})", isStored: true,
            computedExpression: expression);

    /// <summary>Captures the native source together with its finite parsed role tree.</summary>
    private static ExpectedColumnDefinition CapturedDefinition(string source, int length = 40)
    {
        if (!SafeMigrationSqlExpressionParser.TryParseCapturedSource(source, out var expression,
            foldUnquotedIdentifiers: true))
        {
            throw new InvalidOperationException("The source-bearing probe fixture must parse.");
        }

        return new ExpectedColumnDefinition("value", typeof(string), true, $"character varying({length})",
            isStored: true, computedExpression: expression)
        {
            CapturedComputedSql = source,
        };
    }

    /// <summary>Supplies a real unmapped CLR type for the native capability negative case.</summary>
    private sealed class UnmappedProbeValue;
}
