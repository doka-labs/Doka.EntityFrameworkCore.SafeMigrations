namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Protects exact guard SQL while eliminating operation-local rendering intermediates.</summary>
public sealed class PostgreSqlGuardRenderingAllocationTests
{
    /// <summary>Preserves legacy baseline bytes and chooses a tag absent from both action branches.</summary>
    /// <param name="scenario">The baseline termination, whitespace, or dollar-tag boundary.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void DirectGuardRendering_PreservesLegacyBaselineBytesAndDollarTags(
        int scenario
    )
    {
        // Arrange
        using var context = CreateOfflineContext();
        var operation = CreateOperation(varchar: true, policy: SafeMigrationPolicy.RepairIfSafe);
        var plan = CreatePlan(context, operation);
        var (applySql, repairSql) = BaselineInputs(scenario);
        var baseline = CreateBaselineCommands(context, applySql);
        var repairBaseline = CreateBaselineCommands(context, repairSql);
        var expectedTag = scenario switch
        {
            3 or 4 => "$doka_safe_migration_1$",
            5 => "$doka_safe_migration_2$",
            _ => "$doka_safe_migration$",
        };

        // Act
        var guarded = RenderGuardedSql(operation, plan, baseline, repairBaseline);
        var legacyApply = RenderLegacyActionSql(baseline);
        var legacyRepair = RenderLegacyActionSql(repairBaseline);

        // Assert
        Assert.Contains("        IF doka_action = 'apply' THEN\n" + legacyApply
            + "        ELSE\n" + legacyRepair + "        END IF;\n", guarded, StringComparison.Ordinal);
        Assert.StartsWith("DO " + expectedTag + "\nDECLARE\n", guarded, StringComparison.Ordinal);
        Assert.EndsWith("END\n" + expectedTag + ";", guarded, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(guarded, expectedTag));
        Assert.Contains("FOR doka_evaluation_pass IN 1..2 LOOP", guarded, StringComparison.Ordinal);
        Assert.Contains("EXIT WHEN doka_action <> 'repair' OR doka_evaluation_pass = 2;", guarded,
            StringComparison.Ordinal);
        Assert.Contains(" IN ACCESS EXCLUSIVE MODE;", guarded, StringComparison.Ordinal);
        Assert.Contains("        IF NOT COALESCE((\n            " + plan.Postcondition
            + "\n        ), FALSE) THEN\n", guarded, StringComparison.Ordinal);
    }

    /// <summary>Retains every canonical decision arm and the exact catalog binding guard.</summary>
    /// <param name="varchar">Whether the guard combines narrowing and NULL evidence.</param>
    /// <param name="policy">The decision policy used by the canonical planner.</param>
    [Theory]
    [InlineData(false, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(true, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(false, SafeMigrationPolicy.RepairIfSafe)]
    [InlineData(true, SafeMigrationPolicy.RepairIfSafe)]
    public void DirectGuardRendering_PreservesLegacyDecisionAndBindingBytes(
        bool varchar,
        SafeMigrationPolicy policy
    )
    {
        // Arrange
        using var context = CreateOfflineContext();
        var operation = CreateOperation(varchar, policy);
        var plan = CreatePlan(context, operation);

        // Act
        var guarded = RenderGuardedSql(operation, plan, [], []);
        var legacyActionCase = RenderLegacyActionCase(operation, plan.RepairCapability);
        var legacyGuard = RenderLegacyBindingGuard(plan);

        // Assert
        Assert.Contains("doka_action := " + legacyActionCase + ";\n", guarded, StringComparison.Ordinal);
        if (plan.StateEvaluationGuardFailureExpression is not null)
        {
            Assert.Contains(legacyGuard, guarded, StringComparison.Ordinal);
            Assert.True(guarded.IndexOf(legacyGuard, StringComparison.Ordinal)
                < guarded.IndexOf("doka_nullability_repair_eligible := COALESCE((", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain("ELSIF NOT COALESCE((", guarded, StringComparison.Ordinal);
        }

        Assert.Contains("doka_repair_ok := COALESCE((", guarded, StringComparison.Ordinal);
        Assert.Contains("MESSAGE = 'doka_sm_postcondition'", guarded, StringComparison.Ordinal);
    }

    /// <summary>Measures only prepared rendering work and rejects reintroduced legacy SQL copies.</summary>
    [Fact]
    public void PreparedGuardComponents_DirectAppendsAllocateLessThanLegacyCopies()
    {
        // Arrange
        const int sampleCount = 5;
        const int iterations = 100;
        using var context = CreateOfflineContext();
        var operation = CreateOperation(varchar: true, policy: SafeMigrationPolicy.RepairIfSafe);
        var plan = CreatePlan(context, operation);
        var baseline = CreateBaselineCommands(context,
            ["ALTER TABLE proof_rows ADD COLUMN value varchar(800) NOT NULL;",
                "COMMENT ON COLUMN proof_rows.value IS 'canonical';"]);
        var repairBaseline = CreateBaselineCommands(context,
            ["ALTER TABLE proof_rows ALTER COLUMN value TYPE varchar(800);",
                "ALTER TABLE proof_rows ALTER COLUMN value SET NOT NULL;",
                "COMMENT ON COLUMN proof_rows.value IS 'canonical';"]);
        var appendActionCase = BindPrivateRenderer<
            Action<StringBuilder, SafeMigrationOperation, SafeMigrationRepairCapability>>("AppendActionCase");
        var appendActionSql = BindPrivateRenderer<
            Action<StringBuilder, IReadOnlyList<MigrationCommand>, string>>("AppendActionSql");
        var appendDataProbe = BindPrivateRenderer<
            Action<StringBuilder, PostgreSqlSafeMigrationDataProbe, string, string>>("AppendDataProbeEvaluationSql");

        Func<string> direct = () => RenderPreparedComponents(
            operation, plan, baseline, repairBaseline, appendActionCase, appendActionSql, appendDataProbe,
            legacyCopies: false);
        Func<string> legacy = () => RenderPreparedComponents(
            operation, plan, baseline, repairBaseline, appendActionCase, appendActionSql, appendDataProbe,
            legacyCopies: true);

        var directSamples = new long[sampleCount];
        var legacySamples = new long[sampleCount];

        // Act
        var directSql = direct();
        var legacySql = legacy();
        for (var warmup = 0; warmup < 10; warmup++)
        {
            _ = direct();
            _ = legacy();
        }

        var directChecksum = 0L;
        var legacyChecksum = 0L;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var measuredDirect = MeasurePreparedRendering(direct, iterations);
            var measuredLegacy = MeasurePreparedRendering(legacy, iterations);
            directSamples[sample] = measuredDirect.Bytes;
            legacySamples[sample] = measuredLegacy.Bytes;
            directChecksum += measuredDirect.Checksum;
            legacyChecksum += measuredLegacy.Checksum;
        }

        Array.Sort(directSamples);
        Array.Sort(legacySamples);
        var directMedian = directSamples[sampleCount / 2];
        var legacyMedian = legacySamples[sampleCount / 2];

        // Assert
        Assert.Equal(legacySql, directSql);
        Assert.Equal((long)directSql.Length * iterations * sampleCount, directChecksum);
        Assert.Equal((long)legacySql.Length * iterations * sampleCount, legacyChecksum);
        Assert.True(directMedian < legacyMedian,
            "Prepared direct samples: " + string.Join(",", directSamples)
            + "; legacy copy samples: " + string.Join(",", legacySamples));
    }

    /// <summary>Exercises the expression-only delimiter caller through actual custom-index generation.</summary>
    /// <param name="collisionDepth">The number of delimiter candidates embedded only in the index name.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CustomNullsNotDistinctIndex_PreservesExpressionOnlyDollarTags(
        int collisionDepth
    )
    {
        // Arrange
        using var context = CreateOfflineContext();
        var indexName = collisionDepth switch
        {
            0 => "ix_guard",
            1 => "ix_$doka_safe_migration$",
            2 => "ix_$doka_safe_migration$$doka_safe_migration_1$",
            _ => throw new ArgumentOutOfRangeException(nameof(collisionDepth)),
        };
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition(
            indexName, "proof_rows", [new ExpectedIndexKeyDefinition(column: "value")],
            unique: true, nullsDistinct: false), SafeMigrationPolicy.ThrowIfDifferent);
        var helper = context.GetService<ISqlGenerationHelper>();
        var expectedIndexSql = "CREATE UNIQUE INDEX " + helper.DelimitIdentifier(indexName)
            + " ON " + helper.DelimitIdentifier("proof_rows") + " (" + helper.DelimitIdentifier("value")
            + ") NULLS NOT DISTINCT;";
        var innerTag = collisionDepth == 0 ? "$doka_safe_migration$"
            : "$doka_safe_migration_" + collisionDepth.ToString(CultureInfo.InvariantCulture) + "$";
        var outerTag = "$doka_safe_migration_"
            + (collisionDepth + 1).ToString(CultureInfo.InvariantCulture) + "$";

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Assert
        var guarded = Assert.Single(commands).CommandText;
        Assert.Contains("EXECUTE " + innerTag + expectedIndexSql + innerTag + ";", guarded,
            StringComparison.Ordinal);
        Assert.StartsWith("DO " + outerTag + "\nDECLARE\n", guarded, StringComparison.Ordinal);
        Assert.EndsWith("END\n" + outerTag + ";" + Environment.NewLine, guarded, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(guarded, outerTag));
    }

    /// <summary>Retains optional probe rendering and detects a collision confined to the row-query text.</summary>
    /// <param name="hasDataProbe">Whether the runtime plan contains a narrowing data probe.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalDataProbe_PreservesProbeOnlyTagCollisionAndNullProof(
        bool hasDataProbe
    )
    {
        // Arrange
        using var context = CreateOfflineContext();
        var operation = CreateOperation(varchar: true, policy: SafeMigrationPolicy.RepairIfSafe);
        var helper = context.GetService<ISqlGenerationHelper>();
        var table = helper.DelimitIdentifier("proof_rows");
        var dataProbe = hasDataProbe ? new PostgreSqlSafeMigrationDataProbe(
            "proof_rows", null, "$doka_safe_migration$", 800, table,
            helper.DelimitIdentifier("$doka_safe_migration$"), "TRUE", "TRUE") : null;
        var nullQuery = "EXISTS (SELECT 1 FROM " + table + " WHERE value IS NULL LIMIT 1)";
        var plan = new PostgreSqlSafeMigrationRuntimePlan(
            "'matching'", "TRUE", SafeMigrationRepairCapability.Safe, "TRUE")
        {
            DataProbe = dataProbe,
            NullabilityDataProbe = new PostgreSqlSafeMigrationNullabilityDataProbe("TRUE", "TRUE", nullQuery, table),
        };
        var render = BindPrivateRenderer<Func<SafeMigrationOperation, PostgreSqlSafeMigrationRuntimePlan,
            IReadOnlyList<MigrationCommand>, IReadOnlyList<MigrationCommand>, string>>("BuildGuardedSql");
        var expectedTag = hasDataProbe ? "$doka_safe_migration_1$" : "$doka_safe_migration$";

        // Act
        var guarded = render(operation, plan, [], []);
        var blockedExpression = dataProbe?.BuildBlockedExpression();

        // Assert
        Assert.StartsWith("DO " + expectedTag + "\nDECLARE\n", guarded, StringComparison.Ordinal);
        Assert.EndsWith("END\n" + expectedTag + ";", guarded, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(guarded, expectedTag));
        Assert.Equal(1, CountOccurrences(guarded, nullQuery));
        Assert.Contains("doka_complete_not_null := COALESCE((TRUE), FALSE);", guarded, StringComparison.Ordinal);
        Assert.Contains("IF doka_nullability_repair_eligible AND NOT COALESCE((doka_complete_not_null), FALSE) THEN",
            guarded,
            StringComparison.Ordinal);
        if (hasDataProbe)
        {
            Assert.NotNull(blockedExpression);
            Assert.Equal(1, CountOccurrences(guarded, blockedExpression));
            Assert.Contains("IF doka_data_probe_required THEN", guarded, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(blockedExpression);
            Assert.DoesNotContain("char_length(", guarded, StringComparison.Ordinal);
            Assert.DoesNotContain("doka_data_probe_required", guarded, StringComparison.Ordinal);
        }
    }

    /// <summary>Creates a non-connecting context for the real provider's baseline and catalog services.</summary>
    private static SafeMigrationDbContext CreateOfflineContext() => new(
        "Host=127.0.0.1;Port=1;Database=guard_test;Username=test;Password=test");

    /// <summary>Creates a required-column intent for catalog-only or combined row-proof rendering.</summary>
    private static SafeMigrationOperation CreateOperation(
        bool varchar,
        SafeMigrationPolicy policy
    ) => new(new EnsureColumnIntent("proof_rows", new ExpectedColumnDefinition(
        "value", varchar ? typeof(string) : typeof(DateTime), false,
        storeType: varchar ? "character varying(800)" : "timestamp without time zone",
        maxLength: varchar ? 800 : null)), policy);

    /// <summary>Builds the same catalog plan used by the production guard generator.</summary>
    private static PostgreSqlSafeMigrationRuntimePlan CreatePlan(
        DbContext context,
        SafeMigrationOperation operation
    ) => new PostgreSqlSafeMigrationCatalogSqlBuilder(
        context.GetService<IRelationalTypeMappingSource>(),
        context.GetService<ISqlGenerationHelper>()).Build(
            operation, includeAnalysisEvidence: false, includeTransitionEvidence: true);

    /// <summary>Creates real provider commands without executing the fixture SQL.</summary>
    private static IReadOnlyList<MigrationCommand> CreateBaselineCommands(
        DbContext context,
        string[] sqlParts
    ) => context.GetService<IPostgreSqlSafeMigrationsBaselineGenerator>().Generate(
        sqlParts.Select(static sql => new SqlOperation { Sql = sql }).ToArray(), context.Model);

    /// <summary>Binds a typed private renderer before allocation measurement begins.</summary>
    private static TDelegate BindPrivateRenderer<TDelegate>(
        string methodName
    ) where TDelegate : Delegate
    {
        var renderer = typeof(PostgreSqlSafeMigrationsSqlGenerator).GetMethod(
            methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The PostgreSQL renderer was not found: " + methodName + ".");

        return renderer.CreateDelegate<TDelegate>();
    }

    /// <summary>Composes actual typed appenders or independent former copies with identical final bytes.</summary>
    private static string RenderPreparedComponents(
        SafeMigrationOperation operation,
        PostgreSqlSafeMigrationRuntimePlan plan,
        IReadOnlyList<MigrationCommand> baseline,
        IReadOnlyList<MigrationCommand> repairBaseline,
        Action<StringBuilder, SafeMigrationOperation, SafeMigrationRepairCapability> appendActionCase,
        Action<StringBuilder, IReadOnlyList<MigrationCommand>, string> appendActionSql,
        Action<StringBuilder, PostgreSqlSafeMigrationDataProbe, string, string> appendDataProbe,
        bool legacyCopies
    )
    {
        const string indentation = "            ";
        var probe = plan.DataProbe
            ?? throw new InvalidOperationException("The prepared allocation fixture requires a narrowing probe.");
        var probeForTag = probe.BuildBlockedExpression();
        var tag = probeForTag.Contains("$doka_safe_migration$", StringComparison.Ordinal)
            ? "$doka_safe_migration_1$" : "$doka_safe_migration$";
        var builder = new StringBuilder().Append("DO ").Append(tag).Append("\nBEGIN\n");
        if (legacyCopies)
        {
            builder.Append(RenderLegacyBindingGuard(plan));
        }
        else
        {
            AppendPreparedBindingGuard(builder, plan);
        }

        appendDataProbe(builder, probe, legacyCopies ? probe.BuildBlockedExpression() : probeForTag, indentation);
        builder.Append("        doka_action := ");
        if (legacyCopies)
        {
            builder.Append(RenderLegacyActionCase(operation, plan.RepairCapability));
        }
        else
        {
            appendActionCase(builder, operation, plan.RepairCapability);
        }

        builder.Append(";\n        IF doka_action = 'apply' THEN\n");
        if (legacyCopies)
        {
            AppendLegacyCopiedBaseline(builder, baseline, indentation);
        }
        else
        {
            appendActionSql(builder, baseline, indentation);
        }

        builder.Append("        ELSE\n");
        if (legacyCopies)
        {
            AppendLegacyCopiedBaseline(builder, repairBaseline, indentation);
        }
        else
        {
            appendActionSql(builder, repairBaseline, indentation);
        }

        return builder.Append("        END IF;\nEND\n").Append(tag).Append(';').ToString();
    }

    /// <summary>Isolates the production inlined branch's append pattern without a complete branch string.</summary>
    private static void AppendPreparedBindingGuard(
        StringBuilder builder,
        PostgreSqlSafeMigrationRuntimePlan plan
    )
    {
        // WHY: Production keeps this small branch inlined. Its existing byte
        // comparison covers the real guard; this fixture isolates allocation.
        if (plan.StateEvaluationGuardFailureExpression is null)
        {
            return;
        }

        builder.Append("        ELSIF NOT COALESCE((").Append(plan.StateEvaluationGuardExpression)
            .Append("), FALSE) THEN\n            doka_state := (")
            .Append(plan.StateEvaluationGuardFailureExpression)
            .Append(");\n            doka_repair_ok := FALSE;\n");
    }

    /// <summary>Recreates former baseline copies without adding unrelated formatting allocations.</summary>
    private static void AppendLegacyCopiedBaseline(
        StringBuilder builder,
        IReadOnlyList<MigrationCommand> commands,
        string indentation
    )
    {
        var copied = new StringBuilder();
        for (var index = 0; index < commands.Count; index++)
        {
            if (index > 0)
            {
                copied.Append('\n');
            }

            var command = commands[index].CommandText.AsSpan().TrimEnd();
            copied.Append(command);
            if (command.IsEmpty || command[^1] != ';')
            {
                copied.Append(';');
            }
        }

        var flattened = copied.ToString();
        if (flattened.Length == 0)
        {
            builder.Append(indentation).Append("NULL;\n");

            return;
        }

        var start = 0;
        while (start <= flattened.Length)
        {
            var newline = flattened.IndexOf('\n', start);
            var length = newline < 0 ? flattened.Length - start : newline - start;
            builder.Append(indentation).Append(flattened, start, length).Append('\n');
            if (newline < 0)
            {
                return;
            }

            start = newline + 1;
        }
    }

    /// <summary>Measures current-thread allocations with prepared delegates and a consumed output checksum.</summary>
    private static (long Bytes, long Checksum) MeasurePreparedRendering(
        Func<string> render,
        int iterations
    )
    {
        var checksum = 0L;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            checksum += render().Length;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        return (allocated, checksum);
    }

    /// <summary>Invokes the operation-local renderer without adding a production testing API.</summary>
    private static string RenderGuardedSql(
        SafeMigrationOperation operation,
        PostgreSqlSafeMigrationRuntimePlan plan,
        IReadOnlyList<MigrationCommand> baseline,
        IReadOnlyList<MigrationCommand> repairBaseline
    )
    {
        // WHY: Script fragments are test inputs, not new migration contracts.
        // Reflection preserves the renderer's private production boundary.
        var renderer = typeof(PostgreSqlSafeMigrationsSqlGenerator).GetMethod(
            "BuildGuardedSql", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The PostgreSQL guard renderer was not found.");

        return (string)(renderer.Invoke(null, [operation, plan, baseline, repairBaseline])
            ?? throw new InvalidOperationException("The PostgreSQL guard renderer returned no SQL."));
    }

    /// <summary>Provides independent boundaries for termination, CRLF, and collisions in either branch.</summary>
    private static (string[] Apply, string[] Repair) BaselineInputs(
        int scenario
    ) => scenario switch
    {
        0 => ([], []),
        1 => (["SELECT 1"], ["SELECT 2;"]),
        2 => (["SELECT 1\r\n-- retained CRLF\r\n", " SELECT 2;\t \r\n", " \t\r\n"],
            ["SELECT 3;\nSELECT 4\r\n", "\t"]),
        3 => (["SELECT '$doka_safe_migration$';"], ["SELECT 2;"]),
        4 => (["SELECT 1;"], ["SELECT '$doka_safe_migration$';"]),
        5 => (["SELECT '$doka_safe_migration$';"], ["SELECT '$doka_safe_migration_1$';"]),
        6 => (["SELECT '$doka_safe_'", "SELECT 'migration$';"], ["SELECT 2;"]),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    /// <summary>Reproduces the previous flatten-then-indent contract independently of direct rendering.</summary>
    private static string RenderLegacyActionSql(
        IReadOnlyList<MigrationCommand> commands
    )
    {
        const string indentation = "            ";
        var flattened = string.Join("\n", commands.Select(static command =>
        {
            var sql = command.CommandText.TrimEnd();

            return sql.EndsWith(';') ? sql : sql + ";";
        }));

        return flattened.Length == 0
            ? indentation + "NULL;\n"
            : string.Concat(flattened.Split('\n').Select(static line => indentation + line + "\n"));
    }

    /// <summary>Reproduces the previous complete binding-guard string for exact output comparison.</summary>
    private static string RenderLegacyBindingGuard(
        PostgreSqlSafeMigrationRuntimePlan plan
    )
    {
        var proofTable = plan.DataProbe?.QualifiedTable ?? plan.NullabilityDataProbe?.QualifiedTable;
        var indentation = proofTable is null ? "    " : "        ";
        var bodyIndentation = indentation + "    ";

        return plan.StateEvaluationGuardFailureExpression is null
            ? string.Empty
            : indentation + "ELSIF NOT COALESCE((" + plan.StateEvaluationGuardExpression
                + "), FALSE) THEN\n" + bodyIndentation + "doka_state := ("
                + plan.StateEvaluationGuardFailureExpression + ");\n"
                + bodyIndentation + "doka_repair_ok := FALSE;\n";
    }

    /// <summary>Reproduces legacy action-case bytes using every state from the canonical planner.</summary>
    private static string RenderLegacyActionCase(
        SafeMigrationOperation operation,
        SafeMigrationRepairCapability repairCapability
    )
    {
        var builder = new StringBuilder("CASE doka_state ");
        foreach (var state in Enum.GetValues<SafeMigrationObservedState>())
        {
            var action = SafeMigrationDecisionPlanner.PlanAction(
                operation.Intent.Kind, state, operation.Policy, repairCapability);
            var stateCode = state switch
            {
                SafeMigrationObservedState.Missing => "missing",
                SafeMigrationObservedState.Matching => "matching",
                SafeMigrationObservedState.Different => "different",
                SafeMigrationObservedState.Unsupported => "unsupported",
                SafeMigrationObservedState.DataBlocked => "data_blocked",
                SafeMigrationObservedState.PrerequisiteMissing => "prerequisite_missing",
                SafeMigrationObservedState.TransitionReady => "transition_ready",
                _ => throw new InvalidOperationException("The legacy oracle encountered an unknown observed state."),
            };

            builder.Append("WHEN '").Append(stateCode).Append("' THEN ");
            if (action == SafeMigrationAction.Repair)
            {
                builder.Append("CASE WHEN doka_repair_ok THEN 'repair' ELSE 'reject_different' END ");
            }
            else
            {
                var actionCode = action switch
                {
                    SafeMigrationAction.Apply => "apply",
                    SafeMigrationAction.NoOp => "no_op",
                    SafeMigrationAction.RejectDifferent => "reject_different",
                    SafeMigrationAction.RejectUnsupported => "reject_unsupported",
                    SafeMigrationAction.RejectDataBlocked => "reject_data_blocked",
                    SafeMigrationAction.RejectPrerequisiteMissing => "reject_prerequisite_missing",
                    _ => throw new InvalidOperationException("The legacy oracle encountered an unexpected action."),
                };

                builder.Append('\'').Append(actionCode).Append("' ");
            }
        }

        return builder.Append("ELSE 'reject_unsupported' END").ToString();
    }

    /// <summary>Counts exact tag appearances without interpreting provider SQL.</summary>
    private static int CountOccurrences(
        string text,
        string pattern
    )
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(pattern, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += pattern.Length;
        }

        return count;
    }
}
