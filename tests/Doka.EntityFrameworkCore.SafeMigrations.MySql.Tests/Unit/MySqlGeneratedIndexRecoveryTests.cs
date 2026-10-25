namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Keeps generated-index recovery separate from uniqueness, expressions and native features.</summary>
public sealed class MySqlGeneratedIndexRecoveryTests
{
    /// <summary>
    /// Independent captures use the current database and native owner profile, not a prior invocation.
    /// </summary>
    /// <param name="foldOwners">Whether the current native server folds physical table identities.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureRebindsCurrentDatabaseAndOwnerProfile(bool foldOwners)
    {
        // Arrange
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
                MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations().Options;

        using var context = new DbContext(options);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var previous = new MigrationBuilder(context.Database.ProviderName!);
        AddCandidate(previous, "old_owner", "old_owner", "old_owner", "old_database");
        Prepare(analyzer, previous, "old_database", foldOwners: false);
        var current = new MigrationBuilder(context.Database.ProviderName!);
        AddCandidate(current, "CurrentOwner", "currentowner", "CURRENTOWNER", "current_database");

        // Act
        Prepare(analyzer, current, "current_database", foldOwners);
        var captures = typeof(MySqlSafeMigrationProviderAnalyzer)
            .GetField("_generatedIndexIdentityProofs", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(analyzer) as System.Collections.IDictionary;

        // Assert
        if (foldOwners)
        {
            Assert.NotNull(captures);
            Assert.Single(captures);
            Assert.True(captures.Contains(current.Operations[2]));
            Assert.False(captures.Contains(previous.Operations[2]));
        }
        else
        {
            Assert.Null(captures);
        }
    }

    /// <summary>
    /// Builds a candidate whose qualified DROP and unqualified generated member share one native owner.
    /// </summary>
    private static void AddCandidate(MigrationBuilder builder, string dropOwner, string columnOwner,
        string indexOwner, string database)
    {
        builder.DropColumnIfExists("unrelated", dropOwner, database);
        builder.EnsureColumn(columnOwner, new ExpectedColumnDefinition("computed", typeof(int), true, "int",
            computedExpression: SafeMigrationSql.Identifier("source")), SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_computed", indexOwner,
            [new ExpectedIndexKeyDefinition("computed")], schema: database), SafeMigrationPolicy.ThrowIfDifferent);
    }

    /// <summary>
    /// Sets a proved native identity profile before preparing this invocation's reference-bound captures.
    /// </summary>
    private static void Prepare(ISafeMigrationProviderAnalyzer analyzer, MigrationBuilder builder,
        string database, bool foldOwners)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MySqlSafeMigrationProviderAnalyzer).GetField("_currentDatabase", flags)?.SetValue(analyzer, database);
        typeof(MySqlSafeMigrationProviderAnalyzer).GetField("_tableNamesIgnoreCase", flags)?.SetValue(analyzer,
            foldOwners);

        typeof(MySqlSafeMigrationProviderAnalyzer).GetMethod("PrepareGeneratedIndexIdentityCapture", flags)
            ?.Invoke(analyzer, [builder.Operations.Cast<SafeMigrationOperation>().ToArray()]);
    }

    /// <summary>Implicit native index creation expires success and absence but retains known conflicts.</summary>
    /// <param name="identity">The current identity from accepted projection, or null for live fallback.</param>
    /// <param name="changed">Whether an accepted constraint changed the native index namespace.</param>
    /// <param name="expected">Whether this identity can still participate in bounded recovery.</param>
    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, false)]
    [InlineData(SafeMigrationObservedState.Missing, false, true)]
    [InlineData(SafeMigrationObservedState.Missing, true, false)]
    [InlineData(SafeMigrationObservedState.Matching, false, true)]
    [InlineData(SafeMigrationObservedState.Matching, true, false)]
    [InlineData(SafeMigrationObservedState.Different, false, true)]
    [InlineData(SafeMigrationObservedState.Different, true, true)]
    public void NamespaceChangesExpireMissingTombstones(
        SafeMigrationObservedState? identity, bool changed, bool expected)
    {
        // Arrange
        var state = identity;

        // Act
        var reusable = MySqlSafeMigrationProviderAnalyzer.CanReuseGeneratedIndexNamespace(state, changed);

        // Assert
        Assert.Equal(expected, reusable);
    }

    /// <summary>A bounded ordinary nonunique key is the only candidate family.</summary>
    [Fact]
    public void OrdinaryNonuniqueKeyIsEligible()
    {
        // Arrange
        var definition = new ExpectedIndexDefinition("ix_generated", "generated_nodes",
            [new ExpectedIndexKeyDefinition("has_descendants")]);

        // Act
        var eligible = MySqlSafeMigrationProviderAnalyzer.IsGeneratedIndexRecoveryCandidate(definition);

        // Assert
        Assert.True(eligible);
    }

    /// <summary>Namespace evidence cannot authorize contracts requiring separate provider or row proofs.</summary>
    /// <param name="feature">The contract feature excluded from this recovery path.</param>
    [Theory]
    [InlineData("unique")]
    [InlineData("filter")]
    [InlineData("included")]
    [InlineData("method")]
    [InlineData("expression")]
    [InlineData("collation")]
    [InlineData("null_order")]
    [InlineData("operator")]
    public void IndependentContractFamiliesStayExcluded(string feature)
    {
        // Arrange
        var key = feature switch
        {
            "expression" => new ExpectedIndexKeyDefinition(expression: "has_descendants + 1"),
            "collation" => new ExpectedIndexKeyDefinition("has_descendants",
                collation: new SafeMigrationCollationIdentifier("utf8mb4_unicode_ci")),
            "null_order" => new ExpectedIndexKeyDefinition("has_descendants",
                nullOrder: SafeMigrationIndexNullOrder.First),
            "operator" => new ExpectedIndexKeyDefinition("has_descendants", operatorClass: "custom_operator"),
            _ => new ExpectedIndexKeyDefinition("has_descendants"),
        };

        var definition = new ExpectedIndexDefinition("ix_generated", "generated_nodes", [key],
            unique: feature == "unique", filter: feature == "filter" ? "has_descendants > 0" : null,
            includedColumns: feature == "included" ? ["payload"] : null,
            method: feature == "method" ? "HASH" : null);

        // Act
        var eligible = MySqlSafeMigrationProviderAnalyzer.IsGeneratedIndexRecoveryCandidate(definition);

        // Assert
        Assert.False(eligible);
    }

    /// <summary>Namespace absence cannot prove native name limits, reserved names or repeated key members.</summary>
    /// <param name="feature">The independently invalid native identity shape.</param>
    [Theory]
    [InlineData("name_length")]
    [InlineData("reserved")]
    [InlineData("duplicate")]
    public void InvalidNativeIdentityShapesRemainExcluded(string feature)
    {
        // Arrange
        var name = feature switch
        {
            "name_length" => new string('x', 65), "reserved" => "PRIMARY", _ => "ix_generated",
        };

        ExpectedIndexKeyDefinition[] keys = feature == "duplicate"
            ? [new("has_descendants"), new("HAS_DESCENDANTS")] : [new("has_descendants")];

        var definition = new ExpectedIndexDefinition(name, "generated_nodes", keys);

        // Act
        var eligible = MySqlSafeMigrationProviderAnalyzer.IsGeneratedIndexRecoveryCandidate(definition);

        // Assert
        Assert.False(eligible);
    }
}
