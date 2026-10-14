namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>
/// Preserves exact catalog output and operation-local scratch lifetimes behind diagnostic allocation savings.
/// </summary>
public sealed class MySqlRuntimeDiagnosticsAllocationTests
{
    /// <summary>
    /// Direct predicate emission matches the previous materialized join, including optional empty slots.
    /// </summary>
    /// <param name="shape">The independently varied facet-slot shape.</param>
    [Theory]
    [InlineData("complete")]
    [InlineData("optional")]
    [InlineData("empty")]
    public void DirectColumnPredicateMatchesMaterializedReference(string shape)
    {
        // Arrange
        const string table = "'quoted''owner'";
        const string name = "'member\\path'";
        var conditions = shape switch
        {
            "empty" => new[] { string.Empty, string.Empty },
            "optional" => new[] { "c.COLUMN_TYPE = 'int'", string.Empty, "c.IS_NULLABLE = 'YES'" },
            _ => new[] { "c.COLUMN_TYPE = 'int'", "c.IS_NULLABLE = 'NO'", "c.COLUMN_COMMENT = '\u20AC'",
                "c.ORDINAL_POSITION = 3" },
        };

        var included = conditions.Where(static condition => condition.Length > 0).ToArray();
        var expected = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
            + "WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = " + table + " AND c.COLUMN_NAME = " + name
            + (included.Length == 0 ? string.Empty : " AND " + string.Join(" AND ", included)) + ")";

        // Act
        var actual = MySqlSafeMigrationCatalogSqlBuilder.ComposeColumnMatch(table, name, conditions);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }

    /// <summary>
    /// Borrowed predicate spans end with the synchronous call and cannot alias the emitted immutable SQL.
    /// </summary>
    [Fact]
    public void DirectColumnPredicateDoesNotRetainCallerOwnedConditionStorage()
    {
        // Arrange
        var conditions = new[] { "c.COLUMN_TYPE = 'int'", "c.IS_NULLABLE = 'YES'" };
        const string expected = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
            + "WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = 'items' AND c.COLUMN_NAME = 'value' "
            + "AND c.COLUMN_TYPE = 'int' AND c.IS_NULLABLE = 'YES')";

        // Act
        var actual = MySqlSafeMigrationCatalogSqlBuilder.ComposeColumnMatch("'items'", "'value'", conditions);
        Array.Fill(conditions, "FALSE");

        // Assert
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("FALSE", actual, StringComparison.Ordinal);
    }

    /// <summary>Failure priority stays feature-wide, not dependent on the first invalid column's position.</summary>
    /// <param name="reverse">Whether the unmappable column precedes the qualified-collation defect.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeatureFailurePriorityRemainsGlobalAcrossColumns(bool reverse)
    {
        // Arrange
        using var context = Context();
        var qualified = new ExpectedColumnDefinition("qualified", typeof(string), true, "varchar(20)",
            collation: new SafeMigrationCollationIdentifier("utf8mb4_unicode_ci", "foreign_owner"));
        var unmappable = new ExpectedColumnDefinition("unmappable",
            typeof(MySqlRuntimeDiagnosticsAllocationTests), true);
        var columns = reverse ? new[] { unmappable, qualified } : new[] { qualified, unmappable };
        var operation = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("items", columns),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        using var lease = capture.Begin([operation]);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plan = Assert.Single(lease.Complete());

        // Assert
        Assert.Equal("column_type_mapping", plan.UnsupportedCode);
        Assert.True(plan.IsStaticallyUnsupported);
    }

    /// <summary>
    /// Returned captured parameters are snapshots, while the next operation starts from empty scratch.
    /// </summary>
    [Fact]
    public void CatalogScratchDoesNotCrossOperationsOrOverwriteCapturedParameters()
    {
        // Arrange
        using var context = Context();
        var catalog = Catalog(context.GetService<IRelationalTypeMappingSource>(), context);
        var first = Operation("first_items", "first_value", 13);
        var second = Operation("second_items", "second_value", 29);

        // Act
        var firstPlan = catalog.Build(first, NativeContext(context, first));
        var firstBefore = Render(firstPlan, context);
        var secondPlan = catalog.Build(second, NativeContext(context, second));
        var firstAfter = Render(firstPlan, context);
        var secondSql = Render(secondPlan, context);

        // Assert
        Assert.Equal(firstBefore, firstAfter);
        Assert.Contains("'first_items'", firstAfter, StringComparison.Ordinal);
        Assert.DoesNotContain("second_items", firstAfter, StringComparison.Ordinal);
        Assert.Contains("'second_items'", secondSql, StringComparison.Ordinal);
        Assert.DoesNotContain("first_items", secondSql, StringComparison.Ordinal);
        Assert.NotEmpty(firstPlan.ParameterValues);
        Assert.NotEmpty(secondPlan.ParameterValues);
        AssertScratchCleared(catalog);
    }

    /// <summary>
    /// An exception after identifiers are captured clears scratch and allows a clean subsequent operation.
    /// </summary>
    [Fact]
    public void CatalogScratchRestoresScopeAfterMidGenerationException()
    {
        // Arrange
        using var context = Context();
        var mappings = DispatchProxy.Create<IRelationalTypeMappingSource, MappingProbe>();
        var probe = Assert.IsAssignableFrom<MappingProbe>(mappings);
        probe.Inner = context.GetService<IRelationalTypeMappingSource>();
        var catalog = Catalog(mappings, context);
        var operation = Operation("failed_items", "failed_value", 13);
        var sentinel = new IOException("isolated mapping failure");
        var observedCapturedIdentifiers = false;
        probe.OnMapping = () =>
        {
            if (ScratchCount(catalog, "_identifierScratch") > 0)
            {
                observedCapturedIdentifiers = true;
                throw sentinel;
            }
        };

        // Act
        var failure = Record.Exception(() => catalog.Build(operation, NativeContext(context, operation)));
        probe.OnMapping = null;
        var recovery = Operation("recovered_items", "recovered_value", 29);
        var recoveredPlan = catalog.Build(recovery, NativeContext(context, recovery));
        var recoveredSql = Render(recoveredPlan, context);

        // Assert
        Assert.True(observedCapturedIdentifiers);
        Assert.Same(sentinel, failure);
        Assert.Contains("'recovered_items'", recoveredSql, StringComparison.Ordinal);
        Assert.DoesNotContain("failed_items", recoveredSql, StringComparison.Ordinal);
        AssertScratchCleared(catalog);
    }

    /// <summary>
    /// A rejected nested Build cannot clear or overwrite the active outer operation's captured parameters.
    /// </summary>
    [Fact]
    public void CatalogScratchRejectsReentrancyWithoutCorruptingTheOuterScope()
    {
        // Arrange
        using var context = Context();
        var mappings = DispatchProxy.Create<IRelationalTypeMappingSource, MappingProbe>();
        var probe = Assert.IsAssignableFrom<MappingProbe>(mappings);
        probe.Inner = context.GetService<IRelationalTypeMappingSource>();
        var catalog = Catalog(mappings, context);
        var outer = Operation("outer_items", "outer_value", 13);
        var nested = Operation("nested_items", "nested_value", 29);
        Exception? nestedFailure = null;
        probe.OnMapping = () =>
        {
            if (ScratchCount(catalog, "_identifierScratch") > 0)
            {
                probe.OnMapping = null;
                nestedFailure = Record.Exception(() => catalog.Build(nested, NativeContext(context, nested)));
            }
        };

        // Act
        var outerPlan = catalog.Build(outer, NativeContext(context, outer));
        var outerSql = Render(outerPlan, context);

        // Assert
        Assert.Contains("reentrant", Assert.IsType<InvalidOperationException>(nestedFailure).Message,
            StringComparison.Ordinal);
        Assert.Contains("'outer_items'", outerSql, StringComparison.Ordinal);
        Assert.DoesNotContain("nested_items", outerSql, StringComparison.Ordinal);
        Assert.NotEmpty(outerPlan.ParameterValues);
        AssertScratchCleared(catalog);
    }

    /// <summary>Injects an adversarial mapping callback before forwarding the real provider's exact method.</summary>
    public class MappingProbe : DispatchProxy
    {
        /// <summary>Gets or sets the actual scoped provider mapping source.</summary>
        public IRelationalTypeMappingSource Inner { get; set; } = null!;

        /// <summary>Gets or sets the one test-owned nested or failing mapping callback.</summary>
        public Action? OnMapping { get; set; }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new InvalidOperationException("The proxy has no target method.");
            if (method.Name == nameof(IRelationalTypeMappingSource.FindMapping))
            {
                OnMapping?.Invoke();
            }

            return method.Invoke(Inner, args);
        }
    }

    /// <summary>
    /// Checks both payload scratch and active sentinels rather than inferring cleanup from the next result.
    /// </summary>
    private static void AssertScratchCleared(MySqlSafeMigrationCatalogSqlBuilder catalog)
    {
        Assert.Equal(0, ScratchCount(catalog, "_identifierScratch"));
        Assert.Equal(0, ScratchCount(catalog, "_valueScratch"));
        Assert.Equal(0, ScratchCount(catalog, "_parameterScratch"));
        Assert.Null(Field(catalog, "_identifierMarkers"));
        Assert.Null(Field(catalog, "_valueMarkers"));
        Assert.Null(Field(catalog, "_parameterValues"));
    }

    /// <summary>Reads a private scoped scratch collection only as an adversarial test observation.</summary>
    private static int ScratchCount(MySqlSafeMigrationCatalogSqlBuilder catalog, string name) =>
        Assert.IsAssignableFrom<System.Collections.ICollection>(Field(catalog, name)).Count;

    /// <summary>Requires the exact field to exist so an absent scratch probe cannot pass vacuously.</summary>
    private static object? Field(MySqlSafeMigrationCatalogSqlBuilder catalog, string name) =>
        (typeof(MySqlSafeMigrationCatalogSqlBuilder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The scratch field is unavailable.")).GetValue(catalog);

    /// <summary>Renders a captured parameter snapshot using real provider type mappings.</summary>
    private static string Render(MySqlSafeMigrationRuntimePlan plan, DbContext context)
    {
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var stringMapping = mappings.FindMapping(typeof(string))
            ?? throw new InvalidOperationException("The provider has no identifier string mapping.");

        // WHY: Identifier parameters intentionally have no store type; only
        // captured typed values take the explicit native mapping resolver.
        return plan.RenderStateExpression(value => value.StoreType is null
            ? stringMapping.GenerateSqlLiteral(value.Value)
            : MySqlCatalogTypeMapping.Resolve(mappings, value.Value, value.StoreType).GenerateSqlLiteral(value.Value));
    }

    /// <summary>Creates one nullable column whose explicit default also exercises value scratch.</summary>
    private static SafeMigrationOperation Operation(string table, string column, int defaultValue) => new(
        new EnsureColumnIntent(table, new ExpectedColumnDefinition(column, typeof(int), true, "int",
            defaultValue: SafeMigrationDefaultValue.Literal(defaultValue))), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Creates catalog services without introducing a shared or global scratch owner.</summary>
    private static MySqlSafeMigrationCatalogSqlBuilder Catalog(
        IRelationalTypeMappingSource mappings,
        DbContext context
    ) =>
        new(mappings, context.GetService<ISqlGenerationHelper>());

    /// <summary>Uses the pinned provider's internal context constructor, like existing SPI contract fixtures.</summary>
    private static MySqlMigrationOperationContext NativeContext(DbContext context, SafeMigrationOperation operation)
    {
        var version = MySqlServerVersion.MariaDb(new Version(11, 8, 8));
        var profile = typeof(MySqlServerVersion).GetProperty("Profile", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(version) ?? throw new InvalidOperationException("The native profile is unavailable.");
        var featureConstructor = Assert.Single(typeof(MySqlMigrationFeatureSet)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        var features = Assert.IsType<MySqlMigrationFeatureSet>(featureConstructor.Invoke([profile]));
        var constructor = Assert.Single(typeof(MySqlMigrationOperationContext)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Func<MigrationOperation, IReadOnlyList<MySqlMigrationCommandSpec>> renderer = static _ =>
            [MySqlMigrationCommandSpec.Create("DO 0;", transactionSuppressed: true)];

        return Assert.IsType<MySqlMigrationOperationContext>(constructor.Invoke(
            [operation, context.Model, MigrationsSqlGenerationOptions.Default, version, features, 0,
                "Doka.EntityFrameworkCore.SafeMigrations.MySql.SafeMigrationOperation", renderer]));
    }

    /// <summary>Constructs real mapping services without a database connection.</summary>
    private static DbContext Context() => new(new DbContextOptionsBuilder()
        .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
            MySqlServerVersion.MariaDb(new Version(11, 8, 8)))
        .UseMySqlSafeMigrations().Options);
}
