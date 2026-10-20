namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Preserves captured native SQL while validating its bounded structured contract.</summary>
public sealed class SqliteCapturedExpressionSourceTests : SqliteIntegrationTestBase
{
    private const string ComputedSql =
        "CASE WHEN typeof(-9223372036854775808) = 'integer' THEN \"source\" ELSE 0 END";

    /// <summary>Executes the captured source without normalizing away SQLite's integer-token semantics.</summary>
    /// <param name="addColumn">Whether the generated column is added to an existing populated table.</param>
    /// <param name="stored">Whether the fresh table persists the generated value.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CapturedComputedSourceSurvivesCreateAddAndReplay(bool addColumn, bool stored)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (addColumn)
        {
            await ExecuteSqlAsync(connection,
                "CREATE TABLE source_nodes (source INTEGER NOT NULL); INSERT INTO source_nodes VALUES (7);");

            builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
                "derived", "source_nodes", type: "INTEGER", nullable: false,
                computedColumnSql: ComputedSql, stored: false));
        }
        else
        {
            builder.CreateTableIfNotExists("source_nodes", table => new
            {
                source = table.Column<int>(type: "INTEGER", nullable: false),
                derived = table.Column<int>(type: "INTEGER", nullable: false,
                    computedColumnSql: ComputedSql, stored: stored),
            });
        }

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        if (!addColumn)
        {
            await ExecuteSqlAsync(connection, "INSERT INTO source_nodes (source) VALUES (7);");
        }

        var rows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM source_nodes WHERE derived = 7;");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'source_nodes';";
        var sql = await command.ExecuteScalarAsync();
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-captured-generated-source"));

        // Assert
        Assert.Equal(1, rows);
        Assert.Contains("GENERATED ALWAYS AS (" + ComputedSql + ")", Assert.IsType<string>(sql),
            StringComparison.Ordinal);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
    }

    /// <summary>Numeric tokens with equal CLR decimal values can have different native SQLite semantics.</summary>
    /// <param name="decimalSuffix">Whether the live expression promotes the boundary token to REAL.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComputedCatalogDoesNotAliasIntegerAndRealBoundaryTokens(bool decimalSuffix)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var actual = decimalSuffix ? ComputedSql.Replace("5808)", "5808.0)", StringComparison.Ordinal) : ComputedSql;
        await ExecuteSqlAsync(connection,
            "CREATE TABLE source_nodes (source INTEGER NOT NULL, derived INTEGER NOT NULL GENERATED ALWAYS AS ("
            + actual + ") VIRTUAL); INSERT INTO source_nodes (source) VALUES (7);");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
            "derived", "source_nodes", type: "INTEGER", nullable: false,
            computedColumnSql: ComputedSql, stored: false));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-captured-numeric-drift"));

        var rows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM source_nodes WHERE derived = 7;");

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(decimalSuffix ? 0 : 1, rows);
        Assert.Equal(decimalSuffix ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            assessment.ObservedState);
    }

    /// <summary>Captured CHECK contracts preserve numeric spelling instead of accepting a rendered alternate.</summary>
    /// <param name="decimalSuffix">Whether the live constraint has a semantically different REAL token.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckCatalogPreservesNativeNumericSourceSemantics(bool decimalSuffix)
    {
        // Arrange
        const string expected = "typeof(-9223372036854775808) = 'integer'";
        var actual = decimalSuffix ? "typeof(-9223372036854775808.0) = 'integer'" : expected;
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE source_nodes (source INTEGER, CONSTRAINT ck_source CHECK (" + actual + "));");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_source", "source_nodes", expected);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-captured-check-drift"));

        var exception = await Record.ExceptionAsync(() =>
            ExecuteSqlAsync(connection, "INSERT INTO source_nodes VALUES (1);"));

        // Assert
        Assert.Equal(decimalSuffix ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments).ObservedState);
        if (decimalSuffix)
        {
            Assert.IsType<SqliteException>(exception);
        }
        else
        {
            Assert.Null(exception);
        }
    }

    /// <summary>Native CHECK creation retains the captured source instead of a structured-tree rendering.</summary>
    [Fact]
    public async Task FreshCheckExecutesCapturedSourceAndReplays()
    {
        // Arrange
        const string expression = "typeof(-9223372036854775808) = 'integer'";
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("source_nodes", table => new
        {
            source = table.Column<int>(type: "INTEGER", nullable: false),
        }, constraints: table => table.CheckConstraint("ck_source", expression));

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connection, "INSERT INTO source_nodes VALUES (7);");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'source_nodes' AND type = 'table';";
        var sql = await command.ExecuteScalarAsync();
        var rows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM source_nodes;");

        // Assert
        Assert.Contains("CHECK (" + expression + ")", Assert.IsType<string>(sql), StringComparison.Ordinal);
        Assert.Equal(1, rows);
    }

    /// <summary>All native SQLite identifier quote forms retain the captured generated expression source.</summary>
    /// <param name="quotedSource">One valid SQLite column-identifier spelling.</param>
    [Theory]
    [InlineData("\"source\"")]
    [InlineData("`source`")]
    [InlineData("[source]")]
    public async Task CapturedNativeIdentifierDelimitersExecuteWithoutRewriting(string quotedSource)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var expression = quotedSource + " + 1";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("source_nodes", table => new
        {
            source = table.Column<int>(type: "INTEGER", nullable: false),
            derived = table.Column<int>(type: "INTEGER", nullable: false, computedColumnSql: expression),
        });

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connection, "INSERT INTO source_nodes (source) VALUES (7);");
        var rows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM source_nodes WHERE derived = 8;");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'source_nodes' AND type = 'table';";
        var sql = await command.ExecuteScalarAsync();

        // Assert
        Assert.Equal(1, rows);
        Assert.Contains("GENERATED ALWAYS AS (" + expression + ")", Assert.IsType<string>(sql),
            StringComparison.Ordinal);
    }

    /// <summary>Permissive double quotes cannot turn a missing identifier into a trusted dependency proof.</summary>
    /// <param name="missing">Whether the captured identifier is absent from the complete table contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeDoubleQuotedStringFallbackDoesNotAuthorizeMissingColumn(bool missing)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var handle = connection.Handle ?? throw new InvalidOperationException("The fixture connection must be open.");
        var ddlCode = SQLitePCL.raw.sqlite3_db_config(handle, SQLitePCL.raw.SQLITE_DBCONFIG_DQS_DDL, 1,
            out var ddlEnabled);

        var dmlCode = SQLitePCL.raw.sqlite3_db_config(handle, SQLitePCL.raw.SQLITE_DBCONFIG_DQS_DML, 1,
            out var dmlEnabled);

        var expression = missing ? "\"absent\"" : "\"source\"";
        await ExecuteSqlAsync(connection,
            "CREATE TABLE native_dqs (source TEXT NOT NULL, derived TEXT GENERATED ALWAYS AS (" + expression
            + ") VIRTUAL); INSERT INTO native_dqs (source) VALUES ('present');");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("source_nodes", table => new
        {
            source = table.Column<string>(type: "TEXT", nullable: false),
            derived = table.Column<string>(type: "TEXT", nullable: false, computedColumnSql: expression),
        });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-dqs-source-proof"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var tables = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'source_nodes' AND type = 'table';");

        var nativeRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM native_dqs WHERE derived = '" + (missing ? "absent" : "present") + "';");

        // Assert
        Assert.Equal(SQLitePCL.raw.SQLITE_OK, ddlCode);
        Assert.Equal(SQLitePCL.raw.SQLITE_OK, dmlCode);
        Assert.Equal(1, ddlEnabled);
        Assert.Equal(1, dmlEnabled);
        Assert.Equal(1, nativeRows);
        Assert.Equal(missing ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(missing ? 0 : 1, tables);
        if (missing)
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
        else
        {
            Assert.Null(exception);
        }
    }

    /// <summary>Fresh CHECK identifiers must exist even when native double quotes accept a string instead.</summary>
    /// <param name="missing">Whether the parsed CHECK operand is absent from the complete table.</param>
    /// <param name="ensureOnly">Whether the same native CHECK already exists and would otherwise match.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DoubleQuotedCheckRequiresDeclaredOperand(bool missing, bool ensureOnly)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        EnableDoubleQuotedStringCompatibility(connection);
        var expression = (missing ? "\"absent\"" : "\"source\"") + " <> ''";
        await ExecuteSqlAsync(connection,
            "CREATE TABLE native_dqs (source TEXT NOT NULL, CHECK (" + expression
            + ")); INSERT INTO native_dqs VALUES ('present');");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (ensureOnly)
        {
            await ExecuteSqlAsync(connection,
                "CREATE TABLE source_nodes (source TEXT NOT NULL, CONSTRAINT ck_source CHECK (" + expression + "));");

            builder.AddCheckConstraintIfNotExists("ck_source", "source_nodes", expression);
        }
        else
        {
            builder.CreateTableIfNotExists("source_nodes", table => new
            {
                source = table.Column<string>(type: "TEXT", nullable: false),
            }, constraints: table => table.CheckConstraint("ck_source", expression));
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-dqs-check-proof"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var tables = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'source_nodes' AND type = 'table';");

        var nativeRows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM native_dqs;");

        // Assert
        Assert.Equal(1, nativeRows);
        Assert.Equal(missing ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(ensureOnly || !missing ? 1 : 0, tables);
        if (missing)
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
        else
        {
            Assert.Null(exception);
        }
    }

    /// <summary>A computed ADD cannot use native DQS fallback as proof that its source column exists.</summary>
    /// <param name="missing">Whether the parsed generated operand is absent from the existing table.</param>
    /// <param name="exists">Whether the same native column already exists and would otherwise match.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DoubleQuotedComputedAddRequiresLiveOperand(bool missing, bool exists)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        EnableDoubleQuotedStringCompatibility(connection);
        await ExecuteSqlAsync(connection,
            "CREATE TABLE source_nodes (source TEXT NOT NULL); INSERT INTO source_nodes VALUES ('present');");

        await using var context = CreateContext(connection);
        var expression = missing ? "\"absent\"" : "\"source\"";
        if (exists)
        {
            await ExecuteSqlAsync(connection,
                "ALTER TABLE source_nodes ADD derived TEXT NOT NULL GENERATED ALWAYS AS (" + expression + ") VIRTUAL;");
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<string>(
            "derived", "source_nodes", type: "TEXT", nullable: false, computedColumnSql: expression));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-dqs-computed-add-proof"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var columns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('source_nodes') WHERE name = 'derived';");

        var rows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM source_nodes;");

        // Assert
        Assert.Equal(missing ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(exists || !missing ? 1 : 0, columns);
        Assert.Equal(1, rows);
        if (missing)
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
        else
        {
            Assert.Null(exception);
        }
    }

    /// <summary>Enables native compatibility mode only for this isolated connection's adversarial probes.</summary>
    private static void EnableDoubleQuotedStringCompatibility(SqliteConnection connection)
    {
        var handle = connection.Handle ?? throw new InvalidOperationException("The fixture connection must be open.");
        var ddl = SQLitePCL.raw.sqlite3_db_config(handle, SQLitePCL.raw.SQLITE_DBCONFIG_DQS_DDL, 1, out var ddlEnabled);
        var dml = SQLitePCL.raw.sqlite3_db_config(handle, SQLitePCL.raw.SQLITE_DBCONFIG_DQS_DML, 1, out var dmlEnabled);
        if (ddl != SQLitePCL.raw.SQLITE_OK || dml != SQLitePCL.raw.SQLITE_OK || ddlEnabled != 1 || dmlEnabled != 1)
        {
            throw new InvalidOperationException("The isolated native DQS probe could not enable compatibility mode.");
        }
    }

    /// <summary>Column renames rewrite identifier roles without modifying matching string literal contents.</summary>
    [Fact]
    public async Task CapturedRenamePreservesQuotedIdentifiersAndLiteralBytes()
    {
        // Arrange
        const string expression =
            "CASE WHEN \"source\" > 0 AND 'source' = 'source' THEN \"source\" ELSE 0 END";

        const string renamedExpression =
            "CASE WHEN \"renamed\" > 0 AND 'source' = 'source' THEN \"renamed\" ELSE 0 END";

        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.CreateTableIfNotExists("source_nodes", table => new
        {
            source = table.Column<int>(type: "INTEGER", nullable: false),
            derived = table.Column<int>(type: "INTEGER", nullable: false, computedColumnSql: expression),
        });

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteSqlAsync(connection, "INSERT INTO source_nodes (source) VALUES (7);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameColumnIfExists("source", "source_nodes", "renamed");
        var expected = new MigrationBuilder(context.Database.ProviderName!);
        expected.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
            "derived", "source_nodes", type: "INTEGER", nullable: false, computedColumnSql: renamedExpression));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-captured-source-rename"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var versionBefore = await ScalarIntAsync(connection, "PRAGMA schema_version;");
        await ExecuteOperationsAsync(context, expected.Operations);
        var versionAfter = await ScalarIntAsync(connection, "PRAGMA schema_version;");
        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM source_nodes WHERE renamed = 7 AND derived = 7;");

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'source_nodes' AND type = 'table';";
        var sql = await command.ExecuteScalarAsync();

        // Assert
        Assert.True(report.Status == SafeMigrationReportStatus.Ready,
            Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report)));
        Assert.Equal(1, rows);
        Assert.Equal(versionBefore, versionAfter);
        var ddl = Assert.IsType<string>(sql);
        Assert.Contains(renamedExpression, ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("'renamed'", ddl, StringComparison.Ordinal);
    }

    /// <summary>A later verified rename supersedes the strict table's earlier computed source contract.</summary>
    [Fact]
    public async Task StrictDeclaredTableRenameSupersedesPriorComputedSourceContract()
    {
        // Arrange
        const string expression =
            "CASE WHEN \"source\" > 0 AND 'source' = 'source' THEN \"source\" ELSE 0 END";

        const string renamedExpression =
            "CASE WHEN \"renamed\" > 0 AND 'source' = 'source' THEN \"renamed\" ELSE 0 END";

        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.CreateTableIfNotExists("source_nodes", table => new
        {
            source = table.Column<int>(type: "INTEGER", nullable: false),
            derived = table.Column<int>(type: "INTEGER", nullable: false, computedColumnSql: expression),
        }, constraints: table => table.CheckConstraint("ck_source_nodes", "\"source\" >= 0"));

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteSqlAsync(connection, "INSERT INTO source_nodes (source) VALUES (7);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        foreach (var operation in create.Operations)
        {
            builder.Operations.Add(operation);
        }

        builder.RenameColumnIfExists("source", "source_nodes", "renamed");
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
            "derived", "source_nodes", type: "INTEGER", nullable: false, computedColumnSql: renamedExpression));

        builder.AddCheckConstraintIfNotExists("ck_source_nodes", "source_nodes", "\"renamed\" >= 0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-known-computed-source-rename"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM source_nodes WHERE renamed = 7 AND derived = 7;");

        var invalidRow = await Record.ExceptionAsync(
            () => ExecuteSqlAsync(connection, "INSERT INTO source_nodes (renamed) VALUES (-1);"));

        // Assert
        Assert.True(report.Status == SafeMigrationReportStatus.Ready,
            Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report)));
        Assert.Equal(1, rows);
        Assert.Contains("CHECK constraint failed", Assert.IsType<SqliteException>(invalidRow).Message,
            StringComparison.Ordinal);
    }

    /// <summary>Native ASCII column aliases rename without case-folding the CHECK's string literal.</summary>
    /// <param name="literalDrift">Whether the requested terminal CHECK changes only its literal's case.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckRenameUsesNativeColumnRolesWithoutChangingLiteralCase(bool literalDrift)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.CreateTableIfNotExists("source_nodes", table => new
        {
            Value = table.Column<string>(type: "TEXT", nullable: false),
        }, constraints: table => table.CheckConstraint("ck_source_nodes", "\"value\" <> 'VALUE'"));

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteSqlAsync(connection, "INSERT INTO source_nodes (Value) VALUES ('value');");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        foreach (var operation in create.Operations)
        {
            builder.Operations.Add(operation);
        }

        builder.RenameColumnIfExists("Value", "source_nodes", "score");
        builder.AddCheckConstraintIfNotExists("ck_source_nodes", "source_nodes",
            literalDrift ? "\"score\" <> 'value'" : "\"score\" <> 'VALUE'");

        var versionBefore = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var transition = new SqliteTableConstraintTransitions([builder.Operations[0], builder.Operations[1]])
            .Find((SafeMigrationOperation)builder.Operations[0]);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-native-check-column-role"));

        if (!literalDrift)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var column = literalDrift ? "Value" : "score";
        var invalidRow = await Record.ExceptionAsync(() => ExecuteSqlAsync(connection,
            "INSERT INTO source_nodes (\"" + column + "\") VALUES ('VALUE');"));

        var rows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM source_nodes;");
        var versionAfter = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        var diagnostic = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        Assert.NotNull(transition);
        Assert.Equal("\"score\" <> 'VALUE'", Assert.Single(transition.CheckConstraints).CapturedSql);
        Assert.True(report.Status == (literalDrift
            ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready), diagnostic);

        Assert.True(report.Assessments[0].Action == SafeMigrationAction.NoOp, diagnostic);
        Assert.True(report.Assessments[1].Action == SafeMigrationAction.Apply, diagnostic);
        Assert.True(literalDrift ? report.Assessments[2].Action is { } rejection && rejection.RejectsExecution()
            : report.Assessments[2].Action == SafeMigrationAction.NoOp, diagnostic);

        Assert.Contains("CHECK constraint failed", Assert.IsType<SqliteException>(invalidRow).Message,
            StringComparison.Ordinal);

        Assert.Equal(1, rows);
        Assert.Equal(literalDrift, versionBefore == versionAfter);
    }

    /// <summary>Rename supersession cannot hide unrelated default drift in the final strict table contract.</summary>
    [Fact]
    public async Task RenamedStrictTableRetainsUnrelatedDefaultProofDuringPostflight()
    {
        // Arrange
        const string original = "CASE WHEN \"source\" > 0 THEN \"source\" ELSE 0 END";
        const string renamed = "CASE WHEN \"renamed\" > 0 THEN \"renamed\" ELSE 0 END";
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE source_nodes (renamed INTEGER NOT NULL, payload TEXT NOT NULL DEFAULT 'drift', "
            + "derived INTEGER NOT NULL GENERATED ALWAYS AS (" + renamed + ") VIRTUAL); "
            + "INSERT INTO source_nodes (renamed) VALUES (7);");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("source_nodes", table => new
        {
            source = table.Column<int>(type: "INTEGER", nullable: false),
            payload = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "expected"),
            derived = table.Column<int>(type: "INTEGER", nullable: false, computedColumnSql: original),
        });

        builder.RenameColumnIfExists("source", "source_nodes", "renamed");
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
            "derived", "source_nodes", type: "INTEGER", nullable: false, computedColumnSql: renamed));

        var versionBefore = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-rename-retained-default-proof"));

        var versionAfter = await ScalarIntAsync(connection, "PRAGMA schema_version;");
        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM source_nodes WHERE renamed = 7 AND derived = 7 AND payload = 'drift';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
        Assert.Equal(1, rows);
        Assert.Equal(versionBefore, versionAfter);
    }

    /// <summary>Runtime renamed-table revalidation rejects and rolls back an unrelated baseline mutation.</summary>
    [Fact]
    public async Task RuntimeRenameProofRejectsUnrelatedBaselineMutationAndRollsBack()
    {
        // Arrange
        const string original = "CASE WHEN \"source\" > 0 THEN \"source\" ELSE 0 END";
        const string renamed = "CASE WHEN \"renamed\" > 0 THEN \"renamed\" ELSE 0 END";
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("source_nodes", table => new
        {
            source = table.Column<int>(type: "INTEGER", nullable: false),
            derived = table.Column<int>(type: "INTEGER", nullable: false, computedColumnSql: original),
        });

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connection, "INSERT INTO source_nodes (source) VALUES (7);");
        builder.RenameColumnIfExists("source", "source_nodes", "renamed");
        builder.AddColumnIfNotExistsFromModel(operation => operation.AddColumn<int>(
            "derived", "source_nodes", type: "INTEGER", nullable: false, computedColumnSql: renamed));

        var generator = new SqliteSafeMigrationsSqlGenerator(
            new MutatingRenameBaseline(context.GetService<ISqliteSafeMigrationsBaselineGenerator>()),
            context.GetService<ISafeMigrationProviderAnalyzer>(),
            context.GetService<SqliteSafeMigrationExecutionState>(),
            context.GetService<MigrationsSqlGeneratorDependencies>());

        var commands = generator.Generate(builder.Operations, context.Model);
        var versionBefore = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            foreach (var command in commands)
            {
                _ = await command.ExecuteNonQueryAsync(context.GetService<IRelationalConnection>());
            }
        });

        var versionAfter = await ScalarIntAsync(connection, "PRAGMA schema_version;");
        var columns = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('source_nodes') WHERE name IN ('renamed', 'unreviewed');");

        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM source_nodes WHERE source = 7 AND derived = 7;");

        // Assert
        Assert.Contains("doka_sm_postcondition", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);

        Assert.Equal(0, columns);
        Assert.Equal(1, rows);
        Assert.Equal(versionBefore, versionAfter);
        Assert.Null(context.GetService<IRelationalConnection>().CurrentTransaction);
    }

    /// <summary>Introduces one observable unrelated mutation after the accepted native rename.</summary>
    private sealed class MutatingRenameBaseline(ISqliteSafeMigrationsBaselineGenerator inner)
        : ISqliteSafeMigrationsBaselineGenerator
    {
        /// <inheritdoc />
        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default,
            IReadOnlyList<MigrationOperation>? guardedOperations = null
        )
        {
            var commands = inner.Generate(operations, model, options, guardedOperations);
            if (!operations.Any(static operation => operation is RenameColumnOperation))
            {
                return commands;
            }

            var mutation = inner.Generate(
                [new AddColumnOperation
                {
                    Name = "unreviewed",
                    Table = "source_nodes",
                    ClrType = typeof(string),
                    ColumnType = "TEXT",
                    IsNullable = true,
                }], model, options);

            return commands.Concat(mutation).ToArray();
        }
    }
}
