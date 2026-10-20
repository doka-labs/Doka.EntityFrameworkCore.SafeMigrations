namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies the two atomic required-column stages and their single native provider scope.</summary>
public sealed class MySqlRequiredColumnAddGenerationTests
{
    private const string PrepareSql = "PREPARE doka_sm_statement FROM @doka_sm_sql;";
    private const string ExecuteSql = "EXECUTE doka_sm_statement;";
    private const string DeallocateSql = "DEALLOCATE PREPARE doka_sm_statement;";
    private const string ProviderSetup = "DO 11; /* provider setup */";
    private const string ProviderCleanup = "DO 12; /* provider cleanup */";

    /// <summary>Atomic proofs cannot survive unrecognized stateful setup paired with inert native cleanup.</summary>
    /// <param name="isMariaDb">Whether native generation uses MariaDB CHECK grammar.</param>
    /// <param name="setupSql">The independent setup that invalidates a captured strict or CHECK fact.</param>
    [Theory]
    [InlineData(false, "SET SESSION sql_mode = '';")]
    [InlineData(true, "SET SESSION sql_mode = '';")]
    [InlineData(false, "SET SESSION check_constraint_checks = OFF;")]
    [InlineData(true, "SET SESSION check_constraint_checks = OFF;")]
    [InlineData(false, "DO 0; /*M! SET SESSION check_constraint_checks = OFF */")]
    [InlineData(true, "DO 0; /*M! SET SESSION check_constraint_checks = OFF */")]
    public void RequiredAddRejectsStatefulSetupWithInertCleanup(bool isMariaDb, string setupSql)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var handler = CreateHandler(context);
        var operation = RequiredOperation(SafeMigrationPolicy.ThrowIfDifferent);
        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb, standard =>
        [
            MySqlMigrationCommandSpec.CreateScoped([setupSql], standard is AlterColumnOperation
                    ? "ALTER TABLE `items` MODIFY COLUMN `value` int NOT NULL"
                    : "ALTER TABLE `items` ADD `value` int NULL",
                ["DO 0;"], transactionSuppressed: true),
        ]);

        // Act
        var exception = Record.Exception(() => handler.Generate(operationContext));

        // Assert
        var invalidScope = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("cleanup acquisition", invalidScope.Message, StringComparison.Ordinal);
    }

    /// <summary>Inert native setup and cleanup retain their exact boundaries around both atomic stages.</summary>
    /// <param name="isMariaDb">Whether native generation uses MariaDB CHECK grammar.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredAddAcceptsNonMutatingSetupAndCleanup(bool isMariaDb)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var handler = CreateHandler(context);
        const string inertSetup = "DO 11; /* immutable native setup */";
        const string inertCleanup = "DO 0; /* immutable native cleanup */";
        var operation = RequiredOperation(SafeMigrationPolicy.ThrowIfDifferent);
        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb, standard =>
        [
            MySqlMigrationCommandSpec.CreateScoped([inertSetup], standard is AlterColumnOperation
                    ? "ALTER TABLE `items` MODIFY COLUMN `value` int NOT NULL"
                    : "ALTER TABLE `items` ADD `value` int NULL",
                [inertCleanup], transactionSuppressed: true),
        ]);

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var command = Assert.Single(result.Commands);
        var body = Assert.Single(Fragments(command, MySqlMigrationCommandFragmentKind.Body));
        var cleanup = Fragments(command, MySqlMigrationCommandFragmentKind.Cleanup);

        Assert.Single(Fragments(command, MySqlMigrationCommandFragmentKind.Setup), sql => sql == inertSetup);
        Assert.Equal(2, Count(body, PrepareSql));
        Assert.Equal(3, cleanup.Length);
        Assert.Contains(inertCleanup, cleanup);
        Assert.DoesNotContain("@doka_sm_provider_setup_entered", string.Concat(cleanup), StringComparison.Ordinal);
    }

    /// <summary>The public provider rejects setup-without-cleanup before any handler can receive that shape.</summary>
    [Fact]
    public void PublicNativeScopeRejectsEmptyCleanup()
    {
        // Arrange
        const string opaqueSetup = "SET SESSION sql_mode = '';";

        // Act
        var exception = Record.Exception(() => MySqlMigrationCommandSpec.CreateScoped([opaqueSetup], "DO 7;", []));

        // Assert
        var invalidCollection = Assert.IsType<ArgumentException>(exception);

        Assert.Equal("cleanupCommands", invalidCollection.ParamName);
        Assert.Contains("non-empty SQL command collections", invalidCollection.Message, StringComparison.Ordinal);
    }

    /// <summary>Both native stages preserve authored metadata without a default, backfill or marker mutation.</summary>
    /// <param name="isMariaDb">Whether native generation uses MariaDB's CHECK removal grammar.</param>
    /// <param name="guidColumn">Whether the target includes supported GUID-format provider metadata.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RequiredAddRetainsImmutableNativeMetadataAndAtomicBodies(bool isMariaDb, bool guidColumn)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var authored = new AddColumnOperation
        {
            Name = "value",
            Table = "items",
            ClrType = guidColumn ? typeof(Guid) : typeof(int),
            ColumnType = guidColumn ? "char(36)" : "int",
            MaxLength = guidColumn ? 36 : null,
            IsFixedLength = guidColumn ? true : null,
            IsNullable = false,
            Comment = "keep; 'quoted'\\path",
        };

        authored["Doka:MySql:RequiresExplicitBackfill"] = true;
        if (guidColumn)
        {
            authored["Doka:MySql:GuidFormat"] = DokaMySqlGuidFormat.Char36;
            authored["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.ClientGuid;
        }

        var definition = SafeMigrationExpectedDefinitionFactory.From(authored);
        var intent = new EnsureColumnIntent("items", definition);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var first = Assert.Single(generator.Generate([operation], context.Model));
        var second = Assert.Single(generator.Generate([operation], context.Model));
        var alterations = Payloads(first.CommandText).Where(static sql => sql.StartsWith("ALTER TABLE ",
            StringComparison.Ordinal)).ToArray();

        // Assert
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(intent);

        Assert.Equal(2, alterations.Length);
        Assert.Contains(", ADD CONSTRAINT `" + guard + "` CHECK (`value` IS NOT NULL)", alterations[0],
            StringComparison.Ordinal);
        Assert.DoesNotContain(" NOT NULL", alterations[0][..alterations[0].IndexOf(", ADD CONSTRAINT",
            StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains(" MODIFY COLUMN `value` ", alterations[1], StringComparison.Ordinal);
        Assert.EndsWith((isMariaDb ? ", DROP CONSTRAINT `" : ", DROP CHECK `") + guard + "`", alterations[1],
            StringComparison.Ordinal);
        Assert.All(alterations, sql =>
        {
            Assert.Contains(" COMMENT ", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(" DEFAULT ", sql, StringComparison.Ordinal);
            Assert.Equal(1, Count(sql, "ALTER TABLE "));
        });

        Assert.DoesNotContain(Payloads(first.CommandText), static sql => sql.StartsWith("UPDATE ",
            StringComparison.Ordinal));
        Assert.DoesNotContain("LOCK TABLES", first.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation(first.CommandText),
            MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation(second.CommandText));
        Assert.False(authored.IsNullable);
        Assert.False(definition.IsNullable);
        Assert.Equal("keep; 'quoted'\\path", authored.Comment);
        Assert.True(Assert.IsType<bool>(authored["Doka:MySql:RequiresExplicitBackfill"]));
        Assert.True(Assert.IsType<bool>(Assert.Single(definition.ProviderAnnotations,
            static annotation => annotation.Name == "Doka:MySql:RequiresExplicitBackfill").CreateValueSnapshot()));
        Assert.Equal(SafeMigrationDefaultValueKind.None, definition.DefaultValue.Kind);
    }

    /// <summary>Atomic stage composition shares opaque native setup and cleanup exactly once.</summary>
    /// <param name="isMariaDb">Whether the native provider uses MariaDB CHECK removal grammar.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredAddUsesOneSharedProviderScope(bool isMariaDb)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var handler = CreateHandler(context);
        var operation = RequiredOperation(SafeMigrationPolicy.ThrowIfDifferent);
        var rendered = new List<MigrationOperation>();
        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb, standard =>
        {
            rendered.Add(standard);

            return NativeScopedBody(standard);
        });

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var command = Assert.Single(result.Commands);
        var setup = Fragments(command, MySqlMigrationCommandFragmentKind.Setup);
        var body = Assert.Single(Fragments(command, MySqlMigrationCommandFragmentKind.Body));
        var cleanup = Fragments(command, MySqlMigrationCommandFragmentKind.Cleanup);
        var add = Assert.IsType<AddColumnOperation>(rendered[0]);
        var modify = Assert.IsType<AlterColumnOperation>(rendered[1]);

        Assert.Equal(2, rendered.Count);
        Assert.True(add.IsNullable);
        Assert.False(modify.IsNullable);
        Assert.False(modify.OldColumn.IsNullable);
        Assert.Single(setup, static sql => sql == ProviderSetup);
        Assert.Single(cleanup, static sql => sql == ProviderCleanup);
        Assert.Equal(2, Count(body, PrepareSql));
        Assert.Equal(2, Count(body, ExecuteSql));
        Assert.Equal(2, Count(body, DeallocateSql));
        Assert.True(body.LastIndexOf(DeallocateSql, StringComparison.Ordinal)
            < body.IndexOf("SET @doka_sm_post_ok", StringComparison.Ordinal));
        Assert.Contains("@doka_sm_required_add_resume = NULL", string.Concat(cleanup), StringComparison.Ordinal);
        Assert.DoesNotContain("DROP CHECK", string.Concat(cleanup), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP CONSTRAINT", string.Concat(cleanup), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fresh ADD and exact staged resume apply under every ordinary conflict policy.</summary>
    /// <param name="policy">The authored difference policy, which does not restrict proven Missing work.</param>
    [Theory]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(SafeMigrationPolicy.ExistenceOnly)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe)]
    public void RequiredAddApplyResumeAndMatchingRespectEveryPolicy(SafeMigrationPolicy policy)
    {
        // Arrange
        using var context = CreateContext(isMariaDb: false);
        var handler = CreateHandler(context);
        var operation = RequiredOperation(policy);
        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb: false,
            NativeScopedBody);

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var command = Assert.Single(result.Commands);
        var setup = string.Concat(Fragments(command, MySqlMigrationCommandFragmentKind.Setup));
        var body = Assert.Single(Fragments(command, MySqlMigrationCommandFragmentKind.Body));

        Assert.Contains("WHEN 'missing' THEN 'apply'", setup, StringComparison.Ordinal);
        Assert.Contains("WHEN 'matching' THEN 'no_op'", setup, StringComparison.Ordinal);
        Assert.Contains("SET @doka_sm_required_add_resume = CASE WHEN @doka_sm_action = 'apply' THEN COALESCE((",
            setup, StringComparison.Ordinal);
        Assert.StartsWith("SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' "
            + "AND NOT @doka_sm_required_add_resume THEN CONVERT(0x", body, StringComparison.Ordinal);
        Assert.Contains("ELSE 'DO 0' END;", body, StringComparison.Ordinal);
        Assert.Contains("SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' THEN CONVERT(0x", body,
            StringComparison.Ordinal);
        Assert.Contains("THEN COALESCE((", body, StringComparison.Ordinal);
    }

    /// <summary>Direct body composition preserves the complete original atomic transport byte for byte.</summary>
    /// <param name="isMariaDb">Whether finish DDL uses MariaDB's constraint-removal grammar.</param>
    /// <param name="repairRequested">Whether the second stage also admits the unchanged ordinary repair body.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RequiredAddDirectBodyCompositionPreservesExactOriginalTransport(bool isMariaDb, bool repairRequested)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var handler = CreateHandler(context);
        var operation = RequiredOperation(repairRequested
            ? SafeMigrationPolicy.RepairIfSafe
            : SafeMigrationPolicy.ThrowIfDifferent);

        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb, NativeScopedBody);
        var builder = new MySqlSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var plan = builder.Build(operation, operationContext, parameterizeValues: false);
        var guard = plan.RequiredColumnAddPlan?.GuardName
            ?? throw new InvalidOperationException("The expected atomic fixture must have an owned guard.");

        var stageOne = "ALTER TABLE `items` ADD `value` int NULL, ADD CONSTRAINT `" + guard
            + "` CHECK (`value` IS NOT NULL)";
        var plainModify = "ALTER TABLE `items` MODIFY COLUMN `value` int NOT NULL";
        var stageTwo = plainModify + (isMariaDb ? ", DROP CONSTRAINT `" : ", DROP CHECK `") + guard + "`";
        var controls = PrepareSql + ExecuteSql + DeallocateSql + Environment.NewLine;
        var repairBranch = repairRequested
            ? " USING utf8mb4) WHEN @doka_sm_action = 'repair' THEN CONVERT(0x"
                + Convert.ToHexString(Encoding.UTF8.GetBytes(plainModify))
            : string.Empty;

        var expected = "SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' "
            + "AND NOT @doka_sm_required_add_resume THEN CONVERT(0x"
            + Convert.ToHexString(Encoding.UTF8.GetBytes(stageOne))
            + " USING utf8mb4) ELSE 'DO 0' END;" + Environment.NewLine + controls
            + "SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' THEN CONVERT(0x"
            + Convert.ToHexString(Encoding.UTF8.GetBytes(stageTwo)) + repairBranch
            + " USING utf8mb4) ELSE 'DO 0' END;" + Environment.NewLine + controls
            + "SET @doka_sm_post_ok = CASE WHEN @doka_sm_action IN ('apply', 'repair') THEN COALESCE(("
            + plan.RenderPreparedExecutionPostcondition([])
            + "), FALSE) ELSE TRUE END;\nINSERT INTO `__doka_sm_assert` "
            + "SELECT 4, 5, 6, 7, @doka_sm_diag WHERE NOT COALESCE(@doka_sm_post_ok, FALSE);\n";

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var body = Assert.Single(Fragments(Assert.Single(result.Commands), MySqlMigrationCommandFragmentKind.Body));

        Assert.Equal(expected, body);
    }

    /// <summary>Catalog status is materialized once after identity and reused by all protocol runtime guards.</summary>
    /// <param name="isMariaDb">Whether the catalog status belongs to MariaDB's enforced CHECK contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredAddMaterializesStatusOnceAfterIdentityBeforeRuntimeGuards(bool isMariaDb)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items",
                new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "int"),
                schema: "scope"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var sql = command.CommandText;

        // Assert
        const string statusPrefix = "SET @doka_sm_add_status = CASE WHEN @doka_sm_state IS NULL THEN (";
        const string complete = "SET @doka_sm_state = CASE WHEN @doka_sm_add_status = 'complete' THEN 'matching'";
        const string resumePrefix = "SET @doka_sm_required_add_resume = CASE WHEN @doka_sm_action = 'apply'";
        var statusOffset = sql.IndexOf(statusPrefix, StringComparison.Ordinal);
        var completeOffset = sql.IndexOf(complete, StringComparison.Ordinal);
        var resumeOffset = sql.IndexOf(resumePrefix, StringComparison.Ordinal);
        var resumeEnd = sql.IndexOf(';', resumeOffset);
        var postconditionOffset = sql.LastIndexOf("SET @doka_sm_post_ok", StringComparison.Ordinal);
        var assertionOffset = sql.LastIndexOf("SELECT 4, 5, 6, 7, @doka_sm_diag", StringComparison.Ordinal);
        var state = Assert.Single(Payloads(sql), static payload => payload.StartsWith("SELECT (",
            StringComparison.Ordinal) && payload.Contains("INTO @doka_sm_state", StringComparison.Ordinal));

        Assert.Equal(1, Count(sql, statusPrefix));
        Assert.Equal(1, Count(sql, complete));
        Assert.True(sql.IndexOf("ELSE 'unsupported' END;", StringComparison.Ordinal) < statusOffset);
        Assert.True(sql.IndexOf("WHEN NOT @doka_sm_prerequisite_ok", StringComparison.Ordinal) < statusOffset);
        Assert.True(completeOffset > statusOffset);
        Assert.True(sql.IndexOf("SET @doka_sm_sql = CASE WHEN @doka_sm_state IS NULL THEN CONVERT(0x",
            StringComparison.Ordinal) > completeOffset);
        Assert.Contains("@doka_sm_add_status", state, StringComparison.Ordinal);
        Assert.DoesNotContain("INFORMATION_SCHEMA.CHECK_CONSTRAINTS", state, StringComparison.Ordinal);
        Assert.DoesNotContain("SET @doka_sm_state = CASE WHEN @doka_sm_state IS NULL THEN CASE WHEN COALESCE((",
            sql, StringComparison.Ordinal);
        Assert.True(resumeOffset > completeOffset);
        Assert.True(resumeEnd > resumeOffset);
        Assert.Contains("'staged'", sql[resumeOffset..resumeEnd], StringComparison.Ordinal);
        Assert.DoesNotContain("INFORMATION_SCHEMA", sql[resumeOffset..resumeEnd], StringComparison.Ordinal);
        Assert.Contains("@doka_sm_add_status = NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("__DOKA_SM_", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_sm_add_status", sql[postconditionOffset..assertionOffset],
            StringComparison.Ordinal);
    }

    /// <summary>Ordinary approved repair retains native MODIFY without removing any barrier.</summary>
    /// <param name="isMariaDb">Whether the native provider uses MariaDB CHECK removal grammar.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredAddSeparatesOrdinaryRepairFromOwnedBarrierRemoval(bool isMariaDb)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var handler = CreateHandler(context);
        var operation = RequiredOperation(SafeMigrationPolicy.RepairIfSafe);
        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb, NativeScopedBody);

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var body = Assert.Single(Fragments(Assert.Single(result.Commands), MySqlMigrationCommandFragmentKind.Body));
        var alterations = Payloads(body);
        var plainRepair = Assert.Single(alterations, static sql => sql ==
            "ALTER TABLE `items` MODIFY COLUMN `value` int NOT NULL");

        Assert.Contains("WHEN @doka_sm_action = 'repair' THEN CONVERT(0x"
            + Convert.ToHexString(Encoding.UTF8.GetBytes(plainRepair)), body, StringComparison.Ordinal);
        Assert.Equal(3, alterations.Length);
        Assert.DoesNotContain("DROP", plainRepair, StringComparison.OrdinalIgnoreCase);
        Assert.True(body.IndexOf(DeallocateSql, StringComparison.Ordinal)
            < body.IndexOf("WHEN @doka_sm_action = 'repair'", StringComparison.Ordinal));
    }

    /// <summary>Native clause composition delimits qualified names without replacing SQL payload text.</summary>
    /// <param name="isMariaDb">Whether the provider uses MariaDB CHECK removal grammar.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredAddQuotesQualifiedIdentifiersAndPreservesCommentLiteral(bool isMariaDb)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("it`ems",
                new ExpectedColumnDefinition("va`lue", typeof(int), isNullable: false, storeType: "int",
                    comment: "ALTER TABLE fake; 'literal'\\suffix"), schema: "sc`ope"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var alterations = Payloads(command.CommandText).Where(static sql => sql.StartsWith("ALTER TABLE ",
            StringComparison.Ordinal)).ToArray();

        // Assert
        Assert.Equal(2, alterations.Length);
        Assert.All(alterations, static sql => Assert.StartsWith("ALTER TABLE `sc``ope`.`it``ems` ", sql,
            StringComparison.Ordinal));
        Assert.Contains("CHECK (`va``lue` IS NOT NULL)", alterations[0], StringComparison.Ordinal);
        Assert.All(alterations, static sql => Assert.Contains("ALTER TABLE fake;", sql, StringComparison.Ordinal));
        Assert.DoesNotContain("UPDATE ", command.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An excluded TIMESTAMP still renders Matching and ordinary repair without unsafe required ADD.</summary>
    [Fact]
    public void ExcludedRequiredAddKeepsMatchingAndOrdinaryRepairRenderability()
    {
        // Arrange
        using var context = CreateContext(isMariaDb: false);
        var handler = CreateHandler(context);
        var authored = new AddColumnOperation
        {
            Name = "value",
            Table = "items",
            ClrType = typeof(DateTime),
            ColumnType = "timestamp",
            IsNullable = false,
        };

        authored["Doka:MySql:RequiresExplicitBackfill"] = true;
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items",
            SafeMigrationExpectedDefinitionFactory.From(authored)), SafeMigrationPolicy.RepairIfSafe);

        var rendered = new List<MigrationOperation>();
        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb: false, standard =>
        {
            rendered.Add(standard);

            return NativeScopedBody(standard);
        });

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var command = Assert.Single(result.Commands);
        var sql = command.CommandText;

        Assert.Equal(2, rendered.Count);
        Assert.True(Assert.IsType<AddColumnOperation>(rendered[0]).IsNullable);
        Assert.False(Assert.IsType<AlterColumnOperation>(rendered[1]).IsNullable);
        Assert.False(authored.IsNullable);
        Assert.True(Assert.IsType<bool>(authored["Doka:MySql:RequiresExplicitBackfill"]));
        Assert.Contains("WHEN 'matching' THEN 'no_op'", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN @doka_sm_action = 'repair'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_sm_required_add_resume", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(Payloads(sql), static payload => payload.Contains("ADD CONSTRAINT",
            StringComparison.Ordinal) || payload.Contains("DROP CHECK", StringComparison.Ordinal));
    }

    /// <summary>Declared server-side identity generation keeps its original native required ADD and metadata.</summary>
    [Fact]
    public void DeclaredAutoIncrementRequiredAddKeepsNativeGeneration()
    {
        // Arrange
        using var context = CreateContext(isMariaDb: false);
        var handler = CreateHandler(context);
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        var authored = new AddColumnOperation
        {
            Name = "value",
            Table = "items",
            ClrType = typeof(int),
            ColumnType = "int",
            IsNullable = false,
        };

        authored["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.AutoIncrement;
        authored["Doka:MySql:RequiresExplicitBackfill"] = true;
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items",
            SafeMigrationExpectedDefinitionFactory.From(authored)), SafeMigrationPolicy.ThrowIfDifferent);

        var rendered = new List<MigrationOperation>();
        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb: false, standard =>
        {
            rendered.Add(standard);

            return NativeScopedBody(standard);
        });

        // Act
        var result = handler.Generate(operationContext);

        // Assert
        var add = Assert.IsType<AddColumnOperation>(Assert.Single(rendered));

        Assert.False(capture.IsActive);
        Assert.False(capture.HasGenerationContract);
        Assert.False(add.IsNullable);
        Assert.Equal(MySqlValueGenerationStrategy.AutoIncrement,
            Assert.IsType<MySqlValueGenerationStrategy>(add["Doka:MySql:ValueGenerationStrategy"]));
        Assert.True(Assert.IsType<bool>(add["Doka:MySql:RequiresExplicitBackfill"]));
        Assert.DoesNotContain("@doka_sm_required_add_resume", Assert.Single(result.Commands).CommandText,
            StringComparison.Ordinal);
    }

    /// <summary>A certified escaped ENUM or SET member is admitted without changing SQL or session mode.</summary>
    /// <param name="isMariaDb">Whether both native stages use MariaDB grammar.</param>
    /// <param name="storeType">The standard-mode declaration that is invalid without backslash decoding.</param>
    [Theory]
    [InlineData(false, "enum('quo\\'te')")]
    [InlineData(true, "enum('quo\\'te')")]
    [InlineData(false, "set('quo\\'te','semi;colon','a,b')")]
    [InlineData(true, "set('quo\\'te','semi;colon','a,b')")]
    [InlineData(false, "enum(\"quo\\\"te\")")]
    [InlineData(true, "enum(\"quo\\\"te\")")]
    public void RequiredAddAcceptsCertifiedStandardModeEscapedMembers(bool isMariaDb, string storeType)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var definition = new ExpectedColumnDefinition("value", typeof(string), isNullable: false, storeType: storeType);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var alterations = Payloads(command.CommandText).Where(static sql => sql.StartsWith("ALTER TABLE ",
            StringComparison.Ordinal)).ToArray();

        // Assert
        Assert.Equal(2, alterations.Length);
        Assert.All(alterations, sql => Assert.Contains(storeType, sql, StringComparison.Ordinal));
        Assert.Contains("FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) = 0", command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT", alterations[0], StringComparison.Ordinal);
        Assert.Contains(isMariaDb ? "DROP CONSTRAINT" : "DROP CHECK", alterations[1], StringComparison.Ordinal);
        Assert.DoesNotContain("SET SESSION sql_mode", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.False(definition.IsNullable);
        Assert.Equal(storeType, definition.StoreType);
    }

    /// <summary>Standard escape handling does not hide statements, clauses or dangling literal boundaries.</summary>
    /// <param name="corruptModify">Whether the malformed body is native MODIFY rather than native ADD.</param>
    /// <param name="invalidBoundary">The independently violated native boundary.</param>
    [Theory]
    [InlineData(false, "second_statement")]
    [InlineData(true, "second_statement")]
    [InlineData(false, "extra_clause")]
    [InlineData(true, "extra_clause")]
    [InlineData(false, "terminal_escape")]
    [InlineData(true, "terminal_escape")]
    public void RequiredAddCertifiedEscapeParsingRejectsUnownedNativeBoundaries(
        bool corruptModify,
        string invalidBoundary
    )
    {
        // Arrange
        using var context = CreateContext(isMariaDb: false);
        var handler = CreateHandler(context);
        const string storeType = "enum('quo\\'te')";
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items",
            new ExpectedColumnDefinition("value", typeof(string), isNullable: false, storeType: storeType)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb: false, standard =>
        {
            var modify = standard is AlterColumnOperation;
            var body = "ALTER TABLE `items` " + (modify ? "MODIFY COLUMN" : "ADD") + " `value` " + storeType
                + (modify ? " NOT NULL" : " NULL");

            if (modify == corruptModify)
            {
                body += invalidBoundary switch
                {
                    "second_statement" => "; DROP TABLE `items`",
                    "extra_clause" => ", DROP COLUMN `id`",
                    "terminal_escape" => " COMMENT 'unfinished\\",
                    _ => throw new ArgumentOutOfRangeException(nameof(invalidBoundary)),
                };
            }

            return [MySqlMigrationCommandSpec.Create(body, transactionSuppressed: true)];
        });

        // Act
        var exception = Record.Exception(() => handler.Generate(operationContext));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Certified string escapes preserve doubled backticks and quotes in native owner identifiers.</summary>
    [Fact]
    public void RequiredAddCertifiedEscapesPreserveQuotedIdentifierBoundaries()
    {
        // Arrange
        using var context = CreateContext(isMariaDb: false);
        const string storeType = "enum('quo\\'te')";
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("it'ems",
                new ExpectedColumnDefinition("va\\l'`ue", typeof(string), isNullable: false, storeType: storeType)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var alterations = Payloads(command.CommandText).Where(static sql => sql.StartsWith("ALTER TABLE ",
            StringComparison.Ordinal)).ToArray();

        // Assert
        Assert.Equal(2, alterations.Length);
        Assert.All(alterations, static sql => Assert.StartsWith("ALTER TABLE `it'ems` ", sql,
            StringComparison.Ordinal));
        Assert.All(alterations, sql => Assert.Contains("`va\\l'``ue` " + storeType, sql, StringComparison.Ordinal));
        Assert.Contains("CHECK (`va\\l'``ue` IS NOT NULL)", alterations[0], StringComparison.Ordinal);
    }

    /// <summary>Known comment mode rejects Missing while preserving Matching and native scope restoration.</summary>
    /// <param name="isMariaDb">Whether the real native provider uses MariaDB grammar.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredAddKnownCommentModeKeepsMissingUnsupportedAndMatchingNoOp(bool isMariaDb)
    {
        // Arrange
        using var context = CreateContext(isMariaDb);
        var definition = new ExpectedColumnDefinition("value", typeof(string), isNullable: false,
            storeType: "enum('quo\\'te')", comment: "mode\\guard");

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var payloads = Payloads(command.CommandText);

        // Assert
        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(definition));
        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(definition));
        var missingGuard = Assert.Single(payloads, static sql => sql.EndsWith(
            " ELSE NULL END INTO @doka_sm_state", StringComparison.Ordinal));

        // WHY: Native mode qualification wraps the missing-only refusal but
        // must retain both matching precedence and its unconditional fallback.
        Assert.Contains("THEN 'matching' WHEN NOT (FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0) "
            + "THEN 'unsupported' ELSE ('unsupported') END) ELSE NULL END INTO @doka_sm_state",
            missingGuard, StringComparison.Ordinal);
        Assert.Contains("WHEN 'matching' THEN 'no_op'", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_sm_required_add_resume", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain(payloads, static sql => sql.Contains("ADD CONSTRAINT", StringComparison.Ordinal));
        Assert.Contains("SET SESSION sql_mode = IF(", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("SET SESSION sql_mode = IF(COALESCE(@doka_sm_provider_setup_entered = 1, FALSE), "
            + "@__doka_previous_sql_mode, @@SESSION.sql_mode)", command.CommandText,
            StringComparison.Ordinal);
        Assert.True(command.CommandText.LastIndexOf("SET SESSION sql_mode", StringComparison.Ordinal)
            > command.CommandText.IndexOf("EXECUTE doka_sm_statement", StringComparison.Ordinal));
    }

    /// <summary>Opaque native setup cannot extend an active escape certificate.</summary>
    /// <param name="setupSql">The opaque setup that cannot extend the catalog's current-mode certificate.</param>
    [Theory]
    [InlineData("SET SESSION sql_mode = 'NO_BACKSLASH_ESCAPES';")]
    [InlineData("DO 11;")]
    public void RequiredAddRejectsCertifiedEscapesWithUnknownOpaqueNativeSetup(string setupSql)
    {
        // Arrange
        using var context = CreateContext(isMariaDb: false);
        var handler = CreateHandler(context);
        const string storeType = "enum('quo\\'te')";
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items",
                new ExpectedColumnDefinition("value", typeof(string), isNullable: false, storeType: storeType)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb: false, standard =>
        {
            var modify = standard is AlterColumnOperation;
            var body = "ALTER TABLE `items` " + (modify ? "MODIFY COLUMN" : "ADD") + " `value` " + storeType
                + (modify ? " NOT NULL" : " NULL");

            return [MySqlMigrationCommandSpec.CreateScoped([setupSql], body, ["DO 0;"], transactionSuppressed: true)];
        });

        // Act
        var exception = Record.Exception(() => handler.Generate(operationContext));

        // Assert
        var invalidScope = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("native setup scope", invalidScope.Message, StringComparison.Ordinal);
    }

    /// <summary>Unexpected provider bodies or incompatible scopes cannot acquire atomic protocol ownership.</summary>
    /// <param name="invalidShape">The independent native body or scope invariant being violated.</param>
    [Theory]
    [InlineData("second_statement")]
    [InlineData("extra_clause")]
    [InlineData("wrong_owner")]
    [InlineData("unclosed_literal")]
    [InlineData("different_setup")]
    [InlineData("different_cleanup")]
    [InlineData("different_transaction")]
    [InlineData("multiple_commands")]
    public void RequiredAddRejectsUnverifiedNativeBodyOrScope(string invalidShape)
    {
        // Arrange
        using var context = CreateContext(isMariaDb: false);
        var handler = CreateHandler(context);
        var operation = RequiredOperation(SafeMigrationPolicy.ThrowIfDifferent);
        var operationContext = CreateOperationContext(context, handler, operation, isMariaDb: false, standard =>
        {
            var isModify = standard is AlterColumnOperation;
            var body = isModify
                ? "ALTER TABLE `items` MODIFY COLUMN `value` int NOT NULL"
                : invalidShape switch
                {
                    "second_statement" => "ALTER TABLE `items` ADD `value` int NULL; DROP TABLE `items`",
                    "extra_clause" => "ALTER TABLE `items` ADD `value` int NULL, DROP COLUMN `id`",
                    "wrong_owner" => "ALTER TABLE `other_items` ADD `value` int NULL",
                    "unclosed_literal" => "ALTER TABLE `items` ADD `value` int NULL COMMENT 'unclosed",
                    _ => "ALTER TABLE `items` ADD `value` int NULL",
                };

            var command = MySqlMigrationCommandSpec.CreateScoped(
                [isModify && invalidShape == "different_setup" ? "DO 13;" : ProviderSetup],
                body,
                [isModify && invalidShape == "different_cleanup" ? "DO 14;" : ProviderCleanup],
                transactionSuppressed: !isModify || invalidShape != "different_transaction");

            return invalidShape == "multiple_commands" && !isModify
                ? [command, MySqlMigrationCommandSpec.Create("DO 0;", transactionSuppressed: true)]
                : [command];
        });

        // Act
        var exception = Record.Exception(() => handler.Generate(operationContext));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Creates the exact required no-default target consumed by the catalog protocol.</summary>
    /// <param name="policy">The independently selected ordinary difference policy.</param>
    private static SafeMigrationOperation RequiredOperation(SafeMigrationPolicy policy) => new(
        new EnsureColumnIntent("items", new ExpectedColumnDefinition("value", typeof(int), isNullable: false,
            storeType: "int")), policy);

    /// <summary>Renders deterministic single-column native bodies with one opaque resource scope.</summary>
    /// <param name="operation">The native ADD or MODIFY operation cloned by the handler.</param>
    private static IReadOnlyList<MySqlMigrationCommandSpec> NativeScopedBody(MigrationOperation operation) =>
    [
        MySqlMigrationCommandSpec.CreateScoped([ProviderSetup], operation switch
            {
                AddColumnOperation { IsNullable: true } => "ALTER TABLE `items` ADD `value` int NULL",
                AddColumnOperation => "ALTER TABLE `items` ADD `value` int NOT NULL",
                _ => "ALTER TABLE `items` MODIFY COLUMN `value` int NOT NULL",
            },
            [ProviderCleanup], transactionSuppressed: true),
    ];

    /// <summary>Creates the real handler with a bounded substitution of the standard SQL renderer.</summary>
    /// <param name="context">The real offline provider service scope.</param>
    /// <param name="handler">The handler whose public identity is supplied to Doka.</param>
    /// <param name="operation">The authored logical Ensure operation.</param>
    /// <param name="isMariaDb">Whether the canonical feature profile uses MariaDB.</param>
    /// <param name="renderer">The bounded native body/scope producer.</param>
    private static MySqlMigrationOperationContext CreateOperationContext(
        DbContext context,
        MySqlSafeMigrationOperationHandler handler,
        SafeMigrationOperation operation,
        bool isMariaDb,
        Func<MigrationOperation, IReadOnlyList<MySqlMigrationCommandSpec>> renderer
    )
    {
        var version = ServerVersion(isMariaDb);
        var profile = typeof(MySqlServerVersion).GetProperty("Profile", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(version) ?? throw new InvalidOperationException("The provider's canonical profile is missing.");

        var featureConstructor = Assert.Single(typeof(MySqlMigrationFeatureSet)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));

        var features = Assert.IsType<MySqlMigrationFeatureSet>(featureConstructor.Invoke([profile]));
        var constructor = Assert.Single(typeof(MySqlMigrationOperationContext)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));

        return Assert.IsType<MySqlMigrationOperationContext>(constructor.Invoke(
        [
            operation, context.Model, MigrationsSqlGenerationOptions.Default, version, features, 0,
            handler.HandlerId, renderer,
        ]));
    }

    /// <summary>Creates a handler using only real provider services.</summary>
    /// <param name="context">The offline provider scope.</param>
    private static MySqlSafeMigrationOperationHandler CreateHandler(DbContext context) => new(
        context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>(),
        context.GetService<MySqlSafeMigrationPlanCapture>(), context.GetService<IDesignTimeModel>());

    /// <summary>Creates an offline provider context without opening its connection.</summary>
    /// <param name="isMariaDb">Whether the configured canonical server profile uses MariaDB.</param>
    private static DbContext CreateContext(bool isMariaDb) => new(new DbContextOptionsBuilder()
        .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            ServerVersion(isMariaDb))
        .UseMySqlSafeMigrations().Options);

    /// <summary>Selects one of the independently qualified native server grammars.</summary>
    /// <param name="isMariaDb">Whether to use MariaDB rather than MySQL.</param>
    private static MySqlServerVersion ServerVersion(bool isMariaDb) => isMariaDb
        ? MySqlServerVersion.MariaDb(new Version(10, 11, 19))
        : MySqlServerVersion.MySql(new Version(8, 4, 11));

    /// <summary>Reads original provider-validated scoped fragments.</summary>
    /// <param name="command">The single generated logical scope.</param>
    /// <param name="kind">The exact resource role to inspect.</param>
    private static string[] Fragments(MySqlMigrationCommandSpec command, MySqlMigrationCommandFragmentKind kind) =>
        command.Fragments.Where(fragment => fragment.Kind == kind)
            .Select(static fragment => fragment.CommandText.ToString()).ToArray();

    /// <summary>Decodes actual prepared bodies without interpreting their SQL.</summary>
    /// <param name="sql">The complete prepared transport.</param>
    private static string[] Payloads(string sql)
    {
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";
        var payloads = new List<string>();
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var start = offset + prefix.Length;
            var end = sql.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("The prepared SQL payload is unterminated.");
            }

            payloads.Add(Encoding.UTF8.GetString(Convert.FromHexString(sql.AsSpan(start, end - start))));
            offset = end + suffix.Length;
        }

        return payloads.ToArray();
    }

    /// <summary>Counts exact transport controls without interpreting literal payloads.</summary>
    /// <param name="sql">The transport or native body to inspect.</param>
    /// <param name="fragment">The exact control or clause to count.</param>
    private static int Count(string sql, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = sql.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }
}
