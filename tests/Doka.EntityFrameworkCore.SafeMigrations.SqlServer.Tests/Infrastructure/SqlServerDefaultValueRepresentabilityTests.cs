namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies typed default conversion before catalog classification or baseline DDL.</summary>
public sealed class SqlServerDefaultValueRepresentabilityTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=default_values;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Rejects datetime's lower-range failure without rejecting datetime2's valid minimum.</summary>
    [Theory]
    [InlineData("datetime", 1, true)]
    [InlineData("datetime", 1753, false)]
    [InlineData("smalldatetime", 1, true)]
    [InlineData("smalldatetime", 1900, false)]
    [InlineData("datetime2", 1, false)]
    public void LiteralDefault_UsesTheDocumentedTemporalDomain(
        string storeType,
        int year,
        bool unsupported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, storeType,
            defaultValue: SafeMigrationDefaultValue.Literal(new DateTime(year, 1, 1)));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);
        var failure = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        // Assert
        Assert.Equal(unsupported, plan.IsStaticallyUnsupported);
        if (unsupported)
        {
            Assert.Equal("default_value_unrepresentable", plan.UnsupportedCode);
            Assert.IsType<NotSupportedException>(failure);
        }
        else
        {
            Assert.Null(failure);
        }
    }

    /// <summary>Shares source-typed date conversion between literal and structured default guards.</summary>
    [Theory]
    [InlineData("datetime", 1753, false)]
    [InlineData("datetime", 1753, true)]
    [InlineData("smalldatetime", 1900, false)]
    [InlineData("smalldatetime", 1900, true)]
    public void ValidLegacyTemporalDefault_QualifiesItsNaturalOperandBeforeTargetConversion(
        string storeType,
        int year,
        bool structured
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var value = new DateTime(year, 1, 1);
        var defaultValue = structured
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value, storeType))
            : SafeMigrationDefaultValue.Literal(value);

        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, storeType,
            defaultValue: defaultValue);

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var renderer = new SqlServerSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        // Act
        var plan = CreateCatalog(context).Build(operation);
        var rendered = renderer.Render(SafeMigrationSql.Literal(value, storeType));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        var support = Assert.IsType<string>(plan.DefaultValueSupportExpression);
        Assert.Contains("TRY_CAST(TRY_CAST('", support, StringComparison.Ordinal);
        Assert.Contains(" AS datetime2) AS " + storeType + ") IS NOT NULL", support, StringComparison.Ordinal);
        Assert.StartsWith("CAST(CAST('", rendered, StringComparison.Ordinal);
        Assert.Contains(" AS datetime2) AS " + storeType + ")", rendered, StringComparison.Ordinal);
    }

    /// <summary>Applies the same immutable default gate to new tables and both alteration definitions.</summary>
    [Theory]
    [InlineData("table")]
    [InlineData("alter_target")]
    [InlineData("alter_old")]
    public void InvalidDefault_CannotBypassTheGateThroughAnotherColumnEntryPoint(
        string entryPoint
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var invalid = new ExpectedColumnDefinition("Created", typeof(DateTime), false, "datetime",
            defaultValue: SafeMigrationDefaultValue.Literal(DateTime.MinValue));

        var valid = new ExpectedColumnDefinition("Created", typeof(DateTime), false, "datetime2");
        SafeMigrationIntent intent = entryPoint switch
        {
            "table" => new EnsureTableIntent(new ExpectedTableDefinition("default_items", [invalid]),
                SafeMigrationTableMode.StrictDefinition),
            "alter_target" => new AlterColumnIntent("default_items", invalid, valid),
            "alter_old" => new AlterColumnIntent("default_items", valid, invalid),
            _ => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
        };

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);
        var failure = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("default_value_unrepresentable", plan.UnsupportedCode);
        Assert.IsType<NotSupportedException>(failure);
    }

    /// <summary>Uses inferred EF facets when proving temporal and decimal rounding limits.</summary>
    [Theory]
    [InlineData("temporal")]
    [InlineData("decimal")]
    public void OmittedStoreType_DefaultProofUsesTheBaselineFacetMapping(
        string family
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = family == "temporal"
            ? new ExpectedColumnDefinition("Value", typeof(DateTime), false, precision: 3,
                defaultValue: SafeMigrationDefaultValue.Literal(DateTime.MaxValue))
            : new ExpectedColumnDefinition("Value", typeof(decimal), false, precision: 2, scale: 1,
                defaultValue: SafeMigrationDefaultValue.Literal(9.95m));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("default_value_unrepresentable", plan.UnsupportedCode);
    }

    /// <summary>Proves structured literal defaults against both their explicit type and the destination.</summary>
    [Theory]
    [InlineData("datetime", true)]
    [InlineData("datetime2", false)]
    public void StructuredLiteralDefault_CannotHideAnInvalidTargetConversion(
        string storeType,
        bool unsupported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, storeType,
            defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(DateTime.MinValue, storeType)));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.Equal(unsupported, plan.IsStaticallyUnsupported);
        if (unsupported)
        {
            Assert.Equal("default_value_unrepresentable", plan.UnsupportedCode);
        }
    }

    /// <summary>Does not certify captured SQL or text parsing merely because the column has a default.</summary>
    [Theory]
    [InlineData(false, "default_expression_unproven")]
    [InlineData(true, "default_value_unrepresentable")]
    public void UnprovenDefaultConversions_AreInvariantUnsupported(
        bool structured,
        string code
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var defaultValue = structured
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal("not a date"))
            : SafeMigrationDefaultValue.Sql("CONVERT(datetime, N'0001-01-01')");

        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, "datetime",
            defaultValue: defaultValue);

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);
        var failure = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal(code, plan.UnsupportedCode);
        Assert.IsType<NotSupportedException>(failure);
    }

    /// <summary>Retains current temporal defaults with a shared non-throwing conversion boundary.</summary>
    [Theory]
    [InlineData("CURRENT_TIMESTAMP", "datetime")]
    [InlineData("GETDATE()", "datetime2")]
    [InlineData("SYSUTCDATETIME()", "datetime")]
    [InlineData("COALESCE(SYSUTCDATETIME(), CAST(NULL AS datetime2))", "datetime2")]
    public void CurrentTemporalDefault_UsesTheSameScalarGateInCatalogAndRuntime(
        string sql,
        string storeType
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, storeType,
            defaultValue: SafeMigrationDefaultValue.Sql(sql));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);
        var selection = SqlServerSafeMigrationProviderAnalyzer.BuildCatalogSelection(257, plan);
        var runtime = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        var support = Assert.IsType<string>(plan.DefaultValueSupportExpression);
        Assert.Contains("TRY_CAST", support, StringComparison.Ordinal);
        Assert.Contains(support, selection, StringComparison.Ordinal);
        Assert.Contains("default_value_unrepresentable", selection, StringComparison.Ordinal);
        var supportOffset = runtime.IndexOf(support, StringComparison.Ordinal);
        var stateOffset = runtime.IndexOf("DECLARE @doka_state", StringComparison.Ordinal);
        Assert.True(supportOffset >= 0);
        Assert.True(stateOffset > supportOffset);
    }

    /// <summary>Does not let incompatible computed families or failing COALESCE branches authorize a default.</summary>
    [Theory]
    [InlineData("integer")]
    [InlineData("coalesce")]
    public void ComputedDefault_InvalidTypedFamilyOrConstantFailsClosed(
        string kind
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = kind == "integer"
            ? new ExpectedColumnDefinition("Value", typeof(int), false, "int",
                defaultValue: SafeMigrationDefaultValue.Sql("SYSUTCDATETIME()"))
            : new ExpectedColumnDefinition("Value", typeof(DateTime), false, "datetime",
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Function("COALESCE",
                    SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp),
                    SafeMigrationSql.Literal(DateTime.MinValue))));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal(kind == "integer" ? "default_expression_unproven" : "default_value_unrepresentable",
            plan.UnsupportedCode);
    }

    /// <summary>Accepts only the documented zero-argument temporal builtin shape.</summary>
    [Theory]
    [InlineData("GETDATE")]
    [InlineData("GETUTCDATE")]
    [InlineData("SYSDATETIME")]
    [InlineData("SYSUTCDATETIME")]
    [InlineData("SYSDATETIMEOFFSET")]
    public void TemporalBuiltinRenderer_RequiresZeroArity(
        string function
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = new SqlServerSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var valid = SafeMigrationSql.Function(function);
        var invalid = SafeMigrationSql.Function(function, SafeMigrationSql.Literal(1));

        // Act
        var rendered = renderer.Render(valid);
        var invalidFeature = renderer.GetUnsupportedFeature(invalid);
        var failure = Record.Exception(() => renderer.Render(invalid));

        // Assert
        Assert.Equal(function + "()", rendered);
        Assert.Equal("structured_function_arity", invalidFeature);
        Assert.IsType<NotSupportedException>(failure);
    }

    /// <summary>
    /// Shares encoded-byte and roundtrip evidence for ANSI literals before column and inline-table DDL.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnsiLiteralDefault_UsesBothInputEncodingStagesAndTheByteCapacityGate(
        bool inlineTable
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        const string collation = "Latin1_General_100_CI_AS_SC_UTF8";
        var definition = new ExpectedColumnDefinition("Caption", typeof(string), false, "varchar(1)",
            collation: new SafeMigrationCollationIdentifier(collation),
            defaultValue: SafeMigrationDefaultValue.Literal("\u00E9"));

        SafeMigrationIntent intent = inlineTable
            ? new EnsureTableIntent(new ExpectedTableDefinition("default_items", [definition]),
                SafeMigrationTableMode.StrictDefinition)
            : new EnsureColumnIntent("default_items", definition);

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);
        var support = plan.DefaultValueSupportExpression;
        var selection = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.NotNull(support);
        Assert.Contains("COLLATE " + collation, support, StringComparison.Ordinal);
        Assert.Contains("COLLATE DATABASE_DEFAULT", support, StringComparison.Ordinal);
        Assert.Contains("DATALENGTH(CONVERT(varchar(max)", support, StringComparison.Ordinal);
        Assert.Contains("<= 1", support, StringComparison.Ordinal);
        Assert.Contains("CONVERT(varbinary(max)", support, StringComparison.Ordinal);
        Assert.Contains(support.Replace("'", "''", StringComparison.Ordinal), selection, StringComparison.Ordinal);
        Assert.Contains("N'unsupported', 0, 0, N'default_value_unrepresentable'", selection, StringComparison.Ordinal);
    }

    /// <summary>
    /// Preserves provider non-null authority for temporal defaults without weakening Core's generic proof.
    /// </summary>
    [Theory]
    [InlineData("GETDATE()")]
    [InlineData("COALESCE(SYSUTCDATETIME(), CAST(NULL AS datetime2))")]
    public void KnownTemporalDefault_ProvidesAnExplicitOmittedColumnProof(
        string sql
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, "datetime2",
            defaultValue: SafeMigrationDefaultValue.Sql(sql));

        // Act
        var proof = CreateCatalog(context).BuildNonNullDefaultSupportExpression(definition);

        // Assert
        Assert.NotNull(proof);
        Assert.Contains("TRY_CAST", proof, StringComparison.Ordinal);
    }

    /// <summary>Enforces documented builtin arity before any default, check, or filter can be rendered.</summary>
    [Theory]
    [InlineData("ABS", 1, true)]
    [InlineData("ABS", 0, false)]
    [InlineData("ABS", 2, false)]
    [InlineData("LEN", 1, true)]
    [InlineData("LEN", 0, false)]
    [InlineData("LOWER", 1, true)]
    [InlineData("LOWER", 2, false)]
    [InlineData("UPPER", 1, true)]
    [InlineData("UPPER", 0, false)]
    [InlineData("NULLIF", 2, true)]
    [InlineData("NULLIF", 1, false)]
    [InlineData("NULLIF", 3, false)]
    [InlineData("COALESCE", 2, true)]
    [InlineData("COALESCE", 0, false)]
    [InlineData("COALESCE", 1, false)]
    [InlineData("CONCAT", 2, true)]
    [InlineData("CONCAT", 254, true)]
    [InlineData("CONCAT", 1, false)]
    [InlineData("CONCAT", 255, false)]
    public void GenericBuiltinRenderer_EnforcesItsDocumentedArgumentCount(
        string function,
        int count,
        bool supported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = new SqlServerSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var expression = SafeMigrationSql.Function(function,
            Enumerable.Range(0, count).Select(static value => SafeMigrationSql.Literal(value)).ToArray());

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var failure = Record.Exception(() => renderer.Render(expression));

        // Assert
        if (supported)
        {
            Assert.Null(feature);
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("structured_function_arity", feature);
            Assert.IsType<NotSupportedException>(failure);
        }
    }

    /// <summary>Proves installed collations before an authored name appears in delayed scalar SQL.</summary>
    [Fact]
    public void AuthoredDefaultCollation_IsCheckedBeforeNamedScalarBinding()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = new ExpectedColumnDefinition("Caption", typeof(string), false, "varchar(8)",
            collation: new SafeMigrationCollationIdentifier("Doka_missing_collation"),
            defaultValue: SafeMigrationDefaultValue.Literal("a"));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);
        var selection = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);
        var runtime = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);

        // Assert
        var installed = Assert.IsType<string>(plan.ColumnCollationSupportExpression);
        Assert.True(plan.DefaultValueSupportRequiresDelayedBinding);
        Assert.Contains("sys.fn_helpcollations()", installed, StringComparison.Ordinal);
        Assert.Contains("column_collation_unproven", selection, StringComparison.Ordinal);
        Assert.True(selection.IndexOf(installed, StringComparison.Ordinal)
            < selection.IndexOf("DECLARE @doka_default", StringComparison.Ordinal));
        Assert.True(runtime.IndexOf(installed, StringComparison.Ordinal)
            < runtime.IndexOf("DECLARE @doka_default_supported", StringComparison.Ordinal));
    }

    /// <summary>Retains large same-family LOB defaults without the TRY_CAST large-input limitation.</summary>
    [Fact]
    public void LargeStructuredCharacterDefault_UsesIntrinsicConversionAndSeparateCapacityProof()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = new ExpectedColumnDefinition("Caption", typeof(string), false, "nvarchar(max)",
            defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(new string('a', 4_001))));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        var support = Assert.IsType<string>(plan.DefaultValueSupportExpression);
        Assert.Contains("CONVERT(nvarchar(max)", support, StringComparison.Ordinal);
        Assert.DoesNotContain("TRY_CAST", support, StringComparison.Ordinal);
    }

    /// <summary>Maps a typed SQL literal's source independently of the explicit conversion target.</summary>
    [Theory]
    [InlineData("1", "int", "CAST(N'1' AS int)")]
    [InlineData("not a date", "datetime", "CAST(N'not a date' AS datetime)")]
    [InlineData("a", "varchar(8)", "CAST(N'a' AS varchar(8))")]
    public void TypedLiteralRenderer_DoesNotAskEfForAnIncompatibleClrStorePair(
        string value,
        string storeType,
        string expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = new SqlServerSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var expression = SafeMigrationSql.Literal(value, storeType);

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var rendered = renderer.Render(expression);

        // Assert
        Assert.Null(feature);
        Assert.Equal(expected, rendered);
    }

    /// <summary>Rejects incompatible authored column families without entering EF collection inference.</summary>
    [Theory]
    [InlineData("int", false)]
    [InlineData("datetime", false)]
    [InlineData("nvarchar(8)", true)]
    public void ColumnClrType_RequiresTheAuthoredScalarFamily(
        string storeType,
        bool supported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var definition = new ExpectedColumnDefinition("Caption", typeof(string), true, storeType);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.Equal(!supported, plan.IsStaticallyUnsupported);
        Assert.Equal(supported ? null : "column_type_mapping", plan.UnsupportedCode);
    }

    /// <summary>Proves the encoded byte width of typed ANSI intermediate defaults before Unicode conversion.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnsiIntermediateDefault_HasItsOwnRoundtripAndEncodedCapacityProof(
        bool explicitCast
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var expression = explicitCast ? SafeMigrationSql.Cast(SafeMigrationSql.Literal("\u00E9"), "varchar(1)")
            : SafeMigrationSql.Literal("\u00E9", "varchar(1)");

        var definition = new ExpectedColumnDefinition("Caption", typeof(string), false, "nvarchar(10)",
            defaultValue: SafeMigrationDefaultValue.Sql(expression));

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("default_items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = CreateCatalog(context).Build(operation);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        var support = Assert.IsType<string>(plan.DefaultValueSupportExpression);
        Assert.Contains("COLLATE DATABASE_DEFAULT", support, StringComparison.Ordinal);
        Assert.Contains("DATALENGTH(CONVERT(varchar(max)", support, StringComparison.Ordinal);
        Assert.Contains("<= 1", support, StringComparison.Ordinal);
        Assert.Contains("CONVERT(varbinary(max)", support, StringComparison.Ordinal);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
