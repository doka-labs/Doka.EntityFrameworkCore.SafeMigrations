namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Qualifies replacement rows independently of an existing native primary key.</summary>
public sealed class MySqlProjectedKeyReplacementTests
{
    /// <summary>An existing different PK cannot conceal NULL or duplicate replacement values.</summary>
    /// <param name="mariaDb">Whether MariaDB's qualified native profile is used.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimaryKeyPlanCapturesIndependentReplacementRows(bool mariaDb)
    {
        // Arrange
        using var context = Context(mariaDb);
        var operation = new SafeMigrationOperation(
            new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_items", "items", ["event_id", "tag_id"])),
            SafeMigrationPolicy.ThrowIfDifferent);
        SafeMigrationOperation[] operations =
        [
            new(new DropPrimaryKeyIntent("pk_items", "items"), SafeMigrationPolicy.ThrowIfDifferent), operation,
        ];

        // Act
        using var capture = context.GetService<MySqlSafeMigrationPlanCapture>().Begin(operations);
        _ = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);
        var plan = capture.Complete()[1];

        // Assert
        Assert.NotNull(plan.ClassificationCodeExpression);
        Assert.Contains("primary_key_replacement_data_blocked", plan.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("key_replacement_data_safe", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("IS NULL", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Only a provider-specific contract may retain intrinsic siblings across a native DROP.</summary>
    /// <param name="mariaDb">Whether the provider uses MariaDB's native profile.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeColumnDropCertifiesOnlyUnchangedIntrinsicSources(bool mariaDb)
    {
        // Arrange
        using var context = Context(mariaDb);
        var analyzer = Assert.IsAssignableFrom<ISafeMigrationProjectedColumnAnalyzer>(
            context.GetService<ISafeMigrationProviderAnalyzer>());

        // Act
        var preserved = analyzer.PreservesIntrinsicColumnSources(new DropColumnIntent("legacy", "items"));
        var renamed = analyzer.PreservesIntrinsicColumnSources(new RenameColumnIntent("legacy", "items", "changed"));

        // Assert
        Assert.True(preserved);
        Assert.False(renamed);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Ordinary differing PK contracts must not acquire a new replacement-row scan.</summary>
    /// <param name="dropTable">The removed PK's owner, or null when there was no preceding removal.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("other_items")]
    public void UnrelatedPrimaryKeysDoNotCaptureReplacementRows(string? dropTable)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var target = new SafeMigrationOperation(Primary(), SafeMigrationPolicy.ThrowIfDifferent);
        SafeMigrationOperation[] operations = dropTable is null
            ? [target]
            : [new(new DropPrimaryKeyIntent("pk_items", dropTable), SafeMigrationPolicy.ThrowIfDifferent), target];

        // Act
        using var capture = context.GetService<MySqlSafeMigrationPlanCapture>().Begin(operations);
        _ = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);
        var plan = capture.Complete()[^1];

        // Assert
        Assert.Null(plan.ClassificationCodeExpression);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    /// <summary>Full-stream probe selection survives windows without authorizing lookalike operations.</summary>
    [Fact]
    public void ReplacementCaptureUsesExactReferencesAcrossWindows()
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var target = new SafeMigrationOperation(Primary(), SafeMigrationPolicy.ThrowIfDifferent);
        SafeMigrationOperation[] complete =
        [
            new(new DropPrimaryKeyIntent("pk_items", "items"), SafeMigrationPolicy.ThrowIfDifferent), target,
        ];

        var selected = MySqlSafeMigrationPlanCapture.CreatePrimaryKeyReplacementTargets(
            complete, currentDatabase: null, StringComparer.Ordinal);
        var unique = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes(complete);
        var constraints = SafeMigrationExpectedTableConstraints.FromOperations(complete,
            static schema => schema, StringComparer.Ordinal, '`', false, StringComparer.OrdinalIgnoreCase);
        var captureSource = context.GetService<MySqlSafeMigrationPlanCapture>();

        // Act
        using var capture = captureSource.Begin([target], unique, constraints, primaryKeyReplacements: selected);
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([target], context.Model);
        var plan = Assert.Single(capture.Complete());
        var selectedReference = selected.Contains(target);
        var lookalike = selected.Contains(new SafeMigrationOperation(Primary(), SafeMigrationPolicy.ThrowIfDifferent));
        capture.Dispose();
        var runtime = context.GetService<IMigrationsSqlGenerator>().Generate(complete, context.Model);

        // Assert
        Assert.NotNull(plan.ClassificationCodeExpression);
        Assert.True(selectedReference);
        Assert.False(lookalike);
        Assert.False(captureSource.RequiresPrimaryKeyReplacementRows(target));
        Assert.All(runtime, command => Assert.DoesNotContain("key_replacement_data_safe", command.CommandText,
            StringComparison.Ordinal));
    }

    /// <summary>Probe owner identities follow the native table comparer and selected database.</summary>
    /// <param name="ignoreCase">Whether the independently observed native owner policy folds case.</param>
    /// <param name="expected">Whether both spellings identify the same physical owner.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ReplacementCapturePreservesPhysicalOwnerIdentity(bool ignoreCase, bool expected)
    {
        // Arrange
        var target = new SafeMigrationOperation(new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_items", "ITEMS", ["id"])), SafeMigrationPolicy.ThrowIfDifferent);
        SafeMigrationOperation[] operations =
        [
            new(new DropPrimaryKeyIntent("pk_items", "items", "selected"), SafeMigrationPolicy.ThrowIfDifferent),
            target,
        ];

        // Act
        var selected = MySqlSafeMigrationPlanCapture.CreatePrimaryKeyReplacementTargets(operations,
            "selected", ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // Assert
        Assert.Equal(expected, selected.Contains(target));
    }

    /// <summary>Physical feasibility does not substitute for independent replacement-row evidence.</summary>
    /// <param name="code">The actual classifier result for replacement values while the old PK still exists.</param>
    /// <param name="expected">The exact qualified state after accepted independent removal.</param>
    [Theory]
    [InlineData("key_replacement_data_safe", SafeMigrationObservedState.Missing)]
    [InlineData("primary_key_replacement_data_blocked", SafeMigrationObservedState.DataBlocked)]
    public void ExistingReplacementMembersUseIndependentRows(string code, SafeMigrationObservedState expected)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var analyzer = KeyAnalyzer(context);
        var source = Source.Create(valuesPreserved: true);
        var live = Live(SafeMigrationObservedState.Different, code);

        // Act
        var result = analyzer.ValidateProjectedPrimaryKey(Primary(), source, live, Unknown());

        // Assert
        Assert.Equal(expected, result.ObservedState);
        Assert.Null(result.ProviderDeferredOriginOrdinal);
        Assert.Equal(1, source.ValueProofQueries);
    }

    /// <summary>Existing rows cannot authorize a different key without an accepted removal and current epoch.</summary>
    /// <param name="primaryDropped">Whether the ordered stream actually accepted the old PK removal.</param>
    /// <param name="dataChanged">Whether unanalysed row changes invalidate the original comparison.</param>
    /// <param name="recreated">Whether a new physical owner replaced the source relation.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void StaleReplacementScopeCannotBorrowOriginalRows(bool primaryDropped, bool dataChanged, bool recreated)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var source = Source.Create(valuesPreserved: true);
        source.State = new SafeMigrationProjectedTableState(recreated, dataChanged, primaryDropped, true)
        {
            ColumnDropOriginOrdinal = 1,
        };

        var unknown = Unknown();

        // Act
        var result = KeyAnalyzer(context).ValidateProjectedPrimaryKey(Primary(), source,
            Live(SafeMigrationObservedState.Different, "key_replacement_data_safe"), unknown);

        // Assert
        Assert.Same(unknown, result);
        Assert.Null(result.ProviderDeferredOriginOrdinal);
        Assert.Equal(0, source.ValueProofQueries);
    }

    /// <summary>New UUID/default values require actual ordered validation, not prospective uniqueness.</summary>
    /// <param name="dropOrigin">Whether an independent sibling DROP preceded the generated addition.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void NewGeneratedValuesDeferToTheActualAddition(int? dropOrigin)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var source = Source.Create(valuesPreserved: false);
        source.Definition = new ExpectedColumnDefinition("id", typeof(Guid), false, "char(36)",
            maxLength: 36, isFixedLength: true, defaultValue: SafeMigrationDefaultValue.Sql("UUID()"));
        source.MutationOrigin = 2;
        source.State = source.State with { ColumnDropOriginOrdinal = dropOrigin };
        var target = new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]));
        var marker = dropOrigin.HasValue ? Unknown() : Unknown("projected_primary_key_replacement_unproven");

        // Act
        var result = KeyAnalyzer(context).ValidateProjectedPrimaryKey(target, source,
            Live(SafeMigrationObservedState.PrerequisiteMissing, "classified_prerequisite_missing"), marker);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal("projected_key_values_require_runtime", result.Code);
        Assert.Equal(2, result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>Dropping and repopulating a member invalidates its original row or physical rejection.</summary>
    /// <param name="oldState">The immutable result captured before the accepted new member existed.</param>
    /// <param name="oldCode">The old value-dependent row or physical classifier.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.DataBlocked, "classified_data_blocked")]
    [InlineData(SafeMigrationObservedState.Unsupported, "primary_key_exceeds_physical_limit")]
    public void RepopulatedMemberDoesNotReuseStaleFailure(SafeMigrationObservedState oldState, string oldCode)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var source = Source.Create(valuesPreserved: false);
        source.Definition = new ExpectedColumnDefinition("id", typeof(Guid), false, "char(36)",
            maxLength: 36, isFixedLength: true, defaultValue: SafeMigrationDefaultValue.Sql("UUID()"));
        source.MutationOrigin = 2;

        // Act
        var result = KeyAnalyzer(context).ValidateProjectedPrimaryKey(Primary(), source,
            Live(oldState, oldCode), Unknown());

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal("projected_key_values_require_runtime", result.Code);
        Assert.Equal(2, result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>A dropped target member cannot be resurrected by its original captured physical width.</summary>
    [Fact]
    public void DroppedMemberRetainsMissingPrerequisiteInsteadOfDeferring()
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var source = Source.Create(valuesPreserved: false);
        var marker = Unknown();

        // Act
        var result = KeyAnalyzer(context).ValidateProjectedPrimaryKey(Primary(), source,
            Live(SafeMigrationObservedState.Different, "key_replacement_data_safe"), marker);

        // Assert
        Assert.Same(marker, result);
        Assert.Null(result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>Actual unsupported and blocked targets remain refusals despite eligible DROP provenance.</summary>
    /// <param name="state">The independent refusal whose semantics cannot be erased by projection.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    [InlineData(SafeMigrationObservedState.DataBlocked)]
    public void IndependentRefusalIsNeverConvertedToRuntimeValidation(SafeMigrationObservedState state)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var source = Source.Create(valuesPreserved: true);
        var live = Live(state, "independent_refusal");

        // Act
        var result = KeyAnalyzer(context).ValidateProjectedPrimaryKey(Primary(), source, live, Unknown());

        // Assert
        Assert.Same(live, result);
        Assert.Null(result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>Deferred rows cannot authorize a physically over-budget or unsupported key.</summary>
    /// <param name="maximumBytes">The independently captured physical storage budget.</param>
    /// <param name="code">The expected physical refusal.</param>
    [Theory]
    [InlineData(0, "primary_key_storage_engine_unsupported")]
    [InlineData(3, "primary_key_exceeds_physical_limit")]
    public void PhysicalFailurePrecedesRowDeferral(int maximumBytes, string code)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var source = Source.Create(valuesPreserved: true);
        var live = Live(SafeMigrationObservedState.Different, "key_replacement_data_safe", maximumBytes);

        // Act
        var result = KeyAnalyzer(context).ValidateProjectedPrimaryKey(Primary(), source, live, Unknown());

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, result.ObservedState);
        Assert.Equal(code, result.Code);
        Assert.Null(result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>A surviving composite-index alias may change after DROP and must be observed again.</summary>
    /// <param name="unique">Whether runtime must also retain the unique-index row guard.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalIndexIdentityIsNotReusedAfterColumnDrop(bool unique)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var source = Source.Create(valuesPreserved: true);
        var target = new EnsureIndexIntent(new ExpectedIndexDefinition("ix_items_id", "items",
            [new ExpectedIndexKeyDefinition(column: "id")], unique: unique));

        var live = Live(SafeMigrationObservedState.Matching, "classified_matching");

        // Act
        var result = KeyAnalyzer(context).ValidateProjectedIndex(target, source, live, Unknown());

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal(1, result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>Current width proof supersedes stale physical failure, not unsupported native features.</summary>
    /// <param name="constraint">Whether the target is a unique constraint instead of an index.</param>
    /// <param name="physical">Whether the old refusal belongs to the independently requalified width.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void CurrentPhysicalProofDoesNotReuseStaleKeyFailure(bool constraint, bool physical)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var source = Source.Create(valuesPreserved: true);
        var code = physical
            ? constraint ? "unique_constraint_exceeds_physical_limit" : "index_key_exceeds_physical_limit"
            : "independent_unsupported_feature";

        var live = Live(SafeMigrationObservedState.Unsupported, code);
        var analyzer = KeyAnalyzer(context);

        // Act
        var result = constraint
            ? analyzer.ValidateProjectedUniqueConstraint(new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_items", "items", ["id"])), source, live, Unknown())
            : analyzer.ValidateProjectedIndex(new EnsureIndexIntent(new ExpectedIndexDefinition("ix_items", "items",
                [new ExpectedIndexKeyDefinition(column: "id")])), source, live, Unknown());

        // Assert
        if (physical)
        {
            Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
            Assert.Equal(1, result.ProviderDeferredOriginOrdinal);
        }
        else
        {
            Assert.Same(live, result);
            Assert.Null(result.ProviderDeferredOriginOrdinal);
        }
    }

    /// <summary>An independently blocked unique replacement remains blocked before destructive removal.</summary>
    /// <param name="constraint">Whether the target is a constraint instead of an ordinary unique index.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UniqueReplacementDuplicateProofIsNotHiddenByOldIdentity(bool constraint)
    {
        // Arrange
        using var context = Context(mariaDb: true);
        var source = Source.Create(valuesPreserved: true);
        var code = constraint ? "unique_constraint_replacement_data_blocked" : "index_replacement_data_blocked";
        var live = Live(SafeMigrationObservedState.Different, code);
        var analyzer = KeyAnalyzer(context);

        // Act
        var result = constraint
            ? analyzer.ValidateProjectedUniqueConstraint(new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_items", "items", ["id"])), source, live, Unknown())
            : analyzer.ValidateProjectedIndex(new EnsureIndexIntent(new ExpectedIndexDefinition("ix_items", "items",
                [new ExpectedIndexKeyDefinition(column: "id")], unique: true)), source, live, Unknown());

        // Assert
        Assert.Equal(SafeMigrationObservedState.DataBlocked, result.ObservedState);
        Assert.Equal(code, result.Code);
        Assert.Null(result.ProviderDeferredOriginOrdinal);
    }

    /// <summary>Resolves the real native proof implementation without starting a database.</summary>
    private static ISafeMigrationProjectedKeyAnalyzer KeyAnalyzer(DbContext context)
        => Assert.IsAssignableFrom<ISafeMigrationProjectedKeyAnalyzer>(
            context.GetService<ISafeMigrationProviderAnalyzer>());

    /// <summary>Constructs the exact replacement target used by isolated provider contracts.</summary>
    private static EnsurePrimaryKeyIntent Primary()
        => new(new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]));

    /// <summary>Models Core's unproven marker without granting row or table authority.</summary>
    private static SafeMigrationProviderAnalysis Unknown(string code = "projected_key_after_column_drop_unproven")
        => new(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationRepairCapability.None, false, code);

    /// <summary>Represents independently captured physical width plus an original classifier result.</summary>
    private static SafeMigrationProviderAnalysis Live(SafeMigrationObservedState state, string code,
        int maximumBytes = 3072)
        => new(state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching, code)
        {
            IndexPhysicalEnvironment = new SafeMigrationIndexPhysicalEnvironment(maximumBytes,
                Columns: new Dictionary<string, SafeMigrationIndexColumnPhysicalShape>(StringComparer.Ordinal)
                {
                    ["id"] = new(null, 0, 4, false),
                }),
        };

    /// <summary>Supplies independent member and provenance facts, never a complete native table.</summary>
    private sealed class Source : ISafeMigrationProjectedColumnSource, ISafeMigrationProjectedTableSource
    {
        private bool _valuesPreserved;

        /// <summary>Gets or sets the accepted owner lifetime and exact preceding DROP origin.</summary>
        public SafeMigrationProjectedTableState State { get; set; }

        /// <summary>Gets or sets an accepted new member, if one actually exists.</summary>
        public ExpectedColumnDefinition? Definition { get; set; }

        /// <summary>Gets or sets the original complete-stream ordinal of actual member population.</summary>
        public int? MutationOrigin { get; set; }

        /// <summary>Gets how often the provider consumes the value-domain certificate.</summary>
        public int ValueProofQueries { get; private set; }

        /// <summary>Creates a strict source and a real preceding DROP origin.</summary>
        public static Source Create(bool valuesPreserved)
            => new()
            {
                _valuesPreserved = valuesPreserved,
                State = new SafeMigrationProjectedTableState(false, false, true, true)
                {
                    ColumnDropOriginOrdinal = 1,
                },
            };

        /// <inheritdoc />
        public bool TryGetProjectedColumn(string table, string? schema, string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            definition = Definition;

            return definition is not null && column == definition.Name;
        }

        /// <inheritdoc />
        public bool PreservesLiveColumnValues(string table, string? schema, string column)
        {
            ValueProofQueries++;

            return _valuesPreserved;
        }

        /// <inheritdoc />
        public int? GetColumnValueMutationOrigin(string table, string? schema, string column) => MutationOrigin;

        /// <inheritdoc />
        public bool TryGetProjectedTableState(string table, string? schema, out SafeMigrationProjectedTableState state)
        {
            state = State;

            return true;
        }
    }

    /// <summary>Provides real mappings and capture without opening a native connection.</summary>
    private static DbContext Context(bool mariaDb)
    {
        var version = mariaDb
            ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
            : MySqlServerVersion.MySql(new Version(8, 4, 11));

        return new DbContext(new DbContextOptionsBuilder()
            .UseMySql("Server=127.0.0.1;Port=1;Database=key_replacement;User ID=test;Password=test;"
                + "Allow User Variables=true", version)
            .UseMySqlSafeMigrations().Options);
    }
}
