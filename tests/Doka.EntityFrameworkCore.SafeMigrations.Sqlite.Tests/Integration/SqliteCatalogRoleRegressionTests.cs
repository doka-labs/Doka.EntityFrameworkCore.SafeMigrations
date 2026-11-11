namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Protects native catalog roles from identifier, literal and expression-boundary confusion.</summary>
public sealed class SqliteCatalogRoleRegressionTests : SqliteIntegrationTestBase
{
    /// <summary>Expression keys come from the real owner list, never text inside a quoted index name.</summary>
    /// <param name="name">The independently authored physical index name.</param>
    /// <param name="actualExpression">The real indexed expression.</param>
    /// <param name="matching">Whether the physical key matches the expected expression.</param>
    [Theory]
    [InlineData("ON bogus (a + 1)", "a + 2", false)]
    [InlineData("ON bogus (decoy)", "a + 1", true)]
    [InlineData("ON bogus (a + 2)", "a + 2", false)]
    [InlineData("quoted\" ON bogus (a + 2)", "a + 1", true)]
    [InlineData("ordinary", "a + 1", true)]
    public async Task NativeExpressionIndexUsesItsActualKeyList(
        string name,
        string actualExpression,
        bool matching
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        var delimitedName = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        await ExecuteSqlAsync(connection,
            $"""
            CREATE TABLE catalog_indexes(a INTEGER);
            CREATE INDEX {delimitedName} ON catalog_indexes (({actualExpression}));
            INSERT INTO catalog_indexes VALUES (41);
            """,
            CancellationToken.None);

        await using var context = CreateContext(connection);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.EnsureIndex(new ExpectedIndexDefinition(name,
                "catalog_indexes",
                [new ExpectedIndexKeyDefinition(expression: "a + 1")]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                operations.Operations,
                new SafeMigrationRunOptions("sqlite-index-key-source"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            operations.Operations,
            CancellationToken.None));

        var value = await ScalarIntAsync(connection, "SELECT a FROM catalog_indexes;", CancellationToken.None);

        // Assert
        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(matching, failure is null);
        Assert.Equal(41, value);
    }

    /// <summary>Bounded structured index keys and filters tolerate native quoting, but not semantic drift.</summary>
    /// <param name="actualKey">The native expression key.</param>
    /// <param name="actualFilter">The native partial predicate.</param>
    /// <param name="matching">Whether both expressions retain the same contract.</param>
    [Theory]
    [InlineData("a+1", "label='Active' AND a>0", true)]
    [InlineData("[a] + 1", "[label] = 'Active' AND [a] > 0", true)]
    [InlineData("`a` + 1", "`label` = 'Active' AND `a` > 0", true)]
    [InlineData("a+2", "label='Active' AND a>0", false)]
    [InlineData("a+1.0", "label='Active' AND a>0", false)]
    [InlineData("a+1", "label='ACTIVE' AND a>0", false)]
    [InlineData("a+1", "label='Active' OR a>0", false)]
    public async Task StructuredIndexRetainsNativeExpressionSemantics(
        string actualKey,
        string actualFilter,
        bool matching
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await ExecuteSqlAsync(connection,
            $"""
            CREATE TABLE catalog_indexes(a INTEGER, label TEXT); CREATE INDEX ix_roles
            ON catalog_indexes (({actualKey}))
            WHERE {actualFilter};
            """,
            CancellationToken.None);

        await using var context = CreateContext(connection);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        var key = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("a"),
            SafeMigrationSqlBinaryOperator.Add,
            SafeMigrationSql.Literal(1));

        var filter = SafeMigrationSql.Binary(
            SafeMigrationSql.Binary(SafeMigrationSql.Identifier("label"),
                SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal("Active")),
            SafeMigrationSqlBinaryOperator.And,
            SafeMigrationSql.Binary(SafeMigrationSql.Identifier("a"),
                SafeMigrationSqlBinaryOperator.GreaterThan,
                SafeMigrationSql.Literal(0)));

        operations.EnsureIndex(new ExpectedIndexDefinition("ix_roles",
                "catalog_indexes",
                [new ExpectedIndexKeyDefinition(structuredExpression: key)],
                structuredFilter: filter),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                operations.Operations,
                new SafeMigrationRunOptions("sqlite-structured-index"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            operations.Operations,
            CancellationToken.None));

        // Assert
        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(matching, failure is null);
    }

    /// <summary>Double-quoted DEFAULT payloads retain value case independently of DQS session configuration.</summary>
    /// <param name="doubleQuotedStrings">Whether DQS parsing remains enabled after fixture creation.</param>
    /// <param name="actualSql">The native default token.</param>
    /// <param name="expectedSql">The authored default token.</param>
    /// <param name="matching">Whether value-preserving matching is expected.</param>
    [Theory]
    [InlineData(true, "\"Active\"", "\"ACTIVE\"", false)]
    [InlineData(false, "\"Active\"", "\"ACTIVE\"", false)]
    [InlineData(true, "\"Active\"", "\"Active\"", true)]
    [InlineData(false, "\"Active\"", "\"Active\"", true)]
    [InlineData(true, "'Active'", "'ACTIVE'", false)]
    [InlineData(false, "'Active'", "'Active'", true)]
    [InlineData(true, "\"a\"\"B\"", "\"a\"\"b\"", false)]
    [InlineData(true, "(\"ABS\"(1))", "(\"abs\"(1))", true)]
    [InlineData(false, "(\"ABS\"(1))", "(\"abs\"(1))", true)]
    [InlineData(true, "(CAST('Active' AS \"TEXT\"))", "(CAST('Active' AS \"text\"))", true)]
    [InlineData(true, "('Active' COLLATE \"NOCASE\")", "('Active' COLLATE \"nocase\")", true)]
    [InlineData(true, "(CAST('Active' AS \"TEXT\"))", "(CAST('ACTIVE' AS \"text\"))", false)]
    [InlineData(true, "('Active' COLLATE \"NOCASE\")", "('Active' COLLATE \"RTRIM\")", false)]
    public async Task DefaultValueComparisonNeverFoldsQuotedPayload(
        bool doubleQuotedStrings,
        string actualSql,
        string expectedSql,
        bool matching
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await ExecuteSqlAsync(connection,
            $"CREATE TABLE catalog_defaults(v TEXT NOT NULL DEFAULT {actualSql});",
            CancellationToken.None);

        var nativeMode = doubleQuotedStrings
            ? 1
            : 0;

        var ddlConfiguration = raw.sqlite3_db_config(connection.Handle, raw.SQLITE_DBCONFIG_DQS_DDL, nativeMode, out _);
        var dmlConfiguration = raw.sqlite3_db_config(connection.Handle, raw.SQLITE_DBCONFIG_DQS_DML, nativeMode, out _);

        await using var context = CreateContext(connection);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.EnsureColumn("catalog_defaults",
            new ExpectedColumnDefinition("v",
                typeof(string),
                false,
                storeType: "TEXT",
                defaultValue: SafeMigrationDefaultValue.Sql(expectedSql)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                operations.Operations,
                new SafeMigrationRunOptions("sqlite-default-value-roles"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context,
            operations.Operations,
            CancellationToken.None));

        // Assert
        Assert.Equal(0, ddlConfiguration);
        Assert.Equal(0, dmlConfiguration);
        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(matching, failure is null);
    }

    /// <summary>Only outer column COLLATE clauses determine the declared collation of generated columns.</summary>
    /// <param name="outerCollation">The optional explicit declared collation after the generated expression.</param>
    /// <param name="expectedCollation">The expected physical declaration.</param>
    /// <param name="matching">Whether the declared column collations agree.</param>
    [Theory]
    [InlineData("", null, true)]
    [InlineData(" COLLATE BINARY", "BINARY", true)]
    [InlineData(" COLLATE RTRIM", "RTRIM", true)]
    [InlineData(" COLLATE NOCASE", "NOCASE", true)]
    [InlineData(" COLLATE BINARY", "NOCASE", false)]
    [InlineData(" COLLATE NOCASE", "BINARY", false)]
    public async Task GeneratedExpressionCollationDoesNotOverrideColumnDeclaration(
        string outerCollation,
        string? expectedCollation,
        bool matching
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await ExecuteSqlAsync(connection,
            $"""
            CREATE TABLE catalog_generated (
                source TEXT,
                g TEXT AS(source COLLATE NOCASE) VIRTUAL{outerCollation}
            );
            INSERT INTO catalog_generated(source) VALUES ('a');
            """,
            CancellationToken.None);

        await using var context = CreateContext(connection);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.EnsureColumn("catalog_generated",
            new ExpectedColumnDefinition("g",
                typeof(string),
                true,
                storeType: "TEXT",
                collation: expectedCollation is null
                    ? null
                    : new SafeMigrationCollationIdentifier(expectedCollation),
                computedExpression: SafeMigrationSql.Collate(SafeMigrationSql.Identifier("source"), "NOCASE"),
                isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context,
                operations.Operations,
                new SafeMigrationRunOptions("sqlite-declared-generated-collation"),
                CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
            await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        });

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        Assert.Equal(matching
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(matching, failure is null);
        Assert.Equal(outerCollation.Length == 0
                ? "BINARY"
                : outerCollation[9..],
            snapshot
                .Tables["catalog_generated"]
                .Columns["g"].Collation
            ?? "BINARY");
    }

    /// <summary>Single-quoted native constraint names are decoded at identifier grammar positions only.</summary>
    /// <param name="name">The decoded name, possibly containing an apostrophe.</param>
    [Theory]
    [InlineData("quoted constraint")]
    [InlineData("quoted'constraint")]
    public async Task SingleQuotedConstraintNamesRetainTheirPhysicalIdentities(
        string name
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        var token = "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
        await ExecuteSqlAsync(connection,
            $"""
            CREATE TABLE named_primary(a INTEGER, CONSTRAINT {token}
            PRIMARY KEY(a)); CREATE TABLE named_check(a INTEGER,
            CONSTRAINT {token} CHECK(a>0)); CREATE TABLE named_unique(a
            INTEGER,
            CONSTRAINT {token} UNIQUE(a));
            """,
            CancellationToken.None);

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        Assert.Equal(name, snapshot.Tables["named_primary"].PrimaryKeyName);
        Assert.Equal(name,
            Assert.Single(snapshot.Tables["named_check"].Checks)
                .Name);

        Assert.Equal(name,
            Assert.Single(snapshot.Tables["named_unique"].UniqueConstraints)
                .Name);
    }

    /// <summary>Named native constraints really disappear after an atomic rebuild and replay preserves rows.</summary>
    /// <param name="suffix">The suffix inside each single-quoted constraint name.</param>
    [Theory]
    [InlineData("quoted")]
    [InlineData("quo'ted")]
    public async Task SingleQuotedConstraintDropsApplyAndReplayWithoutLosingRows(
        string suffix
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        var escaped = suffix.Replace("'", "''", StringComparison.Ordinal);
        await ExecuteSqlAsync(connection,
            $"""
            CREATE TABLE drop_parents(Id INTEGER NOT NULL, CONSTRAINT pk_drop_parents
            PRIMARY KEY(Id)); CREATE TABLE drop_entities(Id INTEGER NOT NULL,
            Code TEXT NOT NULL, ParentId INTEGER NULL, CONSTRAINT pk_drop_entities PRIMARY
            KEY(Id),
            CONSTRAINT 'uq_{escaped}' UNIQUE(Code), CONSTRAINT 'ck_{escaped}'
            CHECK(length(Code)>0),
            CONSTRAINT 'fk_{escaped}' FOREIGN KEY(ParentId) REFERENCES
            drop_parents(Id)); INSERT INTO drop_parents VALUES(1); INSERT INTO drop_entities
            VALUES(11,'existing',1);
            """,
            CancellationToken.None);

        await using var context = new SqliteConstraintDropTestContext(connection);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.DropUniqueConstraintIfExists("uq_" + suffix, "drop_entities");
        operations.DropCheckConstraintIfExists("ck_" + suffix, "drop_entities");
        operations.DropForeignKeyIfExists("fk_" + suffix, "drop_entities");

        // Act
        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM drop_entities WHERE Id=11 AND Code='existing' AND ParentId=1;",
            CancellationToken.None);

        // Assert
        var table = snapshot.Tables["drop_entities"];
        Assert.Empty(table.Checks);
        Assert.Empty(table.UniqueConstraints);
        Assert.Empty(table.ForeignKeys);
        Assert.Equal(1, rows);
    }

    /// <summary>Removes a single-quoted primary key and preserves existing data through replay.</summary>
    /// <param name="name">The decoded native primary-key name.</param>
    [Theory]
    [InlineData("quoted key")]
    [InlineData("quoted'key")]
    public async Task SingleQuotedPrimaryKeyDropAppliesAndReplays(
        string name
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        var escaped = name.Replace("'", "''", StringComparison.Ordinal);
        await ExecuteSqlAsync(connection,
            $"""
            CREATE TABLE primary_key_drop_entities(Id INTEGER NOT NULL, Value TEXT NOT
            NULL,
            CONSTRAINT '{escaped}' PRIMARY KEY(Id)); INSERT INTO primary_key_drop_entities
            VALUES(41,'existing');
            """,
            CancellationToken.None);

        await using var context = new SqlitePrimaryKeyDropTestContext(connection);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.DropPrimaryKeyIfExists(name, "primary_key_drop_entities");

        // Act
        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM primary_key_drop_entities WHERE Id=41 AND Value='existing';",
            CancellationToken.None);

        // Assert
        Assert.Empty(snapshot.Tables["primary_key_drop_entities"].PrimaryKeyColumns);
        Assert.Equal(1, rows);
    }

    /// <summary>Creation and replay keep expression collations separate from the column declaration.</summary>
    [Fact]
    public async Task CreatedGeneratedColumnRetainsIndependentDeclaredCollation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await ExecuteSqlAsync(connection, "CREATE TABLE generated_add(source TEXT);", CancellationToken.None);
        await using var context = CreateContext(connection);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.EnsureColumn("generated_add",
            new ExpectedColumnDefinition("g",
                typeof(string),
                true,
                storeType: "TEXT",
                computedExpression: SafeMigrationSql.Collate(
                    SafeMigrationSql.Identifier("source"),
                    "NOCASE"),
                isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        Assert.Null(snapshot
            .Tables["generated_add"]
            .Columns["g"].Collation);

        Assert.NotNull(snapshot
            .Tables["generated_add"]
            .Columns["g"].GeneratedSql);
    }

    /// <summary>Default capture retains native quoted function spelling without reinterpreting it.</summary>
    /// <param name="defaultSql">The original provider-owned SQL expression.</param>
    [Theory]
    [InlineData("\"ABS\"(1)")]
    [InlineData("ABS(1)")]
    public async Task CapturedFunctionDefaultRetainsItsAuthoredNativeRole(
        string defaultSql
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        await ExecuteSqlAsync(connection, "CREATE TABLE default_capture(Id INTEGER);", CancellationToken.None);
        await using var context = CreateContext(connection);
        var operations = new MigrationBuilder(context.Database.ProviderName!);
        operations.AddColumnIfNotExistsFromModel(
            operation => operation.AddColumn<int>("Value",
                "default_capture",
                type: "INTEGER",
                nullable: false,
                defaultValueSql: defaultSql),
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        await ExecuteSqlAsync(connection, "INSERT INTO default_capture(Id) VALUES(41);", CancellationToken.None);
        await ExecuteOperationsAsync(context, operations.Operations, CancellationToken.None);
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var value = await ScalarIntAsync(connection, "SELECT Value FROM default_capture;", CancellationToken.None);

        // Assert
        Assert.Equal(defaultSql,
            snapshot
                .Tables["default_capture"]
                .Columns["Value"].DefaultSql);

        Assert.Equal(1, value);
    }

    /// <summary>Quoted UNIQUE names retain column and adjacent-keyword grammar positions.</summary>
    /// <param name="inline">Whether the UNIQUE constraint is declared on the column.</param>
    /// <param name="adjacent">Whether the quoted name touches the following keyword.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SingleQuotedUniqueNamesRetainTheirDeclarationPosition(
        bool inline,
        bool adjacent
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(CancellationToken.None);
        var separator = adjacent
            ? string.Empty
            : " ";

        var declaration = inline
            ? $"a INTEGER CONSTRAINT 'uq''quoted'{separator}UNIQUE"
            : $"a INTEGER, CONSTRAINT 'uq''quoted'{separator}UNIQUE(a)";

        await ExecuteSqlAsync(connection, $"CREATE TABLE named_unique({declaration});", CancellationToken.None);

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        Assert.Equal("uq'quoted",
            Assert.Single(snapshot.Tables["named_unique"].UniqueConstraints)
                .Name);
    }
}
