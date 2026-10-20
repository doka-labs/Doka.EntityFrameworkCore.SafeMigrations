namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Neither a live nor an accepted index-name clash becomes an absent generated-key index.</summary>
    /// <param name="acceptedPrefix">Whether the collision is created by an earlier accepted operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedIndexRetainsExactNameCollision(bool acceptedPrefix)
    {
        // Arrange
        const string name = "ix_generated_namespace_collision";
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY, "
            + "left_bound bigint NOT NULL, right_bound bigint NOT NULL, unrelated int NULL);");
        if (!acceptedPrefix)
        {
            await ExecuteSqlAsync(connectionString, $"CREATE INDEX `{name}` ON `{Table}` (left_bound);");
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (acceptedPrefix)
        {
            builder.EnsureIndex(new ExpectedIndexDefinition(name, Table,
                [new ExpectedIndexKeyDefinition("left_bound")]), SafeMigrationPolicy.ThrowIfDifferent);
        }

        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition(name, Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-name-collision"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[acceptedPrefix ? 1 : 0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^2].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
    }

    /// <summary>A preceding accepted physical alias retains NoOp identity after generated-index recovery.</summary>
    [Fact]
    public async Task GeneratedIndexRetainsAcceptedSemanticAlias()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY, "
            + "left_bound bigint NOT NULL, right_bound bigint NOT NULL, unrelated int NULL);");
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_generated_physical", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_generated_requested", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-accepted-alias"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-accepted-alias-verify"), CancellationToken.None);

        var physical = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND INDEX_NAME = 'ix_generated_physical';");

        var requested = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND INDEX_NAME = 'ix_generated_requested';");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[3].Action);
        Assert.Null(failure);
        Assert.True(postflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(postflight));
        Assert.Equal(1, physical);
        Assert.Equal(0, requested);
    }

    /// <summary>Required generated source guarantees cannot outlive an accepted nullable transition.</summary>
    /// <param name="sourceName">The required integral operand weakened by the accepted transition.</param>
    [GeneratedMariaDbTheory]
    [InlineData("left_bound")]
    [InlineData("right_bound")]
    public async Task GeneratedIndexCannotBorrowWeakenedSourceGuarantees(string sourceName)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY, "
            + "left_bound bigint NOT NULL, right_bound bigint NOT NULL, unrelated int NULL);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(Table,
            new ExpectedColumnDefinition(sourceName, typeof(long), true, "bigint"),
            new ExpectedColumnDefinition(sourceName, typeof(long), false, "bigint"),
            SafeMigrationPolicy.RepairIfSafe);
        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_weakened_generated_source", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-weakened-source"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^2].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^1].Action);
    }

    /// <summary>An opaque SQL boundary keeps runtime validation instead of certifying old index absence.</summary>
    [Fact]
    public async Task GeneratedIndexAfterOpaqueSqlRetainsRuntimeValidation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY, "
            + "left_bound bigint NOT NULL, right_bound bigint NOT NULL, unrelated int NULL);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.Sql("DO 0;");
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_opaque_generated_source", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-opaque-boundary"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.NotNull(report.Assessments[^1].DeferredOrigin);
    }

    /// <summary>Namespace recovery does not bypass physical key-width or explicit prefix requirements.</summary>
    /// <param name="prefix">The bounded native character prefix, or null for an overlong full key.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(100)]
    public async Task GeneratedIndexRetainsPhysicalPrefixLimits(int? prefix)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY, "
            + "left_bound bigint NOT NULL, right_bound bigint NOT NULL, unrelated int NULL, "
            + "payload varchar(1000) CHARACTER SET utf8mb4 NULL) ENGINE=InnoDB ROW_FORMAT=DYNAMIC;");
        await SeedCaseRowsAsync(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_generated_key_width", Table,
            [new ExpectedIndexKeyDefinition("has_descendants"),
                new ExpectedIndexKeyDefinition("payload", prefixLength: prefix)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-key-width"), CancellationToken.None);

        var failure = prefix is null ? null : await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var postflight = prefix is null ? null
            : await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-index-key-width-verify"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(prefix is null ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(prefix is null ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
        Assert.Null(failure);
        Assert.True(postflight is null || postflight.Status == SafeMigrationReportStatus.Ready,
            postflight is null ? null : DescribeGeneratedReport(postflight));
    }

    /// <summary>A column DROP can expose a new semantic alias by shrinking an existing composite index.</summary>
    [Fact]
    public async Task GeneratedIndexCannotInventAliasAbsenceAfterCompositeKeyShrink()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await ExecuteSqlAsync(connectionString, $"ALTER TABLE `{Table}` ADD unrelated int NULL; "
            + $"CREATE INDEX ix_generated_shrink ON `{Table}` (has_descendants, unrelated);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        // WHY: The DROP needs an already accepted exact generated contract;
        // the later reassertion is not evidence available before that DROP.
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_generated_shrink_requested", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-shrunken-alias"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);
        var requested = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' "
            + "AND INDEX_NAME = 'ix_generated_shrink_requested';");

        var alias = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND INDEX_NAME = 'ix_generated_shrink';");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, preflight.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, preflight.Assessments[^1].Action);
        Assert.Equal(0, requested);
        Assert.Equal(1, alias);
    }

    /// <summary>Exact and semantic index matches retain their independently observed physical identity.</summary>
    /// <param name="alias">Whether the matching native index uses another physical name.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedIndexRetainsObservedNativeMatch(bool alias)
    {
        // Arrange
        const string requested = "ix_generated_existing_requested";
        var actual = alias ? "ix_generated_existing_actual" : requested;
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await ExecuteSqlAsync(connectionString, $"ALTER TABLE `{Table}` ADD unrelated int NULL; "
            + $"CREATE INDEX `{actual}` ON `{Table}` (has_descendants);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        // WHY: Native index matching cannot authorize an unrelated DROP by
        // borrowing a generated-column contract from a later operation.
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition(requested, Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-native-match"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-native-match-verify"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, preflight.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[2].Action);
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[^1].Action);
        Assert.Null(failure);
        Assert.True(postflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(postflight));
    }

    /// <summary>A native index match cannot survive recreation of its generated key member.</summary>
    /// <param name="explicitDrop">Whether an accepted index removal establishes the current namespace.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedIndexCannotReuseNativeMatchAfterMemberRecreation(bool explicitDrop)
    {
        // Arrange
        const string name = "ix_generated_native_lifetime";
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await SeedCaseRowsAsync(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE INDEX `{name}` ON `{Table}` (has_descendants);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (explicitDrop)
        {
            builder.DropIndexIfExists(name, Table);
        }

        builder.DropColumnIfExists("has_descendants", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition(name, Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-native-lifetime"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-native-lifetime-verify"), CancellationToken.None);

        var physical = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND INDEX_NAME = '{name}';");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[explicitDrop ? 1 : 0].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[^2].Action);
        Assert.Equal(explicitDrop ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            preflight.Status);
        Assert.Equal(explicitDrop ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            preflight.Assessments[^1].Action);
        Assert.True(postflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(postflight));
        Assert.Equal(1, physical);
    }

    /// <summary>Accepted index dependencies require explicit removal before a computed member is recreated.</summary>
    /// <param name="explicitDrop">Whether the declared stream explicitly removes the accepted index.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedIndexDoesNotSurviveDroppedAndRecreatedMember(bool explicitDrop)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await SeedCaseRowsAsync(connectionString);
        var definition = new ExpectedIndexDefinition("ix_generated_member_lifetime", Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);
        if (explicitDrop)
        {
            builder.DropIndexIfExists(definition.Name, Table);
        }

        builder.DropColumnIfExists("has_descendants", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);
        SafeMigrationRunReport? preflight = null;

        // Act
        var preflightFailure = await Record.ExceptionAsync(async () => preflight =
            await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-index-recreated-member"), CancellationToken.None));

        var failure = explicitDrop ? await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        }) : null;

        var postflight = explicitDrop
            ? await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-index-recreated-member-verify"), CancellationToken.None) : null;

        var index = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' "
            + "AND INDEX_NAME = 'ix_generated_member_lifetime';");

        // Assert
        if (explicitDrop)
        {
            Assert.Null(preflightFailure);
            Assert.NotNull(preflight);
            Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
            Assert.All(preflight.Assessments,
                static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
            Assert.NotNull(postflight);
            Assert.True(postflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(postflight));
        }
        else
        {
            Assert.Contains("Drop the index explicitly",
                Assert.IsType<InvalidOperationException>(preflightFailure).Message, StringComparison.Ordinal);
            Assert.Null(preflight);
        }

        Assert.Null(failure);
        Assert.Equal(explicitDrop ? 1 : 0, index);
    }

    /// <summary>A live index alias cannot outlive the exact-name precedence of a new native supporting index.</summary>
    /// <param name="acceptedAlias">Whether the alias remains accepted instead of becoming a dropped tombstone.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedIndexCannotReuseNativeAliasAfterImplicitForeignKeyIndexCreation(bool acceptedAlias)
    {
        // Arrange
        const string name = "ix_generated_matching_implicit";
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var initial = new MigrationBuilder(context.Database.ProviderName!);
        initial.EnsureTable(CaseTable(stored: false), SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        await ExecuteOperationsAsync(context, initial.Operations);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_matching_parent(id int NOT NULL PRIMARY KEY); "
            + $"ALTER TABLE `{Table}` ADD parent_id int NULL, ADD unrelated int NULL; "
            + $"CREATE INDEX ix_generated_original_alias ON `{Table}` (has_descendants);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (acceptedAlias)
        {
            builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
            builder.EnsureIndex(new ExpectedIndexDefinition(name, Table,
                [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.DropIndexIfExists("ix_generated_original_alias", Table);
        }

        builder.AddForeignKeyIfNotExists(name, Table, ["parent_id"], "generated_matching_parent", ["id"],
            policy: SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists(acceptedAlias ? "unrelated" : "has_descendants", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition(name, Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-matching-implicit-change"), CancellationToken.None);

        var prefixFailure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations.Take(builder.Operations.Count - 1).ToArray()));

        var supporting = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND INDEX_NAME = '{name}' "
            + "AND COLUMN_NAME = 'parent_id';");

        // Assert
        if (acceptedAlias)
        {
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[3].Action);
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[4].Action);
        }
        else
        {
            Assert.All(report.Assessments.Take(4),
                static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        }

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^1].Action);
        Assert.Null(prefixFailure);
        Assert.Equal(1, supporting);
    }

    /// <summary>An ordinary-index tombstone cannot hide a later implicitly created FK supporting index.</summary>
    /// <param name="existingIndex">Whether the initial DROP applies or is already a native NoOp.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedIndexCannotReuseTombstoneBeforeImplicitForeignKeyIndexCreation(bool existingIndex)
    {
        // Arrange
        const string name = "ix_generated_implicit_collision";
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE generated_index_parent(id int NOT NULL PRIMARY KEY); "
            + $"CREATE TABLE `{Table}` (`id` int NOT NULL PRIMARY KEY, left_bound bigint NOT NULL, "
            + "right_bound bigint NOT NULL, parent_id int NULL, unrelated int NULL);");
        if (existingIndex)
        {
            await ExecuteSqlAsync(connectionString, $"CREATE INDEX `{name}` ON `{Table}` (parent_id);");
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists(name, Table);
        builder.AddForeignKeyIfNotExists(name, Table, ["parent_id"], "generated_index_parent", ["id"],
            policy: SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("unrelated", Table);
        builder.EnsureColumn(Table, CaseColumn(stored: false), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition(name, Table,
            [new ExpectedIndexKeyDefinition("has_descendants")]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-implicit-namespace-change"), CancellationToken.None);

        var prefixFailure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations.Take(builder.Operations.Count - 1).ToArray()));

        var supporting = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
            + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{Table}' AND INDEX_NAME = '{name}' "
            + "AND COLUMN_NAME = 'parent_id';");

        // Assert
        Assert.Equal(existingIndex ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp,
            report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[^1].Action);
        Assert.Null(prefixFailure);
        Assert.Equal(1, supporting);
    }
}
