namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies exact provenance and physical boundaries for provider-qualified seed proofs.</summary>
public sealed class SqlServerProjectedSeedProofTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=seed_proofs;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>A proof evaluates only its immutable prefix and never borrows future row references.</summary>
    [Fact]
    public void SeedProof_PrefixDoesNotGrantAuthorityToFutureRows()
    {
        // Arrange
        var table = Table();
        var first = Seed();
        var future = Seed();
        var proof = new SqlServerProjectedSeedKeyProof(table, [first, future], 1, SeedCount: 1);
        var accepted = new SqlServerProjectedSeedLineage(table);
        accepted.Seeds.Add(first);
        var source = new Source(table, newlyCreated: true);

        // Act
        var exactPrefix = proof.Matches(accepted, source);
        var futurePrefix = proof.Matches(accepted, source, future);

        // Assert
        Assert.True(exactPrefix);
        Assert.False(futurePrefix);
    }

    /// <summary>
    /// A prior raw/provider operation prevents a constant-only prospective seed proof from being reused.
    /// </summary>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.Missing)]
    [InlineData(true, SafeMigrationObservedState.PrerequisiteMissing)]
    public async Task ProspectiveSeedProof_RequiresAcceptedUnmutatedLineage(
        bool mutation,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var table = Table();
        var tableOperation = new SafeMigrationOperation(
            new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var seedOperation = new SafeMigrationOperation(Seed(), SafeMigrationPolicy.ThrowIfDifferent);
        await analyzer.CaptureProjectedSeedProofsAsync([tableOperation, seedOperation],
            static (_, _) => Task.FromResult(1), CancellationToken.None);
        var missing = Analysis(SafeMigrationObservedState.Missing, "missing");
        analyzer.ObserveAcceptedOperation(tableOperation, missing, missing,
            new SafeMigrationDecision(SafeMigrationAction.Apply, "apply"));
        if (mutation)
        {
            analyzer.ObserveProviderOperation(new SqlOperation { Sql = "INSERT seeds(Id) VALUES (2);" });
        }

        var source = new Source(table, newlyCreated: true);

        // Act
        var analysis = analyzer.ValidateProjectedOperation(seedOperation, missing, source);

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>An accepted managed mutation discards an earlier captured insert-key proof.</summary>
    [Theory]
    [InlineData(false, false, SafeMigrationObservedState.Missing, "projected_seed_key_data_safe")]
    [InlineData(true, false, SafeMigrationObservedState.PrerequisiteMissing, "projected_key_data_state_unknown")]
    [InlineData(true, true, SafeMigrationObservedState.PrerequisiteMissing, "projected_key_data_state_unknown")]
    public async Task AcceptedManagedMutation_DiscardsCapturedInsertKeyProof(
        bool mutate,
        bool delete,
        SafeMigrationObservedState expected,
        string expectedCode
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var table = Table();
        var tableOperation = new SafeMigrationOperation(
            new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var seedOperation = new SafeMigrationOperation(Seed(), SafeMigrationPolicy.ThrowIfDifferent);
        var indexIntent = new EnsureIndexIntent(new ExpectedIndexDefinition("IX_seeds_Code", "seeds",
            [new ExpectedIndexKeyDefinition("Code")], unique: true));

        var indexOperation = new SafeMigrationOperation(indexIntent, SafeMigrationPolicy.ThrowIfDifferent);
        var mutation = new MigrationBuilder(context.Database.ProviderName!);
        if (delete)
        {
            mutation.DeleteModelManagedDataFromModel("seeds", ["Id"], ["int"], new object?[,] { { 2 } },
                ["Code"], ["nvarchar(20)"], new object?[,] { { "second" } });
        }
        else
        {
            mutation.UpdateModelManagedDataFromModel("seeds", ["Id"], ["int"], new object?[,] { { 2 } },
                ["Code"], ["nvarchar(20)"], new object?[,] { { "second" } }, new object?[,] { { "changed" } });
        }

        var mutationOperation = (SafeMigrationOperation)mutation.Operations.Single();
        var missing = Analysis(SafeMigrationObservedState.Missing, "missing");
        var transition = Analysis(SafeMigrationObservedState.TransitionReady, "transition_ready");
        var apply = new SafeMigrationDecision(SafeMigrationAction.Apply, "apply");
        var source = new Source(table, newlyCreated: true);
        var keyAnalyzer = (ISafeMigrationProjectedKeyAnalyzer)analyzer;

        // Act
        var contractFailure = Record.Exception(() => SafeMigrationModelManagedDataContractValidator.Validate(
            [tableOperation, seedOperation, mutationOperation, indexOperation]));

        await analyzer.CaptureProjectedSeedProofsAsync([tableOperation, seedOperation, indexOperation],
            static (_, _) => Task.FromResult(1), CancellationToken.None);
        analyzer.ObserveAcceptedOperation(tableOperation, missing, missing, apply);
        analyzer.ObserveAcceptedOperation(seedOperation, missing, missing, apply);
        var before = keyAnalyzer.ValidateProjectedIndex(indexIntent, source, missing, missing);
        if (mutate)
        {
            // WHY: The observer consumes an accepted decision independently of
            // the runner. Distinct keys keep this provider probe Core-linear.
            analyzer.ObserveAcceptedOperation(mutationOperation, transition, transition, apply);
        }

        var after = keyAnalyzer.ValidateProjectedIndex(indexIntent, source, missing, missing);

        // Assert
        Assert.Null(contractFailure);
        Assert.Equal(SafeMigrationObservedState.Missing, before.ObservedState);
        Assert.Equal("projected_seed_key_data_safe", before.Code);
        Assert.Equal(expected, after.ObservedState);
        Assert.Equal(expectedCode, after.Code);
    }

    /// <summary>Only the exact immutable accepted insert order can consume the captured provider result.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SeedProof_RequiresTheExactAcceptedReferences(
        bool replacement,
        bool expected
    )
    {
        // Arrange
        var table = Table();
        var seed = Seed();
        var proof = new SqlServerProjectedSeedKeyProof(table, [seed], 1);
        var accepted = new SqlServerProjectedSeedLineage(table);
        accepted.Seeds.Add(replacement ? Seed() : seed);
        var source = new Source(table, newlyCreated: true);

        // Act
        var matches = proof.Matches(accepted, source);

        // Assert
        Assert.Equal(expected, matches);
    }

    /// <summary>A recreated or mutated destination does not borrow a proof captured for an older definition.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SeedProof_RequiresTheExactDestinationDefinition(
        bool recreated,
        bool expected
    )
    {
        // Arrange
        var table = Table();
        var seed = Seed();
        var proof = new SqlServerProjectedSeedKeyProof(table, [seed], 1);
        var accepted = new SqlServerProjectedSeedLineage(recreated ? Table() : table);
        accepted.Seeds.Add(seed);
        var source = new Source(accepted.Table, newlyCreated: true);

        // Act
        var matches = proof.Matches(accepted, source);

        // Assert
        Assert.Equal(expected, matches);
    }

    /// <summary>Row proof does not bypass new-table ownership or physical key-width validation.</summary>
    [Theory]
    [InlineData(true, 20, SafeMigrationObservedState.Missing)]
    [InlineData(false, 20, SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(true, 851, SafeMigrationObservedState.Unsupported)]
    public void SeedProof_RequiresNewTableAndValidPhysicalWidth(
        bool newlyCreated,
        int length,
        SafeMigrationObservedState expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new Source(Table(length), newlyCreated);
        var intent = new EnsureIndexIntent(new ExpectedIndexDefinition("IX_seeds_Code", "seeds",
            [new ExpectedIndexKeyDefinition("Code")], unique: true));

        var missing = Analysis(SafeMigrationObservedState.Missing, "missing");
        var proven = Analysis(SafeMigrationObservedState.Missing, "projected_seed_key_data_safe");

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(intent, source, missing, missing,
            snapshot: null, context.GetService<IRelationalTypeMappingSource>(), proven);

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>An unsupported physical source table cannot borrow a new-table or authored-row proof.</summary>
    [Fact]
    public void SeedProof_UnsupportedPhysicalTableFailsClosed()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new Source(Table(), newlyCreated: true);
        var intent = new EnsureIndexIntent(new ExpectedIndexDefinition("IX_seeds_Code", "seeds",
            [new ExpectedIndexKeyDefinition("Code")], unique: true));

        var snapshot = new SqlServerProjectedKeyTable(true, -1, null,
            new Dictionary<string, SqlServerProjectedKeyColumn>(), PhysicalSupported: false);

        var missing = Analysis(SafeMigrationObservedState.Missing, "missing");
        var proven = Analysis(SafeMigrationObservedState.Missing, "projected_seed_key_data_safe");

        // Act
        var analysis = SqlServerProjectedKeyShape.Validate(intent, source, missing, missing,
            snapshot, context.GetService<IRelationalTypeMappingSource>(), proven);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("physical_table_unproven", analysis.Code);
    }

    private static ExpectedTableDefinition Table(int length = 20)
        => new("seeds", [new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Code", typeof(string), true, $"nvarchar({length})", maxLength: length)]);

    private static EnsureModelManagedDataIntent Seed()
        => new("seeds", ["Id"], ["int"], ["Id", "Code"], ["int", "nvarchar(20)"],
            new object?[,] { { 1, "one" } }, schema: null,
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);

    private static SafeMigrationProviderAnalysis Analysis(
        SafeMigrationObservedState state,
        string code
    )
        => new(state, SafeMigrationRepairCapability.None, false, code);

    private sealed class Source(
        ExpectedTableDefinition table,
        bool newlyCreated
    )
        : ISafeMigrationProjectedColumnSource, ISafeMigrationProjectedTableSource
    {
        public bool TryGetProjectedColumn(
            string name,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        )
        {
            definition = table.Columns.FirstOrDefault(value => StringComparer.Ordinal.Equals(value.Name, column));

            return definition is not null;
        }

        public bool TryGetProjectedTableState(
            string name,
            string? schema,
            out SafeMigrationProjectedTableState state
        )
        {
            state = new SafeMigrationProjectedTableState(newlyCreated, true, false, false);

            return true;
        }
    }
}
