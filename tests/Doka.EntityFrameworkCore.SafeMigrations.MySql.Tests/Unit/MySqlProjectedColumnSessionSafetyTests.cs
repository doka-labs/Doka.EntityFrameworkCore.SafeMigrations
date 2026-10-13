namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies captured session prerequisites for ordered projected column repairs.</summary>
public sealed class MySqlProjectedColumnSessionSafetyTests
{
    /// <summary>Empty projection does not authorize permissive NULL conversion for any string transition.</summary>
    /// <param name="targetType">The same, wider or narrower target layout.</param>
    /// <param name="explicitDefault">Whether an authored non-null backfill is present.</param>
    /// <param name="strict">Whether strict SQL mode was captured.</param>
    [Theory]
    [InlineData("varchar(20)", false, false)]
    [InlineData("varchar(20)", false, true)]
    [InlineData("varchar(20)", true, false)]
    [InlineData("varchar(20)", true, true)]
    [InlineData("varchar(30)", false, false)]
    [InlineData("varchar(30)", false, true)]
    [InlineData("varchar(30)", true, false)]
    [InlineData("varchar(30)", true, true)]
    [InlineData("varchar(10)", false, false)]
    [InlineData("varchar(10)", false, true)]
    [InlineData("varchar(10)", true, false)]
    [InlineData("varchar(10)", true, true)]
    public void EmptyNullabilityTransitionsRequireCapturedStrictMode(
        string targetType,
        bool explicitDefault,
        bool strict
    )
    {
        // Arrange
        using var context = CreateContext();
        var source = Character("varchar(20)", nullable: true);
        var target = Character(targetType, nullable: false, explicitDefault);
        var environment = Environment(strict);

        // Act
        var safe = MySqlProjectedColumnTransition.IsSafe(new AlterColumnIntent("items", target, source), source,
            new TableView(source), environment, context.GetService<IRelationalTypeMappingSource>(),
            canReuseCreationCharacterSet: true);

        // Assert
        Assert.Equal(strict, safe);
    }

    /// <summary>The ordinary same-layout shortcut must retain captured strict-mode prerequisites.</summary>
    /// <param name="complete">Whether Core knows the complete projected table.</param>
    /// <param name="explicitDefault">Whether a non-null default was authored.</param>
    /// <param name="strict">Whether strict SQL mode was captured.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void SameLayoutProjectedTighteningRetainsStrictMode(
        bool complete,
        bool explicitDefault,
        bool strict
    )
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = (ISafeMigrationProjectedColumnAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = Character("varchar(20)", nullable: true);
        var target = Character("varchar(20)", nullable: false, explicitDefault);
        var intent = new AlterColumnIntent("items", target, source);
        var live = Proof(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, strict);
        var projected = Proof(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, strict);
        var state = ProjectionContext(source, complete, canReuse: !complete);

        // Act
        var result = analyzer.ValidateProjectedAlterColumn(intent, source, state, live, projected);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal(strict ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            result.RepairCapability);
    }

    /// <summary>Retaining an original non-null domain does not remove a later DDL session prerequisite.</summary>
    /// <param name="strict">Whether strict SQL mode was captured.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedNonNullDomainCannotBypassPermissiveTightening(bool strict)
    {
        // Arrange
        var original = MySqlProjectedAlterPhysicalSnapshot.CreateCatalogColumn("varchar", false, 10, 40,
            null, null, null, 4, "utf8mb4_bin", "utf8mb4");
        var snapshot = new MySqlProjectedAlterPhysicalSnapshot(
            new Dictionary<string, MySqlAlterColumnPhysicalShape> { ["value"] = original }, [], Environment(strict));
        var source = Character("varchar(20)", nullable: true);
        var target = Character("varchar(30)", nullable: false);

        // Act
        var safe = snapshot.PreservesOriginalValueDomain(new AlterColumnIntent("items", target, source), source);

        // Assert
        Assert.Equal(strict, safe);
    }

    /// <summary>Required TIMESTAMP repair needs session evidence even when the accepted source was required.</summary>
    /// <param name="explicitDefault">Whether the target has an authored temporal default.</param>
    /// <param name="sourceBoundProof">Whether the unchanged source has a captured Safe certificate.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RequiredTimestampRepairNeedsCapturedSessionOrCurrentSafeSource(
        bool explicitDefault,
        bool sourceBoundProof
    )
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = (ISafeMigrationProjectedColumnAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var source = Timestamp(explicitDefault: false, comment: "original");
        var target = Timestamp(explicitDefault, comment: "reviewed");
        var intent = new AlterColumnIntent("items", target, source);
        var live = Proof(sourceBoundProof ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Missing,
            sourceBoundProof ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None, strict: true);
        var projected = Proof(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.Safe, strict: true);
        var state = ProjectionContext(source, complete: true, canReuse: sourceBoundProof);

        // Act
        var result = analyzer.ValidateProjectedAlterColumn(intent, source, state, live, projected);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, result.ObservedState);
        Assert.Equal(sourceBoundProof ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            result.RepairCapability);
    }

    /// <summary>A matching projected target needs no repair session prerequisites.</summary>
    /// <param name="timestamp">Whether the unchanged target is a required TIMESTAMP.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingProjectedTargetRemainsNoOpWithoutRepairSessionEvidence(bool timestamp)
    {
        // Arrange
        using var context = CreateContext();
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var projection = new SafeMigrationPreflightProjection(
            projectedColumnAnalyzer: (ISafeMigrationProjectedColumnAnalyzer)analyzer,
            objectIdentityNormalizer: (ISafeMigrationProviderObjectIdentityNormalizer)analyzer);
        var target = timestamp ? Timestamp(explicitDefault: false) : Character("varchar(20)", nullable: false);
        var create = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("items", [target]),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);
        var missing = Proof(SafeMigrationObservedState.Missing, SafeMigrationRepairCapability.None, strict: false);
        var creation = projection.Project(create, missing);
        projection.Observe(create, missing, creation, SafeMigrationDecisionPlanner.Plan(create.Intent.Kind,
            creation.ObservedState, create.Policy, creation.RepairCapability));
        var alter = new SafeMigrationOperation(new AlterColumnIntent("items", target, target),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = projection.Project(alter, missing);
        var decision = SafeMigrationDecisionPlanner.Plan(alter.Intent.Kind, result.ObservedState,
            alter.Policy, result.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
    }

    private static ExpectedColumnDefinition Character(string type, bool nullable, bool explicitDefault = false) =>
        new("value", typeof(string), nullable, type,
            defaultValue: explicitDefault
                ? SafeMigrationDefaultValue.Literal("fixed") : SafeMigrationDefaultValue.None);

    private static ExpectedColumnDefinition Timestamp(bool explicitDefault, string? comment = null) =>
        new("value", typeof(DateTime), false, "timestamp", comment: comment,
            defaultValue: explicitDefault
                ? SafeMigrationDefaultValue.Literal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified))
                : SafeMigrationDefaultValue.None);

    private static SafeMigrationIndexPhysicalEnvironment Environment(bool strict) => new(3072,
        StorageRowFormat: "Dynamic", StoragePageSize: 16384, DefaultCharacterSet: "utf8mb4", StrictSqlMode: strict);

    private static SafeMigrationProviderAnalysis Proof(
        SafeMigrationObservedState state,
        SafeMigrationRepairCapability repair,
        bool strict
    ) => new(state, repair, false, "captured_session_proof") { IndexPhysicalEnvironment = Environment(strict) };

    private static SafeMigrationProjectedAlterColumnContext ProjectionContext(
        ExpectedColumnDefinition source,
        bool complete,
        bool canReuse
    ) => new(new TableView(source), HasCompleteTable: complete, ProvesEmpty: complete,
        HasForeignKeyDependency: false, CanReuseLiveProof: canReuse, HasDataMutation: false,
        PreservesLiveValueDomain: false, CanReuseCreationCharacterSet: true);

    private static DbContext CreateContext() => new(new DbContextOptionsBuilder<DbContext>()
        .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MySql(new Version(8, 4, 11))).UseMySqlSafeMigrations().Options);

    private sealed class TableView(ExpectedColumnDefinition source) : ISafeMigrationProjectedAlterTable
    {
        public IEnumerable<ExpectedColumnDefinition> Columns => [source];

        public ExpectedPrimaryKeyDefinition? PrimaryKey => null;

        public IEnumerable<ExpectedUniqueConstraintDefinition> UniqueConstraints => [];

        public IEnumerable<ExpectedIndexDefinition> Indexes => [];

        public bool TryGetProjectedColumn(string table, string? schema, string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            definition = StringComparer.Ordinal.Equals(column, source.Name) ? source : null;

            return definition is not null;
        }
    }
}
