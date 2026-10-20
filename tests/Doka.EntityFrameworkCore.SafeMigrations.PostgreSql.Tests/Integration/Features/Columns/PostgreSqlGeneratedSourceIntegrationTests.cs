namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Adopts generated expressions without changing their native numeric semantics.</summary>
public sealed class PostgreSqlGeneratedSourceIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the assembly-owned container with an independent database per test.</summary>
    /// <param name="fixture">The native PostgreSQL fixture.</param>
    public PostgreSqlGeneratedSourceIntegrationTests(PostgreSqlContainerFixture fixture) : base(fixture) { }

    /// <summary>Native alternatives must equal independent DDL, including every implicit coercion.</summary>
    /// <param name="declaration">The native source-column declaration.</param>
    /// <param name="source">The exact captured generated expression.</param>
    [Theory]
    [InlineData("left_bound bigint, right_bound bigint",
        "right_bound > CASE WHEN left_bound = 9223372036854775807 THEN left_bound ELSE left_bound + 1 END")]
    [InlineData("first text, second varchar(20)", "CASE WHEN first < second THEN TRUE ELSE FALSE END")]
    [InlineData("first char(20), second text", "CASE WHEN first < second THEN TRUE ELSE FALSE END")]
    [InlineData("first varchar(20)", "CASE WHEN first = 'x' THEN TRUE ELSE FALSE END")]
    public async Task CapturedNativeCandidateEqualsIndependentDeparser(string declaration, string source)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE native_candidate ({declaration}, "
            + $"result boolean GENERATED ALWAYS AS ({source}) STORED);");

        await using var context = CreateContext(connectionString);
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(source, out var expression));
        var renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var mapping = context.GetService<IRelationalTypeMappingSource>().FindMapping(typeof(string))!;
        var candidate = renderer.RenderCatalogCapturedNativeDeparsedCandidateSql(expression,
            value => mapping.GenerateSqlLiteral(value), "'native_candidate'::regclass");

        // Act
        var expected = await ScalarStringAsync(connectionString,
            "SELECT pg_catalog.pg_get_expr(adbin, adrelid) FROM pg_catalog.pg_attrdef "
            + "WHERE adrelid = 'native_candidate'::regclass;");

        var actual = await ScalarStringAsync(connectionString, "SELECT " + candidate + ";");

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Native unquoted folding cannot select a distinct quoted source column.</summary>
    /// <param name="actualQuoted">Whether the independently stored expression uses the mixed-case column.</param>
    /// <param name="expectedQuoted">Whether the requested source explicitly quotes its mixed-case identifier.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CapturedIdentifierPreservesNativeQuotedBinding(bool actualQuoted, bool expectedQuoted)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var physical = actualQuoted ? "\"Left_Bound\"" : "left_bound";
        await ExecuteSqlAsync(connectionString, "CREATE TABLE identifier_roles (left_bound bigint, "
            + $"\"Left_Bound\" bigint, result boolean GENERATED ALWAYS AS ({physical} > 0) STORED); "
            + "INSERT INTO identifier_roles (left_bound, \"Left_Bound\") VALUES (1, -1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var source = expectedQuoted ? "\"Left_Bound\" > 0" : "Left_Bound > 0";
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "identifier_roles", type: "boolean", nullable: true, computedColumnSql: source, stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-identifier-binding"), CancellationToken.None);

        // Assert
        Assert.Equal(actualQuoted == expectedQuoted ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent,
            Assert.Single(report.Assessments).Action);

        Assert.Equal(actualQuoted ? "false" : "true", await ScalarStringAsync(connectionString,
            "SELECT result::text FROM identifier_roles;"));
    }

    /// <summary>Quoted non-ASCII identities remain exact while the unquoted ASCII grammar fails closed.</summary>
    /// <param name="token">The captured source identifier token.</param>
    /// <param name="action">The independently expected native binding or grammar refusal.</param>
    [Theory]
    [InlineData("\u00C6", SafeMigrationAction.RejectUnsupported)]
    [InlineData("\u00E6", SafeMigrationAction.RejectUnsupported)]
    [InlineData("\"\u00C6\"", SafeMigrationAction.NoOp)]
    [InlineData("\"\u00E6\"", SafeMigrationAction.RejectDifferent)]
    public async Task CapturedUnicodeIdentifierKeepsExactQuotedIdentity(string token, SafeMigrationAction action)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE unicode_roles (\"\u00C6\" bigint, \"\u00E6\" bigint, "
            + "result boolean GENERATED ALWAYS AS (\"\u00C6\" > 0) STORED); "
            + "INSERT INTO unicode_roles (\"\u00C6\", \"\u00E6\") VALUES (1, -1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "unicode_roles", type: "boolean", nullable: true,
            computedColumnSql: token + " > 0", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-unicode-identifier"), CancellationToken.None);

        // Assert
        Assert.Equal(action, Assert.Single(report.Assessments).Action);
        Assert.Equal("true", await ScalarStringAsync(connectionString, "SELECT result::text FROM unicode_roles;"));
    }

    /// <summary>A reserved bare token cannot gain validity from a quoted native catalog candidate.</summary>
    /// <param name="quoted">Whether the original source quotes the reserved column name.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedReservedIdentifierRequiresNativeQuoting(bool quoted)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE keyword_roles (\"select\" integer, "
            + "result integer GENERATED ALWAYS AS (\"select\" + 1) STORED); "
            + "INSERT INTO keyword_roles (\"select\") VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "result", "keyword_roles", type: "integer", nullable: true,
            computedColumnSql: quoted ? "\"select\" + 1" : "select + 1", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-reserved-identifier"), CancellationToken.None);

        // Assert
        Assert.Equal(quoted ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectUnsupported,
            Assert.Single(report.Assessments).Action);

        Assert.Equal("2", await ScalarStringAsync(connectionString, "SELECT result::text FROM keyword_roles;"));
    }

    /// <summary>Qualified labels admit native keywords only after their complete owner has been certified.</summary>
    /// <param name="source">The original qualified source expression.</param>
    /// <param name="valid">Whether every qualifier binds the independently created owning relation.</param>
    [Theory]
    [InlineData("qualified_roles.select + 1", true)]
    [InlineData("public.qualified_roles.select + 1", true)]
    [InlineData("other_roles.select + 1", false)]
    [InlineData("other_schema.qualified_roles.select + 1", false)]
    public async Task CapturedQualifiedKeywordLabelRequiresExactOwner(string source, bool valid)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE qualified_roles (\"select\" integer, "
            + "result integer GENERATED ALWAYS AS (\"select\" + 1) STORED); "
            + "INSERT INTO qualified_roles (\"select\") VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "result", "qualified_roles", type: "integer", nullable: true,
            computedColumnSql: source, stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-qualified-keyword-label"), CancellationToken.None);

        // Assert
        Assert.Equal(valid ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectUnsupported,
            Assert.Single(report.Assessments).Action);

        Assert.Equal("2", await ScalarStringAsync(connectionString, "SELECT result::text FROM qualified_roles;"));
    }

    /// <summary>A source type alias must resolve to the native builtin before its canonical cast can match.</summary>
    /// <param name="shadowed">Whether the active path resolves INT8 to a user domain instead of BIGINT.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedCastPreservesNativeTypeAliasBinding(bool shadowed)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            (shadowed ? "CREATE DOMAIN public.int8 AS bigint CHECK (VALUE < 0); " : string.Empty)
            + "CREATE TABLE type_roles (id integer, result bigint GENERATED ALWAYS AS (CAST(1 AS bigint)) STORED); "
            + "INSERT INTO type_roles (id) VALUES (1);");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET search_path = public, pg_catalog;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<long>(
            "result", "type_roles", type: "bigint", nullable: true,
            computedColumnSql: "CAST(1 AS int8)", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-type-alias-binding"), CancellationToken.None);

        // Assert
        if (shadowed)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.True(Assert.Single(report.Assessments).Action is SafeMigrationAction.RejectDifferent
                or SafeMigrationAction.RejectUnsupported);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        }

        Assert.Equal("1", await ScalarStringAsync(connectionString, "SELECT result::text FROM type_roles;"));
    }

    /// <summary>Metadata-only proof types cannot resolve to application domains on the active path.</summary>
    /// <param name="alias">The builtin metadata alias independently shadowed by an application domain.</param>
    [Theory]
    [InlineData("oid")]
    [InlineData("regnamespace")]
    public async Task CapturedBindingProofUsesNativeMetadataTypeIdentity(string alias)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE FUNCTION public.metadata_type_probe(pg_catalog.oid) "
            + "RETURNS boolean LANGUAGE plpgsql IMMUTABLE AS $$ BEGIN RAISE EXCEPTION USING ERRCODE = 'P9878', "
            + "MESSAGE = 'metadata domain invoked'; END $$; CREATE DOMAIN public." + alias + " AS pg_catalog."
            + alias + " CHECK (public.metadata_type_probe(VALUE::pg_catalog.oid)); "
            + "CREATE TABLE metadata_type_binding (value integer);");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET search_path = public, pg_catalog;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "result", "metadata_type_binding", type: "integer", nullable: true,
            computedColumnSql: "ABS(-1)", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-native-metadata-types"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid = 'metadata_type_binding'::regclass "
            + "AND attname = 'result' AND NOT attisdropped;"));
    }

    /// <summary>Unrepresented unary-plus roles cannot alias a plain generated member or execute an overload.</summary>
    /// <param name="existing">Whether a plain-identifier generated target already exists.</param>
    /// <param name="shadowed">Whether a visible application unary-plus operator would execute a sentinel.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CapturedUnaryPlusCannotLoseItsNativeOperatorRole(bool existing, bool shadowed)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE unary_binding (value integer"
            + (existing ? ", result integer GENERATED ALWAYS AS (value) STORED" : string.Empty)
            + "); INSERT INTO unary_binding(value) VALUES(7);");

        if (shadowed)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE FUNCTION public.unary_binding_sentinel(integer) RETURNS integer LANGUAGE plpgsql "
                + "IMMUTABLE AS $$ BEGIN RAISE EXCEPTION USING ERRCODE = 'P9879', "
                + "MESSAGE = 'application unary operator invoked'; END $$; "
                + "CREATE OPERATOR public.+ (RIGHTARG=integer, FUNCTION=public.unary_binding_sentinel);");
        }

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET search_path = public, pg_catalog;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "result", "unary_binding", type: "integer", nullable: true,
            computedColumnSql: "+value", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-unary-plus-binding"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        Assert.Equal("opaque_sql_expression", report.Assessments[0].AnalysisCode);
        Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
        Assert.Equal(existing ? 1 : 0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid = 'unary_binding'::regclass "
            + "AND attname = 'result' AND NOT attisdropped;"));

        Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT value FROM unary_binding;"));
    }

    /// <summary>Uncertified quoted function roles cannot alias a differently named builtin.</summary>
    /// <param name="sameFunction">Whether native DDL used the same quoted function as the captured source.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedFunctionNameRetainsQuotedIdentity(bool sameFunction)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var originalFunction = sameFunction ? "\"LOWER\"" : "lower";
        await ExecuteSqlAsync(connectionString, "CREATE FUNCTION public.\"LOWER\"(text) RETURNS text "
            + "LANGUAGE sql IMMUTABLE AS 'SELECT upper($1)'; "
            + $"CREATE TABLE quoted_function (name text, result text GENERATED ALWAYS AS ({originalFunction}(name)) "
            + "STORED); INSERT INTO quoted_function (name) VALUES ('AbC');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<string>(
            "result", "quoted_function", type: "text", nullable: true,
            computedColumnSql: "\"LOWER\"(\"name\")", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-quoted-function-identity"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);

        Assert.Equal(sameFunction ? "ABC" : "abc", await ScalarStringAsync(connectionString,
            "SELECT result FROM quoted_function;"));
    }

    /// <summary>Capturing a function never executes an application overload while establishing its proof.</summary>
    /// <param name="computed">Whether the source belongs to a generated column rather than a CHECK.</param>
    /// <param name="constant">Whether the optimizer can fold the call without reading a source row.</param>
    /// <param name="shadowed">Whether an application function shadows the native builtin.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CapturedFunctionBindingDoesNotExecuteApplicationOverloads(
        bool computed, bool constant, bool shadowed)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE function_binding (value text); "
            + "INSERT INTO function_binding VALUES ('A'); "
            + (shadowed ? "CREATE FUNCTION public."
                + (constant ? "abs(integer) RETURNS integer" : "lower(text) RETURNS text")
                + " LANGUAGE plpgsql IMMUTABLE AS $$ BEGIN RAISE EXCEPTION USING ERRCODE = 'P9876', "
                + "MESSAGE = 'application function invoked'; END $$;"
                : string.Empty));

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET search_path = public, pg_catalog;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var source = constant ? "ABS(-1)" : "LOWER(value)";
        if (computed)
        {
            if (constant)
            {
                builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
                    "result", "function_binding", type: "integer", nullable: true,
                    computedColumnSql: source, stored: true));
            }
            else
            {
                builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<string>(
                    "result", "function_binding", type: "text", nullable: true,
                    computedColumnSql: source, stored: true));
            }
        }
        else
        {
            builder.AddCheckConstraintIfNotExists("ck_function", "function_binding", source
                + (constant ? " = 1" : " = 'a'"));
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-function-native-binding"), CancellationToken.None);

        PostgresException? rejection = null;
        if (shadowed)
        {
            rejection = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteOperationsAsync(context, builder.Operations));
        }
        else
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(shadowed ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Apply,
            Assert.Single(report.Assessments).Action);

        Assert.Equal(shadowed ? "captured_sql_function_binding" : "classified_missing",
            Assert.Single(report.Assessments).AnalysisCode);

        Assert.Equal(shadowed ? "P1002" : null, rejection?.SqlState);
    }

    /// <summary>Native generated-column immutability is required before a newly parsed capture is applicable.</summary>
    /// <param name="source">The independently nonimmutable PostgreSQL expression.</param>
    [Theory]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("NOW()")]
    public async Task CapturedGeneratedSourceRejectsNonimmutableExpressions(string source)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_clock (value integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<DateTime>(
            "result", "generated_clock", type: "timestamp with time zone", nullable: true,
            computedColumnSql: source, stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-generated-immutability"), CancellationToken.None);

        var native = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(connectionString,
            $"ALTER TABLE generated_clock ADD COLUMN result timestamptz GENERATED ALWAYS AS ({source}) STORED;"));

        // Assert
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        Assert.Equal("42P17", native.SqlState);
        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid = 'generated_clock'::regclass "
            + "AND attname = 'result' AND NOT attisdropped;"));
    }

    /// <summary>Operator lookup cannot execute an application overload during captured-source analysis.</summary>
    /// <param name="computed">Whether the captured source belongs to a generated column or a CHECK.</param>
    /// <param name="shadowed">Whether the requested operator resolves to an application implementation.</param>
    /// <param name="metadataSignature">Whether the shadow also matches the proof's TEXT metadata operands.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CapturedOperatorBindingDoesNotExecuteApplicationOverloads(
        bool computed, bool shadowed, bool metadataSignature)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var type = metadataSignature ? "text" : "integer";
        var symbol = metadataSignature ? "=" : ">";
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE operator_binding (value {type}); "
            + (metadataSignature ? "INSERT INTO operator_binding VALUES ('x'); "
                : "INSERT INTO operator_binding VALUES (1); ")
            + (shadowed ? $"CREATE FUNCTION public.application_op({type}, {type}) RETURNS boolean "
                + "LANGUAGE plpgsql IMMUTABLE AS $$ BEGIN RAISE EXCEPTION USING ERRCODE = 'P9877', "
                + $"MESSAGE = 'application operator invoked'; END $$; CREATE OPERATOR public.{symbol} "
                + $"(FUNCTION = public.application_op, LEFTARG = {type}, RIGHTARG = {type});" : string.Empty));

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET search_path = public, pg_catalog;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var predicate = metadataSignature ? "value = 'x'" : "value > 0";
        if (computed)
        {
            builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
                "result", "operator_binding", type: "boolean", nullable: true,
                computedColumnSql: "CASE WHEN " + predicate + " THEN TRUE ELSE FALSE END", stored: true));
        }
        else
        {
            builder.AddCheckConstraintIfNotExists("ck_operator", "operator_binding", predicate);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-operator-native-binding"), CancellationToken.None);

        PostgresException? rejection = null;
        if (shadowed)
        {
            rejection = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteOperationsAsync(context, builder.Operations));
        }
        else
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(shadowed ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Apply,
            Assert.Single(report.Assessments).Action);

        Assert.Equal(shadowed ? "P1002" : null, rejection?.SqlState);
    }

    /// <summary>Assignment coercions belong to the generated contract, not only to its inner expression.</summary>
    /// <param name="source">The native source before assignment to the target.</param>
    /// <param name="storeType">The target generated-column type.</param>
    /// <param name="value">The exact final stored value.</param>
    /// <param name="adopt">Whether ordinary native DDL already created the generated column.</param>
    [Theory]
    [InlineData("CASE WHEN value > 0 THEN 1 ELSE 0 END", "bigint", "1", false)]
    [InlineData("CASE WHEN value > 0 THEN 1 ELSE 0 END", "bigint", "1", true)]
    [InlineData("'00112233-4455-6677-8899-aabbccddeeff'", "uuid", "00112233-4455-6677-8899-aabbccddeeff", false)]
    [InlineData("'00112233-4455-6677-8899-aabbccddeeff'", "uuid", "00112233-4455-6677-8899-aabbccddeeff", true)]
    [InlineData("'00112233-4455-6677-8899-AABBCCDDEEFF'", "uuid", "00112233-4455-6677-8899-aabbccddeeff", false)]
    [InlineData("'00112233-4455-6677-8899-AABBCCDDEEFF'", "uuid", "00112233-4455-6677-8899-aabbccddeeff", true)]
    public async Task CapturedGeneratedAssignmentPreservesNativeCoercion(
        string source, string storeType, string value, bool adopt)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE assignment_binding (value integer"
            + (adopt ? $", result {storeType} GENERATED ALWAYS AS ({source}) STORED" : string.Empty)
            + "); INSERT INTO assignment_binding (value) VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (storeType == "uuid")
        {
            builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<Guid>(
                "result", "assignment_binding", type: storeType, nullable: true,
                computedColumnSql: source, stored: true));
        }
        else
        {
            builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<long>(
                "result", "assignment_binding", type: storeType, nullable: true,
                computedColumnSql: source, stored: true));
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-generated-assignment"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-generated-assignment-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(value, await ScalarStringAsync(connectionString, "SELECT result::text FROM assignment_binding;"));
    }

    /// <summary>A UUID certificate cannot authorize malformed input or a different physical target type.</summary>
    /// <param name="malformed">Whether malformed source replaces the physical TEXT-target mismatch.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedUuidAssignmentRejectsUncertifiedSourceAndTarget(bool malformed)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE uuid_assignment_drift (value integer"
            + (malformed ? string.Empty : ", result text GENERATED ALWAYS AS ('not-a-uuid') STORED") + ");");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<Guid>(
            "result", "uuid_assignment_drift", type: "uuid", nullable: true, stored: true,
            computedColumnSql: malformed ? "'not-a-uuid'" : "'00112233-4455-6677-8899-aabbccddeeff'"));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-uuid-assignment-negative"), CancellationToken.None);

        // Assert
        Assert.Equal(malformed ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.RejectDifferent,
            Assert.Single(report.Assessments).Action);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
    }

    /// <summary>Canonicalization cannot make foreign identifier quoting valid PostgreSQL source.</summary>
    /// <param name="identifier">The original source identifier token.</param>
    /// <param name="native">Whether the token follows PostgreSQL identifier syntax.</param>
    [Theory]
    [InlineData("`value`", false)]
    [InlineData("[value]", false)]
    [InlineData("\"value\"", true)]
    public async Task CapturedIdentifierDelimiterMustMatchNativeDialect(string identifier, bool native)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE quoted_identifier (value integer, "
            + "result boolean GENERATED ALWAYS AS (value > 0) STORED); "
            + "INSERT INTO quoted_identifier (value) VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "quoted_identifier", type: "boolean", nullable: true,
            computedColumnSql: identifier + " > 0", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-source-identifier-dialect"), CancellationToken.None);

        // Assert
        Assert.Equal(native ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectUnsupported,
            Assert.Single(report.Assessments).Action);

        Assert.Equal("true", await ScalarStringAsync(connectionString, "SELECT result::text FROM quoted_identifier;"));
    }

    /// <summary>Identifier dialect qualification never treats string literal contents as identifier tokens.</summary>
    [Fact]
    public async Task CapturedDelimiterCharactersInsideLiteralRemainValid()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE literal_delimiters (value integer); INSERT INTO literal_delimiters VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<string>(
            "result", "literal_delimiters", type: "text", nullable: true,
            computedColumnSql: "CASE WHEN value > 0 THEN '`[x]`' ELSE 'ordinary' END", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-literal-delimiter-roles"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-literal-delimiter-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal("`[x]`", await ScalarStringAsync(connectionString, "SELECT result FROM literal_delimiters;"));
    }

    /// <summary>Native folded references retain exact original capture keys through sparse CHECK promotion.</summary>
    /// <param name="sourceType">The independently observed existing source type.</param>
    /// <param name="valid">Whether the native source is compatible with the captured numeric condition.</param>
    [Theory]
    [InlineData("integer", true)]
    [InlineData("uuid", false)]
    public async Task CapturedSparseCaseCheckRetainsReferenceBoundNativeTypes(string sourceType, bool valid)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE sparse_roles (existing {sourceType});");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        // WHY: This required ADD independently proves the existing owner empty.
        // A nullable ADD would not establish the separate CHECK row precondition.
        builder.AddColumnIfNotExists<int>("added", "sparse_roles", type: "integer", nullable: false);
        builder.AddCheckConstraintIfNotExists("ck_sparse_native", "sparse_roles",
            "CASE WHEN EXISTING > 0 THEN ADDED ELSE 0 END >= 0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-sparse-native-reference-binding"), CancellationToken.None);

        if (valid)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(valid ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(valid ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[^1].Action);
    }

    /// <summary>Captured numeric tokens retain native precision, scale and signed scanner behavior.</summary>
    /// <param name="source">The original PostgreSQL numeric source.</param>
    /// <param name="storeType">The physical generated type.</param>
    /// <param name="value">The exact value expected without a CLR floating-point roundtrip.</param>
    /// <param name="adopt">Whether ordinary native DDL created the object before SafeMigrations sees it.</param>
    [Theory]
    [InlineData("1e3", "numeric", "1000", false)]
    [InlineData("1e3", "numeric", "1000", true)]
    [InlineData("1.25e2", "numeric", "125", true)]
    [InlineData("1e-3", "numeric", "0.001", true)]
    [InlineData("1.0", "numeric", "1.0", true)]
    [InlineData("0.000e0", "numeric", "0.000", true)]
    [InlineData("9007199254740993e0", "numeric", "9007199254740993", false)]
    [InlineData("9007199254740993e0", "numeric", "9007199254740993", true)]
    [InlineData("1.234567890123456789e30", "numeric", "1234567890123456789000000000000", true)]
    [InlineData("-9223372036854775808", "bigint", "-9223372036854775808", true)]
    [InlineData("-2147483648", "integer", "-2147483648", true)]
    [InlineData("-1e3", "numeric", "-1000", true)]
    [InlineData("00001", "integer", "1", true)]
    [InlineData("-(-1)", "integer", "1", true)]
    public async Task CapturedGeneratedNumericSourcePreservesNativeMeaning(
        string source, string storeType, string value, bool adopt)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE numeric_source (id integer NOT NULL"
            + (adopt ? $", result {storeType} GENERATED ALWAYS AS ({source}) STORED" : string.Empty)
            + "); INSERT INTO numeric_source (id) VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<decimal>(
            "result", "numeric_source", type: storeType, nullable: true, computedColumnSql: source, stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-original-numeric-source"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-original-numeric-source-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(adopt ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply,
            Assert.Single(report.Assessments).Action);

        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(value, await ScalarStringAsync(connectionString, "SELECT result::text FROM numeric_source;"));
    }

    /// <summary>Neighboring numeric sources cannot alias through an identical CLR double approximation.</summary>
    [Fact]
    public async Task CapturedGeneratedNumericSourceRejectsRoundedNeighbor()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE numeric_source (id integer NOT NULL, "
            + "result numeric GENERATED ALWAYS AS (9007199254740992e0) STORED); "
            + "INSERT INTO numeric_source (id) VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<decimal>(
            "result", "numeric_source", type: "numeric", nullable: true,
            computedColumnSql: "9007199254740993e0", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-numeric-source-drift"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal("9007199254740992", await ScalarStringAsync(connectionString,
            "SELECT result::text FROM numeric_source;"));
    }

    /// <summary>Native numeric common-type coercions remain visible in captured CASE predicates.</summary>
    /// <param name="sourceType">The independently created operand type.</param>
    /// <param name="condition">The source CASE condition requiring native numeric coercion.</param>
    [Theory]
    [InlineData("numeric", "1e3 > 0")]
    [InlineData("numeric", "value > 0")]
    [InlineData("bigint", "value > 1e3")]
    public async Task CapturedCasePreservesNativeNumericComparison(string sourceType, string condition)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE numeric_case (value {sourceType}); "
            + "INSERT INTO numeric_case VALUES (1001);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "numeric_case", type: "boolean", nullable: true,
            computedColumnSql: $"CASE WHEN {condition} THEN TRUE ELSE FALSE END", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-numeric-case"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-native-numeric-case-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal("true", await ScalarStringAsync(connectionString, "SELECT result::text FROM numeric_case;"));
    }

    /// <summary>Only an absent CASE CHECK may be rebound after an accepted source-type replacement.</summary>
    /// <param name="initiallyCompatible">Whether the physical source starts as INTEGER rather than UUID.</param>
    /// <param name="replaceWithInteger">Whether the accepted prefix replaces it with an INTEGER.</param>
    /// <param name="occupied">Whether an unrelated CHECK already owns the requested native name.</param>
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task CaseCheckRequalifiesOnlyAbsentAcceptedPrefixContracts(
        bool initiallyCompatible, bool replaceWithInteger, bool occupied)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var originalType = initiallyCompatible ? "integer" : "uuid";
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE case_check (id integer, value {originalType}"
            + (occupied ? ", CONSTRAINT ck_case CHECK (id > 0)" : string.Empty) + ");");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("value", "case_check");
        if (replaceWithInteger)
        {
            builder.AddColumnIfNotExists<int>("value", "case_check", type: "integer", nullable: true);
        }
        else
        {
            builder.AddColumnIfNotExists<Guid>("value", "case_check", type: "uuid", nullable: true);
        }

        builder.AddCheckConstraintIfNotExists("ck_case", "case_check",
            "CASE WHEN value > 0 THEN TRUE ELSE FALSE END");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("case-check-prefix-native-binding"), CancellationToken.None);

        if (replaceWithInteger && !occupied)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(replaceWithInteger && !occupied ? SafeMigrationReportStatus.Ready
            : SafeMigrationReportStatus.Blocked, report.Status);

        if (occupied)
        {
            // WHY: The native mismatch is independently blocking. A changed
            // source can conservatively invalidate its structural classification
            // without obtaining permission to overwrite the occupied CHECK.
            Assert.True(report.Assessments[^1].Action is SafeMigrationAction.RejectDifferent
                or SafeMigrationAction.RejectPrerequisiteMissing);
        }
        else
        {
            Assert.Equal(replaceWithInteger ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
                report.Assessments[^1].Action);
        }
    }

    /// <summary>CASE CHECK conditions must bind before data evaluation on fresh and existing owners.</summary>
    /// <param name="fresh">Whether the CHECK is part of the complete new table declaration.</param>
    /// <param name="valid">Whether the CASE condition has native Boolean type.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CaseCheckBindsBeforeAnyNativeDataQuery(bool fresh, bool valid)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        if (!fresh)
        {
            await ExecuteSqlAsync(connectionString, "CREATE TABLE case_check (value integer NOT NULL); "
                + "INSERT INTO case_check VALUES (1);");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expression = valid ? "CASE WHEN value > 0 THEN TRUE ELSE FALSE END"
            : "CASE WHEN value THEN TRUE ELSE FALSE END";

        if (fresh)
        {
            builder.CreateTableIfNotExists("case_check", table => new { value = table.Column<int>("integer") },
                constraints: table => table.CheckConstraint("ck_case", expression));
        }
        else
        {
            builder.AddCheckConstraintIfNotExists("ck_case", "case_check", expression);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("case-check-native-binding"), CancellationToken.None);

        if (valid)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(valid ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(valid ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            Assert.Single(report.Assessments).Action);

        Assert.Equal(valid ? 1 : 0, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM pg_catalog.pg_constraint WHERE conname = 'ck_case';"));
    }
}
