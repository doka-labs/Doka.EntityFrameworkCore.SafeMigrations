namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies bounded absence proofs without inferring schema equality or provider capabilities.</summary>
public sealed class SqlServerAbsentTableCaptureTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=absence_bindings;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Retains sparse local result positions and binds every target identity as a parameter.</summary>
    /// <param name="nativeBatch">Whether native batching is available.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedCapture_ReadsOnlyEnsureTablesAndRetainsOffsets(bool nativeBatch)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            ReturnPresenceRows = true,
            PresenceValue = ordinal => ordinal == 3 ? 0 : 1,
        };

        await using var transaction = await connection.BeginTransactionAsync();
        SafeMigrationOperation[] operations = [Column(), Column(), Table("first'item", "custom_schema"),
            Column(), Table("second"), Table("occupied"), Column()];

        // Act
        var absent = await SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
            connection, transaction, operations, 2, 5, 67, CancellationToken.None);

        // Assert
        Assert.Equal([true, false, true, false, false], absent);
        var sql = Assert.Single(connection.RecordedStatements);
        Assert.Contains("FROM sys.objects occupied", sql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN sys.schemas", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.tables", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("first'item", sql, StringComparison.Ordinal);
        Assert.Contains("(0, @schema0, @table0)", sql, StringComparison.Ordinal);
        Assert.Contains("(3, @schema2, @table2)", sql, StringComparison.Ordinal);
        var parameters = Assert.Single(connection.RecordedParameters);
        Assert.Equal(6, parameters.Length);
        Assert.Equal("custom_schema", parameters[0].Value);
        Assert.Equal("first'item", parameters[1].Value);
        Assert.Equal("dbo", parameters[2].Value);
        Assert.All(parameters, parameter =>
        {
            Assert.Equal(DbType.String, parameter.DbType);
            Assert.Equal(128, parameter.Size);
        });
        Assert.Equal(67, Assert.Single(connection.ObservedTimeouts));
        Assert.Same(transaction, Assert.Single(connection.ObservedTransactions));
        Assert.Equal(0, connection.TransactionsDisposed);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchExecutions);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandExecutions);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
        Assert.Equal(nativeBatch ? 1 : 0, connection.ParameterFactoriesDisposed);
    }

    /// <summary>Captures the full bounded plan window in one occupancy statement.</summary>
    [Fact]
    public async Task MaximumCapture_UsesOneBoundedParameterizedStatement()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection { ReturnPresenceRows = true };
        var operations = Enumerable.Range(0, 512).Select(index => Table("target_"
            + index.ToString(CultureInfo.InvariantCulture))).ToArray();

        // Act
        var absent = await SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
            connection, null, operations, 0, operations.Length, null, CancellationToken.None);

        // Assert
        Assert.Equal(512, absent.Length);
        Assert.All(absent, value => Assert.True(value));
        Assert.Single(connection.RecordedStatements);
        Assert.Equal(1024, Assert.Single(connection.RecordedParameters).Length);
        Assert.Equal(1, connection.BatchExecutions);
        Assert.InRange(Assert.Single(connection.BatchPayloadBytes), 1,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
    }

    /// <summary>Empty windows and captures without table ensures perform no database work.</summary>
    /// <param name="count">The number of column-only operations.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task EmptyOrUnrelatedCapture_PerformsNoTransport(int count)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection { ReturnPresenceRows = true };
        var operations = Enumerable.Range(0, count).Select(_ => Column()).ToArray();

        // Act
        var absent = await SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
            connection, null, operations, 0, count, null, CancellationToken.None);

        // Assert
        Assert.Equal(count, absent.Length);
        Assert.All(absent, value => Assert.False(value));
        Assert.Empty(connection.RecordedStatements);
        Assert.Empty(connection.RecordedParameters);
        Assert.Equal(0, connection.BatchExecutions);
    }

    /// <summary>Rejects an invalid window before creating a transport command.</summary>
    /// <param name="start">The requested window start.</param>
    /// <param name="count">The requested window length.</param>
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(0, 513)]
    [InlineData(1, 1)]
    public async Task InvalidWindow_RejectsBeforeExecution(int start, int count)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection { ReturnPresenceRows = true };

        // Act
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
                connection, null, [Table("target")], start, count, null, CancellationToken.None));

        // Assert
        Assert.Empty(connection.RecordedStatements);
        Assert.Equal(0, connection.BatchExecutions);
    }

    /// <summary>Malformed result ownership cannot certify a different table as absent.</summary>
    /// <param name="corruption">The result-stream violation.</param>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("unexpected")]
    [InlineData("extra")]
    [InlineData("reversed")]
    public async Task MalformedPresenceResults_RejectAndDispose(string corruption)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection
        {
            ReturnPresenceRows = true,
            TransformOrdinals = ordinals => corruption switch
            {
                "duplicate" => [ordinals[0], ordinals[0]],
                "missing" => [ordinals[0]],
                "unexpected" => [17, ordinals[1]],
                "extra" => [.. ordinals, 17],
                "reversed" => [ordinals[1], ordinals[0]],
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            },
        };

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
                connection, null, [Table("first"), Table("second")], 0, 2, null, CancellationToken.None));

        // Assert
        Assert.Equal(1, connection.BatchesDisposed);
        Assert.Equal(1, connection.BatchExecutions);
        Assert.Equal(0, connection.CommandsDisposed);
        Assert.Equal(1, connection.ParameterFactoriesDisposed);
    }

    /// <summary>Provider transport failures release resources without manufacturing an absence proof.</summary>
    /// <param name="nativeBatch">Whether native batching is available.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedPresenceTransport_DisposesWithoutRetainingAProof(bool nativeBatch)
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            ReturnPresenceRows = true,
            ThrowOnExecute = true,
        };

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
                connection, null, [Table("target")], 0, 1, 51, CancellationToken.None));

        // Assert
        Assert.Equal("Injected catalog execution failure.", failure.Message);
        Assert.Equal(51, Assert.Single(connection.ObservedTimeouts));
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
        Assert.Equal(nativeBatch ? 1 : 0, connection.ParameterFactoriesDisposed);
    }

    /// <summary>Cancellation before preparation performs no occupancy query.</summary>
    [Fact]
    public async Task AlreadyCancelledCapture_DoesNotPrepareOrExecute()
    {
        // Arrange
        await using var connection = new SqlServerCatalogTestConnection { ReturnPresenceRows = true };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
                connection, null, [Table("target")], 0, 1, null, cancellation.Token));

        // Assert
        Assert.Empty(connection.RecordedStatements);
        Assert.Equal(0, connection.BatchesDisposed);
    }

    /// <summary>Cancellation during occupancy reading releases transport resources but retains caller ownership.</summary>
    /// <param name="nativeBatch">Whether native batching is available.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledRead_DisposesTransportAndPreservesTransaction(bool nativeBatch)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            ReturnPresenceRows = true,
            AfterRowRead = _ => cancellation.Cancel(),
        };

        await using var transaction = await connection.BeginTransactionAsync();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
                connection, transaction, [Table("first"), Table("second")], 0, 2, 81, cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, connection.CancellationTokenSeen);
        Assert.Same(transaction, Assert.Single(connection.ObservedTransactions));
        Assert.Equal(0, connection.TransactionsDisposed);
        Assert.Equal(nativeBatch ? 1 : 0, connection.BatchesDisposed);
        Assert.Equal(nativeBatch ? 0 : 1, connection.CommandsDisposed);
        Assert.Equal(nativeBatch ? 1 : 0, connection.ParameterFactoriesDisposed);
    }

    /// <summary>Absence avoids full matching but keeps every authored support and prerequisite guard.</summary>
    [Fact]
    public void KnownAbsentTable_RetainsGuardsAndRejectsAnyLaterOccupant()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var definition = new ExpectedTableDefinition("guarded_target",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("ParentId", typeof(int), true, "int"),
                new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(80)",
                    defaultValue: SafeMigrationDefaultValue.Literal("default"),
                    collation: new SafeMigrationCollationIdentifier("Latin1_General_100_CI_AS"))],
            foreignKeys: [new ExpectedForeignKeyDefinition("FK_guarded_target_parent", "guarded_target", ["ParentId"],
                "guarded_parent", ["Id"])]);

        var operation = new SafeMigrationOperation(new EnsureTableIntent(definition,
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var full = catalog.Build(operation);
        var absent = catalog.Build(operation, targetTableKnownAbsent: true);

        // Assert
        Assert.Equal(full.PhysicalTableSupportExpression, absent.PhysicalTableSupportExpression);
        Assert.Equal(full.DefaultValueSupportExpression, absent.DefaultValueSupportExpression);
        Assert.Equal(full.ColumnCollationSupportExpression, absent.ColumnCollationSupportExpression);
        Assert.Equal(full.PrerequisiteExpression, absent.PrerequisiteExpression);
        Assert.Equal(full.CatalogPreambleSql, absent.CatalogPreambleSql);
        Assert.Equal(full.ClassificationCodeExpression, absent.ClassificationCodeExpression);
        Assert.Contains("inline_foreign_key_prerequisite", absent.ClassificationCodeExpression,
            StringComparison.Ordinal);
        Assert.Contains("WHEN 1 = 0 THEN N'matching' ELSE N'different'", absent.StateExpression,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT COUNT(*) FROM sys.columns", absent.StateExpression, StringComparison.Ordinal);
        Assert.Equal("CASE WHEN 1 = 0 THEN 1 ELSE 0 END", absent.Postcondition);
    }

    /// <summary>Absence cannot bypass static admission of an impossible table contract.</summary>
    [Fact]
    public void KnownAbsentTable_PreservesStaticUnsupportedAdmission()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var definition = new ExpectedTableDefinition("invalid_target",
            [new ExpectedColumnDefinition("Id", typeof(int), true, "int")],
            primaryKey: new ExpectedPrimaryKeyDefinition("PK_invalid_target", "invalid_target", ["Id"]));

        var operation = new SafeMigrationOperation(new EnsureTableIntent(definition,
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var absent = catalog.Build(operation, targetTableKnownAbsent: true);

        // Assert
        Assert.True(absent.IsStaticallyUnsupported);
        Assert.Equal("primary_key_nullable_column", absent.UnsupportedCode);
    }

    /// <summary>Creates an ordinary target table ensure for a synthetic catalog identity.</summary>
    private static SafeMigrationOperation Table(string name, string? schema = null)
        => new(new EnsureTableIntent(new ExpectedTableDefinition(name,
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], schema: schema),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Creates an unrelated column operation that must not participate in the occupancy prepass.</summary>
    private static SafeMigrationOperation Column()
        => new(new EnsureColumnIntent("columns", new ExpectedColumnDefinition("Id", typeof(int), false, "int")),
            SafeMigrationPolicy.ThrowIfDifferent);
}
