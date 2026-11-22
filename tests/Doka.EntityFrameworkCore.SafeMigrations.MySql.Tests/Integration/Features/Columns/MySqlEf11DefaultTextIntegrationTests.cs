namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Compares Doka EF11 text defaults with and without the public SafeMigrations integration.</summary>
public sealed partial class MySqlEf11DefaultTextIntegrationTests : MySqlIntegrationTestBase
{
    /// <summary>Shares the configured native server while retaining per-test database ownership.</summary>
    /// <param name="fixture">The existing MySQL or MariaDB engine fixture.</param>
    public MySqlEf11DefaultTextIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Options and equivalent fluent defaults retain the same ordinary relational model.</summary>
    /// <param name="safe">Whether SafeMigrations owns its public integration.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EquivalentOptionsAndFluentDefaultsHaveNoDifference(
        bool safe
    )
    {
        // Arrange
        using var services = Services(safe);
        using var source = TextContextFor(services, new TextShape(), safe);
        using var target = TextContextFor(services, new TextShape { FluentModelDefaults = true }, safe);
        var sourceModel = DesignModel(source).GetRelationalModel();
        var targetModel = DesignModel(target).GetRelationalModel();
        var differ = target.GetService<IMigrationsModelDiffer>();

        // Act
        var hasDifferences = differ.HasDifferences(sourceModel, targetModel);
        var operations = differ.GetDifferences(sourceModel, targetModel);

        // Assert
        Assert.False(hasDifferences);
        Assert.Empty(operations);
        Assert.Equal(UnicodeCollation, DesignModel(target).GetCollation());
    }

    /// <summary>
    /// Real explicit/inherited changes stay pending until a generated target snapshot replaces the source.
    /// </summary>
    /// <param name="safe">Whether SafeMigrations owns its public integration.</param>
    /// <param name="guidColumn">Whether the selected member is a Char36 Guid.</param>
    /// <param name="reverse">Whether the change fixes an inherited value explicitly.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ExplicitInheritedChangePreservesPendingAndGeneratedSnapshotContract(
        bool safe,
        bool guidColumn,
        bool reverse
    )
    {
        // Arrange
        using var services = Services(safe);
        var sourceShape = new TextShape { ExplicitColumnCollation = !reverse, GuidColumn = guidColumn };
        var targetShape = sourceShape with { ExplicitColumnCollation = reverse };
        using var source = TextContextFor(services, sourceShape, safe);
        using var target = TextContextFor(services, targetShape, safe);
        var sourceSnapshot = SnapshotAssembly(source, safe);
        var targetSnapshot = SnapshotAssembly(target, safe);
        using var sourceServices = Services(safe);
        using var pendingServices = Services(safe);
        using var updatedServices = Services(safe);
        using var unchanged = TextContextFor(sourceServices, sourceShape, safe, migrationsAssembly: sourceSnapshot);
        using var pending = TextContextFor(pendingServices, targetShape, safe, migrationsAssembly: sourceSnapshot);
        using var updated = TextContextFor(updatedServices, targetShape, safe, migrationsAssembly: targetSnapshot);
        var differ = target.GetService<IMigrationsModelDiffer>();
        var sourceModel = DesignModel(source).GetRelationalModel();
        var targetModel = DesignModel(target).GetRelationalModel();

        // Act
        var operations = differ.GetDifferences(sourceModel, targetModel);
        var hasDifferences = differ.HasDifferences(sourceModel, targetModel);
        var unchangedPending = unchanged.Database.HasPendingModelChanges();
        var changedPending = pending.Database.HasPendingModelChanges();
        var updatedPending = updated.Database.HasPendingModelChanges();
        var generated = Scaffold(source, target, safe);

        // Assert
        Assert.True(hasDifferences);
        var alter = Assert.Single(operations.OfType<AlterColumnOperation>());
        Assert.Equal(guidColumn ? "ExternalId" : "Value", alter.Name);
        Assert.Equal(TableName, alter.Table);
        Assert.Equal(reverse
            ? UnicodeCollation
            : null, alter.Collation);

        Assert.Equal(reverse
            ? null
            : UnicodeCollation, alter.OldColumn.Collation);

        Assert.Equal(safe
            ? 2
            : 1, operations.Count);
        Assert.False(unchangedPending);
        Assert.True(changedPending);
        Assert.False(updatedPending);
        Assert.Single(generated.Up);
        Assert.Single(generated.Down);

        if (safe)
        {
            Assert.IsType<SafeMigrationDesignTimeServicesRequiredOperation>(operations[0]);
            Assert.IsType<SafeMigrationOperation>(generated.Up[0]);
            Assert.Contains("AlterColumnIfDifferentFromModel", generated.Source, StringComparison.Ordinal);
        }
        else
        {
            Assert.IsType<AlterColumnOperation>(generated.Up[0]);
        }
    }

    /// <summary>Centralizing collation cannot erase an accompanying independent column facet change.</summary>
    /// <param name="safe">Whether SafeMigrations owns its public integration.</param>
    /// <param name="facet">The independent target facet changed with collation metadata.</param>
    [Theory]
    [InlineData(false, "length")]
    [InlineData(false, "nullable")]
    [InlineData(false, "default")]
    [InlineData(true, "length")]
    [InlineData(true, "nullable")]
    [InlineData(true, "default")]
    public void CentralizationRetainsIndependentFacetChanges(
        bool safe,
        string facet
    )
    {
        // Arrange
        using var services = Services(safe);
        var sourceShape = new TextShape { ExplicitColumnCollation = true };
        var targetShape = facet switch
        {
            "length" => sourceShape with { ExplicitColumnCollation = false, Length = 96 },
            "nullable" => sourceShape with { ExplicitColumnCollation = false, Nullable = true },
            "default" => sourceShape with { ExplicitColumnCollation = false, DefaultSql = "'changed'" },
            _ => throw new ArgumentOutOfRangeException(nameof(facet)),
        };

        using var source = TextContextFor(services, sourceShape, safe);
        using var target = TextContextFor(services, targetShape, safe);

        // Act
        var operations = target.GetService<IMigrationsModelDiffer>().GetDifferences(
            DesignModel(source).GetRelationalModel(),
            DesignModel(target).GetRelationalModel());

        var generated = Scaffold(source, target, safe);

        // Assert
        var alter = Assert.Single(operations.OfType<AlterColumnOperation>());
        Assert.Null(alter.Collation);
        Assert.Equal(UnicodeCollation, alter.OldColumn.Collation);
        Assert.Equal(targetShape.Length, alter.MaxLength);
        Assert.Equal(targetShape.Nullable, alter.IsNullable);
        Assert.Equal(targetShape.DefaultSql, alter.DefaultValueSql);
        Assert.Single(generated.Up);
        Assert.Equal(safe, generated.Up[0] is SafeMigrationOperation);
    }

    /// <summary>
    /// Matching physical definitions preserve values and type contracts through execution, replay and Down.
    /// </summary>
    /// <param name="safe">Whether SafeMigrations owns its public integration.</param>
    /// <param name="guidColumn">Whether the selected member is a Char36 Guid.</param>
    /// <param name="reverse">Whether the change fixes an inherited value explicitly.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task MatchingDefaultsExecuteAndReplayWithoutChangingStoredContracts(
        bool safe,
        bool guidColumn,
        bool reverse
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var services = Services(safe);
        var sourceShape = new TextShape { ExplicitColumnCollation = !reverse, GuidColumn = guidColumn };
        var targetShape = sourceShape with { ExplicitColumnCollation = reverse };
        await using var source = TextContextFor(services, sourceShape, safe, connectionString);
        await using var target = TextContextFor(services, targetShape, safe, connectionString);
        await source.Database.EnsureCreatedAsync(CancellationToken.None);
        await SeedAsync(connectionString, nullValue: false, CancellationToken.None);
        var initialFacts = await FactsAsync(connectionString, CancellationToken.None);
        var generated = Scaffold(source, target, safe);

        // Act
        var preflight = safe
            ? await AnalyzeAsync(target, generated.Up, CancellationToken.None)
            : null;

        preflight?.ThrowIfBlocked();
        await ExecuteOperationsAsync(target, generated.Up, CancellationToken.None);
        var replay = safe
            ? await AnalyzeAsync(target, generated.Up, CancellationToken.None)
            : null;

        replay?.ThrowIfBlocked();
        await ExecuteOperationsAsync(target, generated.Up, CancellationToken.None);
        var upFacts = await FactsAsync(connectionString, CancellationToken.None);
        await ExecuteOperationsAsync(source, generated.Down, CancellationToken.None);
        var finalFacts = await FactsAsync(connectionString, CancellationToken.None);

        // Assert
        Assert.Equal(initialFacts, upFacts);
        Assert.Equal(initialFacts, finalFacts);
        Assert.Contains("Ascii:ascii:ascii_general_ci", finalFacts, StringComparison.Ordinal);
        Assert.Contains("BinaryId:::binary(16)", finalFacts, StringComparison.Ordinal);
        Assert.Contains("value=preserved", finalFacts, StringComparison.Ordinal);

        if (safe)
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, preflight!.Status);
            Assert.Equal(SafeMigrationReportStatus.Ready, replay!.Status);
            Assert.All(replay.Assessments,
                static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        }

        if (Fixture.IsMariaDb)
        {
            Assert.Contains("Payload:utf8mb4:utf8mb4_bin:longtext", finalFacts, StringComparison.Ordinal);
            Assert.Contains("NativeId:::uuid", finalFacts, StringComparison.Ordinal);
        }
        else
        {
            // WHY: Native JSON retains its own storage contract; catalog text-facet presentation is engine-owned.
            Assert.Contains(finalFacts.Split(Environment.NewLine), static fact =>
                fact.StartsWith("Payload:", StringComparison.Ordinal)
                && fact.EndsWith(":json:NO", StringComparison.Ordinal));
        }
    }

    /// <summary>The actual table default, not the equal global option, determines an inherited column target.</summary>
    /// <param name="safe">Whether SafeMigrations must reject an unsafe physical collation change.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentTableDefaultRemainsARealPhysicalChange(
        bool safe
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var services = Services(safe);
        var sourceShape = new TextShape { TableCollation = BinaryCollation, ExplicitColumnCollation = true };
        var targetShape = sourceShape with { ExplicitColumnCollation = false };
        await using var source = TextContextFor(services, sourceShape, safe, connectionString);
        await using var target = TextContextFor(services, targetShape, safe, connectionString);
        await source.Database.EnsureCreatedAsync(CancellationToken.None);
        await SeedAsync(connectionString, nullValue: false, CancellationToken.None);
        var generated = Scaffold(source, target, safe);

        // Act
        var report = safe
            ? await AnalyzeAsync(target, generated.Up, CancellationToken.None)
            : null;

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(target, generated.Up, CancellationToken.None));

        var collation = await ReadValueCollationAsync(connectionString, CancellationToken.None);
        var retained = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{TableName}`;", CancellationToken.None);

        // Assert
        Assert.Equal(1, retained);

        if (safe)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report!.Status);
            Assert.Contains(report.Assessments,
                static assessment => assessment.Action == SafeMigrationAction.RejectDifferent);
            Assert.IsType<SafeMigrationExecutionException>(failure);
            Assert.Equal(UnicodeCollation, collation);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(BinaryCollation, collation);
        }
    }

    /// <summary>An options-level default cannot conceal deliberate live column collation drift.</summary>
    [Fact]
    public async Task LiveCollationDriftIsRejectedWithoutChangingRows()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var services = Services(safe: true);
        await using var context = TextContextFor(services, new TextShape(), safe: true, connectionString);
        await context.Database.EnsureCreatedAsync(CancellationToken.None);
        await SeedAsync(connectionString, nullValue: false, CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            $"ALTER TABLE `{TableName}` MODIFY COLUMN `Value` varchar(64) COLLATE utf8mb4_bin NOT NULL;",
            CancellationToken.None);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(
            static operation => operation.AddColumn<string>("Value", TableName, "varchar(64)", maxLength: 64),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await AnalyzeAsync(context, builder.Operations, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var retained = await FactsAsync(connectionString, CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Contains(report.Assessments,
            static assessment => assessment.Action == SafeMigrationAction.RejectDifferent);
        Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Contains("Value:utf8mb4:utf8mb4_bin:varchar(64)", retained, StringComparison.Ordinal);
        Assert.Contains("value=preserved", retained, StringComparison.Ordinal);
    }

    /// <summary>Real unsafe NULL data remains blocked when collation metadata changes at the same time.</summary>
    [Fact]
    public async Task CentralizationCannotHideUnsafeNullabilityTightening()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var services = Services(safe: true);
        var sourceShape = new TextShape { ExplicitColumnCollation = true, Nullable = true };
        var targetShape = sourceShape with { ExplicitColumnCollation = false, Nullable = false };
        await using var source = TextContextFor(services, sourceShape, safe: true, connectionString);
        await using var target = TextContextFor(services, targetShape, safe: true, connectionString);
        await source.Database.EnsureCreatedAsync(CancellationToken.None);
        await SeedAsync(connectionString, nullValue: true, CancellationToken.None);
        var generated = Scaffold(source, target, safe: true);

        // Act
        var report = await AnalyzeAsync(target, generated.Up, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(target, generated.Up, CancellationToken.None));

        var retainedNulls = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{TableName}` WHERE `Value` IS NULL;", CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        // WHY: This combined metadata transition is rejected before a row proof is selected.
        // Do not claim NULL detection when the actual policy decision is an unapproved alter.
        Assert.Contains(report.Assessments,
            static assessment => assessment is
            {
                Action: SafeMigrationAction.RejectDifferent,
                DecisionCode: "alter_not_approved",
            });
        Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal(1, retainedNulls);
    }

    /// <summary>The independent required-column data proof still detects NULL under option-level defaults.</summary>
    [Fact]
    public async Task RequiredColumnDataGuardRemainsActiveWithTextDefaults()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var services = Services(safe: true);
        await using var source = TextContextFor(
            services, new TextShape { Nullable = true }, safe: true, connectionString);

        await source.Database.EnsureCreatedAsync(CancellationToken.None);
        await SeedAsync(connectionString, nullValue: true, CancellationToken.None);
        await using var target = TextContextFor(services, new TextShape(), safe: true, connectionString);
        var builder = new MigrationBuilder(target.Database.ProviderName!);
        builder.AddColumnIfNotExistsFromModel(
            static operation => operation.AddColumn<string>("Value", TableName, "varchar(64)", maxLength: 64),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await AnalyzeAsync(target, builder.Operations, CancellationToken.None);
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(target, builder.Operations, CancellationToken.None));

        var retainedNulls = await ScalarIntAsync(connectionString,
            $"SELECT COUNT(*) FROM `{TableName}` WHERE `Value` IS NULL;", CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Contains(report.Assessments,
            static assessment => assessment.Action == SafeMigrationAction.RejectDataBlocked);
        Assert.IsType<SafeMigrationExecutionException>(failure);
        Assert.Equal(1, retainedNulls);
    }
}
