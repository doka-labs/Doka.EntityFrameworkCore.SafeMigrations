namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>A dropped different shape cannot authorize a second physical copy of a required constraint.</summary>
    /// <param name="kind">The native constraint family whose allowed shape capacity is checked.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public async Task StrictConstraintTransition_RejectsDuplicateShapeBorrowingDifferentAllowedSlot(
        SafeMigrationOperationKind kind)
    {
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection, kind, ["physical_a1", "physical_a2"]);
        await using var context = CreateConstraintShapeContext(connection, kind);
        var initial = ConstraintShapeTable(kind, ["logical_a"]);
        var contract = new ExpectedTableDefinition(
            initial.Table, initial.Columns, primaryKey: initial.PrimaryKey,
            uniqueConstraints: kind == SafeMigrationOperationKind.EnsureUniqueConstraint
                ? [initial.UniqueConstraints[0], new ExpectedUniqueConstraintDefinition(
                    "logical_b", "shape_items", ["ParentId"])] : [],
            checkConstraints: kind == SafeMigrationOperationKind.EnsureCheckConstraint
                ? [initial.CheckConstraints[0], new ExpectedCheckConstraintDefinition(
                    "logical_b", "shape_items", "Code < 100")] : [],
            foreignKeys: kind == SafeMigrationOperationKind.EnsureForeignKey
                ? [initial.ForeignKeys[0], new ExpectedForeignKeyDefinition(
                    "logical_b", "shape_items", ["ParentId"], "shape_parents", ["Id"],
                    onDelete: ReferentialAction.Cascade)] : []);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(contract, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        AddConstraintShapeDrop(builder, kind, "logical_b");
        var runner = context.GetService<ISafeMigrationRunner>();

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var observedConstraintCount = ConstraintShapeNames(snapshot.Tables["shape_items"], kind).Count();
        var preflight = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-native-constraint-capacity"));

        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-native-constraint-capacity"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, postflight.Status);
        Assert.False(postflight.Assessments[0].PostconditionSatisfied);
        Assert.True(postflight.Assessments[1].PostconditionSatisfied);
        Assert.Equal(2, observedConstraintCount);
    }

    /// <summary>Two explicitly authored equivalent aliases retain two slots rather than being deduplicated.</summary>
    /// <param name="kind">The native constraint family containing legitimate equivalent authored shapes.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public async Task StrictConstraintTransition_PreservesAuthoredDuplicateAliasCapacity(
        SafeMigrationOperationKind kind)
    {
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection, kind, ["physical_a1", "physical_a2"]);
        await using var context = CreateConstraintShapeContext(connection, kind);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(ConstraintShapeTable(kind, ["logical_a", "logical_b"]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        AddConstraintShapeDrop(builder, kind, "logical_b");
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-authored-duplicate-aliases"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-authored-duplicate-aliases"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, item => Assert.True(item.PostconditionSatisfied));
        // WHY: Equivalent UNIQUE declarations share one native autoindex;
        // FK/check declarations remain independently represented. The UQ
        // capacity adversary is covered by the immutable snapshot probe.
        Assert.Equal(kind == SafeMigrationOperationKind.EnsureUniqueConstraint ? 1 : 2,
            ConstraintShapeNames(SqliteSafeMigrationCatalog.Read(connection, transaction: null)
            .Tables["shape_items"], kind).Count());
    }

    /// <summary>Distinct required FK/check aliases require distinct physical witnesses.</summary>
    /// <param name="kind">The native family which retains duplicate equivalent declarations separately.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public async Task StrictConstraintTransition_RejectsMissingRequiredDuplicateAlias(
        SafeMigrationOperationKind kind)
    {
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection, kind, ["physical_a1"]);
        await using var context = CreateConstraintShapeContext(connection, kind);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(ConstraintShapeTable(kind, ["logical_a", "logical_b"]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-required-constraint-capacity"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
    }

    /// <summary>A captured UQ vector cannot reuse one allowed shape to authorize a different physical slot.</summary>
    [Fact]
    public async Task StrictConstraintTransition_SnapshotRejectsUniqueShapeCapacityBorrowing()
    {
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection,
            SafeMigrationOperationKind.EnsureUniqueConstraint, ["physical_a1"]);
        await using var context = CreateConstraintShapeContext(connection,
            SafeMigrationOperationKind.EnsureUniqueConstraint);
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var owner = snapshot.Tables["shape_items"];
        var unique = owner.UniqueConstraints[0];
        var duplicate = new SqliteUniqueSnapshot(
            "physical_a2", "synthetic_independent_index", unique.Columns, unique.Keys);

        var tables = snapshot.Tables.ToDictionary(pair => pair.Key, pair => pair.Value,
            SqliteIdentifierComparer.Instance);

        tables[owner.Name] = new SqliteTableSnapshot(
            owner.Name, owner.Sql, owner.Columns, owner.Indexes, owner.ForeignKeys, owner.Checks,
            owner.PrimaryKeyColumns, owner.PrimaryKeyKeys, owner.PrimaryKeyName, [unique, duplicate],
            owner.HasUnmodeledConstraintOptions, owner.IsStrict, owner.WithoutRowId);
        var captured = new SqliteCatalogSnapshot(snapshot.Version, snapshot.ForeignKeysEnabled,
            snapshot.LegacyAlterTableEnabled, tables, snapshot.OtherObjects);

        var initial = ConstraintShapeTable(SafeMigrationOperationKind.EnsureUniqueConstraint, ["logical_a"]);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition(initial.Table, initial.Columns, primaryKey: initial.PrimaryKey,
                uniqueConstraints: [initial.UniqueConstraints[0], new ExpectedUniqueConstraintDefinition(
                    "logical_b", "shape_items", ["ParentId"])]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropUniqueConstraintIfExists("logical_b", "shape_items");
        var creator = Assert.IsType<SafeMigrationOperation>(builder.Operations[0]);
        var analyzer = Assert.IsType<SqliteSafeMigrationProviderAnalyzer>(
            context.GetService<ISafeMigrationProviderAnalyzer>());

        var analysis = analyzer.Analyze(captured, connection, transaction: null, creator,
            SqliteRebuildArtifactContract.FromModel(context.GetService<IDesignTimeModel>().Model, builder.Operations));

        Assert.Single(owner.UniqueConstraints);
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }
}
