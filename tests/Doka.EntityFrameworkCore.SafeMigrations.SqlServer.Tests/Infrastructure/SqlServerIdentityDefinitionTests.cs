namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies SQL Server identity definition and physical-slot restrictions before DDL.</summary>
public sealed class SqlServerIdentityDefinitionTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=identity_definition;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Retains only SQL Server's documented non-null, default-free identity column types.</summary>
    [Theory]
    [InlineData("tinyint", false, false, true)]
    [InlineData("smallint", false, false, true)]
    [InlineData("int", false, false, true)]
    [InlineData("bigint", false, false, true)]
    [InlineData("decimal(18,0)", false, false, true)]
    [InlineData("numeric(18,0)", false, false, true)]
    [InlineData("decimal(18,2)", false, false, false)]
    [InlineData("float", false, false, false)]
    [InlineData("uniqueidentifier", false, false, false)]
    [InlineData("int", true, false, false)]
    [InlineData("int", false, true, false)]
    public void IdentityDefinition_RequiresTheDocumentedPhysicalColumnContract(
        string storeType,
        bool nullable,
        bool hasDefault,
        bool supported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var clrType = storeType.StartsWith("decimal", StringComparison.Ordinal)
            || storeType.StartsWith("numeric", StringComparison.Ordinal) ? typeof(decimal)
            : storeType == "float" ? typeof(double) : storeType == "uniqueidentifier" ? typeof(Guid)
            : storeType == "tinyint" ? typeof(byte) : storeType == "smallint" ? typeof(short)
            : storeType == "bigint" ? typeof(long) : typeof(int);

        var definition = new ExpectedColumnDefinition("Id", clrType, nullable, storeType,
            defaultValue: hasDefault ? SafeMigrationDefaultValue.Literal(1) : SafeMigrationDefaultValue.None)
        {
            ProviderAnnotations = IdentityAnnotations("1, 1"),
        };

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("identity_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var catalog = CreateIdentityCatalog(context);

        // Act
        var plan = catalog.Build(operation);
        var inlinePlan = catalog.Build(new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
            "identity_items", [definition]), SafeMigrationTableMode.StrictDefinition), operation.Policy));

        var failure = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        // Assert
        Assert.Equal(!supported, plan.IsStaticallyUnsupported);
        Assert.Equal(!supported, inlinePlan.IsStaticallyUnsupported);
        Assert.Equal(plan.UnsupportedCode, inlinePlan.UnsupportedCode);
        if (supported)
        {
            Assert.Null(plan.UnsupportedCode);
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("identity_definition_unproven", plan.UnsupportedCode);
            Assert.IsType<NotSupportedException>(failure);
        }
    }

    /// <summary>Rejects multiple authored identity columns even before an absent table can be projected.</summary>
    [Fact]
    public void InlineTable_RejectsMoreThanOneIdentityColumn()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var first = IdentityIntColumn("First");
        var second = IdentityIntColumn("Second");
        var operation = new SafeMigrationOperation(new EnsureTableIntent(
            new ExpectedTableDefinition("identity_items", [first, second]), SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateIdentityCatalog(context).Build(operation);
        var failure = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("identity_multiple_columns", plan.UnsupportedCode);
        Assert.IsType<NotSupportedException>(failure);
    }

    /// <summary>Rejects identity seeds and increments outside the authored target's numeric range.</summary>
    [Theory]
    [InlineData("tinyint", "255, 1", true)]
    [InlineData("tinyint", "256, 1", false)]
    [InlineData("tinyint", "1, 256", false)]
    [InlineData("tinyint", "255, -1", false)]
    [InlineData("tinyint", "1, -256", false)]
    [InlineData("tinyint", "1, 0", false)]
    [InlineData("int", "1, 0", false)]
    [InlineData("smallint", "1, -1", true)]
    [InlineData("smallint", "32767, 1", true)]
    [InlineData("smallint", "32768, 1", false)]
    [InlineData("int", "2147483648, 1", false)]
    [InlineData("decimal(1,0)", "9, 1", true)]
    [InlineData("decimal(1,0)", "100, 1", false)]
    [InlineData("decimal(1,0)", "1, 10", false)]
    [InlineData("decimal(38,0)", "9223372036854775807, 1", true)]
    public void IdentityNumericValues_MustFitTheTarget(
        string storeType,
        string identity,
        bool supported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var clr = storeType.StartsWith("decimal", StringComparison.Ordinal) ? typeof(decimal)
            : storeType == "tinyint" ? typeof(byte) : storeType == "smallint" ? typeof(short) : typeof(int);

        var column = new ExpectedColumnDefinition("Id", clr, false, storeType)
        {
            ProviderAnnotations = IdentityAnnotations(identity),
        };

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("identity_items", column),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateIdentityCatalog(context).Build(operation);

        // Assert
        Assert.Equal(!supported, plan.IsStaticallyUnsupported);
        Assert.Equal(supported ? null : "identity_definition_unproven", plan.UnsupportedCode);
    }

    /// <summary>Identity argument slices retain signed values and reject missing or extra fields.</summary>
    /// <param name="identity">The authored seed and increment payload.</param>
    /// <param name="supported">Whether the pair is a complete valid identity definition.</param>
    [Theory]
    [InlineData("1,1", true)]
    [InlineData(" +1, -1 ", true)]
    [InlineData("-9223372036854775808,9223372036854775807", true)]
    [InlineData("", false)]
    [InlineData("1", false)]
    [InlineData("1,", false)]
    [InlineData(",1", false)]
    [InlineData("1,1,1", false)]
    [InlineData("1,,1", false)]
    [InlineData("1,0", false)]
    [InlineData("9223372036854775808,1", false)]
    [InlineData("1,not-a-number", false)]
    public void IdentityArgumentsPreserveExactPairValidation(string identity, bool supported)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var column = new ExpectedColumnDefinition("Id", typeof(long), false, "bigint")
        {
            ProviderAnnotations = IdentityAnnotations(identity),
        };

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("identity_items", column),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateIdentityCatalog(context).Build(operation);

        // Assert
        Assert.Equal(!supported, plan.IsStaticallyUnsupported);
    }

    /// <summary>Shares the occupied-slot prerequisite and lossless, nonthrowing identity metadata proof.</summary>
    [Fact]
    public void IdentityCatalog_ProvesTheSlotAndAllAuthoredMetadataBeforeDdl()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("identity_items", IdentityIntColumn("Id")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateIdentityCatalog(context).Build(operation);
        var sql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.Model).Select(static command => command.CommandText));

        var classifier = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(0, plan);

        // Assert
        Assert.Contains("sys.identity_columns identity_slot", plan.PrerequisiteExpression, StringComparison.Ordinal);
        Assert.Contains("sys.identity_columns identity_slot", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("identity_slot_occupied", plan.ClassificationCodeExpression, StringComparison.Ordinal);
        Assert.Contains("TRY_CONVERT(decimal(38,0), identity_column.seed_value)", plan.StateExpression,
            StringComparison.Ordinal);
        Assert.Contains("TRY_CONVERT(decimal(38,0), identity_column.increment_value)", plan.Postcondition,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CONVERT(bigint, identity_column", sql, StringComparison.Ordinal);
        Assert.Contains("identity_column.is_not_for_replication = 0", sql, StringComparison.Ordinal);
        Assert.Contains("sys.identity_columns identity_slot", sql, StringComparison.Ordinal);
        Assert.Contains("identity_slot_occupied", classifier, StringComparison.Ordinal);
        Assert.Contains(plan.PrerequisiteFailureCodeExpression!, classifier, StringComparison.Ordinal);
        Assert.Contains("HAS_PERMS_BY_NAME", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("c.name", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
    }

    /// <summary>A new analysis invocation cannot inherit the preceding invocation's accepted identity slot.</summary>
    [Fact]
    public async Task EmptyAnalysis_ResetsOrderedIdentityProofs()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = CreateIdentityAnalyzer(context);
        AcceptIdentityOperation(analyzer, new SafeMigrationOperation(new EnsureColumnIntent(
            "identity_items", IdentityIntColumn("First")), SafeMigrationPolicy.ThrowIfDifferent));
        var second = new SafeMigrationOperation(new EnsureColumnIntent("identity_items", IdentityIntColumn("Second")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var missing = MissingIdentityAnalysis();

        // Act
        var before = analyzer.QualifyProjectedIdentityOperation(second, missing);
        await analyzer.AnalyzeAsync(context, []);
        var after = analyzer.QualifyProjectedIdentityOperation(second, missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, before.ObservedState);
        Assert.Same(missing, after);
    }

    /// <summary>Accepted identity creation consumes a table-wide slot even on an initially absent table.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderedIdentity_RejectsASecondSlot(
        bool inline
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = CreateIdentityAnalyzer(context);
        var first = inline
            ? new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
                "identity_items", [IdentityIntColumn("First")]), SafeMigrationTableMode.StrictDefinition),
                SafeMigrationPolicy.ThrowIfDifferent)
            : new SafeMigrationOperation(new EnsureColumnIntent("identity_items", IdentityIntColumn("First")),
                SafeMigrationPolicy.ThrowIfDifferent);

        AcceptIdentityOperation(analyzer, first);
        var second = new SafeMigrationOperation(new EnsureColumnIntent("identity_items", IdentityIntColumn("Second")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = analyzer.ValidateProjectedOperation(second, MissingIdentityAnalysis(), new EmptyIdentityColumns());

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal("identity_slot_occupied", result.Code);
    }

    /// <summary>Unknown column removals and opaque effects cannot be reused as free identity-slot evidence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityRemovalOrOpaqueEffects_RequireFreshPhysicalProof(
        bool opaque
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = CreateIdentityAnalyzer(context);
        AcceptIdentityOperation(analyzer, new SafeMigrationOperation(
            new EnsureColumnIntent("identity_items", IdentityIntColumn("First")),
            SafeMigrationPolicy.ThrowIfDifferent));
        if (opaque)
        {
            analyzer.ObserveProviderOperation(new SqlOperation { Sql = "SELECT 1;" });
        }
        else
        {
            var drop = new SafeMigrationOperation(new DropColumnIntent("First", "identity_items"),
                SafeMigrationPolicy.ThrowIfDifferent);

            var matching = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None, true, "test_matching");

            analyzer.ObserveAcceptedOperation(drop, matching, matching,
                SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, matching.ObservedState, drop.Policy,
                    matching.RepairCapability));
        }

        var second = new SafeMigrationOperation(new EnsureColumnIntent("identity_items", IdentityIntColumn("Second")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var result = analyzer.ValidateProjectedOperation(second, MissingIdentityAnalysis(), new EmptyIdentityColumns());

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, result.ObservedState);
        Assert.Equal("projected_identity_slot_unproven", result.Code);
    }

    /// <summary>An accepted ordinary new table retains a provably free first identity slot.</summary>
    [Fact]
    public void OrdinaryNewTable_AllowsItsFirstIdentitySlot()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = CreateIdentityAnalyzer(context);
        AcceptIdentityOperation(analyzer, new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
            "identity_items", [new ExpectedColumnDefinition("Value", typeof(int), false, "int")]),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent));
        var identity = new SafeMigrationOperation(new EnsureColumnIntent("identity_items", IdentityIntColumn("Id")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var missing = MissingIdentityAnalysis();

        // Act
        var result = analyzer.QualifyProjectedIdentityOperation(identity, missing);

        // Assert
        Assert.Same(missing, result);
    }

    private static SqlServerSafeMigrationProviderAnalyzer CreateIdentityAnalyzer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationProviderAnalysis MissingIdentityAnalysis()
        => new(SafeMigrationObservedState.Missing, SafeMigrationRepairCapability.None, false, "test_missing");

    private static void AcceptIdentityOperation(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        SafeMigrationOperation operation
    )
    {
        var missing = MissingIdentityAnalysis();
        analyzer.ObserveAcceptedOperation(operation, missing, missing,
            SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, missing.ObservedState, operation.Policy,
                missing.RepairCapability));
    }

    private sealed class EmptyIdentityColumns : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? definition
        )
        {
            definition = null;

            return false;
        }
    }

    private static ExpectedColumnDefinition IdentityIntColumn(
        string name
    ) => new(name, typeof(int), false, "int")
    {
        ProviderAnnotations = IdentityAnnotations("1, 1"),
    };

    private static IReadOnlyList<SafeMigrationProviderAnnotation> IdentityAnnotations(
        string identity
    )
    {
        var operation = new AddColumnOperation();
        operation["SqlServer:Identity"] = identity;

        return SafeMigrationProviderAnnotation.Capture(operation);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateIdentityCatalog(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
