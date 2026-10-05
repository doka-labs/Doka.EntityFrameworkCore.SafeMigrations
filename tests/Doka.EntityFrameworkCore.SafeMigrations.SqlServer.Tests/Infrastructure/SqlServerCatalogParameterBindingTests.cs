namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies lossless catalog source parameters independently of live SQL Server.</summary>
public sealed class SqlServerCatalogParameterBindingTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=parameter_bindings;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Supplies complete authored values and their natural source mappings.</summary>
    /// <returns>Synthetic Unicode, binary, decimal, and full-precision temporal sources.</returns>
    public static TheoryData<object, string, SqlDbType> LosslessSources() => new()
    {
        { "a\u20ac\ud83d\ude00", "nvarchar(max)", SqlDbType.NVarChar },
        { new byte[] { 0, 1, 128, 255 }, "varbinary(max)", SqlDbType.VarBinary },
        { 123456789.12345678901234567890m, "decimal(38,20)", SqlDbType.Decimal },
        { 1.0000000000000000000000000000m, "decimal(38,28)", SqlDbType.Decimal },
        { new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1234567), "datetime2(7)", SqlDbType.DateTime2 },
        { new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5)).AddTicks(1234567),
            "datetimeoffset(7)", SqlDbType.DateTimeOffset },
        { new TimeSpan(123456789), "time(7)", SqlDbType.Time },
    };

    /// <summary>Supplies framework-supported scalar domains that must remain valid after parameterization.</summary>
    /// <returns>The source value and an already supported authored store type.</returns>
    public static TheoryData<object, string> NaturalSources() => new()
    {
        { new Guid("13572468-2468-1357-2468-135724681357"), "uniqueidentifier" },
        { new DateOnly(2026, 1, 2), "date" },
        { true, "bit" },
        { (sbyte)-128, "smallint" },
        { ushort.MaxValue, "int" },
        { uint.MaxValue, "bigint" },
        { ulong.MaxValue, "decimal(20,0)" },
        { BindingValue.Large, "bigint" },
    };

    /// <summary>Retains supported CLR scalar domains without requiring a new destination conversion.</summary>
    /// <param name="value">The natural authored scalar.</param>
    /// <param name="destination">The pre-existing supported destination store type.</param>
    [Theory]
    [MemberData(nameof(NaturalSources))]
    public void NaturalSource_RetainsSupportedLiteralDomain(object value, string destination)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("natural_sources", ["Id"], ["int"],
            ["Id", "Value"], ["int", destination], new object?[,] { { 1, value } });

        var operation = (SafeMigrationOperation)builder.Operations[0];
        var unbound = new SqlServerSafeMigrationCatalogSqlBuilder(mappings,
            context.GetService<ISqlGenerationHelper>());

        var bindings = new SqlServerCatalogParameterBindings(mappings, 0);
        var bound = new SqlServerSafeMigrationCatalogSqlBuilder(mappings, context.GetService<ISqlGenerationHelper>(),
            sourceParameter: (source, _) => bindings.Add(source));

        // Act
        var original = unbound.Build(operation);
        var parameterized = bound.Build(operation);
        var captured = bindings.Values.First(parameter => Equals(parameter.Value, value));
        using var command = new SqlCommand();
        var parameter = (SqlParameter)captured.Mapping.CreateParameter(command,
            captured.ExternalName, captured.Value, nullable: false);

        // Assert
        Assert.False(original.IsStaticallyUnsupported);
        Assert.False(parameterized.IsStaticallyUnsupported);
        Assert.Contains(bindings.Values, parameter => Equals(parameter.Value, value));
        Assert.DoesNotContain("@doka_source", parameterized.StateExpression, StringComparison.Ordinal);
        Assert.Contains("@doka_value", parameterized.StateExpression, StringComparison.Ordinal);
        if (value is sbyte or ushort or uint or ulong or BindingValue)
        {
            Assert.Equal(Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                Convert.ToDecimal(parameter.Value, CultureInfo.InvariantCulture));
        }
        else if (value is DateOnly date)
        {
            Assert.Equal(date, parameter.Value switch
            {
                DateOnly actual => actual,
                DateTime actual => DateOnly.FromDateTime(actual),
                _ => throw new InvalidOperationException("The parameter changed the date domain."),
            });
        }
        else
        {
            Assert.Equal(value, parameter.Value);
        }

        if (value is ulong)
        {
            Assert.Equal(SqlDbType.Decimal, parameter.SqlDbType);
            Assert.True(parameter.Precision >= 20);
            Assert.Equal((byte)0, parameter.Scale);
            Assert.Equal((decimal)ulong.MaxValue, Assert.IsType<decimal>(parameter.Value));
        }
    }

    /// <summary>Preserves source bits before destination conversion can narrow their domain.</summary>
    /// <param name="value">The complete authored source.</param>
    /// <param name="storeType">The lossless source store type.</param>
    /// <param name="sqlType">The corresponding native parameter type.</param>
    [Theory]
    [MemberData(nameof(LosslessSources))]
    public void SourceBinding_PreservesValueAndSourceFacets(object value, string storeType, SqlDbType sqlType)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var bindings = new SqlServerCatalogParameterBindings(context.GetService<IRelationalTypeMappingSource>(), 31);

        // Act
        var marker = bindings.Add(value);
        var captured = bindings.Values[0];
        using var command = new SqlCommand();
        var parameter = (SqlParameter)captured.Mapping.CreateParameter(command, captured.ExternalName,
            captured.Value, nullable: false);

        // Assert
        Assert.Single(bindings.Values);
        Assert.Equal("@doka_value0", marker);
        Assert.Equal("@doka_source31_0", parameter.ParameterName);
        Assert.Equal(storeType, captured.Mapping.StoreType);
        Assert.Equal(sqlType, parameter.SqlDbType);
        Assert.Equal(value, parameter.Value);
        Assert.True(captured.PayloadBytes > 0);
        if (value is decimal)
        {
            Assert.Equal((byte)38, parameter.Precision);
            Assert.Equal((byte)captured.Mapping.Scale!.Value, parameter.Scale);
        }
    }

    /// <summary>Retains complete source strings and bytes beyond bounded destination widths.</summary>
    /// <param name="binary">Whether the authored source is binary rather than Unicode text.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WideSource_IsNotSizedToTheDestination(bool binary)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var bindings = new SqlServerCatalogParameterBindings(context.GetService<IRelationalTypeMappingSource>(), 0);
        object source = binary ? new byte[9000] : new string('\u20ac', 9000);

        // Act
        bindings.Add(source);
        var captured = bindings.Values[0];
        using var command = new SqlCommand();
        var parameter = (SqlParameter)captured.Mapping.CreateParameter(command, captured.ExternalName,
            captured.Value, nullable: false);

        // Assert
        Assert.Single(bindings.Values);
        Assert.Equal(-1, parameter.Size);
        Assert.Same(source, captured.Value);
        Assert.True(captured.PayloadBytes >= (binary ? 9000 : 18000));
    }

    /// <summary>Preserves all TimeOnly ticks through the provider's native time conversion.</summary>
    [Fact]
    public void TimeOnlySource_PreservesAllTicksAtFullTimePrecision()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var value = new TimeOnly(123456789);
        var bindings = new SqlServerCatalogParameterBindings(context.GetService<IRelationalTypeMappingSource>(), 0);

        // Act
        bindings.Add(value);
        var captured = bindings.Values[0];
        using var command = new SqlCommand();
        var parameter = (SqlParameter)captured.Mapping.CreateParameter(command, captured.ExternalName,
            captured.Value, nullable: false);

        // Assert
        Assert.Single(bindings.Values);
        Assert.Equal("time(7)", captured.Mapping.StoreType);
        Assert.Equal(SqlDbType.Time, parameter.SqlDbType);
        Assert.Equal(value.Ticks, parameter.Value switch
        {
            TimeOnly time => time.Ticks,
            TimeSpan time => time.Ticks,
            _ => throw new InvalidOperationException("The source mapping changed the time domain."),
        });
    }

    /// <summary>Shares a repeated source binding locally without conflating differing decimal scales.</summary>
    [Fact]
    public void RepeatedSource_ReusesOnlyAnEqualSourceMapping()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var bindings = new SqlServerCatalogParameterBindings(context.GetService<IRelationalTypeMappingSource>(), 71);

        // Act
        var first = bindings.Add(1.0m);
        var repeated = bindings.Add(1.0m, external: true);
        var finer = bindings.Add(1.000m);

        // Assert
        Assert.Equal("@doka_value0", first);
        Assert.Equal("@doka_source71_0", repeated);
        Assert.Equal("@doka_value1", finer);
        Assert.Equal(["decimal(38,1)", "decimal(38,3)"], bindings.Values.Select(value => value.Mapping.StoreType));
    }

    /// <summary>Floating-point signed zero retains distinct authored scalar bits.</summary>
    /// <param name="useReducedPrecision">Whether the source uses 32-bit rather than 64-bit precision.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignedZero_DoesNotMergeSourceBits(bool useReducedPrecision)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var bindings = new SqlServerCatalogParameterBindings(context.GetService<IRelationalTypeMappingSource>(), 0);
        var positive = useReducedPrecision ? (object)0.0f : 0.0d;
        var negative = useReducedPrecision ? (object)BitConverter.Int32BitsToSingle(int.MinValue)
            : BitConverter.Int64BitsToDouble(long.MinValue);

        // Act
        var first = bindings.Add(positive);
        var second = bindings.Add(negative);
        var repeated = bindings.Add(negative);

        // Assert
        Assert.Equal(2, bindings.Values.Count);
        Assert.Equal("@doka_value0", first);
        Assert.Equal("@doka_value1", second);
        Assert.Equal(second, repeated);
        Assert.Same(positive, bindings.Values[0].Value);
        Assert.Same(negative, bindings.Values[1].Value);
    }

    /// <summary>Equal UTC instants do not merge different authored datetimeoffset representations.</summary>
    [Fact]
    public void EqualUtcInstant_DistinctOffsetsKeepSeparateSourceBindings()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var bindings = new SqlServerCatalogParameterBindings(context.GetService<IRelationalTypeMappingSource>(), 19);
        var source = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var target = source.ToOffset(TimeSpan.FromHours(1));

        // Act
        var sourceName = bindings.Add(source);
        var targetName = bindings.Add(target);

        // Assert
        Assert.True(source.Equals(target));
        Assert.False(source.EqualsExact(target));
        Assert.Equal("@doka_value0", sourceName);
        Assert.Equal("@doka_value1", targetName);
        Assert.Equal(2, bindings.Values.Count);
        Assert.True(source.EqualsExact((DateTimeOffset)bindings.Values[0].Value));
        Assert.True(target.EqualsExact((DateTimeOffset)bindings.Values[1].Value));
    }

    /// <summary>Offset-changing seed transitions retain separate inner SQL operands for source and target.</summary>
    [Fact]
    public void OffsetTransition_KeepsDistinctInnerSourceAndTargetMarkers()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var source = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var target = source.ToOffset(TimeSpan.FromHours(1));
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel("offset_bindings", ["Id"], ["int"],
            new object?[,] { { 1 } }, ["Zoned"], ["datetimeoffset(7)"],
            new object?[,] { { source } }, new object?[,] { { target } });

        // Act
        var plan = BuildManagedPlan(context, 71, (SafeMigrationOperation)builder.Operations[0]);
        var sql = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(71, plan);

        // Assert
        Assert.Equal(3, plan.AnalysisParameters.Count);
        Assert.Contains("CAST(@doka_value1 AS datetimeoffset(7))", DelayedInner(sql), StringComparison.Ordinal);
        Assert.Contains("CAST(@doka_value2 AS datetimeoffset(7))", DelayedInner(sql), StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_source", DelayedInner(sql), StringComparison.Ordinal);
        Assert.True(source.EqualsExact((DateTimeOffset)plan.AnalysisParameters[1].Value));
        Assert.True(target.EqualsExact((DateTimeOffset)plan.AnalysisParameters[2].Value));
    }

    /// <summary>Rejects invalid capture identity or null scalars before capturing parameters.</summary>
    [Fact]
    public void InvalidSourceCapture_RejectsWithoutBindings()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var bindings = new SqlServerCatalogParameterBindings(mappings, 0);

        // Act
        var negativeOrdinal = Record.Exception(() => new SqlServerCatalogParameterBindings(mappings, -1));
        var nullSource = Record.Exception(() => bindings.Add(null!));

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(negativeOrdinal);
        Assert.IsType<ArgumentNullException>(nullSource);
        Assert.Empty(bindings.Values);
    }

    /// <summary>Uses identical delayed templates for differing row keys without sharing their result identity.</summary>
    [Fact]
    public void DistinctKeys_ShareStableInnerSqlButRetainIndependentBindings()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var first = BuildUpdate(context, 7, 1);
        var second = BuildUpdate(context, 8, 2);

        // Act
        var firstSql = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(7, first);
        var secondSql = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogSelection(8, second);

        // Assert
        Assert.Equal(DelayedInner(firstSql), DelayedInner(secondSql));
        Assert.NotEqual(first, second);
        Assert.Equal(1, first.AnalysisParameters[0].Value);
        Assert.Equal(2, second.AnalysisParameters[0].Value);
        Assert.Contains("@doka_value0 = @doka_source7_0", firstSql, StringComparison.Ordinal);
        Assert.Contains("@doka_value0 = @doka_source8_0", secondSql, StringComparison.Ordinal);
        Assert.Contains("@doka_value", first.AnalysisOuterStateGuardExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_source7_", first.StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_source", DelayedInner(firstSql), StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_source", DelayedInner(secondSql), StringComparison.Ordinal);
        Assert.Contains("@doka_value", first.StateEvaluationGuardExpression, StringComparison.Ordinal);
    }

    /// <summary>Different parameter payloads cannot reuse a classification merely because SQL templates match.</summary>
    /// <param name="nativeBatch">Whether the connection exposes native batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DistinctKeys_DoNotCoalesceResults(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var plans = new[] { BuildUpdate(context, 0, 1), BuildUpdate(context, 1, 2) };
        var results = new SafeMigrationProviderAnalysis[2];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(connection, null, 93,
            plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, results[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, results[1].ObservedState);
        var parameters = Assert.Single(connection.RecordedParameters);
        Assert.Contains(parameters, parameter => parameter.ParameterName == "@doka_source0_0"
            && Equals(parameter.Value, 1));
        Assert.Contains(parameters, parameter => parameter.ParameterName == "@doka_source1_0"
            && Equals(parameter.Value, 2));
        Assert.Equal(93, Assert.Single(connection.ObservedTimeouts));
    }

    /// <summary>A complete source budget remains admissible without expanding the transport parameter cap.</summary>
    /// <param name="nativeBatch">Whether transport uses native ADO.NET batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdinalBinding_PreservesAFullSourceParameterBudget(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var plan = ParameterizedConstantPlan(mappings, 0, 2000);
        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, [plan], 0, results, CancellationToken.None);

        // Assert
        var statement = Assert.Single(connection.RecordedStatements);
        Assert.Contains("@doka_ordinal = 0,", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_ordinal = @doka_ordinal0", statement, StringComparison.Ordinal);
        Assert.Contains("@doka_ordinal = @doka_ordinal,", statement, StringComparison.Ordinal);
        var parameters = Assert.Single(connection.RecordedParameters);
        Assert.Equal(2000, parameters.Length);
        Assert.DoesNotContain(parameters, static parameter => parameter.ParameterName.StartsWith(
            "@doka_ordinal", StringComparison.Ordinal));
        Assert.Equal(SafeMigrationObservedState.Missing, results[0].ObservedState);
    }

    /// <summary>A real managed-data operation retains every source at the complete transport parameter boundary.</summary>
    /// <param name="nativeBatch">Whether transport uses native ADO.NET batching.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MaximumManagedSourceCapture_PreservesAllAuthoredValues(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var columns = Enumerable.Range(0, 16).Select(index => index == 0 ? "Id"
            : "Value" + index.ToString(CultureInfo.InvariantCulture)).ToArray();

        var values = new object?[125, columns.Length];
        for (var row = 0; row < values.GetLength(0); row++)
        {
            for (var column = 0; column < values.GetLength(1); column++)
            {
                values[row, column] = row * columns.Length + column;
            }
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("complete_source_capture", ["Id"], ["int"],
            columns, Enumerable.Repeat("int", columns.Length).ToArray(), values);

        var plan = BuildManagedPlan(context, 0, (SafeMigrationOperation)builder.Operations[0]);
        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection, null, null, [plan], 0, results, CancellationToken.None);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Equal(125, plan.ModelManagedRowCount);
        Assert.Equal(2000, plan.AnalysisParameters.Count);
        var parameters = Assert.Single(connection.RecordedParameters);
        Assert.Equal(2000, parameters.Length);
        Assert.Equal(Enumerable.Range(0, 2000), parameters.Select(parameter => Assert.IsType<int>(parameter.Value)));
        Assert.All(parameters, parameter =>
        {
            Assert.StartsWith("@doka_source0_", parameter.ParameterName, StringComparison.Ordinal);
            Assert.Equal(DbType.Int32, parameter.DbType);
        });
        var statement = Assert.Single(connection.RecordedStatements);
        Assert.Contains("@doka_ordinal = 0,", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO @doka_analysis", statement, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(statement)
            + plan.AnalysisParameters.Sum(parameter => parameter.PayloadBytes), 1,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
        Assert.Equal(SafeMigrationObservedState.Missing, results[0].ObservedState);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandExecutions);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
        Assert.Equal(nativeBatch ? 1 : 0, connection.ParameterFactoriesDisposed);
        Assert.All(connection.BatchPayloadBytes, bytes => Assert.InRange(bytes, 1,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
    }

    /// <summary>Splits parameter-heavy classifiers before SQL Server's RPC parameter ceiling.</summary>
    /// <param name="nativeBatch">Whether native batching is available.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParameterBudget_SplitsBeforeTheNativeCeiling(bool nativeBatch)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch);
        var mappings = context.GetService<IRelationalTypeMappingSource>();

        // WHY: Each classifier must carry enough parameters that the RPC ceiling, not the
        // statement-width bound, decides where the capture splits. Otherwise this test would
        // silently degrade into another width test when the shape is tuned.
        const int parametersPerPlan = 300;
        var plans = Enumerable.Range(0, 25)
            .Select(ordinal => ParameterizedConstantPlan(mappings, ordinal, parametersPerPlan))
            .ToArray();

        var results = new SafeMigrationProviderAnalysis[plans.Length];
        var operationsPerStatement = SqlServerCatalogParameterBindings.MaximumParameters / parametersPerPlan;
        var expectedStatements = (plans.Length + operationsPerStatement - 1) / operationsPerStatement;

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(connection, null, null,
            plans, 0, results, CancellationToken.None);

        // Assert
        Assert.InRange(operationsPerStatement, 1, SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement - 1);
        Assert.Equal(expectedStatements, connection.RecordedStatements.Count);
        Assert.All(
            connection.RecordedParameters,
            parameters => Assert.InRange(
                parameters.Length,
                1,
                SqlServerCatalogParameterBindings.MaximumParameters));

        // WHY: The parameter ceiling also bounds the whole batch, so a parameter-heavy
        // capture keeps one statement per transport batch instead of packing them.
        Assert.Equal(nativeBatch ? expectedStatements : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : expectedStatements, connection.CommandExecutions);
        Assert.Equal(Enumerable.Range(0, plans.Length).Select(index => (index % 3) switch
        {
            0 => SafeMigrationObservedState.Missing,
            1 => SafeMigrationObservedState.Matching,
            _ => SafeMigrationObservedState.Different,
        }), results.Select(analysis => analysis.ObservedState));
    }

    /// <summary>Includes source payload bytes when splitting otherwise-small SQL templates.</summary>
    [Fact]
    public async Task ParameterPayloadBudget_SplitsBeforeTheWireLimit()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        await using var connection = new SqlServerCatalogTestConnection();
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var plans = Enumerable.Range(0, 3).Select(ordinal =>
            ParameterizedConstantPlan(mappings, ordinal, 1, new string('x', 1_100_000))).ToArray();

        var results = new SafeMigrationProviderAnalysis[3];

        // Act
        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(connection, null, null,
            plans, 0, results, CancellationToken.None);

        // Assert
        Assert.Equal(3, connection.BatchExecutions);
        Assert.Equal(3, connection.RecordedParameters.Count);
        Assert.All(connection.RecordedParameters, parameters => Assert.Equal(2, parameters.Length));
        Assert.All(connection.BatchPayloadBytes, bytes => Assert.InRange(bytes, 1, 4 * 1024 * 1024));
    }

    /// <summary>Rejects a single oversized parameter capture rather than entering a non-progressing split loop.</summary>
    /// <param name="parameterOverflow">Whether count rather than source payload exceeds its limit.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedParameterCapture_RejectsBeforeExecution(bool parameterOverflow)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        await using var connection = new SqlServerCatalogTestConnection();
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var plan = ParameterizedConstantPlan(mappings, 0, parameterOverflow ? 2001 : 1,
            parameterOverflow ? null : new string('x', 2_100_000));

        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(connection, null, null,
                [plan], 0, results, CancellationToken.None));

        // Assert
        Assert.Contains("operation 0 exceeds a bounded query limit", failure.Message, StringComparison.Ordinal);
        Assert.Empty(connection.RecordedStatements);
        Assert.Equal(0, connection.BatchExecutions);
        Assert.Equal(1, connection.BatchesDisposed);
    }

    /// <summary>NULL remains a typed SQL NULL rather than an invented non-null source parameter.</summary>
    [Fact]
    public void NullSource_UsesTheExistingTypedNullPath()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var bindings = new SqlServerCatalogParameterBindings(mappings, 0);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("catalog_nulls", ["Id"], ["int"],
            ["Id", "Caption"], ["int", "nvarchar(80)"], new object?[,] { { 1, null } });

        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(mappings,
            context.GetService<ISqlGenerationHelper>(), sourceParameter: (value, _) => bindings.Add(value));

        // Act
        var plan = catalog.Build((SafeMigrationOperation)builder.Operations[0]);

        // Assert
        Assert.Equal(1, Assert.Single(bindings.Values).Value);
        Assert.Contains("CAST(NULL AS nvarchar(80))", plan.StateExpression, StringComparison.Ordinal);
        Assert.DoesNotContain(bindings.Values, value => value.Value is null);
    }

    /// <summary>Analysis bindings do not replace literal SQL in the migration execution contract.</summary>
    [Fact]
    public void RuntimeGeneration_RemainsLiteralAndUnparameterized()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel("catalog_bindings", ["Id"], ["int"],
            new object?[,] { { 17 } }, ["Caption"], ["nvarchar(80)"],
            new object?[,] { { "source" } }, new object?[,] { { "target" } });

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var sql = string.Join("\n", commands.Select(command => command.CommandText));

        // Assert
        Assert.Contains("CAST(17 AS int)", sql, StringComparison.Ordinal);
        Assert.Contains("source", sql, StringComparison.Ordinal);
        Assert.Contains("target", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_source", sql, StringComparison.Ordinal);
        // WHY: Runtime guards already own an unnumbered @doka_value output scalar. Only the
        // numbered source markers identify the new analysis parameterization boundary.
        Assert.DoesNotMatch(@"@doka_value\d+\b", sql);
    }

    /// <summary>Captures a synthetic one-row update using its original analysis ordinal.</summary>
    private static SqlServerSafeMigrationRuntimePlan BuildUpdate(SafeMigrationDbContext context, int ordinal, int key)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel("catalog_bindings", ["Id"], ["int"],
            new object?[,] { { key } }, ["Caption"], ["nvarchar(80)"],
            new object?[,] { { "source" } }, new object?[,] { { "target" } });

        return BuildManagedPlan(context, ordinal, (SafeMigrationOperation)builder.Operations[0]);
    }

    /// <summary>Creates local and outer parameter scopes for one managed-data analysis contract.</summary>
    private static SqlServerSafeMigrationRuntimePlan BuildManagedPlan(
        SafeMigrationDbContext context,
        int ordinal,
        SafeMigrationOperation operation
    )
    {
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var bindings = new SqlServerCatalogParameterBindings(mappings, ordinal);
        var helper = context.GetService<ISqlGenerationHelper>();
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(mappings, helper,
            sourceParameter: (value, _) => bindings.Add(value));

        var outerCatalog = new SqlServerSafeMigrationCatalogSqlBuilder(mappings, helper,
            sourceParameter: (value, _) => bindings.Add(value));

        var plan = catalog.Build(operation);
        var outerGuard = outerCatalog.BuildModelManagedDataAnalysisGuard((ModelManagedDataIntent)operation.Intent);

        return plan with
        {
            AnalysisParameters = bindings.Values,
            AnalysisOuterStateGuardExpression = outerGuard.Guard,
            AnalysisOuterStateGuardFailureExpression = outerGuard.Failure,
        };
    }

    /// <summary>Extracts the complete escaped classifier without its operation-specific outer guards.</summary>
    private static string DelayedInner(string sql)
    {
        const string prefix = "EXEC sys.sp_executesql N'";
        var start = sql.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = sql.LastIndexOf("', N'@doka_ordinal int", StringComparison.Ordinal);

        return sql[start..end];
    }

    /// <summary>Creates synthetic bound parameters for transport-limit tests without destination dependencies.</summary>
    private static SqlServerSafeMigrationRuntimePlan ParameterizedConstantPlan(
        IRelationalTypeMappingSource mappings,
        int ordinal,
        int count,
        object? source = null
    )
    {
        var bindings = new SqlServerCatalogParameterBindings(mappings, ordinal);
        for (var index = 0; index < count; index++)
        {
            bindings.Add(source ?? index);
        }

        return new SqlServerSafeMigrationRuntimePlan("N'missing'", "0", SafeMigrationRepairCapability.None, "0")
        {
            RequiresDelayedBinding = true,
            AnalysisParameters = bindings.Values,
        };
    }

    private enum BindingValue : long
    {
        Large = 4_294_967_295,
    }
}
