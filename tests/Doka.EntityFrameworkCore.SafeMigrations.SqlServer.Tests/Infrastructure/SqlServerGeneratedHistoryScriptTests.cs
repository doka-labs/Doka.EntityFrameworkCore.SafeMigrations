namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises real generated-upgrade migration SQL without depending on a native engine.</summary>
public sealed class SqlServerGeneratedHistoryScriptTests
{
    /// <summary>Single-line transport preserves UTF-16 payloads beyond 4000 characters.</summary>
    /// <param name="lineEnding">The exact authored newline sequence.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void IdempotentTransportPreservesLongQuotedUnicodePayload(string lineEnding)
    {
        // Arrange
        var original = new string('x', 4097) + "O'Brien\\path" + lineEnding + "GO" + lineEnding + "\u20ac\ud83d\ude00";

        // Act
        var sql = SqlServerSafeMigrationsSqlGenerator.ProtectCommandSql(
            original, MigrationsSqlGenerationOptions.Idempotent);

        var payloads = DecodeIdempotentPayloads(sql);

        // Assert
        Assert.Equal(original, Assert.Single(payloads));
        Assert.DoesNotContain('\r', sql);
        Assert.Single(sql.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("CAST(N'''' AS nvarchar(max))", sql, StringComparison.Ordinal);
    }

    /// <summary>Ordinary and normal-script generation retain the existing no-copy command path.</summary>
    /// <param name="options">A generation mode without EF's idempotent history indentation.</param>
    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Default)]
    [InlineData(MigrationsSqlGenerationOptions.Script)]
    [InlineData(MigrationsSqlGenerationOptions.Script | MigrationsSqlGenerationOptions.NoTransactions)]
    public void NonIdempotentTransportReturnsTheOriginalCommand(MigrationsSqlGenerationOptions options)
    {
        // Arrange
        var original = "EXEC sys.sp_executesql N'SELECT ''value'';';";

        // Act
        var actual = SqlServerSafeMigrationsSqlGenerator.ProtectCommandSql(original, options);

        // Assert
        Assert.Same(original, actual);
    }

    /// <summary>Runtime commands preserve EF continuation and client delimiter shapes without parsing SQL.</summary>
    /// <param name="original">The exact command text the provider's line processing would alter.</param>
    [Theory]
    [InlineData("before\\\nafter")]
    [InlineData("before\\\r\nafter")]
    [InlineData("before\nGO\nafter")]
    [InlineData("before\n\tgo 2\nafter")]
    [InlineData("before\r\nafter")]
    public void RuntimeTransportPreservesLineSensitiveText(string original)
    {
        // Arrange
        const MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default;

        // Act
        var sql = SqlServerSafeMigrationsSqlGenerator.ProtectCommandSql(original, options);
        var payloads = DecodeIdempotentPayloads(sql);

        // Assert
        Assert.Equal(original, Assert.Single(payloads));
    }

    /// <summary>The earlier model-managed mutation pass also preserves a quoted continuation-shaped identity.</summary>
    [Fact]
    public void ManagedDataBaselinePreservesContinuationShapedIdentifier()
    {
        // Arrange
        const string table = "managed\\\nitems";
        using var context = new SafeMigrationDbContext(
            "Server=127.0.0.1,1433;Database=offline_payload;User ID=sa;Password=unused;TrustServerCertificate=True");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(table, ["Id"], ["int"], ["Id"], ["int"], new object?[,] { { 7 } });

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations);
        var pending = new Queue<string>(commands.Select(static command => command.CommandText));
        var payloads = new List<string>();
        while (pending.TryDequeue(out var sql))
        {
            payloads.AddRange(DecodeIdempotentPayloads(sql));
            foreach (System.Text.RegularExpressions.Match scope in System.Text.RegularExpressions.Regex.Matches(
                         sql, "EXEC sys\\.sp_executesql N'((?:''|[^'])*)';"))
            {
                pending.Enqueue(scope.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal));
            }
        }

        // Assert
        Assert.Contains(payloads, static payload => payload.Contains("[" + table + "]", StringComparison.Ordinal));
    }

    /// <summary>Public idempotent generation preserves complete guards across EF's history indentation.</summary>
    [Fact]
    public void IdempotentHistoryFramingPreservesMultilineGuardPayloads()
    {
        // Arrange
        using var context = new SqlServerMultilineScriptContext(
            "Server=127.0.0.1,1433;Database=offline_payload;User ID=sa;Password=unused;TrustServerCertificate=True");
        var assembly = context.GetService<IMigrationsAssembly>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var expected = assembly.Migrations.SelectMany(migration => generator.Generate(
                assembly.CreateMigration(migration.Value, context.Database.ProviderName!).UpOperations,
                options: MigrationsSqlGenerationOptions.Script))
            .SelectMany(static command =>
            {
                var decoded = DecodeIdempotentPayloads(command.CommandText);

                return decoded.Length > 0 ? decoded : [command.CommandText];
            })
            .Select(NormalizePrivateIdentifier)
            .ToArray();

        // Act
        var script = context.GetService<IMigrator>().GenerateScript(
            Migration.InitialDatabase, SqlServerMultilineUpgradeMigration.MigrationIdentifier,
            MigrationsSqlGenerationOptions.Idempotent);

        var payloads = DecodeIdempotentPayloads(script);

        // Assert
        Assert.NotEmpty(payloads);
        Assert.Equal(expected, payloads.Select(NormalizePrivateIdentifier));
        Assert.Contains("IF NOT EXISTS", script, StringComparison.Ordinal);
        Assert.DoesNotContain("before\n    GO", script, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Actual public scripts preserve the authored CRLF, client delimiter and continuation text.</summary>
    /// <param name="idempotent">Whether EF also adds indented history predicates.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicScriptsPreserveAuthoredControlText(bool idempotent)
    {
        // Arrange
        using var context = new SqlServerMultilineScriptContext(
            "Server=127.0.0.1,1433;Database=offline_payload;User ID=sa;Password=unused;TrustServerCertificate=True");
        var options = idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default;

        // Act
        var script = context.GetService<IMigrator>().GenerateScript(
            Migration.InitialDatabase, SqlServerMultilineUpgradeMigration.MigrationIdentifier, options);

        var payloads = DecodeIdempotentPayloads(script);

        // Assert
        Assert.Contains(payloads, static payload => payload.Contains(
            SqlServerMultilineInitialMigration.Payload, StringComparison.Ordinal));
        Assert.Contains(payloads, static payload => payload.Contains(
            SqlServerMultilineUpgradeMigration.ColumnName, StringComparison.Ordinal));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Decodes the emitted literal and newline expression independently of product writer helpers.</summary>
    /// <param name="script">The public EF script, including its real history framing.</param>
    /// <returns>The exact SQL payload reconstructed by each private scripted scope.</returns>
    internal static string[] DecodeIdempotentPayloads(string script)
    {
        var scopes = System.Text.RegularExpressions.Regex.Matches(
            script, "EXEC sys\\.sp_executesql N'((?:''|[^'])*)';");
        var payloads = new List<string>();
        foreach (System.Text.RegularExpressions.Match scope in scopes)
        {
            var body = scope.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal);
            if (!body.StartsWith("DECLARE @doka_script_payload nvarchar(max) = ", StringComparison.Ordinal))
            {
                continue;
            }

            var reconstructed = new StringBuilder();
            foreach (System.Text.RegularExpressions.Match part in System.Text.RegularExpressions.Regex.Matches(
                         body, "N'(?:''|[^'])*'|NCHAR\\((?:10|13)\\)"))
            {
                if (part.Value.StartsWith("N'", StringComparison.Ordinal))
                {
                    reconstructed.Append(part.Value.AsSpan(2, part.Value.Length - 3)
                        .ToString().Replace("''", "'", StringComparison.Ordinal));
                }
                else
                {
                    reconstructed.Append(part.Value == "NCHAR(10)" ? '\n' : '\r');
                }
            }

            payloads.Add(reconstructed.ToString());
        }

        return payloads.ToArray();
    }

    /// <summary>Normalizes only private random identity and external command separators.</summary>
    /// <param name="sql">The complete command whose authored literal content must remain untouched.</param>
    /// <returns>A command with one deterministic private temporary-table identity.</returns>
    private static string NormalizePrivateIdentifier(string sql)
    {
        // WHY: Independent Generate calls intentionally use different GUIDs
        // for their private collision table. This fixture contains no such
        // authored identifier; all user payload characters remain byte-exact.
        return System.Text.RegularExpressions.Regex.Replace(sql.TrimEnd('\r', '\n'),
            "#doka_sm_identifiers_[0-9a-f]{32}", "#doka_sm_identifiers_fixture");
    }

    /// <summary>Generates the actual historical operations without an implicit target-model data mapping.</summary>
    /// <param name="upgrade">Whether to include the computed-column upgrade.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealHistoryUpScriptUsesExplicitHistoricalDataTypes(bool upgrade)
    {
        // Arrange
        using var context = Context();
        var target = upgrade ? SqlServerGeneratedUpgradeMigration.MigrationIdentifier
            : SqlServerGeneratedInitialMigration.MigrationIdentifier;

        // Act
        var script = context.GetService<IMigrator>().GenerateScript("0", target);

        // Assert
        Assert.Contains("INSERT INTO [generated_upgrade_nodes]", script, StringComparison.Ordinal);
        Assert.Contains(SqlServerGeneratedInitialMigration.MigrationIdentifier, script, StringComparison.Ordinal);
        Assert.Equal(upgrade, script.Contains("HasChildren", StringComparison.Ordinal));
    }

    /// <summary>The real reverse migration remains scriptable with the guarded provider enabled.</summary>
    [Fact]
    public void RealHistoryDownScriptRetainsGuardedDropOperations()
    {
        // Arrange
        using var context = Context();

        // Act
        var script = context.GetService<IMigrator>().GenerateScript(
            SqlServerGeneratedUpgradeMigration.MigrationIdentifier, "0");

        // Assert
        Assert.Contains("DROP TABLE", script, StringComparison.Ordinal);
        Assert.Contains("HasChildren", script, StringComparison.Ordinal);
        Assert.Contains(SqlServerGeneratedUpgradeMigration.MigrationIdentifier, script, StringComparison.Ordinal);
    }

    /// <summary>The same fixture cannot silently fall back to nonexistent historical model metadata.</summary>
    [Fact]
    public void RemovingHistoricalStoreTypesReproducesEfDataMappingFailure()
    {
        // Arrange
        using var context = Context();
        var migration = new SqlServerGeneratedInitialMigration { ActiveProvider = context.Database.ProviderName! };
        var insert = migration.UpOperations.OfType<InsertDataOperation>().Single();
        insert.ColumnTypes = null;
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var targetModel = context.GetService<IModelRuntimeInitializer>().Initialize(
            migration.TargetModel, designTime: true);

        // Act
        var failure = Record.Exception(() => generator.Generate(migration.UpOperations, targetModel));

        // Assert
        var actual = Assert.IsType<InvalidOperationException>(failure);

        Assert.Contains("generated_upgrade_nodes", actual.Message, StringComparison.Ordinal);
    }

    private static SqlServerGeneratedUpgradeContext Context()
        => new("Server=127.0.0.1,1433;Database=offline_history;User ID=sa;Password=unused;TrustServerCertificate=True");
}

/// <summary>Discovers isolated multiline-default migrations through the real public EF scripting service.</summary>
public sealed class SqlServerMultilineScriptContext : DbContext
{
    private readonly string _connectionString;

    /// <summary>Creates the fixture context for an offline or independently owned native database.</summary>
    /// <param name="connectionString">The fixture's explicit connection string.</param>
    public SqlServerMultilineScriptContext(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(_connectionString, provider => provider
            .MigrationsAssembly(typeof(SqlServerMultilineScriptContext).Assembly.FullName)
            .CommandTimeout(SafeMigrationDbContext.CommandTimeoutSeconds));
        optionsBuilder.UseSqlServerSafeMigrations<SqlServerMultilineScriptContext>();
    }
}

/// <summary>Creates a raw SQL default whose literal exceeds the non-MAX Unicode concatenation domain.</summary>
[DbContext(typeof(SqlServerMultilineScriptContext))]
[Migration(MigrationIdentifier)]
public sealed class SqlServerMultilineInitialMigration : Migration
{
    /// <summary>Gets the isolated initial migration identity.</summary>
    public const string MigrationIdentifier = "20261025000100_MultilineInitial";

    /// <summary>Gets the original multiline value, including a client batch delimiter inside its literal.</summary>
    public static string Payload { get; } = new string('x', 5000) + "before\r\nGO\nafter";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTableIfNotExists("multiline_payload_items", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
        }, constraints: table => table.PrimaryKey("PK_multiline_payload_items", row => row.Id));
        // WHY: The direct SQL-default API retains authored SQL. Captured table
        // defaults can be rewritten by EF as CONCAT/nchar and would not
        // reproduce this independent command-text transport defect.
        migrationBuilder.AddColumnIfNotExists<string>("Payload", "multiline_payload_items",
            type: "nvarchar(max)", nullable: true,
            defaultValueSql: "'" + Payload.Replace("'", "''", StringComparison.Ordinal) + "'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTableIfExists("multiline_payload_items");
}

/// <summary>Exercises a later migration's independent dynamic scope with another authored multiline default.</summary>
[DbContext(typeof(SqlServerMultilineScriptContext))]
[Migration(MigrationIdentifier)]
public sealed class SqlServerMultilineUpgradeMigration : Migration
{
    /// <summary>Gets the isolated upgrade migration identity.</summary>
    public const string MigrationIdentifier = "20261025000200_MultilineUpgrade";

    /// <summary>Gets the later operation's independently authored default value.</summary>
    public const string Payload = "upgrade's\nsecond line";

    /// <summary>Gets the legal delimited identifier that must not be interpreted as a client continuation.</summary>
    public const string ColumnName = "Later\\\nPayload";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumnIfNotExists<string>(ColumnName, "multiline_payload_items",
            type: "nvarchar(80)", nullable: true,
            defaultValueSql: "'" + Payload.Replace("'", "''", StringComparison.Ordinal) + "'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumnIfExists(ColumnName, "multiline_payload_items");
}
