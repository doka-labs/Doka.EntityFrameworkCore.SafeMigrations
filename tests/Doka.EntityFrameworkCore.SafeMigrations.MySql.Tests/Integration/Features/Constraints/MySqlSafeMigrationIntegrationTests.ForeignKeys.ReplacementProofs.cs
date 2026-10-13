namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Replacement certificates preserve exact ENUM and SET member domains without reading rows.</summary>
    /// <param name="useSetStorage">Whether the endpoint storage uses SET rather than ENUM.</param>
    /// <param name="matchingDomain">Whether member literal case matches the principal's exact domain.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForeignKeyReplacementProofEnumDomainsRequireExactCatalogStorage(
        bool useSetStorage,
        bool matchingDomain
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var keyword = useSetStorage ? "set" : "enum";
        var principalType = keyword + "('Alpha','Beta')";
        var dependentType = keyword + (matchingDomain ? "('Alpha','Beta')" : "('alpha','Beta')");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `fk_enum_principal` (`id` " + principalType + " NOT NULL, "
            + "UNIQUE KEY `uq_enum_principal_id` (`id`)) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin; "
            + "CREATE TABLE `fk_enum_dependent` (`key` int NOT NULL PRIMARY KEY, `owner_id` "
            + dependentType + " NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;");

        await using var context = CreateContext(connectionString);
        var catalog = new MySqlSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var definition = new ExpectedForeignKeyDefinition("fk_enum_certificate", "fk_enum_dependent", ["owner_id"],
            "fk_enum_principal", ["id"], onDelete: ReferentialAction.SetNull);

        // WHY: This certifies exact replacement storage, not every FK shape
        // the engine may permit. No FK or application rows are created, and
        // sourceExists:false isolates the catalog phase from orphan scanning.
        var sql = catalog.BuildForeignKeyReplacementProof(definition, sourceExists: false);

        // Act
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var hasRow = await reader.ReadAsync(CancellationToken.None);
        var eligible = hasRow && reader.GetBoolean(0);
        var nullableMask = hasRow ? Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture) : 0;
        var aliasName = hasRow && !reader.IsDBNull(2) ? reader.GetString(2) : null;
        var extraRow = await reader.ReadAsync(CancellationToken.None);

        // Assert
        Assert.True(hasRow);
        Assert.False(extraRow);
        Assert.Equal(matchingDomain, eligible);
        Assert.Equal(1L, nullableMask);
        Assert.Null(aliasName);
        Assert.DoesNotContain("FROM `fk_enum_dependent`", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JOIN `fk_enum_principal`", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Retains independently captured row proof for either nullable referential action.</summary>
    /// <param name="updateAction">Whether SET NULL belongs to the update rather than delete action.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyReplacementProofPreservesNullableActionChanges(bool updateAction)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"],
            onUpdate: updateAction ? ReferentialAction.SetNull : ReferentialAction.NoAction,
            onDelete: updateAction ? ReferentialAction.NoAction : ReferentialAction.SetNull);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-action"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-action-final"), CancellationToken.None);

        await ExecuteSqlAsync(connectionString, updateAction
            ? "UPDATE `proof_parent` SET `id` = '00000000-0000-0000-0000-000000000002';"
            : "DELETE FROM `proof_parent`;");
        var nullRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `proof_child` WHERE `id` = 1 AND `owner_id` IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal("projected_foreign_key_replacement_safe", preflight.Assessments[1].AnalysisCode);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, nullRows);
    }

    /// <summary>A different requested endpoint needs its own orphan proof, not the removed FK's proof.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofRejectsOrphansOnDifferentRequestedColumn()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyAlternateColumnFixture(orphan: true));
        await using var context = CreateContext(connectionString);
        var operations = AlternateColumnForeignKeyReplacement(context);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("fk-replacement-orphan"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, operations, CancellationToken.None));
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `proof_child` WHERE `id` = 1 "
            + "AND `alternate_owner_id` = '00000000-0000-0000-0000-000000000999';");

        var foreignKeys = await ReplacementForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[1].Action);
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, remaining);
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>A later allowed write cannot turn a preflight certificate into runtime authorization.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofRechecksOrphansAfterPreflight()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyAlternateColumnFixture(orphan: false));
        await using var context = CreateContext(connectionString);
        var operations = AlternateColumnForeignKeyReplacement(context);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("fk-replacement-fresh-rows"), CancellationToken.None);

        // WHY: The existing FK protects owner_id, not alternate_owner_id. This
        // models a legal intervening write without disabling FK enforcement.
        await ExecuteSqlAsync(connectionString,
            "UPDATE `proof_child` SET `alternate_owner_id` = '00000000-0000-0000-0000-000000000999';");
        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, operations, CancellationToken.None));
        var foreignKeys = await ReplacementForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>Captured row safety cannot certify incompatible current target storage.</summary>
    /// <param name="collationDrift">Whether the mismatch is collation rather than store type.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyReplacementProofRejectsTargetStorageDrift(bool collationDrift)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var alternateType = collationDrift
            ? "char(36) CHARACTER SET ascii COLLATE ascii_bin"
            : "int";

        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true)
            + $" ALTER TABLE `proof_child` ADD `alternate_owner_id` {alternateType} NULL;");
        await using var context = CreateContext(connectionString);
        var operations = AlternateColumnForeignKeyReplacement(context);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("fk-replacement-storage-drift"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.NotEqual("projected_foreign_key_replacement_safe", report.Assessments[1].AnalysisCode);
    }

    /// <summary>
    /// Missing target identifiers fail through structured prerequisites without binding missing columns.
    /// </summary>
    /// <param name="missingEndpoint">The requested endpoint absent from the original schema.</param>
    [Theory]
    [InlineData("local-column")]
    [InlineData("principal-column")]
    [InlineData("principal-table")]
    public async Task ForeignKeyReplacementProofRejectsMissingTargetEndpoint(string missingEndpoint)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child",
            [missingEndpoint == "local-column" ? "absent_owner" : "owner_id"],
            missingEndpoint == "principal-table" ? "absent_parent" : "proof_parent",
            [missingEndpoint == "principal-column" ? "absent_id" : "id"],
            onDelete: ReferentialAction.SetNull);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-missing-endpoint"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.NotEqual("projected_foreign_key_replacement_safe", report.Assessments[1].AnalysisCode);
    }

    /// <summary>Every column affected by SET NULL must actually permit NULL.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofRejectsRequiredLocalSetNull()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: false));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-required-column"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));
        var foreignKeys = await ReplacementForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.NotEqual(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>Column-case aliases cannot reuse nullable evidence after an accepted required transition.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofRejectsCaseVariantAcceptedRequiredColumn()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.AlterColumnIfDifferentFromModel(
            operationFactory: static operations => operations.AlterColumn<Guid>(
                    name: "OWNER_ID", table: "proof_child", type: "char(36)",
                    fixedLength: true, maxLength: 36, nullable: false,
                    oldClrType: typeof(Guid), oldType: "char(36)", oldFixedLength: true,
                    oldMaxLength: 36, oldNullable: true)
                .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .Annotation("Doka:MySql:RequiresExplicitBackfill", true)
                .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None)
                .OldAnnotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                .OldAnnotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None));

        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-case-required-column"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));
        var requiredColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'proof_child' AND COLUMN_NAME = 'owner_id' AND IS_NULLABLE = 'NO';");

        var foreignKeys = await ReplacementForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[1].Action);
        Assert.NotEqual(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, requiredColumns);
        Assert.Equal(0, foreignKeys);
    }

    /// <summary>Removing an independently required candidate key expires its captured certificate.</summary>
    /// <param name="uniqueConstraint">Whether the original principal key is unique rather than primary.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyReplacementProofRejectsRemovedPrincipalKey(bool uniqueConstraint)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var fixture = ForeignKeyNullabilityFixture(alreadyNullable: true);
        if (uniqueConstraint)
        {
            fixture = fixture.Replace("`id` char(36) NOT NULL PRIMARY KEY",
                "`id` char(36) NOT NULL, UNIQUE KEY `uq_proof_parent` (`id`)", StringComparison.Ordinal);
        }

        await ExecuteSqlAsync(connectionString, fixture);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        if (uniqueConstraint)
        {
            builder.DropUniqueConstraintIfExists("uq_proof_parent", "proof_parent");
        }
        else
        {
            builder.DropPrimaryKeyIfExists("pk_proof_parent", "proof_parent");
        }

        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-removed-key"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[2].Action);
        Assert.NotEqual("projected_foreign_key_replacement_safe", report.Assessments[2].AnalysisCode);
    }

    /// <summary>Unconfined writes cannot reuse an earlier replacement's row certificate.</summary>
    /// <param name="writeKind">The ordinary, model-managed or opaque write source.</param>
    [Theory]
    [InlineData("ordinary")]
    [InlineData("managed")]
    [InlineData("opaque")]
    public async Task ForeignKeyReplacementProofExpiresAfterAcceptedWrite(string writeKind)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true)
            + " CREATE TABLE `proof_mutations` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        if (writeKind == "managed")
        {
            _ = builder.EnsureModelManagedDataFromModel("proof_mutations", ["id"], ["int"],
                ["id"], ["int"], new object?[,] { { 7 } });
        }
        else if (writeKind == "ordinary")
        {
            builder.InsertData("proof_mutations", ["id"], new object[,] { { 7 } });
        }
        else
        {
            builder.Sql("INSERT INTO `proof_mutations` (`id`) VALUES (7);");
        }

        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-stale-rows"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        if (writeKind == "managed")
        {
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        }

        if (writeKind == "opaque")
        {
            Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
            Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[2].Action);
            Assert.Null(report.Assessments[2].ObservedState);
            Assert.NotNull(report.Assessments[2].DeferredOrigin);
            Assert.Equal(1, report.Assessments[2].DeferredOrigin!.OperationOrdinal);
        }
        else
        {
            Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[2].ObservedState);
        }

        Assert.NotEqual(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.NotEqual("projected_foreign_key_replacement_safe", report.Assessments[2].AnalysisCode);
    }

    /// <summary>Composite orphan checks preserve InnoDB's partial-null foreign-key semantics.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofSupportsCompositePartialNullRows()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, CompositeForeignKeyReplacementFixture());
        await using var context = CreateContext(connectionString);
        var builder = CompositeForeignKeyReplacement(context, reversePrincipalOrder: false);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-composite-nulls"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-composite-final"), CancellationToken.None);

        await ExecuteSqlAsync(connectionString, "DELETE FROM `composite_parent`;");
        var retained = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `composite_child`;");
        var affected = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `composite_child` WHERE `id` = 1 AND `first_id` IS NULL AND `second_id` IS NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(3, retained);
        Assert.Equal(1, affected);
    }

    /// <summary>Independent principal ordinal order is required even when all scalar types match.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofRejectsCompositePrincipalOrderDrift()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, CompositeForeignKeyReplacementFixture());
        await using var context = CreateContext(connectionString);
        var builder = CompositeForeignKeyReplacement(context, reversePrincipalOrder: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-composite-order"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.NotEqual("projected_foreign_key_replacement_safe", report.Assessments[1].AnalysisCode);
    }

    /// <summary>A repeated immutable operation identity is not a duplicate dictionary key or stale alias.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofSupportsReusedOperationInstances()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        MigrationOperation[] operations =
            [builder.Operations[0], builder.Operations[1], builder.Operations[0], builder.Operations[1]];

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, operations,
            new SafeMigrationRunOptions("fk-replacement-reused-operation"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, operations,
            new SafeMigrationRunOptions("fk-replacement-reused-final"), CancellationToken.None);

        var foreignKeys = await ReplacementForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>A requested action change remains different without an accepted physical removal.</summary>
    /// <param name="missingDrop">Whether an unrelated absent FK removal precedes the ensure.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyReplacementProofRequiresAcceptedExactRemoval(bool missingDrop)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (missingDrop)
        {
            builder.DropForeignKeyIfExists("absent_foreign_key", "proof_child");
        }

        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-unaccepted-removal"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        if (missingDrop)
        {
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        }

        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[^1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
    }

    /// <summary>A fresh empty dependent replaces, rather than inherits, an orphaned old table's row evidence.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofDoesNotRetainOrphansAfterDependentTableRecreation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `empty_proof_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `empty_proof_child` (`id` int NOT NULL PRIMARY KEY, "
            + "`owner_id` int NULL, `alternate_owner_id` int NULL, "
            + "CONSTRAINT `fk_empty_proof` FOREIGN KEY (`owner_id`) REFERENCES `empty_proof_parent` (`id`) "
            + "ON DELETE CASCADE) ENGINE=InnoDB; "
            + "INSERT INTO `empty_proof_parent` VALUES (7); "
            + "INSERT INTO `empty_proof_child` VALUES (1, 7, 999);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.CreateTableIfNotExists("empty_proof_parent", table => new
        {
            id = table.Column<int>(type: "int", nullable: false),
        }, constraints: table => table.PrimaryKey("pk_empty_proof_parent", value => value.id));

        builder.DropForeignKeyIfExists("fk_empty_proof", "empty_proof_child");
        builder.DropTableIfExists("empty_proof_child");
        _ = builder.CreateTableIfNotExists("empty_proof_child", table => new
        {
            id = table.Column<int>(type: "int", nullable: false),
            owner_id = table.Column<int>(type: "int", nullable: true),
            alternate_owner_id = table.Column<int>(type: "int", nullable: true),
        }, constraints: table => table.PrimaryKey("pk_empty_proof_child", value => value.id));

        builder.AddForeignKeyIfNotExists("fk_empty_proof", "empty_proof_child", ["alternate_owner_id"],
            "empty_proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-recreated-dependent"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-recreated-final"), CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `empty_proof_child`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(0, rows);
    }

    /// <summary>Conservative replacement certificates must not narrow ordinary provider-supported string FKs.</summary>
    [Fact]
    public async Task ForeignKeyOrdinaryEnsureRetainsDifferentVarcharLengthCompatibility()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `length_proof_parent` (`id` varchar(36) NOT NULL PRIMARY KEY) ENGINE=InnoDB "
            + "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "CREATE TABLE `length_proof_child` (`id` int NOT NULL PRIMARY KEY, `owner_id` varchar(64) NULL) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci; "
            + "INSERT INTO `length_proof_parent` VALUES ('owned'); "
            + "INSERT INTO `length_proof_child` VALUES (1, 'owned');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("fk_length_proof", "length_proof_child", ["owner_id"],
            "length_proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-ordinary-string-length"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-ordinary-string-length-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    /// <summary>An unrelated physical removal does not bypass the engine's FK-symbol ownership boundary.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofRetainsConstraintNameOwnershipBoundary()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true)
            + " CREATE TABLE `other_proof_child` (`id` int NOT NULL PRIMARY KEY, `owner_id` char(36) NULL, "
            + "CONSTRAINT `fk_colliding_target` FOREIGN KEY (`owner_id`) REFERENCES `proof_parent` (`id`)) "
            + "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");
        await using var context = CreateContext(connectionString);
        var tableScopedNames = Fixture.ServerVersion.IsMariaDb && Fixture.ServerVersion.Version >= new Version(12, 1);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_colliding_target", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-name-ownership"), CancellationToken.None);

        MySqlException? exception = null;
        if (tableScopedNames)
        {
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        }
        else
        {
            exception = await Assert.ThrowsAsync<MySqlException>(() =>
                ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));
        }

        var otherForeignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'other_proof_child' "
            + "AND CONSTRAINT_NAME = 'fk_colliding_target';");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(tableScopedNames ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(tableScopedNames ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent,
            report.Assessments[1].Action);
        if (!tableScopedNames)
        {
            Assert.NotNull(exception);
            Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(1, otherForeignKeys);
    }

    /// <summary>Dropping one physical FK must not claim a replacement while a matching alias survives.</summary>
    /// <param name="dropFirstAlias">Whether a second matching alias survives removal of the first one.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyReplacementProofRetainsSurvivingSemanticAlias(bool dropFirstAlias)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeySemanticAliasFixture(includeSecondAlias: dropFirstAlias));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        if (dropFirstAlias)
        {
            builder.DropForeignKeyIfExists("fk_semantic_proof_1", "proof_child");
        }

        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        builder.Operations.Add(builder.Operations[^1]);
        var runner = context.GetService<ISafeMigrationRunner>();
        var expectedPhysicalAlias = dropFirstAlias ? "fk_semantic_proof_2" : "fk_semantic_proof_1";

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-surviving-alias"), CancellationToken.None);

        var projected = await ProjectForeignKeyReplacementAnalysesAsync(context, builder.Operations);
        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var rerun = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-surviving-alias-rerun"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var exactForeignKeys = await ReplacementForeignKeyCountAsync(connectionString);
        var remainingAlias = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'proof_child' "
            + $"AND CONSTRAINT_NAME = '{expectedPhysicalAlias}';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Matching, report.Assessments[^2].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[^2].Action);
        Assert.Equal(SafeMigrationObservedState.Matching, report.Assessments[^1].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[^1].Action);
        Assert.Equal(expectedPhysicalAlias, projected[^2].MatchedObjectName);
        Assert.Equal(expectedPhysicalAlias, projected[^1].MatchedObjectName);
        Assert.Equal(SafeMigrationReportStatus.Ready, rerun.Status);
        Assert.All(rerun.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(0, exactForeignKeys);
        Assert.Equal(1, remainingAlias);
    }

    /// <summary>
    /// A semantic no-op's physical alias binding must expire when that alias is subsequently removed.
    /// </summary>
    /// <param name="uppercaseAliasDrop">Whether the physical alias removal uses a different identifier case.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyReplacementProofExpiresPreviouslyAcceptedSemanticAlias(bool uppercaseAliasDrop)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeySemanticAliasFixture(includeSecondAlias: false));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        var ensure = builder.Operations[^1];
        builder.DropForeignKeyIfExists(uppercaseAliasDrop ? "FK_SEMANTIC_PROOF_1" : "fk_semantic_proof_1",
            "proof_child");
        builder.Operations.Add(ensure);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-removed-accepted-alias"), CancellationToken.None);

        var projected = await ProjectForeignKeyReplacementAnalysesAsync(context, builder.Operations);
        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-removed-accepted-alias-final"), CancellationToken.None);

        var exactForeignKeys = await ReplacementForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal("fk_semantic_proof_1", projected[1].MatchedObjectName);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[3].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[3].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, exactForeignKeys);
    }

    /// <summary>A case-variant authored removal still identifies the engine's existing physical FK.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofSupportsCaseVariantExactRemoval()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeyNullabilityFixture(alreadyNullable: true));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("FK_PROOF_CHILD_PARENT", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-case-exact-removal"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-case-exact-removal-final"), CancellationToken.None);

        var foreignKeys = await ReplacementForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>Removing every matching physical alias requires an apply rather than a stale semantic no-op.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofAppliesAfterEverySemanticAliasIsRemoved()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeySemanticAliasFixture(includeSecondAlias: true));
        await using var context = CreateContext(connectionString);
        var builder = SemanticAliasForeignKeyRemoval(context, includeSecondAlias: true);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-all-aliases-removed"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-all-aliases-final"), CancellationToken.None);

        var exactForeignKeys = await ReplacementForeignKeyCountAsync(connectionString);
        var removedAliases = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'proof_child' "
            + "AND CONSTRAINT_NAME IN ('fk_semantic_proof_1', 'fk_semantic_proof_2');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[^1].ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, exactForeignKeys);
        Assert.Equal(0, removedAliases);
    }

    /// <summary>
    /// A captured alias set cannot suppress a fresh orphan guard after its physical members disappeared.
    /// </summary>
    [Fact]
    public async Task ForeignKeyReplacementProofRechecksRowsAfterSemanticAliasesDisappear()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeySemanticAliasFixture(includeSecondAlias: false));
        await using var context = CreateContext(connectionString);
        var builder = SemanticAliasForeignKeyRemoval(context, includeSecondAlias: false);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-alias-fresh-orphan"), CancellationToken.None);

        // WHY: Removing both real protectors makes the intervening write legal.
        // Runtime must recheck rows even though preflight saw safe semantic aliases.
        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE `proof_child` DROP FOREIGN KEY `fk_proof_child_parent`; "
            + "ALTER TABLE `proof_child` DROP FOREIGN KEY `fk_semantic_proof_1`; "
            + "UPDATE `proof_child` SET `owner_id` = '00000000-0000-0000-0000-000000000999';");
        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));
        var exactForeignKeys = await ReplacementForeignKeyCountAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, exactForeignKeys);
    }

    /// <summary>
    /// A repeated action change cannot hide a surviving exact-name collision behind another semantic FK.
    /// </summary>
    [Fact]
    public async Task ForeignKeyReplacementProofRetainsExactCollisionWithoutAnyRemoval()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeySemanticAliasFixture(includeSecondAlias: false));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        builder.Operations.Add(builder.Operations[0]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-exact-collision-control"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));
        var foreignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'proof_child';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(report.Assessments, assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        });
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, foreignKeys);
    }

    /// <summary>A removed physical semantic alias cannot authorize a different requested name as a no-op.</summary>
    [Fact]
    public async Task ForeignKeyReplacementProofRecreatesAfterCaseVariantSemanticSourceRemoval()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var fixture = ForeignKeyNullabilityFixture(alreadyNullable: true)
            .Replace("fk_proof_child_parent", "fk_semantic_proof_1", StringComparison.Ordinal)
            .Replace("ON DELETE CASCADE", "ON DELETE SET NULL", StringComparison.Ordinal);

        await ExecuteSqlAsync(connectionString, fixture);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("FK_SEMANTIC_PROOF_1", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_requested_proof", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-case-semantic-source"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-case-semantic-source-final"), CancellationToken.None);

        var requestedForeignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'proof_child' "
            + "AND CONSTRAINT_NAME = 'fk_requested_proof';");

        var removedSources = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'proof_child' "
            + "AND CONSTRAINT_NAME = 'fk_semantic_proof_1';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, requestedForeignKeys);
        Assert.Equal(0, removedSources);
    }

    /// <summary>
    /// An FK created on an incomplete owner must be removable even when the initial drop catalog said missing.
    /// </summary>
    [Fact]
    public async Task ForeignKeyReplacementProofDropsAcceptedFkMissingFromOriginalCatalog()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `missing_proof_parent` (`id` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "CREATE TABLE `missing_proof_child` (`id` int NOT NULL PRIMARY KEY, `owner_id` int NULL) ENGINE=InnoDB; "
            + "INSERT INTO `missing_proof_parent` VALUES (7); "
            + "INSERT INTO `missing_proof_child` VALUES (1, 7);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("fk_missing_proof", "missing_proof_child", ["owner_id"],
            "missing_proof_parent", ["id"], onDelete: ReferentialAction.SetNull);
        var ensure = builder.Operations[0];
        builder.DropForeignKeyIfExists("fk_missing_proof", "missing_proof_child");
        builder.Operations.Add(ensure);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-incomplete-owner-created"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-incomplete-owner-created-final"), CancellationToken.None);

        var foreignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'missing_proof_child' "
            + "AND CONSTRAINT_NAME = 'fk_missing_proof';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[2].ObservedState);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, foreignKeys);
    }

    /// <summary>
    /// A newly accepted matching physical alias takes precedence over removed original live identities.
    /// </summary>
    [Fact]
    public async Task ForeignKeyReplacementProofResolvesNewlyRecreatedPhysicalAlias()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeySemanticAliasFixture(includeSecondAlias: true));
        await using var context = CreateContext(connectionString);
        var builder = RecreatedAliasForeignKeyOperations(context, recreatedAction: ReferentialAction.SetNull);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-recreated-physical-alias"), CancellationToken.None);

        var projected = await ProjectForeignKeyReplacementAnalysesAsync(context, builder.Operations);
        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-recreated-physical-alias-final"), CancellationToken.None);

        var rerun = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-recreated-physical-alias-rerun"), CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var exactForeignKeys = await ReplacementForeignKeyCountAsync(connectionString);
        var recreatedAliases = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'proof_child' "
            + "AND CONSTRAINT_NAME = 'fk_recreated_alias';");

        var originalAliases = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'proof_child' "
            + "AND CONSTRAINT_NAME IN ('fk_semantic_proof_1', 'fk_semantic_proof_2');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments.Take(4),
            assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationObservedState.Matching, report.Assessments[4].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[4].Action);
        Assert.Equal("fk_recreated_alias", projected[4].MatchedObjectName);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, rerun.Status);
        Assert.All(rerun.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(0, exactForeignKeys);
        Assert.Equal(1, recreatedAliases);
        Assert.Equal(0, originalAliases);
    }

    /// <summary>
    /// A recreated physical FK with another action cannot stand in for the requested target definition.
    /// </summary>
    [Fact]
    public async Task ForeignKeyReplacementProofDoesNotMatchChangedRecreatedPhysicalAlias()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ForeignKeySemanticAliasFixture(includeSecondAlias: true));
        await using var context = CreateContext(connectionString);
        var builder = RecreatedAliasForeignKeyOperations(context, recreatedAction: ReferentialAction.Cascade);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-changed-recreated-alias"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("fk-replacement-changed-recreated-alias-final"), CancellationToken.None);

        var exactForeignKeys = await ReplacementForeignKeyCountAsync(connectionString);
        var recreatedAliases = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'proof_child' "
            + "AND CONSTRAINT_NAME = 'fk_recreated_alias' AND DELETE_RULE = 'CASCADE';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[4].ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, exactForeignKeys);
        Assert.Equal(1, recreatedAliases);
    }

    /// <summary>Creates one separate requested FK endpoint without changing the original FK-protected value.</summary>
    /// <param name="orphan">Whether the requested new endpoint intentionally lacks a principal row.</param>
    /// <returns>The isolated initial schema and deterministic sentinel values.</returns>
    private static string ForeignKeyAlternateColumnFixture(bool orphan)
        => ForeignKeyNullabilityFixture(alreadyNullable: true)
            + " ALTER TABLE `proof_child` ADD `alternate_owner_id` char(36) NULL; "
            + "UPDATE `proof_child` SET `alternate_owner_id` = "
            + (orphan ? "'00000000-0000-0000-0000-000000000999';" : "'00000000-0000-0000-0000-000000000001';");

    /// <summary>Declares an exact physical removal followed by a distinct requested column mapping.</summary>
    /// <param name="context">The active provider context used to identify the migration dialect.</param>
    /// <returns>The two immutable safe operations in execution order.</returns>
    private static List<MigrationOperation> AlternateColumnForeignKeyReplacement(DbContext context)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["alternate_owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        return builder.Operations;
    }

    /// <summary>Creates composite data with one matching row and two independently nullable key members.</summary>
    /// <returns>The isolated schema with valid original FK enforcement left enabled.</returns>
    private static string CompositeForeignKeyReplacementFixture()
        => "CREATE TABLE `composite_parent` (`first_id` int NOT NULL, `second_id` int NOT NULL, "
            + "PRIMARY KEY (`first_id`, `second_id`)) ENGINE=InnoDB; "
            + "CREATE TABLE `composite_child` (`id` int NOT NULL PRIMARY KEY, "
            + "`first_id` int NULL, `second_id` int NULL, "
            + "CONSTRAINT `fk_composite_proof` FOREIGN KEY (`first_id`, `second_id`) "
            + "REFERENCES `composite_parent` (`first_id`, `second_id`) ON DELETE CASCADE) ENGINE=InnoDB; "
            + "INSERT INTO `composite_parent` VALUES (7, 11); "
            + "INSERT INTO `composite_child` VALUES (1, 7, 11), (2, NULL, 999), (3, 777, NULL);";

    /// <summary>Requests the composite replacement with independently chosen principal key order.</summary>
    /// <param name="context">The active provider context.</param>
    /// <param name="reversePrincipalOrder">Whether the target requires an absent reversed candidate key.</param>
    /// <returns>The ordered drop and replacement builder.</returns>
    private static MigrationBuilder CompositeForeignKeyReplacement(DbContext context, bool reversePrincipalOrder)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_composite_proof", "composite_child");
        builder.AddForeignKeyIfNotExists("fk_composite_proof", "composite_child", ["first_id", "second_id"],
            "composite_parent", reversePrincipalOrder ? ["second_id", "first_id"] : ["first_id", "second_id"],
            onDelete: ReferentialAction.SetNull);

        return builder;
    }

    /// <summary>Creates distinct physical target aliases alongside an exact-name FK with a different action.</summary>
    /// <param name="includeSecondAlias">Whether the initial schema contains two matching physical aliases.</param>
    /// <returns>The populated schema with all original FK checks enforced.</returns>
    private static string ForeignKeySemanticAliasFixture(bool includeSecondAlias)
        => ForeignKeyNullabilityFixture(alreadyNullable: true)
            + " ALTER TABLE `proof_child` ADD CONSTRAINT `fk_semantic_proof_1` FOREIGN KEY (`owner_id`) "
            + "REFERENCES `proof_parent` (`id`) ON DELETE SET NULL;"
            + (includeSecondAlias
                ? " ALTER TABLE `proof_child` ADD CONSTRAINT `fk_semantic_proof_2` FOREIGN KEY (`owner_id`) "
                    + "REFERENCES `proof_parent` (`id`) ON DELETE SET NULL;"
                : string.Empty);

    /// <summary>
    /// Removes the exact source and each requested physical alias before recreating the owned target.
    /// </summary>
    /// <param name="context">The active provider context.</param>
    /// <param name="includeSecondAlias">Whether the second matching alias must also be removed.</param>
    /// <returns>The ordered physical removals followed by one ensure.</returns>
    private static MigrationBuilder SemanticAliasForeignKeyRemoval(DbContext context, bool includeSecondAlias)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.DropForeignKeyIfExists("fk_semantic_proof_1", "proof_child");
        if (includeSecondAlias)
        {
            builder.DropForeignKeyIfExists("fk_semantic_proof_2", "proof_child");
        }

        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        return builder;
    }

    /// <summary>
    /// Replaces all original physical aliases before separately requesting the original semantic target.
    /// </summary>
    /// <param name="context">The active provider context.</param>
    /// <param name="recreatedAction">The action of the newly created physical alias.</param>
    /// <returns>The ordered removals, new physical creation and final semantic ensure.</returns>
    private static MigrationBuilder RecreatedAliasForeignKeyOperations(
        DbContext context,
        ReferentialAction recreatedAction
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_proof_child_parent", "proof_child");
        builder.DropForeignKeyIfExists("fk_semantic_proof_1", "proof_child");
        builder.DropForeignKeyIfExists("fk_semantic_proof_2", "proof_child");
        builder.AddForeignKeyIfNotExists("fk_recreated_alias", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: recreatedAction);
        builder.AddForeignKeyIfNotExists("fk_proof_child_parent", "proof_child", ["owner_id"],
            "proof_parent", ["id"], onDelete: ReferentialAction.SetNull);

        return builder;
    }

    /// <summary>
    /// Exercises the real provider certificate and ordered projection to inspect private physical identity.
    /// </summary>
    /// <param name="context">The live isolated provider context.</param>
    /// <param name="operations">The exclusively safe FK stream being projected.</param>
    /// <returns>The projected states including resolved physical alias names.</returns>
    private static async Task<SafeMigrationProviderAnalysis[]> ProjectForeignKeyReplacementAnalysesAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var safeOperations = operations.Cast<SafeMigrationOperation>().ToArray();
        var provider = context.GetService<ISafeMigrationProviderAnalyzer>();
        var live = await provider.AnalyzeAsync(context, safeOperations, CancellationToken.None);
        var projection = new SafeMigrationPreflightProjection(
            provider as ISafeMigrationProviderOperationProjection,
            provider as ISafeMigrationProjectedKeyAnalyzer,
            provider as ISafeMigrationProviderObjectIdentityNormalizer,
            provider as ISafeMigrationProjectedDependencyAnalyzer,
            provider as ISafeMigrationIndexPrerequisiteSource,
            provider as ISafeMigrationProjectedColumnAnalyzer,
            provider as ISafeMigrationRenamedTableAnalyzer);

        // WHY: Reports deliberately do not expose this internal binding. A
        // NoOp assertion alone cannot prove that a later physical drop is safe.
        var projected = new SafeMigrationProviderAnalysis[safeOperations.Length];
        for (var index = 0; index < safeOperations.Length; index++)
        {
            var operation = safeOperations[index];
            projection.CurrentOperationOrdinal = index;
            projected[index] = projection.Project(operation, live[index]);
            var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind,
                projected[index].ObservedState, operation.Policy, projected[index].RepairCapability);

            projection.Observe(operation, live[index], projected[index], decision);
        }

        return projected;
    }

    /// <summary>Reads only the owned FK's bounded catalog count after a guarded runtime operation.</summary>
    /// <param name="connectionString">The isolated test database connection.</param>
    /// <returns>One when the exact physical FK remains, otherwise zero.</returns>
    private static Task<int> ReplacementForeignKeyCountAsync(string connectionString)
        => ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'proof_child' "
            + "AND CONSTRAINT_NAME = 'fk_proof_child_parent';");
}
