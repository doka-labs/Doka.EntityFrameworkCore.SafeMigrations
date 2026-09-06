namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task VarcharWidening_RepairsWithoutDataLossAndReplaysAsNoOp()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_widening ("
            + "id integer NOT NULL PRIMARY KEY, "
            + "value character varying(10) NULL DEFAULT 'legacy'); "
            + "COMMENT ON COLUMN varchar_widening.value IS 'legacy'; "
            + "CREATE INDEX ix_varchar_widening_value ON varchar_widening (value); "
            + "CREATE INDEX ix_varchar_widening_value_id ON varchar_widening (value, id); "
            + "INSERT INTO varchar_widening (id, value) VALUES (1, 'preserved');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_widening",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "character varying(200)",
                maxLength: 200,
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("legacy")),
            SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-widening"),
            CancellationToken.None);

        var providerAnalysis = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(
                context,
                builder.Operations.Cast<SafeMigrationOperation>().ToArray(),
                CancellationToken.None);

        var assessment = Assert.Single(preflight.Assessments);
        var providerAssessment = Assert.Single(providerAnalysis);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Repair, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, assessment.OperationalImpact);
        Assert.Contains(assessment.Differences, difference => difference.Facet == "column_max_length");
        Assert.Contains(assessment.Differences, difference => difference.Facet == "column_comment_digest");
        Assert.False(providerAssessment.RequiresLiveDataProof);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-widening"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-widening-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(replay.Assessments).ObservedState);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM varchar_widening WHERE id = 1 AND value = 'preserved';"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_indexes "
                + "WHERE schemaname = current_schema() AND tablename = 'varchar_widening' "
                + "AND indexname IN ('ix_varchar_widening_value', 'ix_varchar_widening_value_id');"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public async Task VarcharRepair_PreservesCompatibleForeignKeyDependencies(
        int targetLength
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_fk_parent ("
            + "code character varying(10) NOT NULL PRIMARY KEY); "
            + "CREATE TABLE varchar_fk_child ("
            + "id integer NOT NULL PRIMARY KEY, parent_code character varying(10) NOT NULL, "
            + "CONSTRAINT fk_varchar_fk_child_parent FOREIGN KEY (parent_code) "
            + "REFERENCES varchar_fk_parent (code)); "
            + "INSERT INTO varchar_fk_parent (code) VALUES ('core'); "
            + "INSERT INTO varchar_fk_child (id, parent_code) VALUES (1, 'core');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_fk_parent",
            new ExpectedColumnDefinition(
                "code",
                typeof(string),
                isNullable: false,
                storeType: $"character varying({targetLength})",
                maxLength: targetLength),
            SafeMigrationPolicy.RepairIfSafe);
        builder.EnsureColumn(
            "varchar_fk_child",
            new ExpectedColumnDefinition(
                "parent_code",
                typeof(string),
                isNullable: false,
                storeType: $"character varying({targetLength})",
                maxLength: targetLength),
            SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-foreign-key-compatible"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, static assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.Repair, assessment.Action);
        });

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-foreign-key-compatible"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() "
                + "AND table_name IN ('varchar_fk_parent', 'varchar_fk_child') "
                + "AND column_name IN ('code', 'parent_code') "
                + $"AND character_maximum_length = {targetLength};"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint "
                + "WHERE conname = 'fk_varchar_fk_child_parent' AND contype = 'f';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM varchar_fk_child child "
                + "JOIN varchar_fk_parent parent ON parent.code = child.parent_code "
                + "WHERE child.id = 1 AND child.parent_code = 'core';"));
    }

    [Fact]
    public async Task VarcharWidening_TreatsExplicitNoneValueGenerationAsOrdinaryMetadata()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_none_generation ("
            + "id integer NOT NULL PRIMARY KEY, value character varying(10) NULL);");

        var column = new AddColumnOperation
        {
            Table = "varchar_none_generation",
            Name = "value",
            ClrType = typeof(string),
            ColumnType = "character varying(20)",
            IsNullable = true,
            MaxLength = 20,
        };

        column["Npgsql:ValueGenerationStrategy"] = NpgsqlValueGenerationStrategy.None;

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_none_generation",
            SafeMigrationExpectedDefinitionFactory.From(column),
            SafeMigrationPolicy.RepairIfSafe);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-none-generation"),
            CancellationToken.None);

        var assessment = Assert.Single(preflight.Assessments);

        Assert.True(
            preflight.Status == SafeMigrationReportStatus.Ready,
            $"Expected a repairable explicit-None column but observed "
                + $"{assessment.ObservedState}/{assessment.Action}: "
                + string.Join(
                    ", ",
                    assessment.Differences.Select(static difference =>
                        $"{difference.Facet}={difference.Actual}->{difference.Expected}")));
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Repair, assessment.Action);
        Assert.DoesNotContain(
            assessment.Differences,
            static difference => difference.Facet is "column_default_kind" or "column_value_generation");

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-none-generation-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(replay.Assessments).ObservedState);
    }

    [Fact]
    public async Task VarcharNarrowing_UsesCharacterSemanticsAndPreservesTrailingSpaces()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_narrowing ("
            + "id integer NOT NULL PRIMARY KEY, "
            + "unicode_value character varying(40) NULL, "
            + "trailing_value character varying(40) NULL, "
            + "empty_value character varying(40) NULL, "
            + "required_value character varying(40) NOT NULL); "
            + "CREATE TABLE varchar_narrowing_single ("
            + "id integer NOT NULL PRIMARY KEY, value character varying(40) NULL); "
            + "INSERT INTO varchar_narrowing "
            + "(id, unicode_value, trailing_value, empty_value, required_value) "
            + "VALUES (1, repeat(convert_from(decode('F09F9880', 'hex'), 'UTF8'), 5), "
            + "'a' || repeat(' ', 3), NULL, 'exact'); "
            + "INSERT INTO varchar_narrowing_single (id, value) VALUES (1, 'fits');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        (string Name, bool IsNullable)[] columns =
        [
            ("unicode_value", true),
            ("trailing_value", true),
            ("empty_value", true),
            ("required_value", false),
        ];

        foreach (var column in columns)
        {
            builder.EnsureColumn(
                "varchar_narrowing",
                new ExpectedColumnDefinition(
                    column.Name,
                    typeof(string),
                    column.IsNullable,
                    storeType: "character varying(5)",
                    maxLength: 5),
                SafeMigrationPolicy.RepairIfSafe);
        }

        var singleBuilder = new MigrationBuilder(context.Database.ProviderName!);
        singleBuilder.EnsureColumn(
            "varchar_narrowing_single",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "character varying(5)",
                maxLength: 5),
            SafeMigrationPolicy.RepairIfSafe);

        var singleCommandCount = await CountProviderAnalysisCommandsAsync(
            connectionString,
            singleBuilder.Operations);

        var groupedCommandCount = await CountProviderAnalysisCommandsAsync(
            connectionString,
            builder.Operations);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-narrowing"),
            CancellationToken.None);

        var providerAnalysis = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(
                context,
                builder.Operations.Cast<SafeMigrationOperation>().ToArray(),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(
            preflight.Assessments,
            static assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.Repair, assessment.Action);
                Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, assessment.OperationalImpact);
            });
        Assert.All(providerAnalysis, static analysis => Assert.True(analysis.RequiresLiveDataProof));

        // WHY: All catalog statements are batched identically, so equal
        // command counts prove that four columns add only one table scan.
        Assert.Equal(singleCommandCount, groupedCommandCount);
        Assert.InRange(groupedCommandCount, 1, 8);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-narrowing"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-narrowing-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            5,
            await ScalarIntAsync(
                connectionString,
                "SELECT char_length(unicode_value) FROM varchar_narrowing WHERE id = 1;"));
        Assert.Equal(
            4,
            await ScalarIntAsync(
                connectionString,
                "SELECT char_length(trailing_value) FROM varchar_narrowing WHERE id = 1;"));
        Assert.Equal(
            5,
            await ScalarIntAsync(
                connectionString,
                "SELECT char_length(required_value) FROM varchar_narrowing WHERE id = 1;"));
    }

    [Fact]
    public async Task VarcharNarrowing_FromUnboundedSource_RepairsWhenDataFits()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_unbounded_repair ("
            + "id integer NOT NULL PRIMARY KEY, value character varying NULL); "
            + "INSERT INTO varchar_unbounded_repair (id, value) VALUES (1, 'fits');");

        await using var context = CreateContext(connectionString);
        var builder = BuildVarcharNarrowingOperation(
            context.Database.ProviderName!,
            "varchar_unbounded_repair");

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-unbounded-repair"),
            CancellationToken.None);

        var providerAnalysis = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(
                context,
                builder.Operations.Cast<SafeMigrationOperation>().ToArray(),
                CancellationToken.None);

        var assessment = Assert.Single(preflight.Assessments);
        var lengthDifference = Assert.Single(
            assessment.Differences,
            static difference => difference.Facet == "column_max_length");

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Repair, assessment.Action);
        Assert.Equal("5", lengthDifference.Expected);
        Assert.Equal("unbounded", lengthDifference.Actual);
        Assert.True(Assert.Single(providerAnalysis).RequiresLiveDataProof);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-unbounded-repair"),
            CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("varchar-unbounded-repair-replay"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(
            5,
            await ScalarIntAsync(
                connectionString,
                "SELECT character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'varchar_unbounded_repair' "
                + "AND column_name = 'value';"));
        Assert.Equal(
            "fits",
            await ScalarStringAsync(
                connectionString,
                "SELECT value FROM varchar_unbounded_repair WHERE id = 1;"));
    }

    [Fact]
    public async Task VarcharNarrowing_FromUnboundedSource_RejectsOverlengthDataBeforeDdl()
    {
        const string privateValue = "private-overlength-value";

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_unbounded_blocked ("
            + "id integer NOT NULL PRIMARY KEY, value character varying NULL); "
            + "INSERT INTO varchar_unbounded_blocked (id, value) "
            + $"VALUES (1, 'fits'), (2, '{privateValue}');");

        await using var context = CreateContext(connectionString);
        var builder = BuildVarcharNarrowingOperation(
            context.Database.ProviderName!,
            "varchar_unbounded_blocked");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-unbounded-blocked"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);
        var exception = Assert.Throws<SafeMigrationPreflightException>(report.ThrowIfBlocked);
        var lengthDifference = Assert.Single(
            assessment.Differences,
            static difference => difference.Facet == "column_max_length");

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);
        Assert.Equal("varchar_narrowing_value_too_long", assessment.AnalysisCode);
        Assert.Equal("5", lengthDifference.Expected);
        Assert.Equal("unbounded", lengthDifference.Actual);
        Assert.DoesNotContain(privateValue, exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            "unbounded",
            await ScalarStringAsync(
                connectionString,
                "SELECT COALESCE(character_maximum_length::text, 'unbounded') "
                + "FROM information_schema.columns WHERE table_schema = current_schema() "
                + "AND table_name = 'varchar_unbounded_blocked' AND column_name = 'value';"));
    }

    [Fact]
    public async Task VarcharNarrowing_RejectsOverlengthDataWithoutDisclosingTheValue()
    {
        const string privateValue = "private-overlength-value";

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_narrowing_blocked ("
            + "id integer NOT NULL PRIMARY KEY, value character varying(40) NULL); "
            + "INSERT INTO varchar_narrowing_blocked (id, value) "
            + $"VALUES (1, 'fits'), (2, 'exact'), (3, '{privateValue}');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_narrowing_blocked",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "character varying(5)",
                maxLength: 5),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-blocked"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);
        var exception = Assert.Throws<SafeMigrationPreflightException>(report.ThrowIfBlocked);

        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);
        Assert.Equal("varchar_narrowing_value_too_long", assessment.AnalysisCode);
        Assert.Equal("data_blocked", assessment.DecisionCode);
        Assert.Same(report, exception.Report);
        Assert.DoesNotContain(privateValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            assessment.Differences,
            difference => difference.Expected.Contains(privateValue, StringComparison.Ordinal)
                || difference.Actual.Contains(privateValue, StringComparison.Ordinal));
        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'varchar_narrowing_blocked' "
                + "AND column_name = 'value';"));
    }

    [Fact]
    public async Task VarcharNarrowing_RechecksDataImmediatelyBeforeMutation()
    {
        const string concurrentValue = "arrived-after-preflight";

        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_narrowing_race ("
            + "id integer NOT NULL PRIMARY KEY, value character varying(40) NULL); "
            + "INSERT INTO varchar_narrowing_race (id, value) VALUES (1, 'fits');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "varchar_narrowing_race",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "character varying(5)",
                maxLength: 5),
            SafeMigrationPolicy.RepairIfSafe);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-race"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        await ExecuteSqlAsync(
            connectionString,
            $"INSERT INTO varchar_narrowing_race (id, value) VALUES (2, '{concurrentValue}');");

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        Assert.Equal("P1003", exception.SqlState);
        Assert.Equal("doka_sm_data_blocked", exception.MessageText);
        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'varchar_narrowing_race' "
                + "AND column_name = 'value';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM varchar_narrowing_race WHERE value = '{concurrentValue}';"));
    }

    [Fact]
    public async Task VarcharNarrowing_LocksOutConcurrentWritersBeforeTheFinalProof()
    {
        const string concurrentValue = "committed-while-repair-waited";
        using var testCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var connectionString = await Fixture.CreateDatabaseAsync(testCancellation.Token);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_narrowing_locked_recheck ("
            + "id integer NOT NULL PRIMARY KEY, value character varying(40) NULL); "
            + "INSERT INTO varchar_narrowing_locked_recheck (id, value) VALUES (1, 'fits');");

        await using var context = CreateContext(connectionString);
        var builder = BuildVarcharNarrowingOperation(
            context.Database.ProviderName!,
            "varchar_narrowing_locked_recheck");

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-locked-recheck"),
                testCancellation.Token);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync(testCancellation.Token);
        await using var transaction = await writer.BeginTransactionAsync(testCancellation.Token);
        await using (var insert = writer.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO varchar_narrowing_locked_recheck (id, value) "
                + "VALUES (2, @value);";
            insert.Parameters.AddWithValue("value", concurrentValue);

            _ = await insert.ExecuteNonQueryAsync(testCancellation.Token);
        }

        // WHY: Run the guarded DDL on the thread pool so the test does not
        // serialize its observer and blocked command through xUnit's context.
        var execution = Task.Run(
            () => ExecuteOperationsAsync(
                context,
                builder.Operations,
                testCancellation.Token),
            CancellationToken.None);

        await WaitForBlockedPostgreSqlNarrowingCommandAsync(
            connectionString,
            "varchar_narrowing_locked_recheck",
            testCancellation.Token);

        await transaction.CommitAsync(testCancellation.Token);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => execution);

        Assert.Equal("P1003", exception.SqlState);
        Assert.Equal("doka_sm_data_blocked", exception.MessageText);
        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'varchar_narrowing_locked_recheck' "
                + "AND column_name = 'value';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM varchar_narrowing_locked_recheck "
                + $"WHERE value = '{concurrentValue}';"));
    }

    [Fact]
    public async Task VarcharNarrowing_PropagatesProbeCommandTimeoutWithoutMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_narrowing_timeout ("
            + "id integer NOT NULL PRIMARY KEY, value character varying(40) NULL); "
            + "INSERT INTO varchar_narrowing_timeout (id, value) VALUES (1, 'fits');");

        await using var blocker = new NpgsqlConnection(connectionString);
        await blocker.OpenAsync(CancellationToken.None);
        await using var transaction = await blocker.BeginTransactionAsync(CancellationToken.None);
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "LOCK TABLE varchar_narrowing_timeout IN ACCESS EXCLUSIVE MODE;";

            _ = await lockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(1);
        var builder = BuildVarcharNarrowingOperation(
            context.Database.ProviderName!,
            "varchar_narrowing_timeout");

        _ = await Assert.ThrowsAsync<NpgsqlException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-timeout"),
                CancellationToken.None));

        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'varchar_narrowing_timeout' "
                + "AND column_name = 'value';"));
    }

    [Fact]
    public async Task VarcharNarrowing_PropagatesProbeCancellationWithoutMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE varchar_narrowing_cancellation ("
            + "id integer NOT NULL PRIMARY KEY, value character varying(40) NULL); "
            + "INSERT INTO varchar_narrowing_cancellation (id, value) VALUES (1, 'fits');");

        await using var blocker = new NpgsqlConnection(connectionString);
        await blocker.OpenAsync(CancellationToken.None);
        await using var transaction = await blocker.BeginTransactionAsync(CancellationToken.None);
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "LOCK TABLE varchar_narrowing_cancellation IN ACCESS EXCLUSIVE MODE;";

            _ = await lockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var context = CreateContext(connectionString);
        var builder = BuildVarcharNarrowingOperation(
            context.Database.ProviderName!,
            "varchar_narrowing_cancellation");

        using var cancellation = new CancellationTokenSource();
        var analysis = context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("varchar-narrowing-cancellation"),
                cancellation.Token);

        await WaitForBlockedPostgreSqlNarrowingCommandAsync(
            connectionString,
            "varchar_narrowing_cancellation",
            CancellationToken.None);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);

        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                "SELECT character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'varchar_narrowing_cancellation' "
                + "AND column_name = 'value';"));
    }

    [Fact]
    public async Task MySqlBooleanRepresentationTransition_IsExplicitlyNotApplicable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE boolean_transition (value bit(1) NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "boolean_transition",
            new ExpectedColumnDefinition(
                "value",
                typeof(bool),
                isNullable: true,
                storeType: "boolean"),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("boolean-transition"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, assessment.OperationalImpact);
        Assert.Equal(
            "bit(1)",
            await ScalarStringAsync(
                connectionString,
                "SELECT pg_catalog.format_type(a.atttypid, a.atttypmod) "
                + "FROM pg_catalog.pg_attribute a "
                + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
                + "WHERE c.relname = 'boolean_transition' AND a.attname = 'value';"));
    }

    private static async Task<int> CountProviderAnalysisCommandsAsync(
        string connectionString,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        await using var countingConnection = new CountingDbConnection(
            new NpgsqlConnection(connectionString));

        await using var context = CreateContext(connectionString);
        context.Database.SetDbConnection(countingConnection, contextOwnsConnection: false);
        var safeOperations = operations
            .Cast<SafeMigrationOperation>()
            .ToArray();

        _ = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(context, safeOperations, CancellationToken.None);

        return countingConnection.CommandCount;
    }

    private static MigrationBuilder BuildVarcharNarrowingOperation(
        string providerName,
        string table
    )
    {
        var builder = new MigrationBuilder(providerName);
        builder.EnsureColumn(
            table,
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "character varying(5)",
                maxLength: 5),
            SafeMigrationPolicy.RepairIfSafe);

        return builder;
    }

    private static async Task WaitForBlockedPostgreSqlNarrowingCommandAsync(
        string connectionString,
        string table,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity "
                + "WHERE pid <> pg_backend_pid() AND query LIKE @probe AND wait_event_type = 'Lock');";

            // WHY: PostgreSQL truncates the recorded query text to
            // track_activity_query_size. The target table occurs at the start
            // of the guarded command and remains a stable lock identifier.
            command.Parameters.AddWithValue("probe", $"%{table}%");

            var blocked = Convert.ToBoolean(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (blocked)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new TimeoutException("The PostgreSQL narrowing repair did not reach its blocked table lock.");
    }

    private sealed class CountingDbConnection(
        NpgsqlConnection connection
    ) : DbConnection
    {
        private int _commandCount;

        public int CommandCount => Volatile.Read(ref _commandCount);

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString
        {
            get => connection.ConnectionString;
            set => connection.ConnectionString = value ?? string.Empty;
        }

        public override string Database => connection.Database;

        public override string DataSource => connection.DataSource;

        public override string ServerVersion => connection.ServerVersion;

        public override ConnectionState State => connection.State;

        public override void ChangeDatabase(
            string databaseName
        ) => connection.ChangeDatabase(databaseName);

        public override void Close() => connection.Close();

        public override void Open() => connection.Open();

        public override Task OpenAsync(
            CancellationToken cancellationToken
        ) => connection.OpenAsync(cancellationToken);

        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => connection.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand()
        {
            _ = Interlocked.Increment(ref _commandCount);

            return connection.CreateCommand();
        }

        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing)
            {
                connection.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
