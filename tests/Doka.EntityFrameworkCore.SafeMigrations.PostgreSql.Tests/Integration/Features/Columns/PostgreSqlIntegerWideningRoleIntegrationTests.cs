namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Qualifies native exact-column and independent-owner dependency roles for integer widening.</summary>
public sealed class PostgreSqlIntegerWideningRoleIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the assembly-owned native engine and test-local database ownership.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public PostgreSqlIntegerWideningRoleIntegrationTests(PostgreSqlContainerFixture fixture) : base(fixture) { }

    /// <summary>Rejects the exact stored-column roles which native ALTER TYPE cannot rebind.</summary>
    /// <param name="caseId">The independently selectable native dependency class.</param>
    [Theory]
    [InlineData("sql-function")]
    [InlineData("sql-procedure")]
    [InlineData("trigger-when")]
    [InlineData("trigger-update-of")]
    [InlineData("row-policy")]
    public async Task NativeColumnRolesRemainPreflightAndRuntimeBlockers(string caseId)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var dependent = caseId switch
        {
            "sql-function" => "CREATE FUNCTION role_integer_value() RETURNS integer LANGUAGE SQL "
                + "RETURN (SELECT value FROM role_integer_rows LIMIT 1);",
            // WHY: The server parses this SQL-standard body as one CREATE command;
            // Npgsql's client-side semicolon splitter cannot parse BEGIN ATOMIC.
            "sql-procedure" => "DO $create_procedure$ BEGIN EXECUTE 'CREATE PROCEDURE role_integer_write() "
                + "LANGUAGE SQL BEGIN ATOMIC UPDATE role_integer_rows SET value = value; END'; "
                + "END $create_procedure$;",
            "trigger-when" or "trigger-update-of" => "CREATE FUNCTION role_integer_trigger() RETURNS trigger "
                + "LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; "
                + "CREATE TRIGGER role_integer_trigger BEFORE UPDATE "
                + (caseId == "trigger-update-of" ? "OF value " : string.Empty)
                + "ON role_integer_rows FOR EACH ROW "
                + (caseId == "trigger-when" ? "WHEN (OLD.value IS DISTINCT FROM NEW.value) " : string.Empty)
                + "EXECUTE FUNCTION role_integer_trigger();",
            "row-policy" => "CREATE POLICY integer_value_policy ON role_integer_rows USING (value > 0);",
            _ => throw new ArgumentOutOfRangeException(nameof(caseId)),
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE role_integer_rows(value integer NOT NULL); INSERT INTO role_integer_rows VALUES (7); "
            + dependent);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("role_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-dependent-native-role"));
        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context,
            builder.Operations));
        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
        var nativeFailure = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE role_integer_rows ALTER COLUMN value TYPE bigint;", CancellationToken.None));
        await transaction.RollbackAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal("P1001", failure.SqlState);
        Assert.Equal("0A000", Assert.IsType<PostgresException>(nativeFailure).SqlState);
        Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT value FROM role_integer_rows;"));
    }

    /// <summary>Does not turn safe owner-level or unrelated roles into source-column dependencies.</summary>
    /// <param name="caseId">The independently selectable safe unrelated role.</param>
    [Theory]
    [InlineData("count-function")]
    [InlineData("count-view")]
    [InlineData("other-column-expression-index")]
    [InlineData("other-column-check")]
    [InlineData("constant-check")]
    [InlineData("null-check")]
    [InlineData("whole-row-check")]
    [InlineData("whole-row-view")]
    public async Task IndependentOwnerRolesDoNotRejectWidening(string caseId)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var unrelated = caseId switch
        {
            "count-function" => "CREATE FUNCTION integer_row_count() RETURNS bigint LANGUAGE SQL "
                + "RETURN (SELECT count(*) FROM independent_integer_rows);",
            "count-view" => "CREATE VIEW integer_row_count AS SELECT count(*) FROM independent_integer_rows;",
            "other-column-expression-index" => "CREATE INDEX ix_independent_expression "
                + "ON independent_integer_rows ((unrelated + 1));",
            "other-column-check" => "ALTER TABLE independent_integer_rows ADD CHECK(unrelated > 0);",
            "constant-check" => "ALTER TABLE independent_integer_rows ADD CHECK(TRUE);",
            "null-check" => "ALTER TABLE independent_integer_rows ADD CHECK(NULL::boolean);",
            "whole-row-check" => "ALTER TABLE independent_integer_rows "
                + "ADD CHECK ((independent_integer_rows.*) IS NOT NULL);",
            "whole-row-view" => "CREATE VIEW independent_integer_view AS SELECT row_value "
                + "FROM independent_integer_rows row_value;",
            _ => throw new ArgumentOutOfRangeException(nameof(caseId)),
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE independent_integer_rows(value integer NOT NULL, unrelated integer NOT NULL); "
            + "INSERT INTO independent_integer_rows VALUES (7, 9); " + unrelated);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("independent_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-independent-native-role"));
        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(report.Assessments).Action);
        Assert.Equal("7:9", await ScalarStringAsync(connectionString,
            "SELECT value::text || ':' || unrelated::text FROM independent_integer_rows;"));
        Assert.Equal("bigint", await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema='public' "
            + "AND table_name='independent_integer_rows' AND column_name='value';"));
    }

    /// <summary>Rejects whole-row predicates whose truth is not preserved by an integer range proof.</summary>
    /// <param name="caseId">The independently selectable opaque or type-sensitive whole-row CHECK.</param>
    [Theory]
    [InlineData("type-function")]
    [InlineData("type-operator")]
    [InlineData("column-size")]
    public async Task WholeRowChecksCannotBorrowIntegerRangeProof(string caseId)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var check = caseId switch
        {
            "type-function" => "whole_integer_accepts(whole_integer_rows.*)",
            "type-operator" => "whole_integer_rows.* #=# whole_integer_rows.*",
            _ => "pg_catalog.pg_column_size(whole_integer_rows.*) <= 28",
        };

        var callback = caseId switch
        {
            "type-function" => "CREATE FUNCTION whole_integer_accepts(whole_integer_rows) RETURNS boolean "
                + "LANGUAGE sql AS 'SELECT pg_typeof($1.value) = ''integer''::regtype'; ",
            "type-operator" => "CREATE FUNCTION whole_integer_compares(whole_integer_rows,whole_integer_rows) "
                + "RETURNS boolean LANGUAGE sql AS 'SELECT pg_typeof($1.value) = ''integer''::regtype'; "
                + "CREATE OPERATOR #=# (LEFTARG=whole_integer_rows, RIGHTARG=whole_integer_rows, "
                + "FUNCTION=whole_integer_compares); ",
            _ => string.Empty,
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE whole_integer_rows(value integer NOT NULL); INSERT INTO whole_integer_rows VALUES(7); "
            + callback + "ALTER TABLE whole_integer_rows ADD CONSTRAINT ck_whole_integer CHECK(" + check + ");");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("whole_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-whole-row-check"));
        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context,
            builder.Operations));
        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE whole_integer_rows ALTER COLUMN value TYPE bigint;",
            CancellationToken.None);
        var nativeCheckQuery = caseId switch
        {
            "type-function" => context.Database.SqlQueryRaw<bool>(
                "SELECT whole_integer_accepts(whole_integer_rows.*) AS \"Value\" FROM whole_integer_rows"),
            "type-operator" => context.Database.SqlQueryRaw<bool>(
                "SELECT whole_integer_rows.* #=# whole_integer_rows.* AS \"Value\" FROM whole_integer_rows"),
            _ => context.Database.SqlQueryRaw<bool>(
                "SELECT pg_catalog.pg_column_size(whole_integer_rows.*) <= 28 AS \"Value\" FROM whole_integer_rows"),
        };

        var nativePreservesCheck = await nativeCheckQuery.SingleAsync(CancellationToken.None);
        await transaction.RollbackAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal("P1001", failure.SqlState);
        Assert.False(nativePreservesCheck);
        Assert.Equal("integer", await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema='public' "
            + "AND table_name='whole_integer_rows' AND column_name='value';"));
        Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT value FROM whole_integer_rows;"));
    }

    /// <summary>Preserves direct whole-row NULL predicates with exact ordinary or quoted owner identity.</summary>
    /// <param name="notNull">Whether the direct predicate requires non-NULL fields.</param>
    /// <param name="quotedOwner">Whether the native owner needs escaped identifier quoting.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WholeRowNullnessRetainsTheUnchangedNullContract(bool notNull, bool quotedOwner)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var table = quotedOwner ? "Null\"Rows" : "nullable_integer_rows";
        var owner = quotedOwner ? "\"Null\"\"Rows\"" : table;
        var predicate = "(" + owner + ".*) IS " + (notNull ? "NOT NULL" : "NULL");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE " + owner + "(value integer NULL, unrelated integer NULL, CHECK(" + predicate + ")); "
            + "INSERT INTO " + owner + " VALUES(" + (notNull ? "7,9" : "NULL,NULL") + ");");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(table,
            new ExpectedColumnDefinition("value", typeof(long), true, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), true, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-whole-row-nullness"));
        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM " + owner + " WHERE " + predicate + ";"));
        Assert.Equal("bigint", await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema='public' "
            + "AND table_name='" + table + "' AND column_name='value';"));
    }

    /// <summary>Retains a constant-false contract on an empty owner without inventing a row proof.</summary>
    [Fact]
    public async Task EmptyOwnerConstantFalseCheckRemainsUnchanged()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE empty_integer_rows(value integer NOT NULL, CHECK(FALSE));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("empty_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-constant-false"));
        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(report.Assessments).Action);
        Assert.Equal(0, await ScalarIntAsync(connectionString, "SELECT count(*) FROM empty_integer_rows;"));
        Assert.Equal("bigint", await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema='public' "
            + "AND table_name='empty_integer_rows' AND column_name='value';"));
    }

    /// <summary>Checks rebuilt whole-row index keys and predicates against a forced sequential native result.</summary>
    /// <param name="partial">Whether the whole-row expression is the predicate rather than the index key.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeWholeRowIndexRebuildRemainsCoherent(bool partial)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var definition = partial
            ? "CREATE FUNCTION indexed_accepts(indexed_integer_rows) RETURNS boolean IMMUTABLE LANGUAGE plpgsql "
                + "AS $$ BEGIN RETURN pg_typeof($1.value) = 'integer'::regtype; END $$; "
                + "CREATE INDEX ix_whole_native ON indexed_integer_rows(unrelated) "
                + "WHERE indexed_accepts(indexed_integer_rows.*);"
            : "CREATE FUNCTION indexed_size(indexed_integer_rows) RETURNS integer IMMUTABLE LANGUAGE sql "
                + "AS 'SELECT pg_column_size($1)'; CREATE INDEX ix_whole_native ON indexed_integer_rows "
                + "((indexed_size(indexed_integer_rows.*)));";
        var predicate = partial
            ? "indexed_accepts(indexed_integer_rows.*)"
            : "indexed_size(indexed_integer_rows.*) = @expected";

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE indexed_integer_rows(value integer NOT NULL, unrelated integer NOT NULL); "
            + "INSERT INTO indexed_integer_rows VALUES(7,9); " + definition);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("indexed_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-whole-row-index-rebuild"));
        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await using var command = ((NpgsqlConnection)context.Database.GetDbConnection()).CreateCommand();
        if (!partial)
        {
            command.CommandText = "SELECT indexed_size(indexed_integer_rows.*) FROM indexed_integer_rows;";
            command.Parameters.AddWithValue("expected", (int)(await command.ExecuteScalarAsync())!);
        }

        command.CommandText = "SET enable_indexscan=off; SET enable_indexonlyscan=off; "
            + "SET enable_bitmapscan=off; SET enable_seqscan=on;";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT count(*) FROM indexed_integer_rows WHERE " + predicate;
        var sequentialCount = (long)(await command.ExecuteScalarAsync())!;
        command.CommandText = "SET enable_indexscan=on; SET enable_indexonlyscan=on; SET enable_seqscan=off;";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "EXPLAIN SELECT count(*) FROM indexed_integer_rows WHERE " + predicate;
        var usesIndex = false;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                usesIndex |= reader.GetString(0).Contains("ix_whole_native", StringComparison.Ordinal);
            }
        }

        command.CommandText = "SELECT count(*) FROM indexed_integer_rows WHERE " + predicate;
        var indexedCount = (long)(await command.ExecuteScalarAsync())!;

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(report.Assessments).Action);
        Assert.True(usesIndex);
        Assert.Equal(partial ? 0L : 1L, sequentialCount);
        Assert.Equal(sequentialCount, indexedCount);
        Assert.Equal("7:9", await ScalarStringAsync(connectionString,
            "SELECT value::text || ':' || unrelated::text FROM indexed_integer_rows;"));
    }

    /// <summary>Distinguishes column-filter publications from harmless whole-owner membership.</summary>
    [Fact]
    public async Task PublicationFiltersRetainVersionQualifiedColumnDependencies()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var filtered = Fixture.ServerVersion.Major >= 15;
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE published_integer_rows(value integer NOT NULL); "
            + "INSERT INTO published_integer_rows VALUES(7); "
            + "CREATE PUBLICATION integer_values FOR TABLE published_integer_rows"
            + (filtered ? " WHERE (value > 0)" : string.Empty) + ";");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("published_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-publication-column-filter"));
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(filtered ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Repair,
            Assert.Single(report.Assessments).Action);
        if (filtered)
        {
            Assert.Equal("P1001", Assert.IsType<PostgresException>(failure).SqlState);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT value FROM published_integer_rows;"));
    }

}
