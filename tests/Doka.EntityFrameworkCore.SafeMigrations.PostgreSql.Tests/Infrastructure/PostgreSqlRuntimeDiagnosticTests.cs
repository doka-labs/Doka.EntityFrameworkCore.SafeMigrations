namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Protects script-visible guard identities without depending on client error-detail settings.</summary>
public sealed class PostgreSqlRuntimeDiagnosticTests
{
    /// <summary>Every native rejection carries its unchanged code and an actionable operation identity.</summary>
    /// <param name="code">The existing SafeMigrations native guard token.</param>
    /// <param name="sqlState">The unchanged PostgreSQL SQLSTATE.</param>
    [Theory]
    [InlineData("doka_sm_different", "P1001")]
    [InlineData("doka_sm_unsupported", "P1002")]
    [InlineData("doka_sm_data_blocked", "P1003")]
    [InlineData("doka_sm_prerequisite_missing", "P1004")]
    [InlineData("doka_sm_postcondition", "P1005")]
    public void NativeMessageCarriesIdentityReasonAndNextStep(string code, string sqlState)
    {
        // Arrange
        using var context = Context();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<bool>("IsActive", "diagnostic_tags", type: "boolean", nullable: false);

        // Act
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model)).CommandText;
        var message = ReadMessage(sql, code);

        // Assert
        Assert.StartsWith(code + ":", message, StringComparison.Ordinal);
        Assert.Contains("diagnostic_tags", message, StringComparison.Ordinal);
        Assert.Contains("IsActive", message, StringComparison.Ordinal);
        Assert.Contains("reason=", message, StringComparison.Ordinal);
        Assert.Contains("next=", message, StringComparison.Ordinal);
        Assert.Contains("operation 0", message, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = '" + sqlState + "'", sql, StringComparison.Ordinal);
        Assert.InRange(message.Length, 1, 2048);
    }

    /// <summary>Foreign-key errors identify both ordered endpoints instead of only the constraint name.</summary>
    [Fact]
    public void ForeignKeyMessageRetainsCompositeEndpoints()
    {
        // Arrange
        using var context = Context();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureForeignKey(new ExpectedForeignKeyDefinition("FK_children_parent", "children",
            ["TenantId", "ParentId"], "parents", ["TenantId", "Id"], "application", "catalog"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model)).CommandText;
        var message = ReadMessage(sql, "doka_sm_prerequisite_missing");

        // Assert
        Assert.Contains("application", message, StringComparison.Ordinal);
        Assert.Contains("children", message, StringComparison.Ordinal);
        Assert.Contains("catalog", message, StringComparison.Ordinal);
        Assert.Contains("parents", message, StringComparison.Ordinal);
        Assert.Contains("[TenantId],[ParentId]", message, StringComparison.Ordinal);
        Assert.Contains("[TenantId],[Id]", message, StringComparison.Ordinal);
        Assert.DoesNotContain("A required table is missing", message, StringComparison.Ordinal);
    }

    /// <summary>Messages alone can collide with a dollar delimiter and must participate in its selection.</summary>
    [Fact]
    public void UnsupportedIdentityCannotTerminateTheAnonymousBlock()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("$doka_safe_migration$", null),
            SafeMigrationPolicy.ThrowIfDifferent);
        var plan = new PostgreSqlSafeMigrationRuntimePlan("'unsupported'", "FALSE",
            SafeMigrationRepairCapability.None, "FALSE", "unsupported_fixture")
        {
            IsStaticallyUnsupported = true,
        };

        // Act
        var sql = Render(operation, plan);
        var message = ReadMessage(sql, "doka_sm_unsupported");

        // Assert
        Assert.StartsWith("DO $doka_safe_migration_1$", sql, StringComparison.Ordinal);
        Assert.EndsWith("$doka_safe_migration_1$;", sql, StringComparison.Ordinal);
        Assert.Contains("$doka_safe_migration$", message, StringComparison.Ordinal);
        Assert.Contains("unsupported_fixture", message, StringComparison.Ordinal);
    }

    /// <summary>Every rendered guard message is disjoint from the chosen anonymous-block delimiter.</summary>
    /// <param name="placement">The identity endpoint receiving hostile delimiter candidates.</param>
    [Theory]
    [InlineData("owner")]
    [InlineData("schema")]
    [InlineData("member")]
    [InlineData("foreign_key_name")]
    [InlineData("foreign_key_column")]
    [InlineData("foreign_key_principal")]
    [InlineData("foreign_key_principal_schema")]
    [InlineData("foreign_key_principal_column")]
    [InlineData("rename_column_target")]
    [InlineData("rename_table_target")]
    [InlineData("rename_table_schema")]
    public void ChosenDelimiterIsAbsentFromEveryRenderedMessage(string placement)
    {
        // Arrange
        var operation = HostileDelimiterOperation(placement);
        var plan = new PostgreSqlSafeMigrationRuntimePlan("'different'", "TRUE",
            SafeMigrationRepairCapability.None, "FALSE");

        // Act
        var sql = Render(operation, plan);
        var delimiterEnd = sql.IndexOf('$', "DO $".Length);
        var delimiter = sql["DO ".Length..(delimiterEnd + 1)];
        var messages = ReadAllMessages(sql);

        // Assert
        Assert.Equal("$doka_safe_migration_2$", delimiter);
        Assert.Equal(5, messages.Count);
        Assert.All(messages, message => Assert.DoesNotContain(delimiter, message, StringComparison.Ordinal));
        Assert.Equal(2, sql.Split(delimiter, StringSplitOptions.None).Length - 1);
        Assert.Contains(messages, static message => message.StartsWith("doka_sm_different:", StringComparison.Ordinal));
        Assert.Contains(messages, static message =>
            message.StartsWith("doka_sm_postcondition:", StringComparison.Ordinal));
    }

    /// <summary>A reserved suffix budget cannot leave a partial Core display escape in the emitted MESSAGE.</summary>
    /// <param name="identifier">The authored character requiring a Core display escape.</param>
    /// <param name="displayUnit">The complete escaped display unit.</param>
    /// <param name="inside">The requested prefix boundary inside the unit.</param>
    [Theory]
    [InlineData("\u0001", "\\u0001", 1)]
    [InlineData("\u0001", "\\u0001", 2)]
    [InlineData("\u0001", "\\u0001", 3)]
    [InlineData("\u0001", "\\u0001", 4)]
    [InlineData("\u0001", "\\u0001", 5)]
    [InlineData("\\", "\\\\", 1)]
    public void ReservedMessageBudgetPreservesCompleteDisplayEscapes(string identifier, string displayUnit, int inside)
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("owner" + identifier + "tail"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_different");
        var unitOffset = message.IndexOf(displayUnit, StringComparison.Ordinal);
        var maximumLength = unitOffset + inside + 3;
        var diagnostic = new PostgreSqlSafeMigrationRuntimeDiagnostic(operation, null);
        var sql = new StringBuilder("MESSAGE = ");

        // Act
        diagnostic.AppendMessage(sql, "doka_sm_different", maximumLength: maximumLength);
        var emitted = ReadMessage(sql.ToString(), "doka_sm_different");

        // Assert
        Assert.True(unitOffset >= 0);
        Assert.Equal(message[..unitOffset] + "...", emitted);
        Assert.InRange(emitted.Length, 1, maximumLength);
    }

    /// <summary>A full-copy budget preserves escaped identifier text byte for byte.</summary>
    [Fact]
    public void CompleteMessageBudgetDoesNotAlterDisplayEscapes()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("owner\\\u0001\u20AC'"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_different");
        var diagnostic = new PostgreSqlSafeMigrationRuntimeDiagnostic(operation, null);
        var sql = new StringBuilder("MESSAGE = ");

        // Act
        diagnostic.AppendMessage(sql, "doka_sm_different", maximumLength: message.Length);
        var emitted = ReadMessage(sql.ToString(), "doka_sm_different");

        // Assert
        Assert.Equal(message, emitted);
    }

    /// <summary>Reserved budgets too small for the explicit truncation marker fail at the codec boundary.</summary>
    /// <param name="maximumLength">The invalid reserved native message budget.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReservedMessageBudgetRejectsInsufficientMarkerCapacity(int maximumLength)
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("owner"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var diagnostic = new PostgreSqlSafeMigrationRuntimeDiagnostic(operation, null);
        var sql = new StringBuilder();

        // Act
        var failure = Record.Exception(() => diagnostic.AppendMessage(sql, "doka_sm_different",
            maximumLength: maximumLength));

        // Assert
        var argument = Assert.IsType<ArgumentOutOfRangeException>(failure);
        Assert.Equal("maximumLength", argument.ParamName);
        Assert.Equal(0, sql.Length);
    }

    /// <summary>The minimum supported message budget is occupied solely by its explicit truncation marker.</summary>
    [Fact]
    public void ReservedMessageBudgetAcceptsTheExactMarkerCapacity()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("owner"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var diagnostic = new PostgreSqlSafeMigrationRuntimeDiagnostic(operation, null);
        var sql = new StringBuilder();

        // Act
        diagnostic.AppendMessage(sql, "doka_sm_different", maximumLength: 3);

        // Assert
        Assert.Equal("E'...'", sql.ToString());
    }

    /// <summary>Provider identity failures remain exact Detail codes and readable without Detail disclosure.</summary>
    [Fact]
    public void OwnerFailureCodeIsIncludedInMessageWithoutASecondEvaluation()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new DropTableIntent("guarded_owner", null),
            SafeMigrationPolicy.ThrowIfDifferent);
        var plan = new PostgreSqlSafeMigrationRuntimePlan("'matching'", "TRUE",
            SafeMigrationRepairCapability.None, "FALSE")
        {
            ObjectIdentityGuardExpression = "FALSE",
            ObjectIdentityGuardFailureCodeExpression = "'schema_search_path_mismatch'",
        };

        // Act
        var sql = Render(operation, plan);

        // Assert
        Assert.Contains("MESSAGE = ", sql, StringComparison.Ordinal);
        Assert.Contains("provider_code=", sql, StringComparison.Ordinal);
        Assert.Contains("DETAIL = doka_identity_failure", sql, StringComparison.Ordinal);
        Assert.Equal(1, sql.Split("'schema_search_path_mismatch'", StringSplitOptions.None).Length - 1);
    }

    /// <summary>Bounds printable diagnostics and excludes authored literal payloads from native messages.</summary>
    [Fact]
    public void LongIdentityIsBoundedWithoutDefaultPayload()
    {
        // Arrange
        using var context = Context();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>(new string('c', 3000), new string('t', 3000),
            type: "text", nullable: false, defaultValue: "private-default-payload");

        // Act
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model)).CommandText;
        var message = ReadMessage(sql, "doka_sm_data_blocked");

        // Assert
        Assert.InRange(message.Length, 1, 2048);
        Assert.All(message, static character => Assert.InRange((int)character, 32, 126));
        Assert.DoesNotContain("private-default-payload", message, StringComparison.Ordinal);
        Assert.Contains("reason=", message, StringComparison.Ordinal);
        Assert.Contains("next=", message, StringComparison.Ordinal);
    }

    /// <summary>Reads the native MESSAGE string without interpreting SQL or requiring a database.</summary>
    /// <param name="sql">The generated anonymous-block SQL.</param>
    /// <param name="code">The unchanged guard token whose first diagnostic is selected.</param>
    internal static string ReadMessage(string sql, string code)
    {
        var prefix = "MESSAGE = E'" + code;
        var offset = sql.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(offset >= 0, "The expected guard message was not generated.");
        offset += "MESSAGE = E'".Length;

        return ReadLiteral(sql, offset).Value;
    }

    /// <summary>Reads every complete MESSAGE literal, skipping marker-like text inside quoted identities.</summary>
    /// <param name="sql">The complete anonymous-block SQL.</param>
    /// <returns>The ordered decoded MESSAGE literal texts.</returns>
    private static List<string> ReadAllMessages(string sql)
    {
        const string prefix = "MESSAGE = E'";
        var messages = new List<string>();
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var literal = ReadLiteral(sql, offset + prefix.Length);
            messages.Add(literal.Value);
            offset = literal.End;
        }

        return messages;
    }

    /// <summary>Decodes one E-string literal and reports its complete source extent.</summary>
    /// <param name="sql">The generated anonymous-block SQL.</param>
    /// <param name="offset">The first character after the E-string opening quote.</param>
    /// <returns>The decoded literal and the offset after its closing quote.</returns>
    private static (string Value, int End) ReadLiteral(string sql, int offset)
    {
        var result = new StringBuilder();
        while (offset < sql.Length)
        {
            var character = sql[offset++];
            if (character == '\\' && offset < sql.Length && sql[offset] == '\\')
            {
                result.Append('\\');
                offset++;
                continue;
            }

            if (character != '\'')
            {
                result.Append(character);
                continue;
            }

            if (offset < sql.Length && sql[offset] == '\'')
            {
                result.Append('\'');
                offset++;
                continue;
            }

            return (result.ToString(), offset);
        }

        throw new InvalidOperationException("The generated MESSAGE literal was not terminated.");
    }

    /// <summary>Provides hostile authored endpoints without provider SQL or private data payloads.</summary>
    /// <param name="placement">The identity endpoint being exercised.</param>
    /// <returns>The operation whose rendered messages must not terminate the dollar-quoted block.</returns>
    internal static SafeMigrationOperation HostileDelimiterOperation(string placement)
    {
        const string identifier = "$doka_safe_migration$/$doka_safe_migration_1$/'\\\n\u0001\u20AC";
        var intent = placement switch
        {
            "owner" => (SafeMigrationIntent)new DropTableIntent(identifier),
            "schema" => new DropTableIntent("owner", identifier),
            "member" => new EnsureColumnIntent("owner",
                new ExpectedColumnDefinition(identifier, typeof(int), false, "integer")),
            "foreign_key_name" => new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                identifier, "owner", ["ParentId"], "principal", ["Id"])),
            "foreign_key_column" => new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "FK_owner_principal", "owner", [identifier], "principal", ["Id"])),
            "foreign_key_principal" => new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "FK_owner_principal", "owner", ["ParentId"], identifier, ["Id"])),
            "foreign_key_principal_schema" => new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "FK_owner_principal", "owner", ["ParentId"], "principal", ["Id"], principalSchema: identifier)),
            "foreign_key_principal_column" => new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "FK_owner_principal", "owner", ["ParentId"], "principal", [identifier])),
            "rename_column_target" => new RenameColumnIntent("before", "owner", identifier),
            "rename_table_target" => new RenameTableIntent("before", identifier),
            "rename_table_schema" => new RenameTableIntent("before", newSchema: identifier),
            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };

        return new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
    }

    /// <summary>Invokes only the private operation renderer for adversarial prepared-plan fixtures.</summary>
    /// <param name="operation">The payload-free test contract identity.</param>
    /// <param name="plan">The prepared state and postcondition fixture.</param>
    internal static string Render(SafeMigrationOperation operation, PostgreSqlSafeMigrationRuntimePlan plan)
    {
        var method = typeof(PostgreSqlSafeMigrationsSqlGenerator).GetMethod("BuildGuardedSql",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The PostgreSQL guard renderer is unavailable.");

        return (string)method.Invoke(null, [operation, plan, Array.Empty<MigrationCommand>(),
            Array.Empty<MigrationCommand>()])!;
    }

    /// <summary>Creates connection-free provider services for deterministic generation.</summary>
    private static SafeMigrationDbContext Context()
        => new("Host=localhost;Database=runtime_diagnostics;Username=unused;Password=unused");
}
