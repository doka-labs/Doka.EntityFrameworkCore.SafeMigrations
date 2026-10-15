namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies exact, metadata-qualified native binary-default proofs without reading table rows.</summary>
public sealed class MySqlBinaryDefaultProofTests : IDisposable
{
    private readonly ServiceProvider _services;

    /// <summary>Owns one independent provider configuration for this test case.</summary>
    public MySqlBinaryDefaultProofTests()
    {
        // WHY: Engine variants belong to independent test instances rather
        // than EF's process-wide cache of application service providers.
        _services = new ServiceCollection()
            .AddEntityFrameworkDokaMySql()
            .AddEntityFrameworkDokaMySqlSafeMigrations()
            .BuildServiceProvider();
    }

    /// <summary>Releases the test-owned provider after every context and capture lease has been disposed.</summary>
    public void Dispose() => _services.Dispose();

    /// <summary>Native DEFAULT binding requires a current base table with a binary literal default.</summary>
    [Fact]
    public void CapturedProofRequiresFreshPhysicalMetadataBeforeDefaultBinding()
    {
        // Arrange
        using var context = CreateContext();
        var operation = EnsureBinary("proof_items", "payload", "varbinary(8)", [0, 10]);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command,
            context.GetService<IRelationalTypeMappingSource>());

        // Act
        var plan = Capture(context, operation);
        var proof = plan.BinaryDefaultProofs.Single();
        var eligibility = plan.RenderBinaryDefaultEligibilityExpression(proof, parameterizer.Add);

        // Assert
        Assert.Single(plan.BinaryDefaultProofs);
        Assert.True(plan.RequiresLazyStateEvaluation);
        Assert.Contains("FROM INFORMATION_SCHEMA.COLUMNS binary_default_column", eligibility, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN INFORMATION_SCHEMA.TABLES binary_default_table", eligibility,
            StringComparison.Ordinal);
        Assert.Contains("binary_default_column.TABLE_SCHEMA = DATABASE()", eligibility, StringComparison.Ordinal);
        Assert.Contains("CAST(binary_default_column.TABLE_NAME AS BINARY)", eligibility, StringComparison.Ordinal);
        Assert.Contains("binary_default_table.TABLE_TYPE = 'BASE TABLE'", eligibility, StringComparison.Ordinal);
        Assert.Contains("LOWER(binary_default_column.DATA_TYPE) IN ", eligibility, StringComparison.Ordinal);
        Assert.Contains("'binary', 'varbinary'", eligibility, StringComparison.Ordinal);
        Assert.Contains("binary_default_column.COLUMN_DEFAULT IS NOT NULL", eligibility, StringComparison.Ordinal);
        Assert.Contains("COALESCE(binary_default_column.GENERATION_EXPRESSION, '') = ''", eligibility,
            StringComparison.Ordinal);
        Assert.Contains("LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(binary_default_column.EXTRA, ''))) = 0",
            eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFAULT(", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM `proof_items`", eligibility, StringComparison.Ordinal);
        Assert.Contains(command.Parameters.Cast<DbParameter>(), value => Equals(value.Value, "proof_items"));
        Assert.Contains(command.Parameters.Cast<DbParameter>(), value => Equals(value.Value, "payload"));
    }

    /// <summary>The native value comparison stays in WHERE EXISTS instead of a null-complemented projection.</summary>
    [Fact]
    public void NativeProbeUsesWhereExistsWithoutReadingApplicationRows()
    {
        // Arrange
        using var context = CreateContext();
        var operation = EnsureBinary("proof_items", "payload", "varbinary(8)", [0, 10, 92, 110, 255]);

        // Act
        var plan = Capture(context, operation);
        var proof = plan.BinaryDefaultProofs.Single();

        // Assert
        Assert.Equal("SELECT EXISTS (SELECT 1 FROM (SELECT 1) AS doka_sm_seed "
            + "LEFT JOIN `proof_items` AS doka_sm_default ON FALSE "
            + "WHERE CAST(HEX(DEFAULT(doka_sm_default.`payload`)) AS BINARY) = '000A5C6EFF')",
            proof.QueryExpression);
        Assert.DoesNotContain("SELECT HEX(DEFAULT(", proof.QueryExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", proof.QueryExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_sm_p", proof.QueryExpression, StringComparison.Ordinal);
        Assert.Contains(proof.Marker, plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains(proof.Marker, plan.Postcondition, StringComparison.Ordinal);
        Assert.Null(proof.Binding);
    }

    /// <summary>Only fixed BINARY columns add zero bytes to their stored default identity.</summary>
    /// <param name="storeType">The explicit binary storage contract.</param>
    /// <param name="expectedHex">The exact DEFAULT(column) identity, including fixed-width padding.</param>
    [Theory]
    [InlineData("binary(8)", "000A000000000000")]
    [InlineData("BINARY(8)", "000A000000000000")]
    [InlineData("binary(2)", "000A")]
    [InlineData("varbinary(8)", "000A")]
    [InlineData("VARBINARY(8)", "000A")]
    public void BinaryProofPreservesFixedPaddingAndVariableLengthIdentity(
        string storeType,
        string expectedHex
    )
    {
        // Arrange
        using var context = CreateContext();
        var operation = EnsureBinary("proof_items", "payload", storeType, [0, 10]);

        // Act
        var plan = Capture(context, operation);
        var query = plan.BinaryDefaultProofs.Single().QueryExpression;

        // Assert
        Assert.EndsWith("AS BINARY) = '" + expectedHex + "')", query, StringComparison.Ordinal);
        Assert.Single(plan.BinaryDefaultProofs);
    }

    /// <summary>Ordinary and root-untyped structured literals share the same exact native-value proof.</summary>
    /// <param name="structured">Whether the byte array is an untyped structured literal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootBinaryLiteralFormsRequireOneDeduplicatedProof(
        bool structured
    )
    {
        // Arrange
        using var context = CreateContext();
        var operation = EnsureBinary("proof_items", "payload", "varbinary(8)", [0, 10], structured);

        // Act
        var plan = Capture(context, operation);

        // Assert
        var proof = Assert.Single(plan.BinaryDefaultProofs);

        Assert.Equal(MySqlSafeMigrationBinaryDefaultProof.MarkerPrefix + "0__", proof.Marker);
        Assert.Contains("DEFAULT_GENERATED", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("ELSE " + proof.Marker + " END", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("'000A'", proof.QueryExpression, StringComparison.Ordinal);
    }

    /// <summary>Marker-shaped identifier contents remain quoted bytes rather than executable placeholders.</summary>
    [Fact]
    public void ProofQueryPreservesEscapedIdentifiersAndParameterMarkerContent()
    {
        // Arrange
        using var context = CreateContext();
        var valueMarker = MySqlCatalogSqlTemplate.Marker(0);
        var proofMarker = MySqlSafeMigrationBinaryDefaultProof.MarkerPrefix + "0__";
        var table = "owner`" + valueMarker + proofMarker;
        var column = "value`" + valueMarker + proofMarker;
        var operation = EnsureBinary(table, column, "varbinary(8)", [0, 255]);
        var delimiter = context.GetService<ISqlGenerationHelper>();
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command,
            context.GetService<IRelationalTypeMappingSource>());

        // Act
        var plan = Capture(context, operation);
        var proof = plan.BinaryDefaultProofs.Single();
        var queryBeforeRendering = proof.QueryExpression;
        var eligibility = plan.RenderBinaryDefaultEligibilityExpression(proof, parameterizer.Add);

        // Assert
        Assert.Contains("LEFT JOIN " + delimiter.DelimitIdentifier(table) + " AS doka_sm_default", queryBeforeRendering,
            StringComparison.Ordinal);
        Assert.Contains("DEFAULT(doka_sm_default." + delimiter.DelimitIdentifier(column) + ")", queryBeforeRendering,
            StringComparison.Ordinal);
        Assert.Contains(valueMarker, queryBeforeRendering, StringComparison.Ordinal);
        Assert.Contains(proofMarker, queryBeforeRendering, StringComparison.Ordinal);
        Assert.Equal(queryBeforeRendering, proof.QueryExpression);
        Assert.DoesNotContain(valueMarker, eligibility, StringComparison.Ordinal);
        Assert.Contains(command.Parameters.Cast<DbParameter>(), value => Equals(value.Value, table));
        Assert.Contains(command.Parameters.Cast<DbParameter>(), value => Equals(value.Value, column));
    }

    /// <summary>Unmaterialized proof tokens cannot authorize matching, repair, or successful postconditions.</summary>
    /// <param name="binding">The completed result, or null when no proof ran.</param>
    /// <param name="expected">The exact fail-closed or materialized substitution.</param>
    [Theory]
    [InlineData(null, "FALSE")]
    [InlineData("FALSE", "FALSE")]
    [InlineData("TRUE", "TRUE")]
    [InlineData("@doka_sm_binary_default_0", "@doka_sm_binary_default_0")]
    public void ProofBindingsApplyIdenticallyToStateRepairAndPostcondition(
        string? binding,
        string expected
    )
    {
        // Arrange
        const string marker = "__DOKA_SM_BINARY_DEFAULT_0__";
        var proof = new MySqlSafeMigrationBinaryDefaultProof(marker, "catalog_qualified", "SELECT 1", binding);
        var plan = new MySqlSafeMigrationRuntimePlan(marker, marker, SafeMigrationRepairCapability.Safe, marker)
        {
            BinaryDefaultProofs = [proof],
        };

        // Act
        var state = plan.RenderStateExpression(static _ => throw new InvalidOperationException());
        var repair = plan.RenderRepairPrecondition(static _ => throw new InvalidOperationException());
        var postcondition = plan.RenderPostcondition(static _ => throw new InvalidOperationException());
        var preparedState = plan.RenderPreparedStateExpression([]);
        var preparedPostcondition = plan.RenderPreparedPostcondition([]);

        // Assert
        Assert.Equal(expected, state);
        Assert.Equal(expected, repair);
        Assert.Equal(expected, postcondition);
        Assert.Equal(expected, preparedState);
        Assert.Equal(expected, preparedPostcondition);
        Assert.DoesNotContain("SELECT 1", state, StringComparison.Ordinal);
    }

    /// <summary>Only free structural tokens are substituted; quoted names and parameter payloads stay opaque.</summary>
    [Fact]
    public void BoundProofDoesNotReplaceQuotedMarkersOrParameterPayloads()
    {
        // Arrange
        const string marker = "__DOKA_SM_BINARY_DEFAULT_0__";
        var parameterMarker = MySqlCatalogSqlTemplate.Marker(0);
        var template = marker + " AND `a``" + marker + "` = 'b''" + marker + "' AND " + parameterMarker;
        var parameterSql = "'payload " + marker + "'";
        var expected = "TRUE AND `a``" + marker + "` = 'b''" + marker + "' AND " + parameterSql;
        var plan = new MySqlSafeMigrationRuntimePlan(template, template, SafeMigrationRepairCapability.None, "FALSE")
        {
            ParameterValues = [new MySqlCatalogParameterValue("payload " + marker, null)],
            BinaryDefaultProofs = [new MySqlSafeMigrationBinaryDefaultProof(marker, "TRUE", "SELECT 1", "TRUE")],
        };

        // Act
        var rendered = plan.RenderStateExpression(_ => parameterSql);
        var prepared = plan.RenderPreparedPostcondition([parameterSql]);

        // Assert
        Assert.Equal(expected, rendered);
        Assert.Equal(expected, prepared);
    }

    /// <summary>A structural token cannot borrow a different proof's result or silently become unowned SQL.</summary>
    [Fact]
    public void UnknownProofOrdinalFailsClosed()
    {
        // Arrange
        var proof = new MySqlSafeMigrationBinaryDefaultProof(
            "__DOKA_SM_BINARY_DEFAULT_0__", "TRUE", "SELECT 1", "TRUE");

        var plan = new MySqlSafeMigrationRuntimePlan("__DOKA_SM_BINARY_DEFAULT_1__", "TRUE",
            SafeMigrationRepairCapability.None, "FALSE")
        {
            BinaryDefaultProofs = [proof],
        };

        // Act
        var exception = Record.Exception(() =>
            plan.RenderStateExpression(static _ => throw new InvalidOperationException()));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Native probes are limited to MySQL byte-array literals.</summary>
    /// <param name="kind">The independent excluded default contract.</param>
    [Theory]
    [InlineData("mariadb")]
    [InlineData("guid")]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("opaque")]
    [InlineData("typed")]
    [InlineData("nested")]
    public void UnrelatedContractsDoNotAcquireBinaryDefaultProofs(
        string kind
    )
    {
        // Arrange
        using var context = CreateContext(mariaDb: kind == "mariadb");
        var definition = ExcludedDefinition(kind);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("proof_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = Capture(context, operation);

        // Assert
        Assert.Empty(plan.BinaryDefaultProofs);
    }

    /// <summary>Reusing the catalog builder cannot retain the previous operation's desired binary value.</summary>
    [Fact]
    public void CapturesRetainIndependentOperationLocalProofValues()
    {
        // Arrange
        using var context = CreateContext();
        var firstOperation = EnsureBinary("proof_items", "payload", "varbinary(8)", [0, 10]);
        var secondOperation = EnsureBinary("proof_items", "payload", "varbinary(8)", [0, 11]);

        // Act
        var first = Capture(context, firstOperation);
        var second = Capture(context, secondOperation);

        // Assert
        var firstProof = Assert.Single(first.BinaryDefaultProofs);
        var secondProof = Assert.Single(second.BinaryDefaultProofs);

        Assert.NotSame(firstProof, secondProof);
        Assert.Contains("= '000A')", firstProof.QueryExpression, StringComparison.Ordinal);
        Assert.Contains("= '000B')", secondProof.QueryExpression, StringComparison.Ordinal);
        Assert.Null(firstProof.Binding);
        Assert.Null(secondProof.Binding);
        Assert.Equal(firstProof.Marker, secondProof.Marker);
    }

    /// <summary>Native default binding revalidates physical metadata instead of borrowing a catalog snapshot.</summary>
    [Fact]
    public void BinaryProofEligibilityNeverUsesSnapshotCatalogRelations()
    {
        // Arrange
        using var context = CreateContext();
        var captured = Capture(context, EnsureBinary("proof_items", "payload", "varbinary(8)", [0, 10]));
        var relationCalls = 0;
        var plan = captured with
        {
            CatalogRelations = _ =>
            {
                relationCalls++;

                return "`stale_catalog`";
            },
        };

        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(command);

        // Act
        var eligibility = plan.RenderBinaryDefaultEligibilityExpression(plan.BinaryDefaultProofs.Single(),
            parameterizer.Add);

        // Assert
        Assert.Equal(0, relationCalls);
        Assert.Contains("INFORMATION_SCHEMA.COLUMNS", eligibility, StringComparison.Ordinal);
        Assert.Contains("INFORMATION_SCHEMA.TABLES", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("stale_catalog", eligibility, StringComparison.Ordinal);
    }

    /// <summary>Classification and postconditions refresh their binary-default evidence across native DDL.</summary>
    [Fact]
    public void RuntimeGenerationRefreshesProofBeforeAndAfterDdlAndClearsItsSessionSlot()
    {
        // Arrange
        using var context = CreateContext();
        var operation = EnsureBinary("proof_items", "payload", "varbinary(8)", [0, 10]);
        var variable = MySqlSafeMigrationBinaryDefaultProof.Variable(0);
        var reset = "SET " + variable + " = FALSE";

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var sql = commands.Single().CommandText;
        var initialReset = sql.IndexOf(reset, StringComparison.Ordinal);
        var initialEvaluation = sql.IndexOf("@doka_sm_state IS NULL THEN CONCAT('SELECT '", StringComparison.Ordinal);
        var postReset = sql.LastIndexOf(reset, StringComparison.Ordinal);
        var postEvaluation = sql.IndexOf("@doka_sm_action IN ('apply', 'repair') THEN CONCAT('SELECT '",
            StringComparison.Ordinal);

        var cleanup = sql.LastIndexOf(variable + " = NULL", StringComparison.Ordinal);

        // Assert
        Assert.Single(commands);
        Assert.True(initialReset >= 0);
        Assert.True(initialEvaluation > initialReset);
        Assert.True(postReset > initialEvaluation);
        Assert.True(postEvaluation > postReset);
        Assert.True(cleanup > postEvaluation);
        Assert.Contains("EXECUTE doka_sm_statement;\nSET " + variable + " = FALSE", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE CAST(HEX(DEFAULT(doka_sm_default.`payload`)) AS BINARY)",
            string.Join(Environment.NewLine, DecodePayloads(sql)), StringComparison.Ordinal);
    }

    /// <summary>Each command clears its proof scope before the next operation reuses the same closed slot.</summary>
    [Fact]
    public void SeparateRuntimeOperationsDoNotShareDefaultQueriesOrCompletedFacts()
    {
        // Arrange
        using var context = CreateContext();
        SafeMigrationOperation[] operations =
        [
            EnsureBinary("proof_items", "first", "varbinary(8)", [0, 10]),
            EnsureBinary("proof_items", "second", "varbinary(8)", [0, 11]),
        ];

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);
        var sql = commands.Select(static command => command.CommandText).ToArray();
        var payloads = sql.Select(static text => string.Join(Environment.NewLine, DecodePayloads(text))).ToArray();

        // Assert
        Assert.Equal(2, commands.Count);
        Assert.All(sql, static text =>
        {
            Assert.Contains("SET @doka_sm_binary_default_0 = FALSE", text, StringComparison.Ordinal);
            Assert.Contains("@doka_sm_binary_default_0 = NULL", text, StringComparison.Ordinal);
        });
        Assert.Contains("DEFAULT(doka_sm_default.`first`)", payloads[0], StringComparison.Ordinal);
        Assert.DoesNotContain("DEFAULT(doka_sm_default.`second`)", payloads[0], StringComparison.Ordinal);
        Assert.Contains("DEFAULT(doka_sm_default.`second`)", payloads[1], StringComparison.Ordinal);
        Assert.DoesNotContain("DEFAULT(doka_sm_default.`first`)", payloads[1], StringComparison.Ordinal);
    }

    /// <summary>Existing native contracts with no binary proof incur no binary-proof session-state scope.</summary>
    /// <param name="mariaDb">Whether the exclusion is MariaDB binary rather than MySQL numeric storage.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcludedRuntimeContractsDoNotCreateBinaryProofSessionState(
        bool mariaDb
    )
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var definition = ExcludedDefinition(mariaDb ? "mariadb" : "number");
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("proof_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var sql = string.Concat(commands.Select(static command => command.CommandText));

        // Assert
        Assert.NotEmpty(commands);
        Assert.DoesNotContain("@doka_sm_binary_default_", sql, StringComparison.Ordinal);
    }

    /// <summary>Eligibility selects complete SQL before PREPARE can bind any possibly absent default column.</summary>
    [Fact]
    public void PreparedProofSelectsMetadataQualifiedQueriesAndResetsItsExactBatchSlots()
    {
        // Arrange
        MySqlSafeMigrationBinaryDefaultProof[] proofs =
        [
            new("__DOKA_SM_BINARY_DEFAULT_0__", "metadata_first", "SELECT EXISTS (SELECT 1 WHERE first_default)"),
            new("__DOKA_SM_BINARY_DEFAULT_1__", "metadata_second", "SELECT EXISTS (SELECT 1 WHERE second_default)"),
        ];

        var firstPayload = Convert.ToHexString(Encoding.UTF8.GetBytes("EXISTS (SELECT 1 WHERE first_default)"));
        var secondPayload = Convert.ToHexString(Encoding.UTF8.GetBytes("EXISTS (SELECT 1 WHERE second_default)"));

        // Act
        var sql = MySqlSafeMigrationBinaryDefaultProof.BuildEvaluationSql(proofs,
            ["metadata_first", "metadata_second"], "identity_guard", "doka_sm_binary_probe", "@doka_sm_binary_sql", 3);

        // Assert
        Assert.StartsWith("SET @doka_sm_binary_default_3 = FALSE, @doka_sm_binary_default_4 = FALSE;", sql,
            StringComparison.Ordinal);
        Assert.Contains("CASE WHEN identity_guard THEN CONCAT('SELECT '", sql, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN COALESCE((metadata_first), FALSE) THEN CONVERT(0x" + firstPayload, sql,
            StringComparison.Ordinal);
        Assert.Contains("CASE WHEN COALESCE((metadata_second), FALSE) THEN CONVERT(0x" + secondPayload, sql,
            StringComparison.Ordinal);
        Assert.Contains(" ELSE 'FALSE' END", sql, StringComparison.Ordinal);
        Assert.Contains(" INTO @doka_sm_binary_default_3, @doka_sm_binary_default_4", sql, StringComparison.Ordinal);
        Assert.Contains(" ELSE 'DO 0' END;PREPARE doka_sm_binary_probe FROM @doka_sm_binary_sql;", sql,
            StringComparison.Ordinal);
        Assert.EndsWith("EXECUTE doka_sm_binary_probe;DEALLOCATE PREPARE doka_sm_binary_probe;", sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT EXISTS (SELECT 1 WHERE first_default)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_sm_binary_default_0", sql, StringComparison.Ordinal);
    }

    /// <summary>A native query cannot acquire a result slot without a separately supplied metadata barrier.</summary>
    [Fact]
    public void PreparedProofRejectsMissingMetadataBarrier()
    {
        // Arrange
        var proof = new MySqlSafeMigrationBinaryDefaultProof("__DOKA_SM_BINARY_DEFAULT_0__", "TRUE", "SELECT TRUE");

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationBinaryDefaultProof.BuildEvaluationSql(
            [proof], [], "TRUE", "doka_sm_binary_probe", "@doka_sm_binary_sql"));

        // Assert
        Assert.Equal("eligibility", Assert.IsType<ArgumentException>(exception).ParamName);
    }

    /// <summary>The closed default-proof transport cannot prepare an arbitrary mutation body.</summary>
    [Fact]
    public void PreparedProofRejectsNonSelectBody()
    {
        // Arrange
        var proof = new MySqlSafeMigrationBinaryDefaultProof("__DOKA_SM_BINARY_DEFAULT_0__", "TRUE",
            "DELETE FROM proof_items");

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationBinaryDefaultProof.BuildEvaluationSql(
            [proof], ["TRUE"], "TRUE", "doka_sm_binary_probe", "@doka_sm_binary_sql"));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Reset and cleanup touch only the two slots assigned to their own bounded batch.</summary>
    /// <param name="clear">Whether to clear completed values or initialize a new proof.</param>
    /// <param name="value">The exact assigned SQL value.</param>
    [Theory]
    [InlineData(false, "FALSE")]
    [InlineData(true, "NULL")]
    public void BinaryProofResetRetainsItsOwnedOffset(
        bool clear,
        string value
    )
    {
        // Act
        var sql = MySqlSafeMigrationBinaryDefaultProof.BuildResetSql(2, 7, clear);

        // Assert
        Assert.Equal("SET @doka_sm_binary_default_7 = " + value + ", @doka_sm_binary_default_8 = " + value + ";", sql);
    }

    /// <summary>Unrelated operations allocate no default-proof transport or cleanup text.</summary>
    [Fact]
    public void EmptyProofScopeDoesNotCreateEvaluationOrCleanupSql()
    {
        // Act
        var evaluation = MySqlSafeMigrationBinaryDefaultProof.BuildEvaluationSql([], [], "TRUE",
            "doka_sm_binary_probe", "@doka_sm_binary_sql");
        var cleanup = MySqlSafeMigrationBinaryDefaultProof.BuildResetSql(0, 7, clear: true);

        // Assert
        Assert.Same(string.Empty, evaluation);
        Assert.Same(string.Empty, cleanup);
    }

    /// <summary>Builds an immutable, optionally structured native binary-literal contract.</summary>
    private static SafeMigrationOperation EnsureBinary(
        string table,
        string column,
        string storeType,
        byte[] value,
        bool structured = false
    ) => new(new EnsureColumnIntent(table, new ExpectedColumnDefinition(column, typeof(byte[]), true, storeType,
        defaultValue: structured
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value))
            : SafeMigrationDefaultValue.Literal(value))), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Captures the real provider-generated plan without opening a connection.</summary>
    private static MySqlSafeMigrationRuntimePlan Capture(
        DbContext context,
        SafeMigrationOperation operation
    )
    {
        using var lease = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        return lease.Complete().Single();
    }

    /// <summary>Reads exact prepared payloads without executing or interpreting their SQL.</summary>
    private static IEnumerable<string> DecodePayloads(
        string sql
    )
    {
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var start = offset + prefix.Length;
            var end = sql.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("A generated binary-proof payload is unterminated.");
            }

            yield return Encoding.UTF8.GetString(Convert.FromHexString(sql.AsSpan(start, end - start)));
            offset = end + suffix.Length;
        }
    }

    /// <summary>Creates an independent excluded contract without weakening the positive fixture.</summary>
    private static ExpectedColumnDefinition ExcludedDefinition(
        string kind
    )
    {
        var value = new byte[] { 0, 10, };
        var (clrType, storeType, defaultValue) = kind switch
        {
            "guid" => (typeof(Guid), "binary(16)", SafeMigrationDefaultValue.Literal(Guid.Empty)),
            "text" => (typeof(string), "varchar(8)", SafeMigrationDefaultValue.Literal("value")),
            "number" => (typeof(int), "int", SafeMigrationDefaultValue.Literal(42)),
            "missing" => (typeof(byte[]), "varbinary(8)", SafeMigrationDefaultValue.None),
            "null" => (typeof(byte[]), "varbinary(8)", SafeMigrationDefaultValue.Literal(null)),
            "opaque" => (typeof(byte[]), "varbinary(8)", SafeMigrationDefaultValue.Sql("X'000A'")),
            "typed" => (typeof(byte[]), "varbinary(8)",
                SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value, "binary(8)"))),
            "nested" => (typeof(byte[]), "varbinary(8)", SafeMigrationDefaultValue.Sql(
                SafeMigrationSql.Function("COALESCE",
                    SafeMigrationSql.Literal(value), SafeMigrationSql.Literal(null)))),
            "mariadb" => (typeof(byte[]), "varbinary(8)", SafeMigrationDefaultValue.Literal(value)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown excluded proof contract."),
        };

        return new ExpectedColumnDefinition("payload", clrType, true, storeType, defaultValue: defaultValue);
    }

    /// <summary>Creates an offline context using only this test case's explicitly owned provider.</summary>
    private SafeMigrationDbContext CreateContext(
        bool mariaDb = false
    )
    {
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseInternalServiceProvider(_services);
        options.UseMySql("Server=127.0.0.1;Port=1;User ID=test;Database=binary_proof;Allow User Variables=true",
            mariaDb ? MySqlServerVersion.MariaDb(new Version(10, 11, 19))
                : MySqlServerVersion.MySql(new Version(8, 4, 11)));
        options.UseMySqlSafeMigrations();

        return new SafeMigrationDbContext(options.Options);
    }
}
