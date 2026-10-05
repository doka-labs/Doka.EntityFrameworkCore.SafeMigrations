namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies bounded table-wide matching without relaxing any authored column facet.</summary>
public sealed class SqlServerTableColumnSetSqlTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=table_column_set;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Renders all 1,024 column contracts with one catalog join per matching predicate.</summary>
    [Fact]
    public void MaximumOrdinaryTable_UsesOneSetBasedColumnMatch()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        const int count = 1024;
        var columns = Enumerable.Range(0, count).Select(index => new ExpectedColumnDefinition(
            "C" + index.ToString(CultureInfo.InvariantCulture), typeof(int), false, "int")).ToArray();

        var operation = TableOperation(columns);
        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(operation);
        var predicates = new[] { plan.StateExpression, plan.Postcondition, plan.ExecutionPostcondition! };
        var nameCounts = columns.Select(column => CountFragment(plan.Postcondition, "(N'" + column.Name + "',"))
            .ToArray();

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.All(nameCounts, static occurrences => Assert.Equal(1, occurrences));
        foreach (var predicate in predicates)
        {
            Assert.Equal(1, CountFragment(predicate, "NOT EXISTS (SELECT 1 FROM (VALUES "));
            Assert.Equal(1, CountFragment(predicate, "LEFT JOIN sys.columns c "));
            Assert.Equal(count, CountFragment(predicate, "N'int'"));
            Assert.Contains("SELECT COUNT(*) FROM sys.columns c", predicate, StringComparison.Ordinal);
            Assert.Contains("= 1024", predicate, StringComparison.Ordinal);
            Assert.DoesNotContain("EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types", predicate,
                StringComparison.Ordinal);

            Assert.True(predicate.Length < count * 256 + 12000);
        }
    }

    /// <summary>Preserves inferred length, Unicode, precision, and scale in the VALUES contract.</summary>
    [Theory]
    [InlineData(
        typeof(string),
        "varchar(80)",
        false,
        80,
        false,
        null,
        null,
        "N'Value', N'varchar', 1, 0, 80, CAST(NULL AS int), CAST(NULL AS int), 1")]
    [InlineData(
        typeof(string),
        "nchar(80)",
        true,
        80,
        true,
        null,
        null,
        "N'Value', N'nchar', 1, 0, 160, CAST(NULL AS int), CAST(NULL AS int), 1")]
    [InlineData(
        typeof(string),
        "nvarchar(80)",
        true,
        80,
        false,
        null,
        null,
        "N'Value', N'nvarchar', 1, 0, 160, CAST(NULL AS int), CAST(NULL AS int), 1")]
    [InlineData(
        typeof(byte[]),
        "varbinary(80)",
        null,
        80,
        false,
        null,
        null,
        "N'Value', N'varbinary', 1, 0, 80, CAST(NULL AS int), CAST(NULL AS int), 0")]
    [InlineData(
        typeof(decimal),
        "decimal(12,3)",
        null,
        null,
        null,
        12,
        3,
        "N'Value', N'decimal', 1, 0, CAST(NULL AS int), 12, 3, 0")]
    [InlineData(
        typeof(DateTime),
        "datetime2(3)",
        null,
        null,
        null,
        3,
        null,
        "N'Value', N'datetime2', 1, 0, CAST(NULL AS int), CAST(NULL AS int), 3, 0")]
    public void InferredFacets_MatchTheExplicitColumnSetContract(
        Type clrType,
        string storeType,
        bool? unicode,
        int? length,
        bool? fixedLength,
        int? precision,
        int? scale,
        string expectedRow
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var inferred = new ExpectedColumnDefinition("Value", clrType, true, isUnicode: unicode, maxLength: length,
            isFixedLength: fixedLength, precision: precision, scale: scale);

        var explicitType = new ExpectedColumnDefinition("Value", clrType, true, storeType, unicode, length,
            fixedLength, precision: precision, scale: scale);

        // Act
        var inferredPlan = catalog.Build(TableOperation([inferred]));
        var explicitPlan = catalog.Build(TableOperation([explicitType]));

        // Assert
        Assert.False(inferredPlan.IsStaticallyUnsupported);
        Assert.Equal(explicitPlan.StateExpression, inferredPlan.StateExpression);
        Assert.Equal(explicitPlan.Postcondition, inferredPlan.Postcondition);
        Assert.Contains(expectedRow, inferredPlan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("expected.max_length IS NULL OR c.max_length = expected.max_length",
            inferredPlan.Postcondition, StringComparison.Ordinal);

        Assert.Contains("expected.precision_value IS NULL OR c.precision = expected.precision_value",
            inferredPlan.Postcondition, StringComparison.Ordinal);

        Assert.Contains("expected.scale_value IS NULL OR c.scale = expected.scale_value",
            inferredPlan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>Rejects provider-owned physical attributes and nonbuilt-in type identities.</summary>
    [Theory]
    [InlineData("ty.is_user_defined = 0")]
    [InlineData("ty.is_assembly_type = 0")]
    [InlineData("c.is_nullable = expected.is_nullable")]
    [InlineData("c.is_identity = expected.is_identity")]
    [InlineData("c.is_computed = 0")]
    [InlineData("c.is_sparse = 0")]
    [InlineData("c.is_column_set = 0")]
    [InlineData("c.is_rowguidcol = 0")]
    [InlineData("c.is_filestream = 0")]
    [InlineData("c.is_hidden = 0")]
    [InlineData("c.generated_always_type = 0")]
    public void OrdinaryColumnSet_RequiresEveryPhysicalInvariant(
        string invariant
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operation = TableOperation([new ExpectedColumnDefinition("Value", typeof(int), true, "int")]);

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains(invariant, plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains(invariant, plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains(invariant, plan.ExecutionPostcondition!, StringComparison.Ordinal);
    }

    /// <summary>Retains database-default and explicitly authored character collations.</summary>
    [Theory]
    [InlineData(null, "CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))")]
    [InlineData("Latin1_General_100_BIN2", "N'Latin1_General_100_BIN2'")]
    public void CharacterColumnSet_RequiresTheExpectedCollation(
        string? name,
        string expectedCollation
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var column = new ExpectedColumnDefinition("Value", typeof(string), true, "varchar(80)",
            collation: name is null ? null : new SafeMigrationCollationIdentifier(name));

        // Act
        var plan = CreateCatalog(context).Build(TableOperation([column]));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains(expectedCollation, plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("expected.is_character = 0 OR c.collation_name = expected.collation_name",
            plan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>Does not narrow signed identity bounds or accept replication-specific identities.</summary>
    [Theory]
    [InlineData("1, -1", "CAST(1 AS decimal(38,0))", "CAST(-1 AS decimal(38,0))")]
    [InlineData("-9223372036854775808, 9223372036854775807",
        "CAST(-9223372036854775808 AS decimal(38,0))", "CAST(9223372036854775807 AS decimal(38,0))")]
    public void IdentityColumnSet_PreservesNonthrowingExactNumericMetadata(
        string identity,
        string expectedSeed,
        string expectedIncrement
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var annotationSource = new AddColumnOperation();
        annotationSource["SqlServer:Identity"] = identity;
        var column = new ExpectedColumnDefinition("Id", typeof(long), false, "bigint")
        {
            ProviderAnnotations = SafeMigrationProviderAnnotation.Capture(annotationSource),
        };

        // Act
        var plan = CreateCatalog(context).Build(TableOperation([column]));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains(expectedSeed, plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains(expectedIncrement, plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("identity_column.is_not_for_replication = 0", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("TRY_CONVERT(decimal(38,0), identity_column.seed_value) = expected.identity_seed",
            plan.Postcondition, StringComparison.Ordinal);

        Assert.Contains("TRY_CONVERT(decimal(38,0), identity_column.increment_value) = expected.identity_increment",
            plan.Postcondition, StringComparison.Ordinal);

        Assert.DoesNotContain("CONVERT(bigint, identity_column", plan.Postcondition, StringComparison.Ordinal);
        Assert.DoesNotContain("EXISTS (SELECT 1 FROM sys.identity_columns identity_column", plan.Postcondition,
            StringComparison.Ordinal);
    }

    /// <summary>Uses the same binary default stamp as the single-column matcher, never text similarity.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultColumnSet_RetainsTheExactFingerprintContract(
        bool sqlDefault
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = CreateCatalog(context);
        var defaultColumn = new ExpectedColumnDefinition("Value", typeof(int), false, "int",
            defaultValue: sqlDefault ? SafeMigrationDefaultValue.Sql("7") : SafeMigrationDefaultValue.Literal(7));

        var noDefaultColumn = new ExpectedColumnDefinition("Other", typeof(int), true, "int");
        var columnOperation = new SafeMigrationOperation(new EnsureColumnIntent("set_items", defaultColumn),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var columnPlan = catalog.Build(columnOperation);
        var tablePlan = catalog.Build(TableOperation([defaultColumn, noDefaultColumn]));
        var fingerprint = System.Text.RegularExpressions.Regex.Matches(columnPlan.Postcondition, "N'[0-9A-F]{64}'")
            .Select(static match => match.Value)
            .Single();

        // Assert
        Assert.False(tablePlan.IsStaticallyUnsupported);
        Assert.Contains(fingerprint, tablePlan.Postcondition, StringComparison.Ordinal);
        Assert.Contains(fingerprint, tablePlan.PostApplySql!, StringComparison.Ordinal);
        Assert.Contains("expected.default_fingerprint IS NULL AND c.default_object_id = 0",
            tablePlan.Postcondition, StringComparison.Ordinal);

        Assert.Contains("expected.default_fingerprint IS NOT NULL AND dc.object_id IS NOT NULL",
            tablePlan.Postcondition, StringComparison.Ordinal);

        Assert.Contains("ep.class = 1 AND ep.major_id = dc.object_id", tablePlan.Postcondition,
            StringComparison.Ordinal);

        Assert.Contains("ep.minor_id = 0 AND ep.name = N'Doka:SafeMigrations:Contract'", tablePlan.Postcondition,
            StringComparison.Ordinal);

        Assert.Contains("CONVERT(nvarchar(64), ep.value) = expected.default_fingerprint "
            + "COLLATE Latin1_General_100_BIN2",
            tablePlan.Postcondition, StringComparison.Ordinal);

        Assert.DoesNotContain("dc.definition", tablePlan.Postcondition, StringComparison.Ordinal);
        Assert.DoesNotContain("default_column", tablePlan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects missing columns, type rows, identity rows, and stamps even when predicates become UNKNOWN.
    /// </summary>
    [Fact]
    public void MissingCatalogEvidence_IsAnExplicitMismatchRatherThanUnknown()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var operation = TableOperation([new ExpectedColumnDefinition("Value", typeof(int), true, "int")]);

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.Contains("WHERE CASE WHEN c.column_id IS NOT NULL", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("THEN 0 ELSE 1 END = 1)", plan.Postcondition, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE NOT (", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN sys.types ty", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN sys.identity_columns identity_column", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN sys.default_constraints dc", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN sys.extended_properties ep", plan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>Builds an ordinary strict table operation with unchanged default constraint ownership.</summary>
    private static SafeMigrationOperation TableOperation(
        ExpectedColumnDefinition[] columns
    ) => new(new EnsureTableIntent(new ExpectedTableDefinition("set_items", columns),
        SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Uses the active EF mappings and identifier renderer without opening a connection.</summary>
    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Counts exact SQL fragments without treating metacharacters as pattern syntax.</summary>
    private static int CountFragment(
        string sql,
        string fragment
    ) => sql.Split(fragment, StringSplitOptions.None).Length - 1;
}
