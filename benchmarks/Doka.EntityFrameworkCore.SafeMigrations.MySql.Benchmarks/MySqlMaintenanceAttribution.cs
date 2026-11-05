using Microsoft.EntityFrameworkCore.Storage;

namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Attributes prerequisite extraction and native-profile backfill plan generation without a server.</summary>
public class MySqlMaintenanceAttribution : IDisposable
{
    private MySqlSafeMigrationCatalogSqlBuilder _catalog = null!;
    private ProfileContext _context = null!;
    private bool _disposed;
    private SafeMigrationOperation _operation = null!;
    private MySqlMigrationOperationContext _operationContext = null!;

    /// <summary>Gets or sets whether the real MariaDB rather than MySQL profile is used.</summary>
    [Params(false, true)]
    public bool MariaDb { get; set; }

    /// <summary>Gets or sets the literal domain or negative-control workload.</summary>
    [Params(
        "ascii",
        "latin1",
        "bmp",
        "supplementary",
        "control",
        "too_long",
        "boolean",
        "nullable_fast_path")]
    public string Workload { get; set; } = "ascii";

    /// <summary>Releases the context owning the native profile services.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs the independent prerequisite and native-profile generation benchmark families.</summary>
    /// <param name="args">The BenchmarkDotNet command-line arguments.</param>
    /// <returns>The shared benchmark runner exit code.</returns>
    internal static int Run(
        string[] args
    ) => BenchmarkProgram.Run(args, typeof(MySqlPrerequisiteAttribution), typeof(MySqlMaintenanceAttribution));

    /// <summary>Prepares real native mappings and verifies exact producer output before timing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _disposed = false;
        _context = new ProfileContext(MariaDb);
        _catalog = new MySqlSafeMigrationCatalogSqlBuilder(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());

        object value = Workload switch
        {
            "ascii" or "nullable_fast_path" => "replacement",
            "latin1" => "caf\u00e9",
            "bmp" => "\u20ac",
            "supplementary" => "\U0001F600",
            "control" => "a\0b\nc\\d",
            "too_long" => new string('x', 65),
            "boolean" => true,
            _ => throw new ArgumentOutOfRangeException(nameof(Workload)),
        };

        var type = Workload == "boolean"
            ? typeof(bool)
            : typeof(string);

        var nullable = Workload == "nullable_fast_path";
        var target = new ExpectedColumnDefinition(
            "value",
            type,
            nullable,
            type == typeof(bool)
                ? "tinyint(1)"
                : "varchar(64)",
            maxLength: type == typeof(string)
                ? 64
                : null,
            defaultValue: SafeMigrationDefaultValue.Literal(value));

        // WHY: Equal VARCHAR widths bypass the charset producer. Real narrowing owns this attribution.
        var source = new ExpectedColumnDefinition(
            "value",
            type,
            true,
            type == typeof(bool)
                ? "bit(1)"
                : "varchar(128)",
            maxLength: type == typeof(string)
                ? 128
                : null);

        _operation = new SafeMigrationOperation(
            new AlterColumnIntent("maintenance_rows", target, source),
            SafeMigrationPolicy.RepairIfSafe);

        _operationContext = OperationContext(_context, _operation, MariaDb);
        var plan = CreatePlan();
        var charsetGuardPresent = VerifyProducerPath(plan, Workload, nullable);
        if (Workload == "ascii")
        {
            VerifyProducerPathCounterprobe(plan);
        }

        var shape = Shape(plan);
        VerifyShapeNonVacuity(plan);
        MaintenanceAttribution.Require(
            shape == Shape(CreatePlan()),
            "MySQL catalog invocation retained stale parameters or proof state.");

        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            _ = MaintenanceAttribution.Write(
                Path.Combine(output, $"mysql-{MariaDb}-{Workload}.json"),
                "Functional/source evidence; MySQL/MariaDB client generation only; not timing evidence",
                [
                    new
                    {
                        Id = "PERF-02",
                        EngineProfile = MariaDb
                            ? "mariadb-11.8.8"
                            : "mysql-8.4.11",
                        Workload,
                        ProducerPath = type == typeof(bool)
                            ? "boolean_transition_control"
                            : nullable
                                ? "nullable_narrowing_control"
                                : "required_varchar_narrowing",
                        CharsetGuardPresent = charsetGuardPresent,
                        NarrowingProbePresent = plan.DataProbe is not null,
                        NullabilityProbePresent = plan.NullabilityDataProbe is not null,
                        plan.UnsupportedCode,
                        plan.IsStaticallyUnsupported,
                        Parameters = plan.ParameterValues.Length,
                        BinaryProofs = plan.BinaryDefaultProofs.Count,
                        IdentifierProofs = plan.IdentifierReferenceProofs.Count,
                        SqlCharacters =
                            plan.StateExpression.Length + plan.Postcondition.Length + plan.RepairPrecondition.Length,
                        ExactOutput = shape,
                    },
                ]);
        }
    }

    /// <summary>Builds one complete catalog plan using prebuilt operations and native profile services.</summary>
    /// <returns>The complete runtime plan, consumed by BenchmarkDotNet.</returns>
    [Benchmark]
    public object BuildPlan() => CreatePlan();

    /// <summary>Retains the typed plan for setup-only structural assertions.</summary>
    /// <returns>The complete real provider plan.</returns>
    private MySqlSafeMigrationRuntimePlan CreatePlan() => _catalog.Build(
        _operation,
        _operationContext,
        includeAnalysisEvidence: false,
        includeTransitionEvidence: true);

    /// <summary>Disposes the context owning the real native profile services.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases owned managed resources once while supporting generated benchmark subclasses.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (!disposing || _disposed)
        {
            return;
        }

        _context.Dispose();
        _disposed = true;
    }

    /// <summary>Rejects a vacuous workload that misses its producer or loses existing capability limits.</summary>
    /// <param name="plan">The actual real-profile narrowing plan.</param>
    /// <param name="scenario">The stable literal or control case identifier.</param>
    /// <param name="nullable">Whether the target remains nullable and therefore needs no backfill proof.</param>
    /// <returns>Whether the actual producer emitted the charset-selection guard.</returns>
    private static bool VerifyProducerPath(
        MySqlSafeMigrationRuntimePlan plan,
        string scenario,
        bool nullable
    )
    {
        var invariant = plan.NullabilityDataProbe?.RepairInvariantExpression ?? plan.RepairPrecondition;
        var hasCharsetGuard = invariant.Contains("c.CHARACTER_SET_NAME IN (", StringComparison.Ordinal);
        var expectedCharsetGuard = scenario is "ascii" or "latin1" or "bmp" or "control";

        MaintenanceAttribution.Require(
            hasCharsetGuard == expectedCharsetGuard,
            $"MySQL attribution scenario '{scenario}' did not reach its exact charset-domain producer.");

        MaintenanceAttribution.Require(
            plan.DataProbe is not null == (scenario != "boolean"),
            $"MySQL attribution scenario '{scenario}' did not retain its real narrowing probe.");

        MaintenanceAttribution.Require(
            plan.NullabilityDataProbe is not null == !nullable,
            $"MySQL attribution scenario '{scenario}' lost its required-column proof role.");

        // WHY: Supplementary literal identity is intentionally rejected before charset selection;
        // too-long replacements exhaust the allowed set. Neither is permission to widen capability.
        if (scenario is "supplementary" or "too_long")
        {
            MaintenanceAttribution.Require(
                invariant.Contains("AND (FALSE)", StringComparison.Ordinal),
                $"MySQL attribution scenario '{scenario}' lost its conservative backfill rejection.");
        }

        return hasCharsetGuard;
    }

    /// <summary>Proves that an accidentally bypassed charset producer makes the attribution guard fail.</summary>
    /// <param name="plan">The actual required-ASCII narrowing plan.</param>
    private static void VerifyProducerPathCounterprobe(
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        var probe = plan.NullabilityDataProbe
            ?? throw new InvalidOperationException("The producer counterprobe requires a nullability invariant.");

        var withoutCharsetGuard = plan with
        {
            NullabilityDataProbe = probe with { RepairInvariantExpression = "FALSE" },
        };

        var rejected = false;
        try
        {
            _ = VerifyProducerPath(withoutCharsetGuard, "ascii", nullable: false);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains(
            "exact charset-domain producer",
            StringComparison.Ordinal))
        {
            rejected = true;
        }

        MaintenanceAttribution.Require(
            rejected,
            "The MySQL attribution guard accepted a plan that bypassed charset proof construction.");
    }

    /// <summary>Retains ordered SQL, parameter values and proof identities outside measured construction.</summary>
    /// <param name="plan">The complete real provider plan.</param>
    /// <returns>The digest of every non-delegate plan output, retaining parameter and proof order.</returns>
    private static string Shape(
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        // WHY: This workload has no catalog-snapshot resolver. Silently ignoring a non-null delegate
        // would turn a different rendered relation binding into false output-equivalence evidence.
        MaintenanceAttribution.Require(
            plan.CatalogRelations is null,
            "The attribution oracle requires an unbound catalog plan.");

        var snapshot = new
        {
            plan.StateExpression,
            plan.Postcondition,
            plan.RepairCapability,
            plan.RepairPrecondition,
            plan.UnsupportedCode,
            plan.ParameterValues,
            plan.BinaryDefaultProofs,
            IdentifierReferenceProofs = plan
                .IdentifierReferenceProofs
                .Select(static proof => new
                {
                    proof.Marker,
                    proof.RootIdentifier,
                    proof.ScopeId,
                    proof.Binding,
                    proof.ResultVariable,
                    proof.InnerStatementName,
                    proof.OuterStatementName,
                })
                .ToArray(),
            plan.PrerequisiteExpression,
            plan.ExecutionPostcondition,
            plan.CurrentDatabaseQualificationExpression,
            plan.CurrentDatabaseQualificationFailureCode,
            plan.StateEvaluationGuardExpression,
            plan.ClassificationCodeExpression,
            plan.StateEvaluationGuardFailureExpression,
            plan.RequiresLazyStateEvaluation,
            plan.DataProbe,
            plan.NullabilityDataProbe,
            plan.RequiredColumnAddPlan,
            plan.RequiresDataProbe,
            plan.MayRequireNullabilityDataProof,
            plan.RepairOperationalImpact,
            plan.DiagnosticEvidenceExpression,
            plan.IndexPhysicalEnvironment,
            plan.IndexNameExistsExpression,
            plan.CaptureProjectedIndexIdentity,
            plan.CapturedIndexIdentity,
            plan.DropLeavesNoGeneratedColumnsExpression,
            plan.CapturedSourceSyntaxExpression,
            plan.DifferentDifference,
            plan.MatchedObjectNameExpression,
            plan.IsStaticallyUnsupported,
            plan.MutationSql,
            plan.ModelManagedRowEvidenceExpression,
            plan.ModelManagedDependencyCountsExpression,
            plan.ModelManagedRowCount,
            plan.ModelManagedDependencyCount,
        };

        // WHY: Exact output includes gates and flags as well as SQL. This completeness fence
        // makes a future plan property an explicit oracle update rather than a silent blind spot.
        var planNames = typeof(MySqlSafeMigrationRuntimePlan)
            .GetProperties()
            .Where(static property => !typeof(Delegate).IsAssignableFrom(property.PropertyType))
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal);

        var snapshotNames = snapshot
            .GetType()
            .GetProperties()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal);

        MaintenanceAttribution.Require(
            planNames.SequenceEqual(snapshotNames, StringComparer.Ordinal),
            "The MySQL attribution oracle does not cover every non-delegate runtime-plan output.");

        return MaintenanceAttribution.Hash(JsonSerializer.Serialize(snapshot));
    }

    /// <summary>Proves that SQL, guards, impacts, flags and ordered evidence influence the output oracle.</summary>
    /// <param name="plan">The real measured plan used as the non-vacuity seed.</param>
    private static void VerifyShapeNonVacuity(
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        var shape = Shape(plan);
        var variants = new[]
        {
            plan with { StateExpression = plan.StateExpression + " /* changed */" },
            plan with { StateEvaluationGuardExpression = "attribution_changed_guard" },
            plan with { StateEvaluationGuardFailureExpression = "attribution_changed_failure" },
            plan with { ClassificationCodeExpression = "attribution_changed_classification" },
            plan with { MayRequireNullabilityDataProof = !plan.MayRequireNullabilityDataProof },
            plan with { RequiresLazyStateEvaluation = !plan.RequiresLazyStateEvaluation },
            plan with { CaptureProjectedIndexIdentity = !plan.CaptureProjectedIndexIdentity },
            plan with { IsStaticallyUnsupported = !plan.IsStaticallyUnsupported },
            plan with
            {
                RepairOperationalImpact = plan.RepairOperationalImpact == SafeMigrationOperationalImpact.Unknown
                    ? SafeMigrationOperationalImpact.TableRewritePossible
                    : SafeMigrationOperationalImpact.Unknown,
            },
        };

        MaintenanceAttribution.Require(
            variants.All(variant => Shape(variant) != shape),
            "The MySQL plan-output oracle ignores a changed SQL, guard, impact or flag.");

        var first = new MySqlCatalogParameterValue("first", "varchar(64)");
        var second = new MySqlCatalogParameterValue("second", "varchar(64)");
        var binaryFirst = new MySqlSafeMigrationBinaryDefaultProof("__DOKA_SM_BINARY_DEFAULT_0__", "TRUE", "SELECT 1");

        var binarySecond = new MySqlSafeMigrationBinaryDefaultProof(
            "__DOKA_SM_BINARY_DEFAULT_1__",
            "FALSE",
            "SELECT 2");

        var identifierFirst = new MySqlSafeMigrationIdentifierReferenceProof(
            "__DOKA_SM_IDENTIFIER_REFERENCE_0__",
            "first_head",
            "00000000000000000000000000000001");

        var identifierSecond = new MySqlSafeMigrationIdentifierReferenceProof(
            "__DOKA_SM_IDENTIFIER_REFERENCE_1__",
            "second_head",
            "00000000000000000000000000000001");

        var ordered = plan with
        {
            ParameterValues = [first, second],
            BinaryDefaultProofs = [binaryFirst, binarySecond],
            IdentifierReferenceProofs = [identifierFirst, identifierSecond],
        };

        var orderedShape = Shape(ordered);
        MaintenanceAttribution.Require(
            Shape(ordered with { ParameterValues = [second, first] }) != orderedShape
            && Shape(ordered with { BinaryDefaultProofs = [binarySecond, binaryFirst] }) != orderedShape
            && Shape(ordered with { IdentifierReferenceProofs = [identifierSecond, identifierFirst] }) != orderedShape,
            "The MySQL plan-output oracle ignores ordered parameters or proof identity.");
    }

    /// <summary>Builds empty, duplicate, multiple, old/target and deep expression dependency roles.</summary>
    /// <returns>The complete immutable prerequisite workload inventory and exact column oracles.</returns>
    internal static (string Id, SafeMigrationIntent Intent, string[] Expected)[] Prerequisites()
    {
        var identifier = SafeMigrationSql.Identifier("source");
        var duplicate = SafeMigrationSql.Binary(identifier, SafeMigrationSqlBinaryOperator.Add, identifier);
        var multiple = Enumerable
            .Range(0, 32)
            .Select(static index => $"source_{index:D2}")
            .ToArray();

        var deep = identifier;
        for (var index = 0; index < 1024; index++)
        {
            deep = SafeMigrationSql.Binary(deep, SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(1));
        }

        var definitions = new (string Id, SafeMigrationIntent Intent, string[] Expected)[]
        {
            ("no_expression",
                new EnsureColumnIntent("rows", new ExpectedColumnDefinition("value", typeof(int), false)), []),
            ("one_identifier", Computed(identifier), ["source"]),
            ("duplicate_identifiers", Computed(duplicate), ["source"]),
            ("deep_identifier", Computed(deep), ["source"]),
            ("many_index_columns",
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix",
                        "rows",
                        multiple.Select(static column => new ExpectedIndexKeyDefinition(column)))), multiple),
            ("old_and_target",
                new AlterColumnIntent(
                    "rows",
                    new ExpectedColumnDefinition(
                        "value",
                        typeof(int),
                        true,
                        defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Identifier("new_source"))),
                    new ExpectedColumnDefinition(
                        "value",
                        typeof(int),
                        true,
                        defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Identifier("old_source")))),
                ["new_source", "old_source"]),
            ("check",
                new EnsureCheckConstraintIntent(
                    ExpectedCheckConstraintDefinition.FromExpression("ck", "rows", duplicate)), ["source"]),
        };

        return definitions;

        static EnsureColumnIntent Computed(
            SafeMigrationSqlExpression expression
        ) => new("rows", new ExpectedColumnDefinition("value", typeof(int), true, computedExpression: expression));
    }

    /// <summary>Constructs the real pinned Doka profile once, outside all measured loops.</summary>
    /// <param name="context">The context supplying native mappings and model.</param>
    /// <param name="operation">The immutable operation under measurement.</param>
    /// <param name="mariaDb">Whether the canonical MariaDB profile is selected.</param>
    /// <returns>The fully initialized Doka migration-operation context.</returns>
    private static MySqlMigrationOperationContext OperationContext(
        DbContext context,
        SafeMigrationOperation operation,
        bool mariaDb
    )
    {
        // WHY: Doka intentionally exposes no public feature/context constructor. This is fixture setup,
        // not timed reflection, and uses its canonical profile rather than an uninitialized capability stub.
        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.NonPublic;

        var version = mariaDb
            ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
            : MySqlServerVersion.MySql(new Version(8, 4, 11));

        var property = typeof(MySqlServerVersion).GetProperty("Profile", flags)
            ?? throw new InvalidOperationException("The pinned Doka canonical profile property is absent.");

        var profile = property.GetValue(version)
            ?? throw new InvalidOperationException("The pinned Doka canonical profile is absent.");

        var features = typeof(MySqlMigrationFeatureSet)
            .GetConstructors(flags)
            .Single()
            .Invoke([profile]);

        return (MySqlMigrationOperationContext)typeof(MySqlMigrationOperationContext)
            .GetConstructors(flags)
            .Single()
            .Invoke(
            [
                operation,
                context.Model,
                MigrationsSqlGenerationOptions.Default,
                version,
                features,
                0,
                "maintenance-attribution",
                (Func<MigrationOperation, IReadOnlyList<MySqlMigrationCommandSpec>>)Standard,
            ]);

        static IReadOnlyList<MySqlMigrationCommandSpec> Standard(
            MigrationOperation _
        ) => [];
    }

    /// <summary>Provides real type mappings for one explicitly selected generation profile.</summary>
    /// <param name="mariaDb">Whether MariaDB rather than MySQL is selected.</param>
    private sealed class ProfileContext(
        bool mariaDb
    ) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            var version = mariaDb
                ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
                : MySqlServerVersion.MySql(new Version(8, 4, 11));

            optionsBuilder.UseMySql("Server=127.0.0.1;Port=1;User ID=benchmark;Database=benchmark", version);
        }
    }
}
