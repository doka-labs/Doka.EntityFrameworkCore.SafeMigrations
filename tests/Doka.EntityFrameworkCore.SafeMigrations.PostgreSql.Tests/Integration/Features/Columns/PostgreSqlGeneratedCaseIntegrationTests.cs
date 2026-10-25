namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Exercises generated-column and CHECK contracts without consumer-specific models.</summary>
public sealed class PostgreSqlGeneratedCaseIntegrationTests : PostgreSqlIntegrationTestBase
{
    private const string Expression = "\"right_bound\" > CASE WHEN \"left_bound\" = 9223372036854775807 "
        + "THEN \"left_bound\" ELSE \"left_bound\" + 1 END";

    /// <summary>Uses the assembly-owned native container with test-owned databases.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public PostgreSqlGeneratedCaseIntegrationTests(PostgreSqlContainerFixture fixture) : base(fixture) { }

    /// <summary>Preserves CASE catalog text and authored literal contents through EF's real script framing.</summary>
    /// <param name="options">The actual EF script-generation transport contract.</param>
    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Default)]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent)]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent | MigrationsSqlGenerationOptions.NoTransactions)]
    public async Task GeneratedScriptsPreserveMultilineLiteralsAndReplay(
        MigrationsSqlGenerationOptions options)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = new GeneratedScriptDbContext(connectionString);
        const string settings = "SET standard_conforming_strings = on; SET backslash_quote = off; ";

        // Act
        var script = context.GetService<IMigrator>().GenerateScript(options: options);
        await ExecuteSqlAsync(connectionString, settings + script);
        await ExecuteSqlAsync(connectionString, "INSERT INTO scripted_generated_nodes (id, left_bound, right_bound) "
            + "VALUES (1, 1, 4), (2, 1, 2);");
        if ((options & MigrationsSqlGenerationOptions.Idempotent) != 0)
        {
            await ExecuteSqlAsync(connectionString, settings + script);
        }

        var values = await ScalarStringAsync(connectionString,
            "SELECT string_agg(has_descendants::text, ',' ORDER BY id) FROM scripted_generated_nodes;");
        var payload = await ScalarStringAsync(connectionString,
            "SELECT payload FROM scripted_generated_nodes WHERE id = 1;");
        var comment = await ScalarStringAsync(connectionString,
            "SELECT obj_description('scripted_generated_nodes'::regclass);");
        var history = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM \"__GeneratedScriptHistory\";");

        // Assert
        Assert.Equal("true,false", values);
        Assert.Equal(GeneratedScriptMigration.Payload, payload);
        Assert.Equal(GeneratedScriptMigration.Payload, comment);
        Assert.Equal(1, history);
    }

    /// <summary>The anonymous-body encoder is independent of native string settings and outer dollar tags.</summary>
    /// <param name="standardStrings">Whether ordinary PostgreSQL strings process backslash escapes.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousBodyEncodingPreservesExactCodeAndOuterDelimiter(bool standardStrings)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var encoder = typeof(PostgreSqlSafeMigrationsSqlGenerator).GetMethod("RenderEscapedAnonymousBlock",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The production anonymous-body encoder was not found.");
        var body = new StringBuilder("BEGIN\nCREATE TABLE encoded_payload (value text);\n"
            + "INSERT INTO encoded_payload VALUES (E'row ''quoted'' \\\\path\\r\\n$EF$$doka_safe_migration$');\nEND\n");

        // Act
        var encoded = encoder.Invoke(null, [body]) as string
            ?? throw new InvalidOperationException("The production anonymous-body encoder returned no SQL.");
        var script = "SET standard_conforming_strings = " + (standardStrings ? "on" : "off")
            + "; SET backslash_quote = off; DO $EF$\nBEGIN\n    " + encoded + "\nEND $EF$;";

        await ExecuteSqlAsync(connectionString, script);
        var payload = await ScalarStringAsync(connectionString, "SELECT value FROM encoded_payload;");

        // Assert
        Assert.DoesNotContain('\n', encoded);
        Assert.DoesNotContain('\r', encoded);
        Assert.DoesNotContain('$', encoded);
        Assert.Equal(GeneratedScriptMigration.Payload, payload);
    }

    /// <summary>Script framing never turns a genuinely different generated expression into equality.</summary>
    /// <param name="noTransactions">Whether the idempotent script omits EF's transaction statements.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedScriptsRejectActualExpressionDrift(bool noTransactions)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = new GeneratedScriptDbContext(connectionString);
        await context.Database.MigrateAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "ALTER TABLE scripted_generated_nodes DROP COLUMN has_descendants; "
            + "ALTER TABLE scripted_generated_nodes ADD has_descendants boolean "
            + "GENERATED ALWAYS AS (right_bound > left_bound + 2) STORED NOT NULL; "
            + "DELETE FROM \"__GeneratedScriptHistory\";");
        var options = MigrationsSqlGenerationOptions.Idempotent
            | (noTransactions ? MigrationsSqlGenerationOptions.NoTransactions : 0);
        var script = context.GetService<IMigrator>().GenerateScript(options: options);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString, script));
        var history = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM \"__GeneratedScriptHistory\";");

        // Assert
        Assert.Equal("P1001", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(0, history);
    }

    /// <summary>Two source operands require compatible native families rather than a guessed literal type.</summary>
    /// <param name="leftType">The physical first operand type.</param>
    /// <param name="rightType">The physical second operand type.</param>
    /// <param name="compatible">Whether PostgreSQL provides the certified builtin comparison.</param>
    [Theory]
    [InlineData("bigint", "integer", true)]
    [InlineData("text COLLATE \"C\"", "varchar(20) COLLATE \"C\"", true)]
    [InlineData("varchar(20) COLLATE \"C\"", "varchar(20) COLLATE \"C\"", true)]
    [InlineData("char(20) COLLATE \"C\"", "varchar(20) COLLATE \"C\"", true)]
    [InlineData("char(20) COLLATE \"C\"", "text COLLATE \"C\"", true)]
    [InlineData("boolean", "boolean", true)]
    [InlineData("uuid", "uuid", true)]
    [InlineData("uuid", "bigint", false)]
    [InlineData("boolean", "integer", false)]
    [InlineData("text COLLATE \"C\"", "text COLLATE \"POSIX\"", false)]
    [MemberData(nameof(NumericSourcePairs))]
    public async Task GeneratedCaseComparesNativeSourcePairs(string leftType, string rightType, bool compatible)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE generated_pairs (left_value {leftType}, right_value {rightType});");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "generated_pairs", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN left_value < right_value THEN TRUE ELSE FALSE END", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-source-pairs"), CancellationToken.None);

        if (compatible)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var replay = compatible ? await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("generated-case-source-pairs-replay"),
            CancellationToken.None) : null;

        // Assert
        Assert.Equal(compatible ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        if (compatible)
        {
            Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay!.Assessments).Action);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
            Assert.Equal("generated_case_operand_type", Assert.Single(report.Assessments).AnalysisCode);
        }
    }

    /// <summary>Covers every ordered pair in the certified builtin numeric comparison family.</summary>
    /// <returns>The native type pairs and their compatible comparison result.</returns>
    public static IEnumerable<object[]> NumericSourcePairs()
    {
        string[] types = ["smallint", "integer", "bigint", "numeric", "real", "double precision"];
        foreach (var first in types)
        {
            foreach (var second in types)
            {
                yield return [first, second, true];
            }
        }
    }

    /// <summary>Fresh and populated paths preserve guarded extrema, generated facets and independent CHECKs.</summary>
    /// <param name="populated">Whether the generated column is added to an existing populated table.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCaseWithChecksConvergesAndReplays(bool populated)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (populated)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_nodes "
                + "(id integer NOT NULL PRIMARY KEY, left_bound bigint NOT NULL, right_bound bigint NOT NULL, "
                + "depth integer NOT NULL, position integer NOT NULL); "
                + "INSERT INTO generated_nodes VALUES (1, 1, 2, 0, 0), (2, 3, 6, 0, 1), "
                + "(3, 9223372036854775806, 9223372036854775807, 0, 2);");
        }

        await using var context = CreateContext(connectionString);
        var builder = BuildGeneratedNodes(context, populated);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-initial"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        if (!populated)
        {
            await ExecuteSqlAsync(connectionString, "INSERT INTO generated_nodes "
                + "(id, left_bound, right_bound, depth, position) VALUES "
                + "(1, 1, 2, 0, 0), (2, 3, 6, 0, 1), (3, 9223372036854775806, 9223372036854775807, 0, 2);");
        }

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply,
            assessment.Action));

        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp,
            assessment.Action));

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal("false,true,false", await ScalarStringAsync(connectionString,
            "SELECT string_agg(has_descendants::text, ',' ORDER BY id) FROM generated_nodes;"));

        Assert.Equal(4, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM pg_catalog.pg_constraint WHERE conrelid = 'generated_nodes'::regclass "
            + "AND contype = 'c';"));

        Assert.Equal("boolean:true:s", await ScalarStringAsync(connectionString,
            "SELECT pg_catalog.format_type(atttypid, atttypmod) || ':' || attnotnull::text "
            + "|| ':' || attgenerated::text "
            + "FROM pg_catalog.pg_attribute WHERE attrelid = 'generated_nodes'::regclass "
            + "AND attname = 'has_descendants';"));
    }

    /// <summary>Changing generation, type, nullability or expression must not become an idempotent success.</summary>
    /// <param name="declaration">The independently created incompatible live column declaration.</param>
    [Theory]
    [InlineData("boolean NULL")]
    [InlineData("boolean GENERATED ALWAYS AS (right_bound > left_bound + 1) STORED NULL")]
    [InlineData("boolean GENERATED ALWAYS AS (right_bound >= left_bound + 1) STORED NOT NULL")]
    [InlineData("text GENERATED ALWAYS AS (left_bound::text) STORED NOT NULL")]
    public async Task GeneratedCaseDoesNotHidePhysicalDrift(string declaration)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_nodes "
            + "(id integer NOT NULL PRIMARY KEY, left_bound bigint NOT NULL, right_bound bigint NOT NULL, "
            + $"depth integer NOT NULL, position integer NOT NULL, has_descendants {declaration}); "
            + "INSERT INTO generated_nodes (id, left_bound, right_bound, depth, position) VALUES (1, 1, 2, 0, 0);");

        await using var context = CreateContext(connectionString);
        var builder = GeneratedColumn(context);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-drift"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT count(*) FROM generated_nodes;"));
    }

    /// <summary>An omitted ELSE remains a nullable generated contract, not an implicit FALSE.</summary>
    [Fact]
    public async Task GeneratedCaseWithoutElsePreservesNullResultAndCatalogReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_nodes (id integer NOT NULL); INSERT INTO generated_nodes VALUES (0), (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "has_descendants", "generated_nodes", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN id > 0 THEN TRUE END", stored: true));

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-null-branch"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-null-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM generated_nodes WHERE has_descendants IS NULL;"));

        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM generated_nodes WHERE has_descendants IS TRUE;"));
    }

    /// <summary>The maximum operand takes the guarded branch without evaluating overflowing addition.</summary>
    [Fact]
    public async Task GeneratedCasePreservesOverflowGuardAtTheActualMaximum()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_nodes "
            + "(id integer NOT NULL, left_bound bigint NOT NULL, right_bound bigint NOT NULL); "
            + "INSERT INTO generated_nodes VALUES (1, 9223372036854775807, 9223372036854775807);");

        await using var context = CreateContext(connectionString);
        var builder = GeneratedColumn(context);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-real-maximum"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-maximum-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal("false", await ScalarStringAsync(connectionString,
            "SELECT has_descendants::text FROM generated_nodes;"));
    }

    /// <summary>Native matching preserves whitespace inside literals and rejects changed branch values.</summary>
    /// <param name="drift">Whether the expected THEN branch changes an independently stored literal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCaseStringLiteralsRetainTheirExactValues(bool drift)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_text (id integer NOT NULL, "
            + "label text GENERATED ALWAYS AS (CASE WHEN id > 0 THEN 'a  b' ELSE 'a b' END) STORED); "
            + "INSERT INTO generated_text (id) VALUES (1), (0);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var thenValue = drift ? "a b" : "a  b";
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<string>(
            "label", "generated_text", type: "text", nullable: true,
            computedColumnSql: $"CASE WHEN id > 0 THEN '{thenValue}' ELSE 'a b' END", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-literal-boundary"), CancellationToken.None);

        // Assert
        Assert.Equal(drift ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(drift ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.NoOp,
            Assert.Single(report.Assessments).Action);

        Assert.Equal("a  b", await ScalarStringAsync(connectionString,
            "SELECT label FROM generated_text WHERE id = 1;"));
    }

    /// <summary>Native branch coercion and nesting retain exact catalog identity after populated ADD.</summary>
    /// <param name="expression">The guarded branch shape, including native common-type coercions.</param>
    /// <param name="expected">The ordered values, with NULL represented explicitly.</param>
    [Theory]
    [InlineData("CASE WHEN id < 0 THEN left_bound WHEN id = 0 THEN NULL ELSE 7 END", "10,null,7,7")]
    [InlineData("CASE WHEN id > 0 THEN CASE WHEN id > 1 THEN left_bound ELSE CAST(1 AS bigint) END "
        + "ELSE CAST(2 AS bigint) END", "2,2,1,40")]
    [InlineData("CASE WHEN id > 0 THEN left_bound ELSE NULL END", "null,null,30,40")]
    [InlineData("CASE WHEN id > 0 THEN left_bound ELSE id END", "-1,0,30,40")]
    public async Task GeneratedCaseBranchShapesPreserveDataAndReplay(string expression, string expected)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_cases (id integer NOT NULL, "
            + "left_bound bigint NOT NULL); INSERT INTO generated_cases VALUES (-1, 10), (0, 20), (1, 30), (2, 40);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<long>(
            "selected_bound", "generated_cases", type: "bigint", nullable: true,
            computedColumnSql: expression, stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-branch-shapes"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-branch-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(expected, await ScalarStringAsync(connectionString,
            "SELECT string_agg(COALESCE(selected_bound::text, 'null'), ',' ORDER BY id) FROM generated_cases;"));
    }

    /// <summary>Native character coercions remain part of the canonical CASE comparison contract.</summary>
    /// <param name="sourceType">The native TEXT, VARCHAR or fixed-width character source.</param>
    [Theory]
    [InlineData("text")]
    [InlineData("varchar(20)")]
    [InlineData("character(20)")]
    public async Task GeneratedCaseCharacterConditionPreservesNativeCoercion(string sourceType)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE generated_varchar (label {sourceType}); "
            + "INSERT INTO generated_varchar VALUES ('x'), ('y');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "flag", "generated_varchar", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN label = 'x' THEN TRUE ELSE FALSE END", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-varchar-condition"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-varchar-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT count(*) FROM generated_varchar WHERE flag;"));
    }

    /// <summary>CASE cannot borrow a type-only certificate when its implicit text collations conflict.</summary>
    /// <param name="fresh">Whether all source metadata is authored in the same fresh table operation.</param>
    /// <param name="compatible">Whether the two source columns have the same physical collation.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedCaseRequiresCompatibleSourceCollations(bool fresh, bool compatible)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var rightCollation = compatible ? "C" : "POSIX";
        const string expression = "CASE WHEN id > 0 THEN left_text ELSE right_text END > 'm'";
        if (!fresh)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_collation (id integer NOT NULL, "
                + $"left_text text COLLATE \"C\", right_text text COLLATE \"{rightCollation}\");");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (fresh)
        {
            builder.CreateTableIfNotExists("generated_collation", table => new
            {
                id = table.Column<int>("integer", nullable: false),
                left_text = table.Column<string>("text", nullable: true, collation: "C"),
                right_text = table.Column<string>("text", nullable: true, collation: rightCollation),
                flag = table.Column<bool>("boolean", nullable: true, computedColumnSql: expression, stored: true),
            });
        }
        else
        {
            builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
                "flag", "generated_collation", type: "boolean", nullable: true,
                computedColumnSql: expression, stored: true));
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-collation-binding"), CancellationToken.None);

        if (compatible)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(compatible ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(compatible ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            Assert.Single(report.Assessments).Action);

        Assert.Equal(compatible ? 1 : 0, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() "
            + "AND table_name = 'generated_collation' AND column_name = 'flag';"));
    }

    /// <summary>Source replacement invalidates collation proof even when its native type OID is unchanged.</summary>
    /// <param name="compatible">Whether the replacement retains the independently live sibling's collation.</param>
    /// <param name="initialCompatible">Whether the original source collations already agree.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task GeneratedCaseRequalifiesPrefixCollationChanges(bool compatible, bool initialCompatible)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var initialCollation = initialCompatible ? "C" : "POSIX";
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_collation (id integer NOT NULL, "
            + $"left_text text COLLATE \"C\", right_text text COLLATE \"{initialCollation}\");");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("right_text", "generated_collation");
        builder.AddColumnIfNotExists<string>("right_text", "generated_collation", type: "text", nullable: true,
            collation: new SafeMigrationCollationIdentifier(compatible ? "C" : "POSIX"));

        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "flag", "generated_collation", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN id > 0 THEN left_text ELSE right_text END > 'm'", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-prefix-collation"), CancellationToken.None);

        if (compatible)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(compatible ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(compatible ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[^1].Action);

        Assert.Equal(compatible ? 1 : 0, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() "
            + "AND table_name = 'generated_collation' AND column_name = 'flag';"));
    }

    /// <summary>Unsupported subqueries and physical virtual storage remain fail-closed before mutation.</summary>
    /// <param name="expression">The SQL expression that cannot cross the supported boundary.</param>
    /// <param name="stored">Whether the unsupported operation requests stored generation.</param>
    [Theory]
    [InlineData("CASE WHEN EXISTS (SELECT 1) THEN TRUE ELSE FALSE END", true)]
    [InlineData("CASE WHEN id > 0 THEN TRUE ELSE FALSE END", false)]
    public async Task GeneratedCaseUnsupportedContractsDoNotMutate(string expression, bool stored)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_nodes (id integer NOT NULL); INSERT INTO generated_nodes VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "has_descendants", "generated_nodes", type: "boolean", nullable: true,
            computedColumnSql: expression, stored: stored));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-unsupported"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM pg_catalog.pg_attribute WHERE attrelid = 'generated_nodes'::regclass "
            + "AND attname = 'has_descendants';"));
    }

    /// <summary>Non-Boolean WHEN operands are rejected both from declared metadata and real live types.</summary>
    /// <param name="fresh">Whether the source column is declared in the same fresh table operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCaseInvalidConditionDoesNotReachNativeDdl(bool fresh)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (!fresh)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE invalid_case (id integer NOT NULL);");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (fresh)
        {
            builder.CreateTableIfNotExists("invalid_case", table => new
            {
                id = table.Column<int>("integer", nullable: false),
                flag = table.Column<bool>("boolean", nullable: true,
                    computedColumnSql: "CASE WHEN id THEN TRUE ELSE FALSE END", stored: true),
            });
        }
        else
        {
            builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
                "flag", "invalid_case", type: "boolean", nullable: true,
                computedColumnSql: "CASE WHEN id THEN TRUE ELSE FALSE END", stored: true));
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-invalid-condition"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        Assert.Equal("generated_case_operand_type", Assert.Single(report.Assessments).AnalysisCode);
        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() "
            + "AND table_name = 'invalid_case' AND column_name = 'flag';"));
    }

    /// <summary>A new or replaced source cannot borrow an incompatible earlier live CASE binding.</summary>
    /// <param name="replace">Whether the source starts live and is replaced by accepted prefix operations.</param>
    /// <param name="compatible">Whether the accepted source contract remains numeric.</param>
    /// <param name="initialCompatible">Whether the original live source can bind the generated expression.</param>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public async Task GeneratedCaseUsesAcceptedPrefixSourceTypes(bool replace, bool compatible, bool initialCompatible)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (replace)
        {
            var initialType = initialCompatible ? "bigint" : "uuid";

            await ExecuteSqlAsync(connectionString, $"CREATE TABLE prefix_case (id {initialType} NOT NULL);");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (replace)
        {
            builder.DropColumnIfExists("id", "prefix_case");
            builder.EnsureColumn("prefix_case", new ExpectedColumnDefinition("id",
                compatible ? typeof(long) : typeof(Guid), true, compatible ? "bigint" : "uuid"),
                SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureTable(new ExpectedTableDefinition("prefix_case",
                [new ExpectedColumnDefinition("id", compatible ? typeof(long) : typeof(Guid),
                    false, compatible ? "bigint" : "uuid")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "flag", "prefix_case", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN id > 0 THEN TRUE ELSE FALSE END", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-case-prefix-binding"), CancellationToken.None);

        if (compatible)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(compatible ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(compatible ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[^1].Action);

        Assert.Equal(compatible ? 1 : 0, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() "
            + "AND table_name = 'prefix_case' AND column_name = 'flag';"));
    }

    /// <summary>Builds the generated source-shaped operation rather than a hand-authored expression graph.</summary>
    private static MigrationBuilder GeneratedColumn(DbContext context)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "has_descendants", "generated_nodes", type: "boolean", nullable: false,
            computedColumnSql: Expression, stored: true));

        return builder;
    }

    /// <summary>Keeps generated columns, their index and simple CHECKs in one minimal provider stream.</summary>
    private static MigrationBuilder BuildGeneratedNodes(DbContext context, bool populated)
    {
        var builder = populated ? GeneratedColumn(context) : new MigrationBuilder(context.Database.ProviderName!);
        if (!populated)
        {
            builder.CreateTableIfNotExists("generated_nodes", table => new
            {
                id = table.Column<int>("integer", nullable: false),
                left_bound = table.Column<long>("bigint", nullable: false),
                right_bound = table.Column<long>("bigint", nullable: false),
                depth = table.Column<int>("integer", nullable: false),
                position = table.Column<int>("integer", nullable: false),
                has_descendants = table.Column<bool>("boolean", nullable: false,
                    computedColumnSql: Expression, stored: true),
            }, constraints: table => table.PrimaryKey("pk_generated_nodes", item => item.id));
        }

        builder.CreateCompositeIndexIfNotExistsFromModel("ix_generated_nodes_branch", "generated_nodes",
            ["has_descendants", "left_bound"]);

        builder.AddCheckConstraintIfNotExists("ck_generated_left", "generated_nodes", "\"left_bound\" >= 1");
        builder.AddCheckConstraintIfNotExists("ck_generated_right", "generated_nodes",
            "\"right_bound\" > \"left_bound\"");
        builder.AddCheckConstraintIfNotExists("ck_generated_depth", "generated_nodes", "\"depth\" >= 0");
        builder.AddCheckConstraintIfNotExists("ck_generated_position", "generated_nodes", "\"position\" >= 0");

        return builder;
    }

    /// <summary>Owns only the model-free migration contract for the real generated-script regression.</summary>
    public sealed class GeneratedScriptDbContext(string connectionString) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseNpgsql(connectionString,
                    options => options.MigrationsHistoryTable("__GeneratedScriptHistory"))
                .UsePostgreSqlSafeMigrations<GeneratedScriptDbContext>();
    }

    /// <summary>Prevents unrelated model discovery from changing the authored fixture's script contract.</summary>
    [DbContext(typeof(GeneratedScriptDbContext))]
    public sealed class GeneratedScriptSnapshot : ModelSnapshot
    {
        /// <inheritdoc />
        protected override void BuildModel(ModelBuilder modelBuilder)
            => ArgumentNullException.ThrowIfNull(modelBuilder);
    }

    /// <summary>Combines native CASE deparsing with multiline defaults, comments and delimiter-like data.</summary>
    [DbContext(typeof(GeneratedScriptDbContext))]
    [Migration("20261025010100_GeneratedScript")]
    public sealed class GeneratedScriptMigration : Migration
    {
        /// <summary>Retains every character that is significant to the two nested SQL literal layers.</summary>
        public const string Payload = "row 'quoted' \\path\r\n$EF$$doka_safe_migration$";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.CreateTableIfNotExists("scripted_generated_nodes", table => new
            {
                id = table.Column<int>("integer", nullable: false),
                left_bound = table.Column<long>("bigint", nullable: false),
                right_bound = table.Column<long>("bigint", nullable: false),
                has_descendants = table.Column<bool>("boolean", nullable: false,
                    computedColumnSql: Expression, stored: true),
                payload = table.Column<string>("text", nullable: false, defaultValue: Payload),
            }, constraints: table =>
            {
                table.PrimaryKey("pk_scripted_generated_nodes", item => item.id);
                table.CheckConstraint("ck_scripted_generated_bounds", "right_bound > left_bound");
            }, comment: Payload);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTableIfExists("scripted_generated_nodes");
    }
}
