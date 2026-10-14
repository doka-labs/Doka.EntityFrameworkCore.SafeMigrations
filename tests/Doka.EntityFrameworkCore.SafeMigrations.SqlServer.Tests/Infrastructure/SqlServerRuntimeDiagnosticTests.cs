namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Protects bounded, SQL-escaped rejection messages inside the existing private guard scope.</summary>
public sealed class SqlServerRuntimeDiagnosticTests
{
    /// <summary>Every native rejection retains its number and supplies actionable operation context.</summary>
    /// <param name="code">The existing SafeMigrations guard token.</param>
    /// <param name="number">The existing SQL Server guard error number.</param>
    [Theory]
    [InlineData("doka_sm_different", 51001)]
    [InlineData("doka_sm_unsupported", 51002)]
    [InlineData("doka_sm_data_blocked", 51003)]
    [InlineData("doka_sm_prerequisite_missing", 51004)]
    [InlineData("doka_sm_postcondition", 51005)]
    public void NativeGuardRetainsErrorNumberStateAndActionableMessage(string code, int number)
    {
        // Arrange
        var operation = ColumnOperation("diagnostic_tags", "IsActive");
        var plan = Plan();

        // Act
        var sql = SqlServerGuardedSqlTestContract.DecodeScope(
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []));
        var message = ReadMessage(sql, code, number);

        // Assert
        Assert.StartsWith(code + ":", message, StringComparison.Ordinal);
        Assert.Contains("diagnostic_tags", message, StringComparison.Ordinal);
        Assert.Contains("IsActive", message, StringComparison.Ordinal);
        Assert.Contains("reason=", message, StringComparison.Ordinal);
        Assert.Contains("next=", message, StringComparison.Ordinal);
        Assert.InRange(message.Length, 1, 2048);
        Assert.DoesNotContain("\nGO\n", sql, StringComparison.Ordinal);
    }

    /// <summary>Quote, percent, control and non-ASCII identifiers cannot corrupt T-SQL or message formatting.</summary>
    [Fact]
    public void AdversarialIdentityIsEscapedAndBounded()
    {
        // Arrange
        var operation = ColumnOperation("owner'%; THROW 51099, N'outside', 1; --\n\u20AC",
            new string('x', 3000));

        // Act
        var sql = SqlServerGuardedSqlTestContract.DecodeScope(
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, Plan(), [], []));
        var message = ReadMessage(sql, "doka_sm_data_blocked", 51003);

        // Assert
        Assert.Contains("owner''%%", sql, StringComparison.Ordinal);
        Assert.InRange(message.Length, 1, 2048);
        Assert.All(message, static character => Assert.InRange((int)character, 32, 126));
        Assert.Contains("reason=", message, StringComparison.Ordinal);
        Assert.Contains("next=", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
    }

    /// <summary>The native argument budget includes percent escapes before THROW interprets them.</summary>
    /// <param name="code">The stable SafeMigrations guard token.</param>
    /// <param name="number">The unchanged native rejection number.</param>
    [Theory]
    [InlineData("doka_sm_different", 51001)]
    [InlineData("doka_sm_unsupported", 51002)]
    [InlineData("doka_sm_data_blocked", 51003)]
    [InlineData("doka_sm_prerequisite_missing", 51004)]
    [InlineData("doka_sm_postcondition", 51005)]
    public void MaximumPercentIdentityBoundsTheNativeArgument(string code, int number)
    {
        // Arrange
        var operation = MaximumPercentForeignKeyOperation();

        // Act
        var sql = SqlServerGuardedSqlTestContract.DecodeScope(
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, Plan(), [], []));
        var argument = ReadNativeArgument(sql, code, number);

        // Assert
        Assert.InRange(argument.Length, 1, 2048);
        Assert.StartsWith(code + ":", argument, StringComparison.Ordinal);
        Assert.Contains("owner", argument, StringComparison.Ordinal);
        Assert.Contains("...", argument, StringComparison.Ordinal);
        Assert.Contains("reason=", argument, StringComparison.Ordinal);
        Assert.Contains("next=", argument, StringComparison.Ordinal);
        Assert.True(HasCompletePercentPairs(argument));
    }

    /// <summary>Session-template literals obey the same native budget without splitting percent pairs.</summary>
    [Fact]
    public void SessionLiteralBoundsPercentEscapesBeforeNativeFormatting()
    {
        // Arrange
        var operation = MaximumPercentForeignKeyOperation();
        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, "doka_sm_identity_scope_in_use");

        // Act
        var sql = "THROW 51007, " + SqlServerSafeMigrationRuntimeDiagnostic.FormatLiteral(message) + ", 1;";
        var argument = ReadNativeArgument(sql, "doka_sm_identity_scope_in_use", 51007);

        // Assert
        Assert.InRange(argument.Length, 1, 2048);
        Assert.Contains("owner", argument, StringComparison.Ordinal);
        Assert.Contains("...", argument, StringComparison.Ordinal);
        Assert.True(HasCompletePercentPairs(argument));
    }

    /// <summary>An authored policy-like substring cannot replace the final typed Core policy suffix.</summary>
    /// <param name="operationOrdinal">The known ordinal, or null when there is no trusted ordinal.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public void PercentTruncationRetainsTheRealPolicyAfterASpoofedIdentityMarker(int? operationOrdinal)
    {
        // Arrange
        var owner = "owner; policy=Attacker" + new string('%', 106);
        var operation = MaximumPercentForeignKeyOperation(owner);
        var expectedSuffix = operationOrdinal is null
            ? "; policy=ThrowIfDifferent; reasonCode="
            : "; operation 7; policy=ThrowIfDifferent; reasonCode=";

        // Act
        var sql = SqlServerGuardedSqlTestContract.DecodeScope(
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, Plan(), [], [],
                operationOrdinal: operationOrdinal));
        var argument = ReadNativeArgument(sql, "doka_sm_different", 51001);

        // Assert
        Assert.InRange(argument.Length, 1, 2048);
        Assert.Contains("owner; policy=Attacker", argument, StringComparison.Ordinal);
        Assert.Contains(expectedSuffix, argument, StringComparison.Ordinal);
        Assert.Equal(operationOrdinal.HasValue, argument.Contains("operation 7", StringComparison.Ordinal));
        Assert.Contains("reason=", argument, StringComparison.Ordinal);
        Assert.Contains("next=", argument, StringComparison.Ordinal);
        Assert.True(HasCompletePercentPairs(argument));
    }

    /// <summary>Native percent budgeting cuts back to the start of a partially fitting Core display escape.</summary>
    /// <param name="displayUnit">The complete Core display unit placed across the logical prefix boundary.</param>
    /// <param name="inside">The number of unit characters that would fit without escape-aware truncation.</param>
    [Theory]
    [InlineData("\\u0001", 1)]
    [InlineData("\\u0001", 2)]
    [InlineData("\\u0001", 3)]
    [InlineData("\\u0001", 4)]
    [InlineData("\\u0001", 5)]
    [InlineData("\\\\", 1)]
    public void NativePrefixBudgetPreservesCompleteCoreDisplayEscapes(string displayUnit, int inside)
    {
        // Arrange
        const string label = "doka_sm_data_blocked: owner=[";
        const string suffix = "; policy=ThrowIfDifferent; reasonCode=data_blocked; reason=known; next=known";
        var prefixBudget = 2048 - 3 - suffix.Length;
        var fillerLength = prefixBudget - label.Length - 1800 - inside;
        var prefix = label + new string('%', 900) + new string('x', fillerLength);
        var message = new StringBuilder(prefix + displayUnit + "tail]" + suffix);

        // Act
        var sql = RenderEscapedCoreScratch(message);
        var emitted = ReadMessage(sql, "doka_sm_data_blocked", 51003);
        var argument = ReadNativeArgument(sql, "doka_sm_data_blocked", 51003);

        // Assert
        Assert.Equal(prefix + "..." + suffix, emitted);
        Assert.InRange(argument.Length, 1, 2048);
        Assert.True(HasCompletePercentPairs(argument));
    }

    /// <summary>Raw generic literal text must not be interpreted as Core display escape notation.</summary>
    [Fact]
    public void GenericLiteralBudgetDoesNotInterpretRawBackslashText()
    {
        // Arrange
        const string label = "doka_sm_identity_scope_in_use: owner=";
        var prefix = label + new string('%', 900) + new string('x', 2045 - label.Length - 1800 - 2);
        var message = prefix + "\\u0001tail";

        // Act
        var sql = "THROW 51007, " + SqlServerSafeMigrationRuntimeDiagnostic.FormatLiteral(message) + ", 1;";
        var emitted = ReadMessage(sql, "doka_sm_identity_scope_in_use", 51007);
        var argument = ReadNativeArgument(sql, "doka_sm_identity_scope_in_use", 51007);

        // Assert
        Assert.Equal(prefix + "\\u...", emitted);
        Assert.InRange(argument.Length, 1, 2048);
        Assert.True(HasCompletePercentPairs(argument));
    }

    /// <summary>
    /// Real managed-data session templates retain their whole identity before generic native truncation.
    /// </summary>
    /// <param name="template">The actual bootstrap or recovery template receiving the Core message.</param>
    /// <param name="character">The authored Unicode, control or backslash character at each identifier's end.</param>
    /// <param name="displayUnit">The complete Core display representation expected after SQL decoding.</param>
    [Theory]
    [InlineData("bootstrap", "\u0001", "\\u0001")]
    [InlineData("recovery", "\u0001", "\\u0001")]
    [InlineData("bootstrap", "\u20AC", "\\u20AC")]
    [InlineData("recovery", "\u20AC", "\\u20AC")]
    [InlineData("bootstrap", "\\", "\\\\")]
    [InlineData("recovery", "\\", "\\\\")]
    public void ManagedDataSessionTemplatesRetainWorstWidthEscapedIdentities(string template, string character,
        string displayUnit)
    {
        // Arrange
        var schema = "S" + new string('%', 127 - displayUnit.Length) + character;
        var table = "owner" + new string('%', 123 - displayUnit.Length) + character;
        var columnSuffix = new string('%', 126 - displayUnit.Length) + character;
        string[] columns = ["C0" + columnSuffix, "C1" + columnSuffix, "C2" + columnSuffix, "C3" + columnSuffix];
        var operation = new SafeMigrationOperation(new EnsureModelManagedDataIntent(table,
            [columns[0]], ["int"], columns, ["int", "int", "int", "int"],
            new object?[,] { { 1, 2, 3, 4 } }, schema, null), SafeMigrationPolicy.ThrowIfDifferent);

        var identity = SafeMigrationDiagnosticMessages.FormatIdentity(
            SafeMigrationOperationIdentity.Create(operation.Intent));

        var code = template == "bootstrap"
            ? "doka_sm_identity_scope_in_use"
            : "doka_sm_identity_acquisition_unproven";

        var message = SafeMigrationDiagnosticMessages.FormatRuntime(operation, code, int.MaxValue);

        // Act
        var sql = template == "bootstrap"
            ? SqlServerSafeMigrationIdentityInsertScope.BuildBootstrapSql("0123456789ABCDEF0123456789ABCDEF", message)
            : SqlServerSafeMigrationIdentityInsertScope.BuildRecoverySql("0123456789ABCDEF0123456789ABCDEF",
                "SET IDENTITY_INSERT [dbo].[owner] OFF;", acquisitionUnprovenMessage: message);

        var emitted = ReadMessage(sql, code, 51007);
        var argument = ReadNativeArgument(sql, code, 51007);

        // Assert
        Assert.Contains(identity, emitted, StringComparison.Ordinal);
        Assert.Contains(displayUnit, emitted, StringComparison.Ordinal);
        Assert.InRange(argument.Length, 1, 2048);
        Assert.All(emitted, static value => Assert.InRange((int)value, 32, 126));
        Assert.True(HasCompletePercentPairs(argument));
    }

    /// <summary>
    /// Generic session literal text never interprets a caller-supplied policy substring as trusted metadata.
    /// </summary>
    [Fact]
    public void GenericLiteralTruncationDoesNotTrustPolicyLikeText()
    {
        // Arrange
        var message = "doka_sm_identity_scope_in_use: owner=" + new string('%', 2048)
            + "; policy=Attacker; reason=fake";

        // Act
        var sql = "THROW 51007, " + SqlServerSafeMigrationRuntimeDiagnostic.FormatLiteral(message) + ", 1;";
        var argument = ReadNativeArgument(sql, "doka_sm_identity_scope_in_use", 51007);

        // Assert
        Assert.InRange(argument.Length, 1, 2048);
        Assert.StartsWith("doka_sm_identity_scope_in_use: owner=", argument, StringComparison.Ordinal);
        Assert.EndsWith("...", argument, StringComparison.Ordinal);
        Assert.DoesNotContain("Attacker", argument, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=fake", argument, StringComparison.Ordinal);
        Assert.True(HasCompletePercentPairs(argument));
    }

    /// <summary>FK refusal identifies schemas and ordered composite columns without claiming absence.</summary>
    [Fact]
    public void ForeignKeyMessageIdentifiesDependentAndPrincipal()
    {
        // Arrange
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(
            new ExpectedForeignKeyDefinition("FK_child_parent", "children", ["TenantId", "ParentId"],
                "parents", ["TenantId", "Id"], "application", "catalog")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var sql = SqlServerGuardedSqlTestContract.DecodeScope(
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, Plan(), [], []));
        var message = ReadMessage(sql, "doka_sm_prerequisite_missing", 51004);

        // Assert
        Assert.Contains("application", message, StringComparison.Ordinal);
        Assert.Contains("children", message, StringComparison.Ordinal);
        Assert.Contains("catalog", message, StringComparison.Ordinal);
        Assert.Contains("parents", message, StringComparison.Ordinal);
        Assert.Contains("[TenantId],[ParentId]", message, StringComparison.Ordinal);
        Assert.Contains("[TenantId],[Id]", message, StringComparison.Ordinal);
        Assert.DoesNotContain("A required table is missing", message, StringComparison.Ordinal);
    }

    /// <summary>Generation ordinals come from the complete stream, including ordinary provider operations.</summary>
    [Fact]
    public void GeneratedGuardIncludesTheActualStreamOrdinal()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=localhost;Database=runtime_diagnostics;TrustServerCertificate=True");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Sql("SELECT 1;");
        builder.AddColumnIfNotExists<bool>("IsActive", "diagnostic_tags", type: "bit", nullable: false);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var guard = Assert.Single(commands, static command =>
            command.CommandText.Contains("@doka_state", StringComparison.Ordinal));

        var message = ReadMessage(SqlServerGuardedSqlTestContract.DecodeScope(guard.CommandText),
            "doka_sm_data_blocked", 51003);

        // Assert
        Assert.Contains("operation 1", message, StringComparison.Ordinal);
        Assert.DoesNotContain("operation 0", message, StringComparison.Ordinal);
    }

    /// <summary>Stream-level ambiguity is explained without falsely assigning it to one table or ordinal.</summary>
    [Fact]
    public void IdentifierContractMessageDoesNotInventAnOperationOwner()
    {
        // Arrange
        SqlServerIdentifierReference[] identifiers =
        [new(SqlServerIdentifierScope.SchemaObject, "dbo", null, new string('x', 129))];

        // Act
        var sql = Assert.Single(SqlServerIdentifierContract.BuildCollisionGuardCommands(identifiers,
            throwOnCollision: true));
        var message = ReadMessage(sql, "doka_sm_unsupported", 51002);

        // Assert
        Assert.Contains("owner=<unavailable>", message, StringComparison.Ordinal);
        Assert.Contains("catalog-colliding", message, StringComparison.Ordinal);
        Assert.Contains("next=", message, StringComparison.Ordinal);
        Assert.DoesNotContain("operation 0", message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 129), message, StringComparison.Ordinal);
    }

    /// <summary>Exact layout causes are selected from one existing proof evaluation before any row binding.</summary>
    [Fact]
    public void LayoutCauseSelectionEvaluatesItsCatalogExpressionExactlyOnce()
    {
        // Arrange
        const string expression = "CAST(N'column_limit' AS nvarchar(64))";
        var operation = ColumnOperation("layout_owner", "AddedColumn");
        var plan = Plan() with { ColumnLayoutFailureExpression = expression };

        // Act
        var sql = SqlServerGuardedSqlTestContract.DecodeScope(
            SqlServerSafeMigrationsSqlGenerator.BuildGuardedSql(operation, plan, [], []));

        // Assert
        Assert.Equal(1, sql.Split(expression, StringSplitOptions.None).Length - 1);
        Assert.Contains("DECLARE @doka_layout_message nvarchar(2048)", sql, StringComparison.Ordinal);
        Assert.Contains("reasonCode=column_limit", sql, StringComparison.Ordinal);
        Assert.Contains("reasonCode=column_fixed_row_limit", sql, StringComparison.Ordinal);
        Assert.Contains("reasonCode=column_layout_unproven", sql, StringComparison.Ordinal);
        Assert.True(sql.IndexOf("THROW 51002, @doka_layout_message", StringComparison.Ordinal)
            < sql.IndexOf("DECLARE @doka_state", StringComparison.Ordinal));
    }

    /// <summary>Reads one SQL THROW literal while retaining its unchanged state-one terminator.</summary>
    /// <param name="sql">The decoded operation-private T-SQL guard body.</param>
    /// <param name="code">The unchanged SafeMigrations guard token.</param>
    /// <param name="number">The existing error number associated with that guard token.</param>
    internal static string ReadMessage(string sql, string code, int number)
        => ReadNativeArgument(sql, code, number).Replace("%%", "%", StringComparison.Ordinal);

    /// <summary>Decodes SQL quote pairs but retains percent pairs counted in the native argument budget.</summary>
    /// <param name="sql">The decoded operation-private T-SQL guard body.</param>
    /// <param name="code">The unchanged SafeMigrations guard token.</param>
    /// <param name="number">The existing error number associated with the guard token.</param>
    private static string ReadNativeArgument(string sql, string code, int number)
    {
        var prefix = "THROW " + number.ToString(CultureInfo.InvariantCulture) + ", N'" + code;
        var offset = sql.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(offset >= 0, "The expected native rejection was not generated.");
        offset += prefix.Length - code.Length;
        var result = new StringBuilder();
        while (offset < sql.Length)
        {
            var character = sql[offset++];
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

            Assert.StartsWith(", 1;", sql[offset..], StringComparison.Ordinal);

            return result.ToString();
        }

        throw new InvalidOperationException("The generated THROW literal was not terminated.");
    }

    /// <summary>Invokes the private Core-scratch codec with deterministic escaped-text boundaries.</summary>
    /// <param name="message">Printable Core-style text with a trusted typed policy suffix.</param>
    /// <returns>A complete THROW statement containing the bounded native argument.</returns>
    private static string RenderEscapedCoreScratch(StringBuilder message)
    {
        var diagnostic = new SqlServerSafeMigrationRuntimeDiagnostic(ColumnOperation("owner", "Id"), null);
        var method = typeof(SqlServerSafeMigrationRuntimeDiagnostic).GetMethod("AppendEscaped",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The SQL Server scratch codec is unavailable.");

        var sql = new StringBuilder("THROW 51003, N'");
        _ = method.Invoke(diagnostic, [sql, message]);

        return sql.Append("', 1;").ToString();
    }

    /// <summary>Creates a required-column contract without a default or other private payload.</summary>
    /// <param name="table">The authored owner, including adversarial identifier characters.</param>
    /// <param name="column">The authored column name.</param>
    internal static SafeMigrationOperation ColumnOperation(string table, string column)
        => new(new EnsureColumnIntent(table,
            new ExpectedColumnDefinition(column, typeof(bool), false, "bit")),
            SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Creates valid-width composite identifiers that maximize native percent-format expansion.</summary>
    /// <param name="owner">An optional valid-width adversarial table identifier.</param>
    internal static SafeMigrationOperation MaximumPercentForeignKeyOperation(string? owner = null)
    {
        var identifier = new string('%', 128);
        var table = owner ?? "owner" + new string('%', 123);
        var suffix = new string('%', 126);

        return new SafeMigrationOperation(new EnsureForeignKeyIntent(
            new ExpectedForeignKeyDefinition(identifier, table,
                ["C0" + suffix, "C1" + suffix, "C2" + suffix, "C3" + suffix], identifier,
                ["P0" + suffix, "P1" + suffix, "P2" + suffix, "P3" + suffix], identifier, identifier)),
            SafeMigrationPolicy.ThrowIfDifferent);
    }

    /// <summary>Checks every native percent escape pair rather than merely the overall percent count.</summary>
    /// <param name="argument">The decoded SQL string before native percent formatting.</param>
    /// <returns>Whether every percent is followed by its paired escape character.</returns>
    private static bool HasCompletePercentPairs(string argument)
    {
        for (var index = 0; index < argument.Length; index++)
        {
            if (argument[index] != '%')
            {
                continue;
            }

            index++;
            if (index == argument.Length || argument[index] != '%')
            {
                return false;
            }

        }

        return true;
    }

    /// <summary>Provides a direct scalar fixture without extra metadata or delayed binding.</summary>
    internal static SqlServerSafeMigrationRuntimePlan Plan()
        => new("N'matching'", "1", SafeMigrationRepairCapability.None, "0");
}
