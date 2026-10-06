namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Preserves values, indexes and schema identity through a length transition and replay.</summary>
    /// <param name="sourceLength">The current VARCHAR limit.</param>
    /// <param name="targetLength">The requested VARCHAR limit.</param>
    /// <param name="alias">Whether the public contract uses the VARCHAR alias.</param>
    [Theory]
    [InlineData(10, 40, false)]
    [InlineData(40, 10, true)]
    [InlineData(null, 10, true)]
    public async Task AlterTransition_LengthChangePreservesDataAndReplays(
        int? sourceLength,
        int targetLength,
        bool alias
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var liveType = sourceLength is null ? "character varying" : $"character varying({sourceLength})";
        await ExecuteSqlAsync(connectionString,
            "CREATE SCHEMA alter_scope; CREATE TABLE alter_rows (value integer); "
            + $"CREATE TABLE alter_scope.alter_rows (value {liveType} NULL); "
            + "CREATE INDEX ix_alter_rows_value ON alter_scope.alter_rows (value); "
            + "INSERT INTO alter_scope.alter_rows VALUES ('preserved');");

        await using var context = CreateContext(connectionString);
        var storeType = alias ? "varchar" : "character varying";
        var oldType = sourceLength is null ? storeType : $"{storeType}({sourceLength})";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, $"{storeType}({targetLength})",
                maxLength: targetLength, comment: "canonical", defaultValue: SafeMigrationDefaultValue.Literal("next")),
            new ExpectedColumnDefinition("value", typeof(string), true, oldType,
                maxLength: sourceLength), SafeMigrationPolicy.RepairIfSafe, schema: "alter_scope");

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("alter-transition"));

        var provider = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context,
            builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("alter-transition-replay"));

        var length = await ScalarIntAsync(connectionString,
            "SELECT character_maximum_length FROM information_schema.columns "
            + "WHERE table_schema = 'alter_scope' AND table_name = 'alter_rows' AND column_name = 'value';");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM alter_scope.alter_rows WHERE value = 'preserved';");

        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE schemaname = 'alter_scope' "
            + "AND indexname = 'ix_alter_rows_value';");

        // Assert
        var assessment = Assert.Single(preflight.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Repair, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, assessment.OperationalImpact);
        Assert.Contains(assessment.Differences, static difference => difference.Facet == "column_max_length");
        Assert.Equal(sourceLength is null || targetLength < sourceLength,
            Assert.Single(provider).RequiresLiveDataProof);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(targetLength, length);
        Assert.Equal(1, rows);
        Assert.Equal(1, indexes);
    }

    /// <summary>Rejects overlong values without executing a potentially truncating ALTER.</summary>
    /// <param name="value">The overlong value, including significant trailing spaces.</param>
    [Theory]
    [InlineData("longer-than-ten")]
    [InlineData("fits       ")]
    public async Task AlterTransition_OverlongValueBlocksWithoutMutation(
        string value
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE alter_rows (value character varying(40) NULL); "
            + $"INSERT INTO alter_rows VALUES ('{value}');");

        await using var context = CreateContext(connectionString);
        var operation = AlterLengthOperation(40, 10);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("alter-transition-overlong"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var preserved = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM alter_rows WHERE value = '{value}';");

        var length = await ReadAlterLengthAsync(connectionString);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal("varchar_narrowing_value_too_long", assessment.AnalysisCode);
        Assert.Equal("P1003", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(1, preserved);
        Assert.Equal(40, length);
    }

    /// <summary>Does not borrow transition eligibility from another operation's old or target definition.</summary>
    /// <param name="invalidFirst">Whether the ineligible operation enters the shared cache first.</param>
    /// <param name="overlong">Whether the shared row fact blocks the eligible narrowing.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AlterTransition_SharedRowProofKeepsOldDefinitionsIndependent(
        bool invalidFirst,
        bool overlong
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE alter_rows (value character varying(40) NULL); "
            + $"INSERT INTO alter_rows VALUES ('{(overlong ? "longer-than-ten" : "fits")}');");

        await using var context = CreateContext(connectionString);
        var valid = AlterLengthOperation(40, 10);
        var invalid = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, "character varying(10)"),
            new ExpectedColumnDefinition("value", typeof(string), true, "character varying(40)",
                comment: "not-the-live-source")), SafeMigrationPolicy.RepairIfSafe);

        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, "character varying(10)")),
            SafeMigrationPolicy.RepairIfSafe);

        SafeMigrationOperation[] operations = invalidFirst ? [invalid, valid, ensure] : [valid, invalid, ensure];

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, operations);
        var invalidException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [invalid]));
        var length = await ReadAlterLengthAsync(connectionString);

        // Assert
        var validAnalysis = analyses[invalidFirst ? 1 : 0];
        var invalidAnalysis = analyses[invalidFirst ? 0 : 1];
        Assert.Equal(overlong ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            validAnalysis.ObservedState);
        Assert.True(validAnalysis.RequiresLiveDataProof);
        Assert.Equal(overlong ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            validAnalysis.RepairCapability);
        Assert.Equal(SafeMigrationObservedState.Different, invalidAnalysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, invalidAnalysis.RepairCapability);
        Assert.False(invalidAnalysis.RequiresLiveDataProof);
        Assert.Equal(validAnalysis.ObservedState, analyses[2].ObservedState);
        Assert.Equal("P1001", Assert.IsType<PostgresException>(invalidException).SqlState);
        Assert.Equal(40, length);
    }

    /// <summary>Requires all declared old facets and the explicitly requested repair policy.</summary>
    /// <param name="drift">The mismatching source facet or excluded policy.</param>
    [Theory]
    [InlineData("length")]
    [InlineData("default")]
    [InlineData("nullable")]
    [InlineData("collation")]
    [InlineData("strict")]
    [InlineData("text")]
    [InlineData("missing-old")]
    public async Task AlterTransition_SourceOrPolicyMismatchFailsClosed(
        string drift
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var liveType = drift == "text" ? "text" : "character varying(40)";
        var collation = drift == "collation" ? " COLLATE \"C\"" : string.Empty;
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE alter_rows (value {liveType}{collation} NULL); INSERT INTO alter_rows VALUES ('fits');");

        await using var context = CreateContext(connectionString);
        var sourceType = drift == "length" ? "character varying(30)" : liveType;
        var source = drift == "missing-old"
            ? null
            : new ExpectedColumnDefinition("value", typeof(string), drift != "nullable", sourceType,
                defaultValue: drift == "default" ? SafeMigrationDefaultValue.Literal("other") : null);

        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, "character varying(10)"), source),
            drift == "strict" ? SafeMigrationPolicy.ThrowIfDifferent : SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("alter-transition-source"));

        var provider = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM alter_rows WHERE value = 'fits';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.False(Assert.Single(provider).RequiresLiveDataProof);
        Assert.Equal("P1001", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(1, rows);
    }

    /// <summary>Keeps PostgreSQL's no-backfill nullability policy during length changes.</summary>
    /// <param name="containsNull">Whether a NULL row must prevent tightening.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTransition_NullabilityUsesFreshRowsDespiteDefault(
        bool containsNull
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE alter_rows (value character varying(10) NULL); "
            + $"INSERT INTO alter_rows VALUES ({(containsNull ? "NULL" : "'fits'")});");

        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), false, "character varying(40)",
                defaultValue: SafeMigrationDefaultValue.Literal("fallback")),
            new ExpectedColumnDefinition("value", typeof(string), true, "character varying(10)")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var provider = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var nullRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM alter_rows WHERE value IS NULL;");
        var length = await ReadAlterLengthAsync(connectionString);

        // Assert
        var analysis = Assert.Single(provider);
        Assert.True(analysis.RequiresLiveDataProof);
        Assert.Equal(containsNull ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            analysis.ObservedState);
        if (containsNull)
        {
            Assert.Equal("P1003", Assert.IsType<PostgresException>(exception).SqlState);
        }
        else
        {
            Assert.Null(exception);
        }

        Assert.Equal(containsNull ? 1 : 0, nullRows);
        Assert.Equal(containsNull ? 10 : 40, length);
    }

    /// <summary>Does not create a missing alteration target or modify an already matching target.</summary>
    /// <param name="exists">Whether the column already matches the requested target.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTransition_MissingRejectsAndMatchingReplays(
        bool exists
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            exists ? "CREATE TABLE alter_rows (value character varying(10) NULL);"
                : "CREATE TABLE alter_rows (id integer);");

        await using var context = CreateContext(connectionString);
        var operation = AlterLengthOperation(40, 10);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("alter-transition-existence"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() "
            + "AND table_name = 'alter_rows' AND column_name = 'value';");

        // Assert
        Assert.Equal(exists ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent,
            Assert.Single(report.Assessments).Action);
        if (exists)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Equal("P1001", Assert.IsType<PostgresException>(exception).SqlState);
        }

        Assert.Equal(exists ? 1 : 0, columns);
    }

    /// <summary>Leaves the column and dependent view untouched when PostgreSQL rejects dependent DDL.</summary>
    [Fact]
    public async Task AlterTransition_DependentViewFailureIsAtomic()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE alter_rows (value character varying(10) NULL); "
            + "INSERT INTO alter_rows VALUES ('preserved'); CREATE VIEW alter_view AS SELECT value FROM alter_rows;");

        await using var context = CreateContext(connectionString);
        var operation = AlterLengthOperation(10, 40);

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var length = await ReadAlterLengthAsync(connectionString);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM alter_view WHERE value = 'preserved';");

        // Assert
        Assert.Equal("0A000", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(10, length);
        Assert.Equal(1, rows);
    }

    /// <summary>Uses character counts instead of UTF-8 byte counts for narrowing evidence.</summary>
    [Fact]
    public async Task AlterTransition_MultibyteCharactersFitWithoutTruncation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE alter_rows (value character varying(40) NULL); "
            + "INSERT INTO alter_rows VALUES (repeat(chr(233), 9));");

        await using var context = CreateContext(connectionString);
        var operation = AlterLengthOperation(40, 10);

        // Act
        var analysis = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM alter_rows WHERE value = repeat(chr(233), 9) AND octet_length(value) = 18;");

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, Assert.Single(analysis).RepairCapability);
        Assert.Equal(1, rows);
    }

    /// <summary>Rechecks length facts after an earlier opaque write or a post-analysis data change.</summary>
    /// <param name="opaque">Whether the earlier write is part of the analyzed operation sequence.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlterTransition_RuntimeRejectsNewOverlongRowsAfterAnalysis(
        bool opaque
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE alter_rows (value character varying(40) NULL); INSERT INTO alter_rows VALUES ('fits');");

        await using var context = CreateContext(connectionString);
        var operation = AlterLengthOperation(40, 10);
        const string mutation = "UPDATE alter_rows SET value = 'longer-than-ten';";
        MigrationOperation[] operations = opaque ? [new SqlOperation { Sql = mutation }, operation] : [operation];

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("alter-transition-fresh-proof"));

        if (!opaque)
        {
            await ExecuteSqlAsync(connectionString, mutation);
        }

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var length = await ReadAlterLengthAsync(connectionString);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM alter_rows WHERE value = 'longer-than-ten';");

        // Assert
        Assert.Equal(opaque ? SafeMigrationReportStatus.RuntimeValidationRequired : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(opaque ? SafeMigrationAction.ValidateAtRuntime : SafeMigrationAction.Repair,
            report.Assessments[^1].Action);
        Assert.Equal("P1003", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(40, length);
        Assert.Equal(1, rows);
    }

    /// <summary>Uses provider evidence after an exact accepted Ensure or creation of an empty table.</summary>
    /// <param name="createTable">Whether the source is a newly created empty table.</param>
    /// <param name="sourceLength">The accepted old column limit.</param>
    /// <param name="targetLength">The requested new column limit.</param>
    [Theory]
    [InlineData(false, 10, 40)]
    [InlineData(false, 40, 10)]
    [InlineData(true, 10, 40)]
    [InlineData(true, 40, 10)]
    public async Task AlterTransition_OrderedExactSourceRetainsProviderQualification(
        bool createTable,
        int sourceLength,
        int targetLength
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        if (!createTable)
        {
            await ExecuteSqlAsync(connectionString,
                $"CREATE TABLE alter_rows (value character varying({sourceLength}) NULL); "
                + "INSERT INTO alter_rows VALUES ('fits');");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var source = new ExpectedColumnDefinition("value", typeof(string), true,
            $"character varying({sourceLength})", maxLength: sourceLength);

        if (createTable)
        {
            builder.EnsureTable(new ExpectedTableDefinition("alter_rows", [source]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureColumn("alter_rows", source, SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.AlterColumnIfDifferent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, $"character varying({targetLength})",
                maxLength: targetLength), source, SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("alter-transition-ordered-source"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var length = await ReadAlterLengthAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[1].Action);
        Assert.Equal(targetLength, length);
    }

    /// <summary>Does not promote an empty table's unsupported source family into provider repair authority.</summary>
    [Fact]
    public async Task AlterTransition_NewEmptyTableStillRejectsTextSource()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var source = new ExpectedColumnDefinition("value", typeof(string), true, "text");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("alter_rows", [source]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.AlterColumnIfDifferent("alter_rows",
            new ExpectedColumnDefinition("value", typeof(string), true, "character varying(10)"),
            source, SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("alter-transition-empty-unsupported"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
    }

    /// <summary>Does not claim repair from a rename destination that has no captured PostgreSQL source proof.</summary>
    [Fact]
    public async Task AlterTransition_RenamedExistingTableDoesNotBorrowUnboundEvidence()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE source_rows (value character varying(40) NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("source_rows", "alter_rows");
        builder.Operations.Add(AlterLengthOperation(40, 10));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("alter-transition-unbound-rename"));

        // Assert
        Assert.NotEqual(SafeMigrationReportStatus.Ready, report.Status);
        Assert.NotEqual(SafeMigrationAction.Repair, report.Assessments[1].Action);
    }

    /// <summary>Creates an explicit public alteration contract without application-specific metadata.</summary>
    private static SafeMigrationOperation AlterLengthOperation(
        int sourceLength,
        int targetLength
    ) => new(new AlterColumnIntent("alter_rows",
        new ExpectedColumnDefinition("value", typeof(string), true, $"character varying({targetLength})",
            maxLength: targetLength),
        new ExpectedColumnDefinition("value", typeof(string), true, $"character varying({sourceLength})",
            maxLength: sourceLength)), SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Reads the physical limit after a successful or rejected operation.</summary>
    private static Task<int> ReadAlterLengthAsync(
        string connectionString
    ) => ScalarIntAsync(connectionString,
        "SELECT character_maximum_length FROM information_schema.columns WHERE table_schema = current_schema() "
        + "AND table_name = 'alter_rows' AND column_name = 'value';");
}
