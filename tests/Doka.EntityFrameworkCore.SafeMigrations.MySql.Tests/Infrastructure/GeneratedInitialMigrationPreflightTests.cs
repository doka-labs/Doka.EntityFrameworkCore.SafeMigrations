namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Reports generated lifecycle, discriminator filtering and discriminator writes independently.</summary>
public sealed class GeneratedInitialMigrationPreflightTests : IClassFixture<GeneratedInitialMigrationFixture>
{
    private readonly GeneratedInitialMigrationFixture _fixture;

    /// <summary>Uses the once-only schema owner shared by the independent tooling contracts.</summary>
    /// <param name="fixture">Captures each profile's lifecycle without depending on test-method order.</param>
    public GeneratedInitialMigrationPreflightTests(GeneratedInitialMigrationFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Preserves the strict initial application, idempotent replay and verification scenario.</summary>
    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING")]
    public Task StrictGeneratedInitialMigrationConvergesThroughPreflightAndReplay() =>
        VerifyLifecycleAsync(legacy: false);

    /// <summary>Preserves the legacy initial application, idempotent replay and verification scenario.</summary>
    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING")]
    public Task LegacyGeneratedInitialMigrationConvergesThroughPreflightAndReplay() =>
        VerifyLifecycleAsync(legacy: true);

    /// <summary>Excludes unknown numeric discriminator rows in the strict profile.</summary>
    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING")]
    public Task StrictGeneratedNumericDiscriminatorFiltersUnknownRows() =>
        VerifyNumericFilteringAsync(legacy: false);

    /// <summary>Excludes unknown numeric discriminator rows in the legacy profile.</summary>
    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING")]
    public Task LegacyGeneratedNumericDiscriminatorFiltersUnknownRows() =>
        VerifyNumericFilteringAsync(legacy: true);

    /// <summary>
    /// Resolves attributed string discriminator values and excludes unknown rows in the strict profile.
    /// </summary>
    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING")]
    public Task StrictGeneratedAttributedDiscriminatorFiltersUnknownRows() =>
        VerifyAttributedFilteringAsync(legacy: false);

    /// <summary>
    /// Resolves attributed string discriminator values and excludes unknown rows in the legacy profile.
    /// </summary>
    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING")]
    public Task LegacyGeneratedAttributedDiscriminatorFiltersUnknownRows() =>
        VerifyAttributedFilteringAsync(legacy: true);

    /// <summary>Persists converted attributed discriminator values in the strict profile.</summary>
    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING")]
    public Task StrictGeneratedAttributedDiscriminatorWritesConvertedValues() =>
        VerifyAttributedWritesAsync(legacy: false);

    /// <summary>Persists converted attributed discriminator values in the legacy profile.</summary>
    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING")]
    public Task LegacyGeneratedAttributedDiscriminatorWritesConvertedValues() =>
        VerifyAttributedWritesAsync(legacy: true);

    /// <summary>Asserts the captured lifecycle without repeating its destructive initial application.</summary>
    private async Task VerifyLifecycleAsync(bool legacy)
    {
        // Arrange.
        var fixture = _fixture;

        // Act.
        var observation = await fixture.GetInitializedAsync(legacy);

        // Assert.
        var dataAssessment = Assert.Single(
            observation.Preflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureModelManagedData);

        GeneratedInitialMigrationFixture.AssertReady(observation.Preflight);
        Assert.Equal(SafeMigrationObservedState.Missing, dataAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, dataAssessment.Action);
        Assert.Equal("projected_missing", dataAssessment.Code);
        GeneratedInitialMigrationFixture.AssertReady(observation.Replay);
        Assert.All(
            observation.Replay.Assessments,
            static assessment =>
            {
                Assert.True(
                    assessment is
                    {
                        ObservedState: SafeMigrationObservedState.Matching,
                        Action: SafeMigrationAction.NoOp,
                    },
                    $"Replay operation {assessment.Ordinal} ({assessment.OperationKind} "
                    + $"{assessment.ObjectName}) was {assessment.ObservedState}/{assessment.Action}; "
                    + $"analysis={assessment.AnalysisCode}; decision={assessment.DecisionCode}.");
            });

        Assert.Equal(SafeMigrationReportStatus.Ready, observation.Postflight.Status);
        Assert.All(observation.Postflight.Assessments, assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(1, observation.SeedCount);
    }

    /// <summary>Exercises numeric query filtering with transaction-owned rows independent of other contracts.</summary>
    private async Task VerifyNumericFilteringAsync(bool legacy)
    {
        // Arrange.
        var observation = await _fixture.GetInitializedAsync(legacy);
        await using var context = observation.CreateContext();
        // WHY: Discriminator probes share an expensive generated schema, never committed mutable rows.
        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        // Act.
        _ = await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `scaffolding_work_items` (`Id`, `Caption`, `Discriminator`) "
            + "VALUES (1, 'Known', 0), (2, 'External', 1), (3, 'Future', 2);",
            CancellationToken.None);

        var baseCount = await context.Set<SafeMigrationScaffoldingWorkItem>()
            .CountAsync(CancellationToken.None);
        var taskCount = await context.Set<SafeMigrationScaffoldingTask>().CountAsync(CancellationToken.None);
        var externalCount = await context.Set<SafeMigrationScaffoldingExternalWorkItem>()
            .CountAsync(CancellationToken.None);

        // Assert.
        Assert.Equal(2, baseCount);
        Assert.Equal(1, taskCount);
        Assert.Equal(1, externalCount);
    }

    /// <summary>Exercises attributed read filtering without relying on the separate writer's row population.</summary>
    private async Task VerifyAttributedFilteringAsync(bool legacy)
    {
        // Arrange.
        var observation = await _fixture.GetInitializedAsync(legacy);
        await using var context = observation.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        // Act.
        _ = await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO `scaffolding_attributed_work_items` "
            + "(`Id`, `Caption`, `AttributedDiscriminator`) VALUES "
            + "(11, 'Json', 'json-named'), "
            + "(12, 'Contract', 'contract-named'), "
            + "(13, 'Fallback', 'Fallback'), "
            + "(14, 'Future', 'future-value');",
            CancellationToken.None);

        var baseCount = await context.Set<SafeMigrationAttributedWorkItem>().CountAsync(CancellationToken.None);
        var jsonCount = await context.Set<SafeMigrationJsonNamedWorkItem>().CountAsync(CancellationToken.None);
        var contractCount = await context.Set<SafeMigrationContractNamedWorkItem>().CountAsync(CancellationToken.None);
        var fallbackCount = await context.Set<SafeMigrationFallbackNamedWorkItem>().CountAsync(CancellationToken.None);

        // Assert.
        Assert.Equal(3, baseCount);
        Assert.Equal(1, jsonCount);
        Assert.Equal(1, contractCount);
        Assert.Equal(1, fallbackCount);
    }

    /// <summary>
    /// Exercises converted writes independently of filtering's unknown-row and preexisting-row inputs.
    /// </summary>
    private async Task VerifyAttributedWritesAsync(bool legacy)
    {
        // Arrange.
        var observation = await _fixture.GetInitializedAsync(legacy);
        await using var context = observation.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);

        // Act.
        context.AddRange(
            new SafeMigrationJsonNamedWorkItem
            {
                Id = 15,
                Caption = "Converted JSON name",
            },
            new SafeMigrationContractNamedWorkItem
            {
                Id = 16,
                Caption = "Converted contract name",
            },
            new SafeMigrationFallbackNamedWorkItem
            {
                Id = 17,
                Caption = "Converted fallback name",
            });
        _ = await context.SaveChangesAsync(CancellationToken.None);
        var baseCount = await context.Set<SafeMigrationAttributedWorkItem>().CountAsync(CancellationToken.None);
        var jsonCount = await context.Set<SafeMigrationJsonNamedWorkItem>().CountAsync(CancellationToken.None);
        var contractCount = await context.Set<SafeMigrationContractNamedWorkItem>().CountAsync(CancellationToken.None);
        var fallbackCount = await context.Set<SafeMigrationFallbackNamedWorkItem>().CountAsync(CancellationToken.None);
        var convertedCount = await ScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `scaffolding_attributed_work_items` "
            + "WHERE (`Id` = 15 AND `AttributedDiscriminator` = 'json-named') "
            + "OR (`Id` = 16 AND `AttributedDiscriminator` = 'contract-named') "
            + "OR (`Id` = 17 AND `AttributedDiscriminator` = 'Fallback');");

        // Assert.
        Assert.Equal(3, baseCount);
        Assert.Equal(1, jsonCount);
        Assert.Equal(1, contractCount);
        Assert.Equal(1, fallbackCount);
        Assert.Equal(3, convertedCount);
    }

    /// <summary>Reads exact stored discriminator values in the current test's transaction.</summary>
    private static async Task<int> ScalarIntAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class GeneratedToolingFactAttribute : FactAttribute
{
    public GeneratedToolingFactAttribute(
        string connectionVariable
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionVariable);

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(connectionVariable)))
        {
            Skip = "The generated-migration preflight is executed by eng/verify-ef-tooling.sh.";
        }
    }
}
