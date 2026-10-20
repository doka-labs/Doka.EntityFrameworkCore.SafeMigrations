namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Requires complete mapped facets before a model can supply an untrusted dependency candidate.</summary>
public sealed class MySqlGeneratedColumnModelCandidatesTests
{
    /// <summary>Uninitialized historical targets retain complete facets after one cached reference capture.</summary>
    [Fact]
    public void RawHistoricalTargetIsInitializedWithoutSubstitutingCurrentModel()
    {
        // Arrange
        using var context = new GeneratedFacetPendingDropDbContext(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MariaDb(new Version(11, 8, 8)));

        var migration = new GeneratedFacetPendingDropMigration();
        var first = new SafeMigrationOperation(new DropColumnIntent("unrelated", "generated_facet_pending_drop"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var second = new SafeMigrationOperation(new DropColumnIntent("another", "generated_facet_pending_drop"),
            SafeMigrationPolicy.ThrowIfDifferent);

        MigrationOperation[] operations = [first, second];
        IModel?[] targets = [migration.TargetModel, migration.TargetModel];

        // Act
        var candidates = MySqlGeneratedColumnModelCandidates.CaptureTargets(operations, targets, context.Model,
            context.GetService<IModelRuntimeInitializer>());

        // Assert
        Assert.Same(candidates[first], candidates[second]);
        var column = Assert.Single(Assert.Single(candidates[first]).Columns);
        Assert.Equal("candidate-marker", column.Comment);
        Assert.Equal("utf8mb4_bin", column.Collation?.Name);
    }

    /// <summary>Historical facet fixtures match canonical metadata before any native container is needed.</summary>
    /// <param name="isMariaDb">Whether the provider conventions use the MariaDB engine contract.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingFacetFixtureMatchesCanonicalModelWithoutDatabase(bool isMariaDb)
    {
        // Arrange
        using var context = new GeneratedFacetPendingDropDbContext(
            "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            isMariaDb ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
                : MySqlServerVersion.MySql(new Version(8, 4, 11)));

        const string provider = "model-candidate-fixture";

        // Act
        var fingerprint = SafeMigrationRunner.ValidateCanonicalMigrationModelAndCreateFingerprint(context, provider);

        // Assert
        Assert.Equal(SafeMigrationModelFingerprint.Create(context.GetService<IDesignTimeModel>().Model, provider),
            fingerprint);
    }

    /// <summary>Operation-specific historical models cannot borrow a different current model's facets.</summary>
    [Fact]
    public void HistoricalTargetsRemainBoundToTheirExactOperation()
    {
        // Arrange
        using var original = new FacetedCandidateContext();
        using var changed = new ChangedFacetedCandidateContext();
        var first = Drop();
        var second = Drop();
        MigrationOperation[] operations = [first, second];
        IModel?[] targets =
        [
            original.GetService<IDesignTimeModel>().Model, changed.GetService<IDesignTimeModel>().Model,
        ];

        // Act
        var candidates = MySqlGeneratedColumnModelCandidates.CaptureTargets(operations, targets, changed.Model);

        // Assert
        Assert.Equal("candidate-marker", Assert.Single(Assert.Single(candidates[first]).Columns).Comment);
        Assert.Equal("changed-marker", Assert.Single(Assert.Single(candidates[second]).Columns).Comment);
        Assert.NotSame(candidates[first], candidates[second]);
    }

    /// <summary>Many operations sharing one target reuse one immutable whole-owner snapshot.</summary>
    [Fact]
    public void RepeatedHistoricalModelReusesItsSnapshot()
    {
        // Arrange
        using var context = new FacetedCandidateContext();
        var first = Drop();
        var second = Drop();
        MigrationOperation[] operations = [first, second];
        var model = context.GetService<IDesignTimeModel>().Model;
        IModel?[] targets = [model, model];

        // Act
        var candidates = MySqlGeneratedColumnModelCandidates.CaptureTargets(operations, targets, null);

        // Assert
        Assert.Same(candidates[first], candidates[second]);
        Assert.Equal("candidate-marker", Assert.Single(Assert.Single(candidates[first]).Columns).Comment);
    }

    /// <summary>An explicitly missing historical target is not replaced by the current complete model.</summary>
    [Fact]
    public void MissingHistoricalTargetDoesNotBorrowDefaultModel()
    {
        // Arrange
        using var context = new FacetedCandidateContext();
        MigrationOperation[] operations = [Drop()];
        IModel?[] targets = [null];

        // Act
        var candidates = MySqlGeneratedColumnModelCandidates.CaptureTargets(operations, targets,
            context.GetService<IDesignTimeModel>().Model);

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>Misaligned historical models cannot be associated with a neighboring operation.</summary>
    [Fact]
    public void HistoricalTargetCardinalityMismatchIsRejected()
    {
        // Arrange
        MigrationOperation[] operations = [Drop()];
        IModel?[] targets = [];

        // Act
        var failure = Record.Exception(() =>
            MySqlGeneratedColumnModelCandidates.CaptureTargets(operations, targets, null));

        // Assert
        Assert.IsType<ArgumentException>(failure);
    }

    /// <summary>Available mapped-property facets survive capture even though column annotations omit them.</summary>
    [Fact]
    public void DesignTimeModelPreservesMappedCommentAndCollation()
    {
        // Arrange
        using var context = new FacetedCandidateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var nativeTable = Assert.IsAssignableFrom<ITable>(
            model.GetRelationalModel().FindTable("model_candidate_facets", null));

        var nativeColumn = Assert.IsAssignableFrom<IColumn>(nativeTable.FindColumn("result"));

        MigrationOperation[] operations =
        [
            new SafeMigrationOperation(new DropColumnIntent("unrelated", "model_candidate_facets"),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

        // Act
        var candidates = MySqlGeneratedColumnModelCandidates.Capture(model, operations);

        // Assert
        Assert.Null(nativeColumn.FindAnnotation(RelationalAnnotationNames.Comment));
        Assert.Null(nativeColumn.FindAnnotation(RelationalAnnotationNames.Collation));
        Assert.Equal("candidate-marker", nativeColumn.Comment);
        Assert.Equal("utf8mb4_bin", nativeColumn.Collation);
        var column = Assert.Single(Assert.Single(candidates).Columns);
        Assert.Equal("candidate-marker", column.Comment);
        Assert.Equal("utf8mb4_bin", column.Collation?.Name);
    }

    /// <summary>Missing runtime metadata is unknown, never an inferred default comment or collation.</summary>
    [Fact]
    public void RuntimeModelDoesNotInventNullFacets()
    {
        // Arrange
        using var context = new FacetedCandidateContext();
        MigrationOperation[] operations =
        [
            new SafeMigrationOperation(new DropColumnIntent("unrelated", "model_candidate_facets"),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

        // Act
        var candidates = MySqlGeneratedColumnModelCandidates.Capture(context.Model, operations);

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>Exposes complete design-time metadata and its intentionally reduced EF runtime counterpart.</summary>
    private class FacetedCandidateContext : DbContext
    {
        /// <summary>Gets a context-type-bound facet for independent historical-model snapshots.</summary>
        protected virtual string CandidateComment => "candidate-marker";

        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
                MySqlServerVersion.MySql(new Version(8, 4, 11)));

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity("candidate", entity =>
            {
                entity.ToTable("model_candidate_facets");
                entity.Property<int>("id").ValueGeneratedNever();
                entity.HasKey("id");
                entity.Property<int?>("unrelated");
                entity.Property<string>("result").HasColumnType("varchar(20)")
                    .HasComputedColumnSql("'fixed'", stored: false).HasComment(CandidateComment)
                    .UseCollation("utf8mb4_bin");
            });
        }
    }

    /// <summary>Separates the changed target through EF's ordinary context-type model identity.</summary>
    private sealed class ChangedFacetedCandidateContext : FacetedCandidateContext
    {
        /// <inheritdoc />
        protected override string CandidateComment => "changed-marker";
    }

    /// <summary>Creates a distinct immutable operation reference with the same native owner identity.</summary>
    private static SafeMigrationOperation Drop() =>
        new(new DropColumnIntent("unrelated", "model_candidate_facets"), SafeMigrationPolicy.ThrowIfDifferent);
}
