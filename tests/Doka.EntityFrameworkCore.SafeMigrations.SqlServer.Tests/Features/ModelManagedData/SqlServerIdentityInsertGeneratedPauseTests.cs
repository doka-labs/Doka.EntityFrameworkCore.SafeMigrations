namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises identity readiness injection against the real offline SQL Server generator.</summary>
public sealed class SqlServerIdentityInsertGeneratedPauseTests
{
    /// <summary>Publishes readiness inside the actual generated identity-acquisition execution scope.</summary>
    /// <param name="options">The real runtime or script generation mode.</param>
    /// <param name="beforeConfirmation">Whether readiness interrupts the uncertain acquisition interval.</param>
    /// <param name="resource">The exact application-lock resource preserved through nested SQL literals.</param>
    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Default, false, "generated_ready")]
    [InlineData(MigrationsSqlGenerationOptions.Default, true, "generated_ready")]
    [InlineData(MigrationsSqlGenerationOptions.Script, false, "generated_ready")]
    [InlineData(MigrationsSqlGenerationOptions.Script, true, "generated_ready")]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent, false, "generated_ready")]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent, true, "generated_ready")]
    [InlineData(MigrationsSqlGenerationOptions.Default, false, "ready'/* hostile */;--resource")]
    [InlineData(MigrationsSqlGenerationOptions.Default, true, "ready'/* hostile */;--resource")]
    [InlineData(MigrationsSqlGenerationOptions.Script, false, "ready'/* hostile */;--resource")]
    [InlineData(MigrationsSqlGenerationOptions.Script, true, "ready'/* hostile */;--resource")]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent, false, "ready'/* hostile */;--resource")]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent, true, "ready'/* hostile */;--resource")]
    public void GeneratedIdentityAcquisitionPublishesExactlyOneOrderedPause(
        MigrationsSqlGenerationOptions options,
        bool beforeConfirmation,
        string resource
    )
    {
        // Arrange
        using var context = CreateContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "identity_roles",
            ["Id"],
            ["int"],
            ["Id", "Caption"],
            ["int", "nvarchar(80)"],
            new object?[,] { { 7, "Administrator" } });

        var command = context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model, options)
            .Single(candidate => candidate.CommandText.Contains("SET IDENTITY_INSERT", StringComparison.Ordinal));

        // Act
        var paused = SqlServerIdentityInsertPauseInterceptor.BuildPausedCommand(
            command.CommandText,
            resource,
            beforeConfirmation);

        var execution = IdentityExecution(paused);

        var identity = execution.IndexOf("SET IDENTITY_INSERT [dbo].[identity_roles]", StringComparison.Ordinal);
        var identityOn = execution.IndexOf("ON;", identity, StringComparison.Ordinal);
        var acquired = execution.IndexOf(
            SqlServerSafeMigrationIdentityInsertScope.AcquiredMarkerComment,
            StringComparison.Ordinal);

        var marker = execution.IndexOf("EXEC @doka_identity_ready = sys.sp_getapplock", StringComparison.Ordinal);
        var wait = execution.IndexOf("WAITFOR DELAY '00:05:00';", StringComparison.Ordinal);
        var insert = execution.IndexOf("INSERT INTO [dbo].[identity_roles]", StringComparison.Ordinal);

        // Assert
        Assert.NotEqual(command.CommandText, paused);
        Assert.Equal(1, execution.Split("EXEC @doka_identity_ready =", StringSplitOptions.None).Length - 1);
        Assert.True(identity >= 0 && identity < identityOn && identityOn < marker && marker < wait && wait < insert);
        Assert.True(acquired > identityOn);
        Assert.Equal(beforeConfirmation, wait < acquired);
        Assert.Contains(
            "@Resource = N'" + resource.Replace("'", "''", StringComparison.Ordinal) + "'",
            execution,
            StringComparison.Ordinal);

        Assert.Contains("@LockOwner = N'Session'", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("RAISERROR", paused, StringComparison.Ordinal);
        Assert.DoesNotContain("NOWAIT", paused, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Token spacing changes only the acquisition statement, never the authored value payload.</summary>
    /// <param name="options">The actual runtime or script producer.</param>
    /// <param name="beforeConfirmation">Whether the pause precedes ownership confirmation.</param>
    /// <param name="separator">The native whitespace between the target table and ON.</param>
    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Default, false, "\n")]
    [InlineData(MigrationsSqlGenerationOptions.Default, true, "\n")]
    [InlineData(MigrationsSqlGenerationOptions.Script, false, "\n")]
    [InlineData(MigrationsSqlGenerationOptions.Script, true, "\n")]
    [InlineData(MigrationsSqlGenerationOptions.Default, false, "\r\n")]
    [InlineData(MigrationsSqlGenerationOptions.Default, true, "\r\n")]
    [InlineData(MigrationsSqlGenerationOptions.Script, false, "\r\n")]
    [InlineData(MigrationsSqlGenerationOptions.Script, true, "\r\n")]
    [InlineData(MigrationsSqlGenerationOptions.Default, false, "\t")]
    [InlineData(MigrationsSqlGenerationOptions.Default, true, "\t")]
    [InlineData(MigrationsSqlGenerationOptions.Script, false, "\t")]
    [InlineData(MigrationsSqlGenerationOptions.Script, true, "\t")]
    public void GeneratedAcquisitionLayoutDoesNotAlterAuthoredValues(
        MigrationsSqlGenerationOptions options,
        bool beforeConfirmation,
        string separator
    )
    {
        // Arrange
        using var context = CreateContext();
        const string acquisition = "SET IDENTITY_INSERT [dbo].[identity_roles]\nON;";
        var generated = GenerateIdentity(context, options, caption: "line1\r\nGO\nline2\tO'Brien");

        // WHY: Reflow only the known provider-owned acquisition fragment. Whole-command normalization
        // would erase the authored CRLF, GO line and tab whose byte preservation this test requires.
        var command = generated.Replace(acquisition,
            "SET IDENTITY_INSERT [dbo].[identity_roles]" + separator + "ON;", StringComparison.Ordinal);

        // Act
        var paused = SqlServerIdentityInsertPauseInterceptor.BuildPausedCommand(
            command, "layout'_resource", beforeConfirmation);

        var originalExecution = IdentityExecution(command);
        var execution = IdentityExecution(paused);
        var identity = execution.IndexOf("SET IDENTITY_INSERT [dbo].[identity_roles]", StringComparison.Ordinal);
        var identityOn = execution.IndexOf("ON;", identity, StringComparison.Ordinal);
        var acquired = execution.IndexOf(
            SqlServerSafeMigrationIdentityInsertScope.AcquiredMarkerComment, StringComparison.Ordinal);

        var marker = execution.IndexOf("EXEC @doka_identity_ready =", StringComparison.Ordinal);
        var wait = execution.IndexOf("WAITFOR DELAY '00:05:00';", StringComparison.Ordinal);
        var insert = execution.IndexOf("INSERT INTO [dbo].[identity_roles]", StringComparison.Ordinal);
        var originalInsert = originalExecution.IndexOf("INSERT INTO [dbo].[identity_roles]", StringComparison.Ordinal);

        // Assert
        Assert.Equal(1, generated.Split(acquisition, StringSplitOptions.None).Length - 1);
        Assert.Equal(1, execution.Split("EXEC @doka_identity_ready =", StringSplitOptions.None).Length - 1);
        Assert.True(identity >= 0 && identity < identityOn && identityOn < marker && marker < wait && wait < insert);
        Assert.True(acquired > identityOn);
        Assert.Equal(beforeConfirmation, wait < acquired);
        Assert.Equal(originalExecution[originalInsert..], execution[insert..]);
        Assert.Contains("@Resource = N'layout''_resource'", execution, StringComparison.Ordinal);
        Assert.DoesNotContain("RAISERROR", paused, StringComparison.Ordinal);
        Assert.DoesNotContain("NOWAIT", paused, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Authored seed text cannot move the pause from the native acquisition into a VALUE literal.</summary>
    /// <param name="options">The actual runtime or script producer.</param>
    /// <param name="beforeConfirmation">Whether the pause precedes ownership confirmation.</param>
    /// <param name="multiline">Whether CRLF and a GO line also require the published protected transport.</param>
    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Default, false, false)]
    [InlineData(MigrationsSqlGenerationOptions.Default, true, false)]
    [InlineData(MigrationsSqlGenerationOptions.Script, false, false)]
    [InlineData(MigrationsSqlGenerationOptions.Script, true, false)]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent, false, false)]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent, true, false)]
    [InlineData(MigrationsSqlGenerationOptions.Default, false, true)]
    [InlineData(MigrationsSqlGenerationOptions.Default, true, true)]
    [InlineData(MigrationsSqlGenerationOptions.Script, false, true)]
    [InlineData(MigrationsSqlGenerationOptions.Script, true, true)]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent, false, true)]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent, true, true)]
    public void GeneratedIdentityValuesCannotForgeAnAcquisitionOrConfirmation(
        MigrationsSqlGenerationOptions options,
        bool beforeConfirmation,
        bool multiline
    )
    {
        // Arrange
        using var context = CreateContext();
        const string forged = "SET IDENTITY_INSERT [dbo].[identity_roles] ON; "
            + "/* doka_sm_identity_acquired */ EXEC sys.sp_executesql N'SELECT 1;'; O'Brien";

        var caption = multiline ? "line1\r\nGO\n" + forged : forged;
        var command = GenerateIdentity(context, options, caption: caption);

        // Act
        var paused = SqlServerIdentityInsertPauseInterceptor.BuildPausedCommand(
            command, "value_resource", beforeConfirmation);

        var originalExecution = IdentityExecution(command);
        var execution = IdentityExecution(paused);
        var insert = execution.IndexOf("INSERT INTO [dbo].[identity_roles]", StringComparison.Ordinal);
        var originalInsert = originalExecution.IndexOf("INSERT INTO [dbo].[identity_roles]", StringComparison.Ordinal);
        var acquired = execution.IndexOf(
            SqlServerSafeMigrationIdentityInsertScope.AcquiredMarkerComment, StringComparison.Ordinal);

        var marker = execution.IndexOf("EXEC @doka_identity_ready =", StringComparison.Ordinal);
        var wait = execution.IndexOf("WAITFOR DELAY '00:05:00';", StringComparison.Ordinal);

        // Assert
        Assert.NotEqual(command, paused);
        Assert.Equal(1, execution.Split("EXEC @doka_identity_ready =", StringSplitOptions.None).Length - 1);
        Assert.True(marker >= 0 && marker < wait && wait < insert);
        Assert.Equal(beforeConfirmation, wait < acquired);
        Assert.Equal(originalExecution[originalInsert..], execution[insert..]);
        Assert.DoesNotContain("RAISERROR", paused, StringComparison.Ordinal);
        Assert.DoesNotContain("NOWAIT", paused, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Different physical tables never publish readiness for the fixture's dbo identity target.</summary>
    /// <param name="table">The non-target physical table.</param>
    /// <param name="schema">The non-target physical schema.</param>
    [Theory]
    [InlineData("identity_roles_other", null)]
    [InlineData("identity_roles", "other_schema")]
    public void GeneratedOtherIdentityTargetsAreUnchanged(
        string table,
        string? schema
    )
    {
        // Arrange
        using var context = CreateContext();
        var command = GenerateIdentity(context, MigrationsSqlGenerationOptions.Default, table, schema);

        // Act
        var paused = SqlServerIdentityInsertPauseInterceptor.BuildPausedCommand(command, "unrelated_resource");

        // Assert
        Assert.Same(command, paused);
        Assert.DoesNotContain("EXEC @doka_identity_ready =", paused, StringComparison.Ordinal);
        Assert.DoesNotContain("WAITFOR", paused, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Forged control text in defaults or ordinary data cannot publish identity readiness.</summary>
    /// <param name="ordinarySql">Whether the real generator processes ordinary SQL rather than a safe column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedNonIdentityDataAndPreamblesAreUnchanged(
        bool ordinarySql
    )
    {
        // Arrange
        using var context = CreateContext();
        const string forged = "SET IDENTITY_INSERT [dbo].[identity_roles] ON; "
            + "/* doka_sm_identity_acquired */ EXEC sys.sp_executesql N'SELECT 1;'; --value";

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (ordinarySql)
        {
            builder.Sql("SELECT N'" + forged.Replace("'", "''", StringComparison.Ordinal) + "';");
        }
        else
        {
            builder.AddColumnIfNotExists<string>(
                "Caption", "identity_roles", type: "nvarchar(max)", nullable: true, defaultValue: forged);
        }

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Act
        var paused = commands.Select(command => SqlServerIdentityInsertPauseInterceptor.BuildPausedCommand(
            command.CommandText, "forged_resource")).ToArray();

        // Assert
        Assert.NotEmpty(commands);
        Assert.Contains(commands, command => command.CommandText.Contains(
            "SET IDENTITY_INSERT", StringComparison.Ordinal));
        for (var index = 0; index < commands.Count; index++)
        {
            Assert.Same(commands[index].CommandText, paused[index]);
            Assert.DoesNotContain("EXEC @doka_identity_ready =", paused[index], StringComparison.Ordinal);
            Assert.DoesNotContain("WAITFOR", paused[index], StringComparison.Ordinal);
        }

        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Identifier inventory and guard literals cannot impersonate an identity-acquisition command.</summary>
    [Fact]
    public void GeneratedIdentifierPreambleCannotForgeReadiness()
    {
        // Arrange
        using var context = CreateContext();
        const string table = "DECLARE @doka_state; SET IDENTITY_INSERT [dbo].[identity_roles] ON;";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameColumnIfExists("Caption", table, "DisplayCaption");
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Act
        var paused = commands.Select(command => SqlServerIdentityInsertPauseInterceptor.BuildPausedCommand(
            command.CommandText, "preamble_resource")).ToArray();

        // Assert
        Assert.Contains(commands, command => command.CommandText.Contains(
            "CREATE TABLE #doka_sm_identifiers_", StringComparison.Ordinal)
            && command.CommandText.Contains(table, StringComparison.Ordinal));

        for (var index = 0; index < commands.Count; index++)
        {
            Assert.Same(commands[index].CommandText, paused[index]);
            Assert.DoesNotContain("EXEC @doka_identity_ready =", paused[index], StringComparison.Ordinal);
            Assert.DoesNotContain("WAITFOR", paused[index], StringComparison.Ordinal);
        }

        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Generates the real managed-data mutation without copying or normalizing its acquisition text.</summary>
    /// <param name="context">The offline SQL Server context.</param>
    /// <param name="options">The generation mode consumed by the real producer.</param>
    /// <param name="table">The physical target table.</param>
    /// <param name="schema">The optional physical target schema.</param>
    /// <param name="caption">The source-frozen authored seed value.</param>
    /// <returns>The complete operation command including its private guard.</returns>
    private static string GenerateIdentity(
        DbContext context,
        MigrationsSqlGenerationOptions options,
        string table = "identity_roles",
        string? schema = null,
        string caption = "Administrator"
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            table, ["Id"], ["int"], ["Id", "Caption"], ["int", "nvarchar(512)"],
            new object?[,] { { 7, caption } }, schema: schema);

        return context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model, options)
            .Single(candidate => candidate.CommandText.Contains("SET IDENTITY_INSERT", StringComparison.Ordinal))
            .CommandText;
    }

    /// <summary>Finds the actual mutation batch, not an enclosing carrier containing its quoted source.</summary>
    /// <param name="command">The complete generated operation command.</param>
    /// <returns>The decoded native identity-acquisition and insertion batch.</returns>
    private static string IdentityExecution(
        string command
    ) => SqlServerGeneratedHistoryScriptTests.DecodeNestedPayloads(command)
        .Single(payload => payload.StartsWith("DECLARE @doka_identity_insert bit", StringComparison.Ordinal));

    /// <summary>Creates provider services without opening a native connection or loading migration history.</summary>
    /// <returns>An offline context configured with the production safe SQL generator.</returns>
    private static DbContext CreateContext() => new(new DbContextOptionsBuilder()
        .UseSqlServer("Server=127.0.0.1,1433;Database=offline_identity_pause;"
            + "User ID=sa;Password=unused;TrustServerCertificate=True")
        .UseSqlServerSafeMigrations()
        .Options);
}
