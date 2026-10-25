namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises constraint shape identity with independently owned SQLite state.</summary>
public sealed partial class SqliteSafeMigrationConstraintShapeIdentityIntegrationTests
    : SqliteSafeMigrationCatalogIntegrationTests, IClassFixture<SqliteConstraintShapeServiceFixture>
{
    private readonly SqliteConstraintShapeServiceFixture _constraintShapeServices;

    /// <summary>
    /// Shares provider services for the fixed-family constraint models without process-wide cache growth.
    /// </summary>
    /// <param name="constraintShapeServices">The class-owned provider and model caches.</param>
    public SqliteSafeMigrationConstraintShapeIdentityIntegrationTests(
        SqliteConstraintShapeServiceFixture constraintShapeServices
    ) => _constraintShapeServices = constraintShapeServices
        ?? throw new ArgumentNullException(nameof(constraintShapeServices));

    /// <summary>
    /// Strict table aliases retain native constraint symbols for no-op and physical replacement decisions.
    /// </summary>
    /// <param name="kind">The matched constraint family.</param>
    /// <param name="dropPhysical">Whether to remove the native constraint and recreate its semantic alias.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint, false)]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint, true)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint, false)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint, true)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey, false)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey, true)]
    public async Task StrictTableConstraintAliasUsesActualPhysicalName(
        SafeMigrationOperationKind kind,
        bool dropPhysical
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection, kind, ["physical_constraint"]);
        await using var context = CreateConstraintShapeContext(connection, kind);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(ConstraintShapeTable(kind, ["logical_constraint"]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        AddConstraintShapeDrop(builder, kind, dropPhysical ? "physical_constraint" : "logical_constraint");
        if (dropPhysical)
        {
            AddConstraintShapeEnsure(builder, kind, "logical_constraint");
        }

        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-strict-table-constraint-alias"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-strict-table-constraint-postflight"));

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var names = ConstraintShapeNames(snapshot.Tables["shape_items"], kind);
        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM shape_items WHERE Id = 1 AND Code = 5 AND ParentId = 1;");

        var finalSchemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(dropPhysical ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp,
            report.Assessments[1].Action);
        if (dropPhysical)
        {
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        }
        else
        {
            Assert.Equal(schemaVersion, finalSchemaVersion);
        }

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(dropPhysical ? "logical_constraint" : "physical_constraint", Assert.Single(names));
        Assert.Equal(1, rows);
    }

    /// <summary>A standalone unique alias binds the declared symbol, never its sqlite_autoindex backing name.</summary>
    [Fact]
    public async Task StandaloneUniqueAliasDropAndReensureUsesConstraintSymbolInsteadOfBackingIndex()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection, SafeMigrationOperationKind.EnsureUniqueConstraint,
            ["physical_constraint"]);

        await using var context = CreateConstraintShapeContext(connection,
            SafeMigrationOperationKind.EnsureUniqueConstraint);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AddConstraintShapeEnsure(builder, SafeMigrationOperationKind.EnsureUniqueConstraint, "logical_constraint");
        AddConstraintShapeDrop(builder, SafeMigrationOperationKind.EnsureUniqueConstraint, "physical_constraint");
        AddConstraintShapeEnsure(builder, SafeMigrationOperationKind.EnsureUniqueConstraint, "logical_constraint");
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var live = await analyzer.AnalyzeAsync(context, builder.Operations.OfType<SafeMigrationOperation>().ToArray());
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-standalone-unique-symbol"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-standalone-unique-symbol-postflight"));

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var rows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM shape_items WHERE Id = 1 AND Code = 5 AND ParentId = 1;");

        // Assert
        Assert.Equal("physical_constraint", live[0].MatchedObjectName);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        var unique = Assert.Single(snapshot.Tables["shape_items"].UniqueConstraints);

        Assert.Equal("logical_constraint", unique.Name);
        Assert.StartsWith("sqlite_autoindex_", unique.PhysicalName, StringComparison.Ordinal);
        Assert.NotEqual(unique.Name, unique.PhysicalName);
        Assert.Equal(1, rows);
    }

    /// <summary>Anonymous constraints certify semantic shape without inventing an authored physical name.</summary>
    /// <param name="kind">The anonymous native constraint family.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public async Task StrictTableAnonymousConstraintDoesNotCertifyPhysicalAlias(SafeMigrationOperationKind kind)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection, kind, [null]);
        await using var context = CreateConstraintShapeContext(connection, kind);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(ConstraintShapeTable(kind, ["logical_constraint"]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var analyses = await analyzer.AnalyzeAsync(
            context, builder.Operations.OfType<SafeMigrationOperation>().ToArray());

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
        Assert.Null(Assert.Single(ConstraintShapeNames(snapshot.Tables["shape_items"], kind)));
        Assert.DoesNotContain(analysis.TableConstraintIdentities ?? [],
            binding => binding.Kind == ConstraintShapeObjectKind(kind));
    }

    /// <summary>Two equivalent named witnesses cannot arbitrarily certify an unrelated authored alias.</summary>
    /// <param name="kind">The family supporting two distinct equivalent native declarations.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public async Task StrictTableAmbiguousConstraintWitnessesDoNotCertifyPhysicalAlias(SafeMigrationOperationKind kind)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection, kind, ["physical_first", "physical_second"]);
        await using var context = CreateConstraintShapeContext(connection, kind);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(ConstraintShapeTable(kind, ["logical_first", "logical_second"]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var analyses = await analyzer.AnalyzeAsync(
            context, builder.Operations.OfType<SafeMigrationOperation>().ToArray());

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
        Assert.Equal(["physical_first", "physical_second"],
            ConstraintShapeNames(snapshot.Tables["shape_items"], kind).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(analysis.TableConstraintIdentities ?? [],
            binding => binding.Kind == ConstraintShapeObjectKind(kind));
    }

    /// <summary>
    /// An exact named witness takes priority while the other ambiguous semantic alias stays unresolved.
    /// </summary>
    /// <param name="kind">The family with an exact witness and another equivalent physical member.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public async Task StrictTableExactConstraintNameWinsWithoutResolvingOtherAmbiguousAlias(
        SafeMigrationOperationKind kind
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection, kind, ["logical_constraint", "physical_second"]);
        await using var context = CreateConstraintShapeContext(connection, kind);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(ConstraintShapeTable(kind, ["logical_constraint", "other_alias"]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var analyses = await analyzer.AnalyzeAsync(
            context, builder.Operations.OfType<SafeMigrationOperation>().ToArray());

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        var binding = Assert.Single(analysis.TableConstraintIdentities ?? [],
            candidate => candidate.Kind == ConstraintShapeObjectKind(kind));

        Assert.Equal("logical_constraint", binding.AuthoredName);
        Assert.Equal("logical_constraint", binding.PhysicalName);
        Assert.DoesNotContain(analysis.TableConstraintIdentities ?? [],
            candidate => candidate.AuthoredName == "other_alias");
    }

    /// <summary>Exact named constraints retain matching no-op and postflight behavior.</summary>
    /// <param name="kind">The exactly named native constraint family.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public async Task StrictTableExactNamedConstraintCertifiesPhysicalIdentityWithoutDdl(
        SafeMigrationOperationKind kind
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await CreateConstraintShapeOwnerAsync(connection, kind, ["logical_constraint"]);
        await using var context = CreateConstraintShapeContext(connection, kind);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(ConstraintShapeTable(kind, ["logical_constraint"]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        AddConstraintShapeEnsure(builder, kind, "logical_constraint");
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var live = await analyzer.AnalyzeAsync(context, builder.Operations.OfType<SafeMigrationOperation>().ToArray());
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-exact-constraint-name"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-exact-constraint-postflight"));

        var finalSchemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        var binding = Assert.Single(live[0].TableConstraintIdentities ?? [],
            candidate => candidate.Kind == ConstraintShapeObjectKind(kind));

        Assert.Equal("logical_constraint", binding.AuthoredName);
        Assert.Equal("logical_constraint", binding.PhysicalName);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(schemaVersion, finalSchemaVersion);
    }

    /// <summary>
    /// Creation remains a physical-name proof even though the original live catalog contains no table.
    /// </summary>
    /// <param name="kind">The named family created alongside its owner.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public async Task CreatedTableConstraintNamesRemainPhysicalProofs(SafeMigrationOperationKind kind)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE shape_parents (Id INTEGER NOT NULL, CONSTRAINT pk_shape_parents PRIMARY KEY (Id)); "
            + "INSERT INTO shape_parents (Id) VALUES (1);");
        await using var context = CreateConstraintShapeContext(connection, kind);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(ConstraintShapeTable(kind, ["logical_constraint"]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        AddConstraintShapeEnsure(builder, kind, "logical_constraint");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-created-constraint-identity"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-created-constraint-postflight"));

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal("logical_constraint", Assert.Single(ConstraintShapeNames(snapshot.Tables["shape_items"], kind)));
    }

    /// <summary>Creates one independent fixture with explicit native names and preserved valid rows.</summary>
    /// <param name="connection">The independent in-memory connection.</param>
    /// <param name="kind">The fixture's native constraint family.</param>
    /// <param name="names">The declared symbols, or null for anonymous constraints.</param>
    /// <returns>The completion of fixture creation before the Act section.</returns>
    private static async Task CreateConstraintShapeOwnerAsync(
        DbConnection connection,
        SafeMigrationOperationKind kind,
        IReadOnlyList<string?> names
    )
    {
        var constraints = string.Join(", ", names.Select(name =>
            (name is null ? string.Empty : "CONSTRAINT " + name + " ")
            + (kind switch
            {
                SafeMigrationOperationKind.EnsureUniqueConstraint => "UNIQUE (Code)",
                SafeMigrationOperationKind.EnsureCheckConstraint => "CHECK (Code > 0)",
                SafeMigrationOperationKind.EnsureForeignKey => "FOREIGN KEY (ParentId) REFERENCES shape_parents (Id)",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            })));

        await ExecuteSqlAsync(connection,
            "CREATE TABLE shape_parents (Id INTEGER NOT NULL, CONSTRAINT pk_shape_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE shape_items (Id INTEGER NOT NULL, Code INTEGER NOT NULL, ParentId INTEGER NOT NULL, "
            + "CONSTRAINT pk_shape_items PRIMARY KEY (Id), " + constraints + "); "
            + (kind == SafeMigrationOperationKind.EnsureForeignKey
                ? "CREATE INDEX ix_shape_items_parent ON shape_items (ParentId); " : string.Empty)
            + "INSERT INTO shape_parents (Id) VALUES (1); "
            + "INSERT INTO shape_items (Id, Code, ParentId) VALUES (1, 5, 1);");
    }

    /// <summary>Builds a strict table contract without changing physical naming requirements.</summary>
    /// <param name="kind">The expected family.</param>
    /// <param name="names">The authored constraint aliases.</param>
    /// <returns>The complete expected table definition.</returns>
    private static ExpectedTableDefinition ConstraintShapeTable(
        SafeMigrationOperationKind kind,
        IReadOnlyList<string> names
    ) => new(
        "shape_items",
        [
            new ExpectedColumnDefinition("Id", typeof(int), false, "INTEGER"),
            new ExpectedColumnDefinition("Code", typeof(int), false, "INTEGER"),
            new ExpectedColumnDefinition("ParentId", typeof(int), false, "INTEGER"),
        ],
        primaryKey: new ExpectedPrimaryKeyDefinition("pk_shape_items", "shape_items", ["Id"]),
        uniqueConstraints: kind == SafeMigrationOperationKind.EnsureUniqueConstraint
            ? names.Select(name => new ExpectedUniqueConstraintDefinition(name, "shape_items", ["Code"])) : [],
        checkConstraints: kind == SafeMigrationOperationKind.EnsureCheckConstraint
            ? names.Select(name => SafeMigrationExpectedDefinitionFactory.CheckConstraint(
                name, "shape_items", "Code > 0", schema: null)) : [],
        foreignKeys: kind == SafeMigrationOperationKind.EnsureForeignKey
            ? names.Select(name => new ExpectedForeignKeyDefinition(name, "shape_items", ["ParentId"],
                "shape_parents", ["Id"])) : []);

    /// <summary>Adds the same semantic family contract under one authored symbol.</summary>
    /// <param name="builder">The operation stream receiving the ensure.</param>
    /// <param name="kind">The declared constraint family.</param>
    /// <param name="name">The expected symbol, which may be a semantic alias.</param>
    private static void AddConstraintShapeEnsure(MigrationBuilder builder, SafeMigrationOperationKind kind, string name)
    {
        switch (kind)
        {
            case SafeMigrationOperationKind.EnsureUniqueConstraint:
                builder.AddUniqueConstraintIfNotExists(name, "shape_items", ["Code"]);
                break;
            case SafeMigrationOperationKind.EnsureCheckConstraint:
                builder.AddCheckConstraintIfNotExists(name, "shape_items", "Code > 0");
                break;
            case SafeMigrationOperationKind.EnsureForeignKey:
                builder.AddForeignKeyIfNotExists(name, "shape_items", ["ParentId"], "shape_parents", ["Id"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

    }

    /// <summary>Adds an exact physical-symbol drop, never a backing-index drop.</summary>
    /// <param name="builder">The operation stream receiving the drop.</param>
    /// <param name="kind">The declared constraint family.</param>
    /// <param name="name">The physical symbol requested for removal.</param>
    private static void AddConstraintShapeDrop(MigrationBuilder builder, SafeMigrationOperationKind kind, string name)
    {
        switch (kind)
        {
            case SafeMigrationOperationKind.EnsureUniqueConstraint:
                builder.DropUniqueConstraintIfExists(name, "shape_items");
                break;
            case SafeMigrationOperationKind.EnsureCheckConstraint:
                builder.DropCheckConstraintIfExists(name, "shape_items");
                break;
            case SafeMigrationOperationKind.EnsureForeignKey:
                builder.DropForeignKeyIfExists(name, "shape_items");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

    }

    /// <summary>Reads declared symbols without conflating automatic index identities.</summary>
    /// <param name="table">The current physical table snapshot.</param>
    /// <param name="kind">The family whose declared symbols are read.</param>
    /// <returns>The actual symbols, including null for anonymous declarations.</returns>
    private static IEnumerable<string?> ConstraintShapeNames(
        SqliteTableSnapshot table,
        SafeMigrationOperationKind kind
    ) =>
        kind switch
        {
            SafeMigrationOperationKind.EnsureUniqueConstraint => table.UniqueConstraints.Select(value => value.Name),
            SafeMigrationOperationKind.EnsureCheckConstraint => table.Checks.Select(value => value.Name),
            SafeMigrationOperationKind.EnsureForeignKey => table.ForeignKeys.Select(value => value.Name),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>Maps an ensure family to its native catalog identity kind.</summary>
    /// <param name="kind">The ensure operation family.</param>
    /// <returns>The corresponding native object family.</returns>
    private static SafeMigrationDatabaseObjectKind ConstraintShapeObjectKind(SafeMigrationOperationKind kind) =>
        kind switch
        {
            SafeMigrationOperationKind.EnsureUniqueConstraint => SafeMigrationDatabaseObjectKind.UniqueConstraint,
            SafeMigrationOperationKind.EnsureCheckConstraint => SafeMigrationDatabaseObjectKind.CheckConstraint,
            SafeMigrationOperationKind.EnsureForeignKey => SafeMigrationDatabaseObjectKind.ForeignKey,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>
    /// Selects fixed model types so EF's model cache cannot reuse a different family's constraint contract.
    /// </summary>
    /// <param name="connection">The independently opened in-memory connection.</param>
    /// <param name="kind">The immutable fixture family.</param>
    /// <returns>The concrete fixed-family model context.</returns>
    private SqliteConstraintShapeContext CreateConstraintShapeContext(
        DbConnection connection,
        SafeMigrationOperationKind kind
    ) =>
        kind switch
        {
            SafeMigrationOperationKind.EnsureUniqueConstraint =>
                new SqliteUniqueConstraintShapeContext(connection, _constraintShapeServices.Provider),
            SafeMigrationOperationKind.EnsureCheckConstraint =>
                new SqliteCheckConstraintShapeContext(connection, _constraintShapeServices.Provider),
            SafeMigrationOperationKind.EnsureForeignKey =>
                new SqliteForeignKeyConstraintShapeContext(connection, _constraintShapeServices.Provider),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}

/// <summary>Owns one provider for independently cached constraint identity and projected-FK CLR models.</summary>
public sealed class SqliteConstraintShapeServiceFixture : IDisposable
{
    /// <summary>Builds bounded shared provider services without entering EF's process-wide singleton cache.</summary>
    public SqliteConstraintShapeServiceFixture()
    {
        Provider = new ServiceCollection()
            .AddEntityFrameworkSqlite()
            .AddSqliteSafeMigrations()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Gets the class-owned shared provider and its fixed-family model caches.</summary>
    public ServiceProvider Provider { get; }

    /// <inheritdoc />
    public void Dispose() => Provider.Dispose();
}
