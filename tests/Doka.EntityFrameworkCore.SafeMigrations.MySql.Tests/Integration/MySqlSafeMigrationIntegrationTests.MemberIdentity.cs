namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the MemberIdentity migration contracts in an independent class collection.</summary>
public sealed partial class MySqlMemberIdentityIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlMemberIdentityIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Native index casing shares one drop tombstone and cannot revive a removed rename source.</summary>
    [Fact]
    public async Task MemberIdentity_IndexCaseVariantsSharePhysicalDropLifetime()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id int NOT NULL, Code int NULL, KEY IX_Code (Code)); "
            + "INSERT INTO member_rows VALUES (7, 29);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_Code", "member_rows");
        builder.DropIndexIfExists("ix_code", "member_rows");
        builder.RenameIndexIfExists("ix_code", "member_rows", "ix_renamed");
        var rename = (SafeMigrationOperation)builder.Operations[^1];
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [rename]);
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-identity-index-lifetime"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-identity-index-lifetime-final"));

        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'member_rows';");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM member_rows WHERE id = 7 AND Code = 29;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(analyses).ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(0, indexes);
        Assert.Equal(1, rows);
    }

    /// <summary>CI unique-index names share cross-kind aliases and require recreation after a physical drop.</summary>
    /// <param name="dropConstraint">Whether the physical unique index is dropped through the constraint API.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberIdentity_SharedUniqueCaseVariantsInvalidateAcrossKinds(bool dropConstraint)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id int NOT NULL, Code int NULL, UNIQUE KEY UX_Code (Code)); "
            + "INSERT INTO member_rows VALUES (7, 29);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (dropConstraint)
        {
            builder.EnsureIndex(new ExpectedIndexDefinition("ux_code", "member_rows",
                [new ExpectedIndexKeyDefinition("Code")], unique: true), SafeMigrationPolicy.ThrowIfDifferent);
            builder.DropUniqueConstraintIfExists("UX_CODE", "member_rows");
            builder.EnsureIndex(new ExpectedIndexDefinition("Ux_Code", "member_rows",
                [new ExpectedIndexKeyDefinition("Code")], unique: true), SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureUniqueConstraint(new ExpectedUniqueConstraintDefinition("ux_code", "member_rows", ["Code"]),
                SafeMigrationPolicy.ThrowIfDifferent);
            builder.DropIndexIfExists("UX_CODE", "member_rows");
            builder.EnsureUniqueConstraint(new ExpectedUniqueConstraintDefinition("Ux_Code", "member_rows", ["Code"]),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        var finalEnsure = (SafeMigrationOperation)builder.Operations[^1];
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [finalEnsure]);
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-identity-shared-unique"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-identity-shared-unique-final"));

        var keys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'member_rows' AND NON_UNIQUE = 0;");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM member_rows WHERE id = 7 AND Code = 29;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(analyses).ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(1, keys);
        Assert.Equal(1, rows);
    }

    /// <summary>Candidate-key and FK columns use native casing, while genuinely missing columns reject.</summary>
    /// <param name="keyKind">The primary-key, unique-constraint or unique-index candidate-key family.</param>
    /// <param name="missingPrincipalColumn">Whether the FK asks for a different, absent principal column.</param>
    [Theory]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("index", false)]
    [InlineData("index", true)]
    public async Task MemberIdentity_CandidateKeysUseColumnIdentifierSemantics(
        string keyKind,
        bool missingPrincipalColumn
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("member_parent",
                [new ExpectedColumnDefinition("Id", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        switch (keyKind)
        {
            case "primary":
                builder.EnsurePrimaryKey(new ExpectedPrimaryKeyDefinition("pk_member_parent", "member_parent", ["id"]),
                    SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "unique":
                builder.EnsureUniqueConstraint(new ExpectedUniqueConstraintDefinition(
                    "uq_member_parent", "member_parent", ["id"]), SafeMigrationPolicy.ThrowIfDifferent);
                break;
            case "index":
                builder.EnsureIndex(new ExpectedIndexDefinition("ux_member_parent", "member_parent",
                    [new ExpectedIndexKeyDefinition("id")], unique: true), SafeMigrationPolicy.ThrowIfDifferent);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(keyKind));
        }

        builder.EnsureTable(new ExpectedTableDefinition("member_child",
                [new ExpectedColumnDefinition("ParentId", typeof(int), true, "int")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureForeignKey(new ExpectedForeignKeyDefinition("fk_member_case", "member_child", ["parentid"],
            "member_parent", [missingPrincipalColumn ? "MissingId" : "ID"]), SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-identity-candidate-key"));

        if (!missingPrincipalColumn)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var foreignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'member_child' "
            + "AND CONSTRAINT_NAME = 'fk_member_case' AND CONSTRAINT_TYPE = 'FOREIGN KEY';");

        // Assert
        Assert.Equal(missingPrincipalColumn ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            preflight.Status);
        Assert.Equal(missingPrincipalColumn ? SafeMigrationAction.RejectPrerequisiteMissing : SafeMigrationAction.Apply,
            preflight.Assessments[^1].Action);
        Assert.Equal(missingPrincipalColumn ? 0 : 1, foreignKeys);
    }

    /// <summary>Case-insensitive members never merge different physical table owners in native mode zero.</summary>
    [Fact]
    public async Task MemberIdentity_IndexCasingDoesNotMergeCaseDistinctTableOwners()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var ownerMode = await ScalarIntAsync(connectionString, "SELECT @@lower_case_table_names;");

        // WHY: Qualified Linux containers use native mode zero, which permits
        // two exact case-distinct owners. Only member names may be folded.
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE member_rows (id int NOT NULL, Code int NULL, KEY IX_Code (Code)); "
            + "CREATE TABLE Member_Rows (id int NOT NULL, Code int NULL, KEY IX_Code (Code)); "
            + "INSERT INTO member_rows VALUES (7, 29); INSERT INTO Member_Rows VALUES (11, 41);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_Code", "member_rows");
        builder.DropIndexIfExists("ix_code", "Member_Rows");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("member-identity-exact-owners"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(DISTINCT TABLE_NAME, INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('member_rows', 'Member_Rows');");

        var firstRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM member_rows WHERE id = 7;");
        var secondRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM Member_Rows WHERE id = 11;");

        // Assert
        Assert.Equal(0, ownerMode);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(0, indexes);
        Assert.Equal(1, firstRows);
        Assert.Equal(1, secondRows);
    }
}
