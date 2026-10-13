namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies atomic required-SQL-default add eligibility and projected Missing callback boundaries.</summary>
public sealed class MySqlProjectedMissingSqlDefaultColumnTests
{
    /// <summary>Engine inventory returns fixed ordinals rather than substituting native owner spelling.</summary>
    [Fact]
    public void EngineInventoryBindsAuthoredOrdinalsWithNativeOwnerPredicates()
    {
        // Arrange
        const string owners = "SELECT 7 AS ordinal, @owner AS owner";

        // Act
        var sql = MySqlSafeMigrationProviderAnalyzer.BuildRequiredColumnAddOwnerEngineQuery(owners, "@owner");

        // Assert
        Assert.StartsWith("SELECT source_owner.ordinal, EXISTS (", sql, StringComparison.Ordinal);
        Assert.Contains(owners, sql, StringComparison.Ordinal);
        Assert.Contains("physical_owner.TABLE_NAME = source_owner.owner", sql, StringComparison.Ordinal);
        Assert.Contains("physical_owner.TABLE_NAME IN (@owner)", sql, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(physical_owner.TABLE_NAME AS BINARY) = CAST(source_owner.owner AS BINARY)",
            sql, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(LOWER(physical_owner.TABLE_NAME) AS BINARY) = CAST(LOWER(source_owner.owner) AS BINARY)", sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CAST(physical_owner.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", sql, StringComparison.Ordinal);
        Assert.Contains("UPPER(physical_owner.ENGINE) = 'INNODB'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT physical_owner.TABLE_NAME", sql, StringComparison.Ordinal);
    }

    /// <summary>No-default staging retains every canonical feature and captured engine/session prerequisite.</summary>
    /// <param name="innoDb">Whether transactional owner storage is captured.</param>
    /// <param name="strict">Whether strict assignment semantics are captured.</param>
    /// <param name="checks">Whether the session enforces CHECK constraints.</param>
    /// <param name="supported">Whether canonical provider capabilities support enforced CHECK constraints.</param>
    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void AtomicNoDefaultRequiresEveryCapturedPrerequisite(bool innoDb, bool strict, bool checks, bool supported)
    {
        // Arrange
        var expected = innoDb && strict && checks && supported;

        // Act
        var safe = MySqlProjectedColumnTransition.HasSafeAtomicRequiredColumnAddEnvironment(
            innoDb, strict, checks, supported);

        // Assert
        Assert.Equal(expected, safe);
    }

    /// <summary>Every ENUM/SET declaration retains quote semantics, not only standard-escape protocols.</summary>
    /// <param name="storeType">The exact declared member list or a non-member-list control.</param>
    /// <param name="requiresEscapes">Whether the canonical producer requires standard backslash decoding.</param>
    /// <param name="escapesEnabled">Whether captured native mode decodes backslashes.</param>
    /// <param name="quotesAreLiteral">Whether captured native mode permits double-quoted literals.</param>
    /// <param name="expected">Whether the captured mode supports this exact declaration.</param>
    [Theory]
    [InlineData("enum(\"Alpha\")", false, true, false, false)]
    [InlineData("enum(\"Alpha\")", false, true, true, true)]
    [InlineData("enum(\"Alpha\")", false, false, false, false)]
    [InlineData("enum(\"Alpha\")", false, false, true, true)]
    [InlineData("set(\"Alpha\")", false, false, false, false)]
    [InlineData("set('Alpha')", false, false, false, true)]
    [InlineData("enum('can\\'t')", true, true, true, true)]
    [InlineData("enum('can\\'t')", true, false, true, false)]
    [InlineData("enum('Alpha'", false, true, true, false)]
    [InlineData("int", false, false, false, true)]
    public void RequiredAddLiteralModeRetainsBackslashAndAnsiBounds(
        string storeType,
        bool requiresEscapes,
        bool escapesEnabled,
        bool quotesAreLiteral,
        bool expected
    )
    {
        // Arrange
        var definition = new ExpectedColumnDefinition("value", typeof(string), false, storeType);

        // Act
        var safe = MySqlProjectedColumnTransition.HasSafeRequiredAddLiteralMode(definition,
            requiresEscapes, escapesEnabled, quotesAreLiteral);

        // Assert
        Assert.Equal(expected, safe);
    }

    /// <summary>Only actual fresh unsafe Missing can produce an empty-row certificate.</summary>
    /// <param name="sqlNull">Whether the ordinary add uses a known structured SQL NULL default.</param>
    /// <param name="staged">Whether the source is an owned staged add with potentially populated rows.</param>
    /// <param name="state">The captured provider classification.</param>
    [Theory]
    [InlineData(false, false, SafeMigrationObservedState.Missing)]
    [InlineData(false, true, SafeMigrationObservedState.Missing)]
    [InlineData(false, false, SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(true, false, SafeMigrationObservedState.Missing)]
    [InlineData(true, false, SafeMigrationObservedState.PrerequisiteMissing)]
    public void FreshMissingProofNeverPromotesOwnedStageOrMissingOwner(
        bool sqlNull,
        bool staged,
        SafeMigrationObservedState state
    )
    {
        // Arrange
        var target = new ExpectedColumnDefinition("value", typeof(int), false, "int", defaultValue: sqlNull
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(null)) : SafeMigrationDefaultValue.None);
        var intent = new EnsureColumnIntent("items", target);
        var plan = new MySqlSafeMigrationRuntimePlan("'missing'", "FALSE", SafeMigrationRepairCapability.None, "FALSE")
        {
            RequiredColumnAddPlan = sqlNull ? null
                : new MySqlSafeMigrationRequiredColumnAddPlan("guard", "fresh", false)
            {
                StatusBinding = staged ? "'staged'" : "'fresh'",
                SupportsCheck = true,
            },
        };

        // Act
        var carriesEmptyProof = MySqlSafeMigrationProviderAnalyzer.IsFreshRequiredColumnAddMissing(intent, plan,
            state, staged ? "column_atomic_add_resume" : "classified_missing");

        // Assert
        Assert.Equal(state == SafeMigrationObservedState.Missing && !staged, carriesEmptyProof);
    }

    /// <summary>A blocked fresh add is row-dependent but never an empty-table certificate.</summary>
    /// <param name="sqlNull">Whether the ordinary add uses a known structured SQL NULL default.</param>
    /// <param name="staged">Whether the protocol instead represents an owned staged add.</param>
    /// <param name="state">The actual captured provider state.</param>
    [Theory]
    [InlineData(false, false, SafeMigrationObservedState.DataBlocked)]
    [InlineData(false, true, SafeMigrationObservedState.DataBlocked)]
    [InlineData(true, false, SafeMigrationObservedState.DataBlocked)]
    [InlineData(true, false, SafeMigrationObservedState.Missing)]
    [InlineData(true, false, SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(true, false, SafeMigrationObservedState.Unsupported)]
    public void BlockedFreshAddProofExpiresAfterDataMutationWithoutProvingEmpty(
        bool sqlNull,
        bool staged,
        SafeMigrationObservedState state
    )
    {
        // Arrange
        var target = new ExpectedColumnDefinition("value", typeof(int), false, "int", defaultValue: sqlNull
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(null)) : SafeMigrationDefaultValue.None);
        var intent = new EnsureColumnIntent("items", target);
        var plan = new MySqlSafeMigrationRuntimePlan("'data_blocked'", "FALSE", SafeMigrationRepairCapability.None,
            "FALSE")
        {
            RequiredColumnAddPlan = sqlNull ? null
                : new MySqlSafeMigrationRequiredColumnAddPlan("guard", "fresh", false)
                {
                    StatusBinding = staged ? "'staged'" : "'fresh'",
                    SupportsCheck = true,
                },
        };

        // Act
        var rowDependent = MySqlSafeMigrationProviderAnalyzer.IsBlockedRequiredColumnAddRowProof(intent, plan,
            state, staged ? "column_atomic_add_resume" : "classified_data_blocked");
        var provesEmpty = MySqlSafeMigrationProviderAnalyzer.IsFreshRequiredColumnAddMissing(intent, plan,
            state, staged ? "column_atomic_add_resume" : "classified_data_blocked");

        // Assert
        Assert.Equal(state == SafeMigrationObservedState.DataBlocked && !staged, rowDependent);
        Assert.Equal(state == SafeMigrationObservedState.Missing && !staged, provesEmpty);
        Assert.False(rowDependent && provesEmpty);
    }

    /// <summary>Every required SQL-default add needs transactional storage and strict assignment semantics.</summary>
    /// <param name="innoDb">Whether the relevant existing or creation engine is InnoDB.</param>
    /// <param name="strict">Whether strict SQL mode was captured.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RequiredSqlDefaultEnvironmentRequiresInnoDbAndStrictMode(bool innoDb, bool strict)
    {
        // Arrange
        using var context = CreateContext();
        var definition = SqlDefaultColumn(timestamp: false);

        // Act
        var safe = MySqlProjectedColumnTransition.HasSafeRequiredSqlDefaultAddEnvironment(definition,
            context.GetService<IRelationalTypeMappingSource>(), innoDb, strict, explicitTimestampDefaults: true);

        // Assert
        Assert.Equal(innoDb && strict, safe);
    }

    /// <summary>Required temporal SQL defaults also retain the captured legacy TIMESTAMP switch.</summary>
    /// <param name="explicitTimestampDefaults">Whether explicit TIMESTAMP defaults were captured.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredTimestampSqlDefaultNeedsExplicitTimestampDefaults(bool explicitTimestampDefaults)
    {
        // Arrange
        using var context = CreateContext();
        var definition = SqlDefaultColumn(timestamp: true);

        // Act
        var safe = MySqlProjectedColumnTransition.HasSafeRequiredSqlDefaultAddEnvironment(definition,
            context.GetService<IRelationalTypeMappingSource>(), isInnoDb: true, strictSqlMode: true,
            explicitTimestampDefaults);

        // Assert
        Assert.Equal(explicitTimestampDefaults, safe);
    }

    /// <summary>All policies qualify Missing adds after a created or existing owner proves absence.</summary>
    /// <param name="policy">The caller's unchanged conflict policy.</param>
    /// <param name="created">Whether an accepted earlier operation created this owner.</param>
    /// <param name="strict">Whether the test certificate authorizes strict-mode add eligibility.</param>
    [Theory]
    [InlineData(SafeMigrationPolicy.ExistenceOnly, false, false)]
    [InlineData(SafeMigrationPolicy.ExistenceOnly, false, true)]
    [InlineData(SafeMigrationPolicy.ExistenceOnly, true, false)]
    [InlineData(SafeMigrationPolicy.ExistenceOnly, true, true)]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent, false, false)]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent, false, true)]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent, true, false)]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent, true, true)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe, false, false)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe, false, true)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe, true, false)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe, true, true)]
    public void ProjectedMissingConsultsProviderQualificationForEveryPolicy(
        SafeMigrationPolicy policy,
        bool created,
        bool strict
    )
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = new MissingAddAnalyzer(context.GetService<IRelationalTypeMappingSource>(), strict);
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: analyzer);
        var owner = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("items",
            [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
            SafeMigrationTableMode.ConvergenceContainer), SafeMigrationPolicy.ThrowIfDifferent);
        var ownerLive = Proof(created ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching);
        var ownerResult = projection.Project(owner, ownerLive);
        projection.Observe(owner, ownerLive, ownerResult, SafeMigrationDecisionPlanner.Plan(owner.Intent.Kind,
            ownerResult.ObservedState, owner.Policy, ownerResult.RepairCapability));
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("items", SqlDefaultColumn(timestamp: false)),
            policy);
        var live = Proof(created ? SafeMigrationObservedState.PrerequisiteMissing : SafeMigrationObservedState.Missing);

        // Act
        var result = projection.Project(ensure, live);
        var decision = SafeMigrationDecisionPlanner.Plan(ensure.Intent.Kind, result.ObservedState,
            ensure.Policy, result.RepairCapability);

        // Assert
        Assert.Equal(1, analyzer.MissingCalls);
        Assert.Equal(created, analyzer.LastContext.TableWasCreated);
        Assert.Equal(strict ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Unsupported,
            result.ObservedState);
        Assert.Equal(strict ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported, decision.Action);
    }

    /// <summary>Existing exact Matching targets are never reclassified by a Missing-only eligibility hook.</summary>
    /// <param name="policy">The unchanged conflict policy.</param>
    [Theory]
    [InlineData(SafeMigrationPolicy.ExistenceOnly)]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe)]
    public void MatchingRequiredSqlDefaultDoesNotCallMissingHook(SafeMigrationPolicy policy)
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = new MissingAddAnalyzer(context.GetService<IRelationalTypeMappingSource>(), strict: false);
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: analyzer);
        var target = SqlDefaultColumn(timestamp: false);
        var owner = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("items", [target]),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);
        var missing = Proof(SafeMigrationObservedState.Missing);
        var creation = projection.Project(owner, missing);
        projection.Observe(owner, missing, creation, SafeMigrationDecisionPlanner.Plan(owner.Intent.Kind,
            creation.ObservedState, owner.Policy, creation.RepairCapability));
        var ensure = new SafeMigrationOperation(new EnsureColumnIntent("items", target), policy);

        // Act
        var result = projection.Project(ensure, missing);
        var decision = SafeMigrationDecisionPlanner.Plan(ensure.Intent.Kind, result.ObservedState,
            ensure.Policy, result.RepairCapability);

        // Assert
        Assert.Equal(0, analyzer.MissingCalls);
        Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
    }

    /// <summary>Providers without Missing qualification retain their existing projection behavior.</summary>
    [Fact]
    public void DefaultMissingProviderHookPreservesBehavior()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", SqlDefaultColumn(timestamp: false)),
            SafeMigrationPolicy.RepairIfSafe);
        var missing = Proof(SafeMigrationObservedState.Missing);

        // Act
        var result = projection.Project(operation, missing);

        // Assert
        Assert.Same(missing, result);
    }

    private static ExpectedColumnDefinition SqlDefaultColumn(bool timestamp) => new("value",
        timestamp ? typeof(DateTime) : typeof(int), false, timestamp ? "timestamp" : "int",
        defaultValue: SafeMigrationDefaultValue.Sql(timestamp
            ? SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp) : SafeMigrationSql.Literal(1)));

    private static SafeMigrationProviderAnalysis Proof(SafeMigrationObservedState state) =>
        new(state, SafeMigrationRepairCapability.None, false, "test_owned_column");

    private static DbContext CreateContext() => new(new DbContextOptionsBuilder<DbContext>()
        .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11))).UseMySqlSafeMigrations().Options);

    private sealed class MissingAddAnalyzer(IRelationalTypeMappingSource mappings, bool strict)
        : ISafeMigrationProjectedColumnAnalyzer
    {
        internal int MissingCalls { get; private set; }

        internal SafeMigrationProjectedMissingColumnContext LastContext { get; private set; }

        public SafeMigrationProviderAnalysis ValidateProjectedMissingColumnAdd(EnsureColumnIntent intent,
            SafeMigrationProjectedMissingColumnContext context, SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis)
        {
            MissingCalls++;
            LastContext = context;

            return MySqlProjectedColumnTransition.HasSafeRequiredSqlDefaultAddEnvironment(intent.Definition,
                mappings, isInnoDb: true, strict, explicitTimestampDefaults: true)
                    ? projectedAnalysis
                    : new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
                        SafeMigrationRepairCapability.None, false, "column_required_sql_add_unavailable");
        }

        public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(AlterColumnIntent intent,
            ExpectedColumnDefinition source, SafeMigrationProjectedAlterColumnContext context,
            SafeMigrationProviderAnalysis liveAnalysis, SafeMigrationProviderAnalysis projectedAnalysis) =>
            throw new InvalidOperationException("This fixture validates only Missing column adds.");
    }
}
