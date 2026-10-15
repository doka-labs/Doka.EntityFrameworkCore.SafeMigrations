namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Locks exact owned guard transport, attribution, bounded payloads and query-free diagnostic rendering.</summary>
public sealed class MySqlRuntimeDiagnosticsTests
{
    private static readonly string[] s_ownedAssertionColumns =
    [
        "`different_code` VARCHAR(40) NOT NULL",
        "`unsupported_code` VARCHAR(40) NOT NULL",
        "`data_blocked_code` VARCHAR(40) NOT NULL",
        "`prerequisite_missing_code` VARCHAR(40) NOT NULL",
        "`postcondition_code` VARCHAR(40) NOT NULL",
    ];

    /// <summary>Both engines' key qualification styles retain exact native category and reason-slot ownership.</summary>
    /// <param name="keyPrefix">The table-qualified MySQL or unqualified MariaDB key name.</param>
    /// <param name="guardCode">The existing native guard constraint name.</param>
    [Theory]
    [InlineData("", "doka_sm_different")]
    [InlineData("", "doka_sm_unsupported")]
    [InlineData("", "doka_sm_data_blocked")]
    [InlineData("", "doka_sm_prerequisite_missing")]
    [InlineData("", "doka_sm_postcondition")]
    [InlineData("__doka_sm_assert.", "doka_sm_data_blocked")]
    [InlineData("__doka_sm_assert.", "doka_sm_postcondition")]
    public void OwnedDuplicateTransportPreservesCategoryAndCorrelation(string keyPrefix, string guardCode)
    {
        // Arrange
        var diagnostic = Diagnostic();
        var payload = diagnostic.Correlation + "-0";
        var message = "Duplicate entry '" + payload + "' for key '" + keyPrefix + guardCode + "'";

        // Act
        var recognized = diagnostic.TryReadFailure(1062, "23000", message, out var actualCode, out var slot);

        // Assert
        Assert.True(recognized);
        Assert.Equal(guardCode, actualCode);
        Assert.Equal(0, slot);
        Assert.InRange(payload.Length, 1, 64);
        Assert.All(payload, character => Assert.InRange((int)character, 0, 127));
    }

    /// <summary>Ordinary duplicate failures, spoofed names, stale tokens and malformed transport remain native.</summary>
    /// <param name="defect">The independently varied ownership invariant.</param>
    [Theory]
    [InlineData("ordinary_value")]
    [InlineData("stale_token")]
    [InlineData("similar_key")]
    [InlineData("other_table")]
    [InlineData("other_number")]
    [InlineData("other_sqlstate")]
    [InlineData("unknown_slot")]
    [InlineData("unproven_slot")]
    [InlineData("missing_quote")]
    [InlineData("extra_text")]
    [InlineData("wrong_duplicate_code")]
    public void UnownedOrMalformedNativeErrorsAreNotReclassified(string defect)
    {
        // Arrange
        var diagnostic = Diagnostic();
        var value = defect switch
        {
            "ordinary_value" => "1",
            "stale_token" => Guid.NewGuid().ToString("N") + "-0",
            "unknown_slot" => diagnostic.Correlation + "-10",
            "unproven_slot" => diagnostic.Correlation + "-7",
            "wrong_duplicate_code" => diagnostic.Correlation + "-0-1",
            _ => diagnostic.Correlation + "-0",
        };

        var key = defect switch
        {
            "similar_key" => "doka_sm_data_blocked_external",
            "other_table" => "records.doka_sm_data_blocked",
            _ => "doka_sm_data_blocked",
        };

        var message = "Duplicate entry '" + value + "' for key '" + key
            + (defect == "missing_quote" ? string.Empty : "'")
            + (defect == "extra_text" ? " additional user message" : string.Empty);

        // Act
        var recognized = diagnostic.TryReadFailure(defect == "other_number" ? 1146 : 1062,
            defect == "other_sqlstate" ? "HY000" : "23000", message, out _, out _);

        // Assert
        Assert.False(recognized);
    }

    /// <summary>Markers are bounded, exact and ordinal-associated without scanning or copying entire commands.</summary>
    [Fact]
    public void CommandMarkerRoundTripsExactAuthoredOwnership()
    {
        // Arrange
        var diagnostic = Diagnostic();
        var commandText = "\r\n " + diagnostic.Marker + "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`;";

        // Act
        var recognized = MySqlSafeMigrationRuntimeDiagnostic.TryReadMarker(commandText, out var ordinal,
            out var correlation);

        // Assert
        Assert.True(recognized);
        Assert.Equal(17, ordinal);
        Assert.True(correlation.Equals(diagnostic.Correlation, StringComparison.Ordinal));
    }

    /// <summary>No marker substring inside ordinary SQL or malformed correlation may acquire runtime ownership.</summary>
    /// <param name="sql">A structurally invalid, incomplete or non-leading authored marker.</param>
    [Theory]
    [InlineData("SELECT 1;/*doka_sm_runtime:17:0123456789abcdef0123456789abcdef*/")]
    [InlineData("/*doka_sm_runtime:-1:0123456789abcdef0123456789abcdef*/")]
    [InlineData("/*doka_sm_runtime:0017:0123456789abcdef0123456789abcdef*/")]
    [InlineData("/*doka_sm_runtime:2147483648:0123456789abcdef0123456789abcdef*/")]
    [InlineData("/*doka_sm_runtime:17:0123456789abcdef0123456789abcdeg*/")]
    [InlineData("/*doka_sm_runtime:17:0123456789abcdef0123456789abcde*/")]
    [InlineData("/*doka_sm_runtime:17:0123456789abcdef0123456789abcdef")]
    public void InvalidOrNonLeadingMarkerCannotOwnACommand(string sql)
    {
        // Arrange
        // Act
        var recognized = MySqlSafeMigrationRuntimeDiagnostic.TryReadMarker(sql, out _, out _);

        // Assert
        Assert.False(recognized);
    }

    /// <summary>Explanation selection uses completed proof variables, never duplicated catalog or row queries.</summary>
    [Fact]
    public void ReasonSlotsConsumeOnlyMaterializedFacts()
    {
        // Arrange
        var plan = Plan() with
        {
            CurrentDatabaseQualificationFailureCode = "database_qualifier_mismatch",
            RequiredColumnAddPlan = new MySqlSafeMigrationRequiredColumnAddPlan("guard", "status", true),
            NullabilityDataProbe = new MySqlSafeMigrationNullabilityDataProbe("exists", "eligible", "blocked"),
        };

        var diagnostic = new MySqlSafeMigrationRuntimeDiagnostic(Operation(), 0, plan);

        // Act
        var sql = diagnostic.BuildReasonSlotExpression();

        // Assert
        Assert.DoesNotContain("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INFORMATION_SCHEMA", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FROM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@doka_sm_action", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_sm_diag", sql, StringComparison.Ordinal);
        Assert.Contains("@doka_sm_add_status", sql, StringComparison.Ordinal);
        Assert.Contains("@doka_sm_nullability_blocked", sql, StringComparison.Ordinal);
        Assert.Equal("database_qualifier_mismatch", diagnostic.GetReasonCode(1));
        Assert.Equal("column_atomic_add_guard_conflict", diagnostic.GetReasonCode(3));
        Assert.Equal("column_atomic_add_unavailable", diagnostic.GetReasonCode(4));
        Assert.Equal("column_set_null_dependency", diagnostic.GetReasonCode(5));
        Assert.Equal("column_atomic_add_resume", diagnostic.GetReasonCode(6));
        Assert.Equal("required_column_backfill_missing", diagnostic.GetReasonCode(7));
        Assert.Equal("column_nullability_data_blocked", diagnostic.GetReasonCode(8));
    }

    /// <summary>Atomic CHECK attribution requires the exact admitted fingerprint, owner and native failure contract.</summary>
    /// <param name="mariaDb">Whether to use the qualified MariaDB error shape.</param>
    /// <param name="owned">Whether the message identifies this exact required-add CHECK.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void AtomicCheckFailureRequiresExactOwnerAndFingerprint(bool mariaDb, bool owned)
    {
        // Arrange
        var guardName = "doka_sm_add1_nn_" + new string('a', 48);
        var plan = Plan() with
        {
            RequiredColumnAddPlan = new MySqlSafeMigrationRequiredColumnAddPlan(guardName, "status", mariaDb),
        };

        var diagnostic = new MySqlSafeMigrationRuntimeDiagnostic(Operation(), 0, plan);
        var actualName = owned ? guardName : "doka_sm_add1_nn_" + new string('b', 48);
        var message = mariaDb ? "CONSTRAINT `" + actualName + "` failed for `application`.`records`"
            : "Check constraint '" + actualName + "' is violated.";

        // Act
        var recognized = diagnostic.TryReadFailure(mariaDb ? 4025 : 3819, mariaDb ? "23000" : "HY000", message,
            out var code, out var slot);

        // Assert
        Assert.Equal(owned, recognized);
        if (owned)
        {
            Assert.Equal("doka_sm_data_blocked", code);
            Assert.Equal(10, slot);
            Assert.Equal("column_atomic_add_check_failed", diagnostic.GetReasonCode(slot));
        }
    }

    /// <summary>Error numbers and similar CHECK names alone never acquire atomic required-column ownership.</summary>
    /// <param name="defect">The independently altered certificate or native error field.</param>
    [Theory]
    [InlineData("no_protocol")]
    [InlineData("foreign_owner")]
    [InlineData("similar_name")]
    [InlineData("different_number")]
    [InlineData("different_sqlstate")]
    [InlineData("extra_text")]
    public void UnownedAtomicCheckErrorsStayNative(string defect)
    {
        // Arrange
        var guardName = "doka_sm_add1_nn_" + new string('a', 48);
        var plan = defect == "no_protocol" ? Plan() : Plan() with
        {
            RequiredColumnAddPlan = new MySqlSafeMigrationRequiredColumnAddPlan(guardName, "status", true),
        };

        var diagnostic = new MySqlSafeMigrationRuntimeDiagnostic(Operation(), 0, plan);
        var name = guardName + (defect == "similar_name" ? "_external" : string.Empty);
        var owner = defect == "foreign_owner" ? "other_records" : "records";
        var message = "CONSTRAINT `" + name + "` failed for `application`.`" + owner + "`"
            + (defect == "extra_text" ? " additional user text" : string.Empty);

        // Act
        var recognized = diagnostic.TryReadFailure(defect == "different_number" ? 1146 : 4025,
            defect == "different_sqlstate" ? "HY000" : "23000", message, out _, out _);

        // Assert
        Assert.False(recognized);
    }

    /// <summary>Each decorated operation keeps its own native scope and scripts emit real diagnostic result SQL.</summary>
    /// <param name="script">Whether generation emits a script rather than a managed command.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationAssociatesEachNativeScopeAndSeparatesScriptDiagnostics(bool script)
    {
        // Arrange
        using var context = OfflineContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("first", "first_records", type: "int", nullable: true);
        builder.AddColumnIfNotExists<bool>("active", "second_records", type: "tinyint(1)", nullable: false);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model,
            script ? MigrationsSqlGenerationOptions.Script : MigrationsSqlGenerationOptions.Default);

        // Assert
        Assert.Equal(2, commands.Count);
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            Assert.True(MySqlSafeMigrationRuntimeDiagnostic.TryReadMarker(command.CommandText, out var ordinal,
                out _));
            Assert.Equal(index, ordinal);
            Assert.Equal(!script, command is MySqlSafeMigrationDiagnosticCommand);
            if (script)
            {
                Assert.Contains("SELECT 'safe_migration_runtime_diagnostic' AS documentKind", command.CommandText,
                    StringComparison.Ordinal);
                var diagnosticOffset = command.CommandText.IndexOf("AS documentKind", StringComparison.Ordinal);
                var assertionOffset = command.CommandText.IndexOf("INSERT INTO `__doka_sm_assert`",
                    StringComparison.Ordinal);
                Assert.InRange(diagnosticOffset, 0, assertionOffset - 1);
            }
            else
            {
                Assert.DoesNotContain("safe_migration_runtime_diagnostic' AS documentKind", command.CommandText,
                    StringComparison.Ordinal);
            }
        }

        Assert.False(context.GetService<MySqlSafeMigrationPlanCapture>().HasGenerationContract);
    }

    /// <summary>Scripts need local failure-row metadata but never populate the managed decorator's generation lookup.</summary>
    /// <param name="script">Whether the outer generation scope emits a raw script.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeDiagnosticRegistrationHasANonVacuousManagedConsumer(bool script)
    {
        // Arrange
        using var context = OfflineContext();
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        var probe = new RuntimeDiagnosticRegistrationProbe(context.GetService<IMigrationsSqlGenerator>(), capture);
        var generator = new MySqlSafeMigrationsSqlGenerator(probe, capture,
            context.GetService<MigrationsSqlGeneratorDependencies>());
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("first", "diagnostic_first", type: "int", nullable: true);
        builder.AddColumnIfNotExists<int>("second", "diagnostic_second", type: "int", nullable: true);

        // Act
        var commands = generator.Generate(builder.Operations, context.Model,
            script ? MigrationsSqlGenerationOptions.Script : MigrationsSqlGenerationOptions.Default);

        // Assert
        Assert.Equal(2, commands.Count);
        Assert.Equal(script ? 0 : 2, probe.RegisteredCount);
        Assert.All(commands, command => Assert.Equal(!script, command is MySqlSafeMigrationDiagnosticCommand));
        Assert.False(capture.HasGenerationContract);
        Assert.All(commands, command => Assert.Null(capture.FindRuntimeDiagnostic(command.CommandText)));
    }

    /// <summary>Positional owned assertions require an exact five-column order and bounded binary ASCII values.</summary>
    [Fact]
    public void PositionalRuntimeAssertionsHaveExactlyTheAdmittedOwnedShape()
    {
        // Arrange
        using var context = OfflineContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("value", "items", type: "int", nullable: true);

        // Act
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model)).CommandText;

        // Assert
        AssertOwnedAssertionShape(sql);
        Assert.Contains("INSERT INTO `__doka_sm_assert` SELECT @doka_sm_diag", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO `__doka_sm_assert` SELECT 4, 5, 6, 7, @doka_sm_diag", sql,
            StringComparison.Ordinal);
    }

    /// <summary>The exact-shape oracle rejects reordered or wider columns instead of accepting a vacuous schema match.</summary>
    /// <param name="mutation">The independently invalidated owned-table invariant.</param>
    [Theory]
    [InlineData("reordered")]
    [InlineData("wider")]
    [InlineData("extra_column")]
    public void OwnedAssertionShapeOracleRejectsChangedPositionalContract(string mutation)
    {
        // Arrange
        using var context = OfflineContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("value", "items", type: "int", nullable: true);
        var sql = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model)).CommandText;

        var mutated = mutation switch
        {
            "reordered" => sql.Replace("`different_code` VARCHAR(40) NOT NULL, `unsupported_code` VARCHAR(40) NOT NULL",
                "`unsupported_code` VARCHAR(40) NOT NULL, `different_code` VARCHAR(40) NOT NULL", StringComparison.Ordinal),
            "wider" => sql.Replace("`different_code` VARCHAR(40)", "`different_code` VARCHAR(41)",
                StringComparison.Ordinal),
            _ => sql.Replace("CONSTRAINT `doka_sm_different`", "`extra` VARCHAR(40), CONSTRAINT `doka_sm_different`",
                StringComparison.Ordinal),
        };

        // Act
        var failure = Record.Exception(() => AssertOwnedAssertionShape(mutated));

        // Assert
        Assert.NotEqual(sql, mutated);
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
    }

    /// <summary>Checks physical schema and key positions before approving tuple-only INSERT generation.</summary>
    private static void AssertOwnedAssertionShape(string sql)
    {
        const string createPrefix = "CREATE TEMPORARY TABLE `__doka_sm_assert` (";
        var start = sql.IndexOf(createPrefix, StringComparison.Ordinal) + createPrefix.Length;
        var constraints = sql.IndexOf("CONSTRAINT `doka_sm_different`", start, StringComparison.Ordinal);
        var definitions = sql[start..constraints].TrimEnd(' ', ',').Split(", ", StringSplitOptions.None);
        Assert.Equal(s_ownedAssertionColumns, definitions);

        Assert.Contains(") CHARACTER SET ascii COLLATE ascii_bin;", sql, StringComparison.Ordinal);
        var names = new[] { "different", "unsupported", "data_blocked", "prerequisite_missing", "postcondition" };
        Assert.All(names, name => Assert.Contains("CONSTRAINT `doka_sm_" + name + "` UNIQUE (`"
            + name + "_code`)", sql, StringComparison.Ordinal));
    }

    /// <summary>Inspects the active outer lease before cleanup, so a missing registration assertion cannot pass vacuously.</summary>
    private sealed class RuntimeDiagnosticRegistrationProbe(IMigrationsSqlGenerator inner,
        MySqlSafeMigrationPlanCapture capture) : IMigrationsSqlGenerator
    {
        /// <summary>Gets the positively correlated commands recorded while the generation scope remains active.</summary>
        internal int RegisteredCount { get; private set; }

        /// <inheritdoc />
        public IReadOnlyList<MigrationCommand> Generate(IReadOnlyList<MigrationOperation> operations, IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
        {
            var commands = inner.Generate(operations, model, options);
            RegisteredCount = commands.Count(command => capture.FindRuntimeDiagnostic(command.CommandText) is not null);

            return commands;
        }
    }

    /// <summary>Creates an uncomplicated authored operation with no live-state assumptions.</summary>
    private static SafeMigrationOperation Operation() => new(
        new EnsureColumnIntent("records", new ExpectedColumnDefinition("active", typeof(bool), false, "tinyint(1)")),
        SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Creates a minimal plan whose generic reason does not imply any live row condition.</summary>
    private static MySqlSafeMigrationRuntimePlan Plan() => new("'missing'", "TRUE", SafeMigrationRepairCapability.None,
        "FALSE");

    /// <summary>Creates one operation-local transport context without database access.</summary>
    private static MySqlSafeMigrationRuntimeDiagnostic Diagnostic() => new(Operation(), 17, Plan());

    /// <summary>Uses real EF and Doka generation services without opening a connection.</summary>
    internal static DbContext OfflineContext() => new(new DbContextOptionsBuilder()
        .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MariaDb(new Version(11, 8, 8)))
        .UseMySqlSafeMigrations().Options);
}
