namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies SQL Server's source-frozen model-managed data SQL without a live server.
/// </summary>
public sealed class SqlServerModelManagedDataSqlTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=composition;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Requires exact stored value bytes despite case-insensitive database collations.
    /// </summary>
    [Fact]
    public void Ensure_UsesTypedValuesAndExactStoredContentComparison()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var intent = new EnsureModelManagedDataIntent(
            "roles", ["Id"], ["int"], ["Id", "Caption"], ["int", "nvarchar(80)"],
            new object?[,] { { 1, "Administrator" } }, null, null);

        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var mutation = catalog.BuildModelManagedDataMutationSql(intent);

        // Assert
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains("compatibility_level", plan.StateEvaluationGuardExpression,
            StringComparison.Ordinal);
        Assert.Contains("N'unsupported'", plan.StateEvaluationGuardFailureExpression,
            StringComparison.Ordinal);
        Assert.Contains("CONVERT(varbinary(max), doka_actual.[Caption])", plan.StateExpression,
            StringComparison.Ordinal);
        Assert.Contains("DATALENGTH(doka_actual.[Caption])", plan.StateExpression,
            StringComparison.Ordinal);
        Assert.Contains("CAST(", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO [dbo].[roles]", mutation, StringComparison.Ordinal);
        Assert.Contains("WHERE NOT EXISTS", mutation, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE doka_actual", mutation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Renders character, GUID, and binary seed values with their SQL Server store types.
    /// </summary>
    [Fact]
    public void Ensure_RendersTypedCharacterGuidAndBinaryValues()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var intent = new EnsureModelManagedDataIntent(
            "typed_roles", ["Id"], ["int"],
            ["Id", "Marker", "ExternalId", "Payload"],
            ["int", "char(1)", "uniqueidentifier", "varbinary(16)"],
            new object?[,] { { 1, 'A', Guid.Parse("00000000-0000-0000-0000-000000000001"),
                new byte[] { 0x01, 0x02 } } }, null, null);

        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var mutation = catalog.BuildModelManagedDataMutationSql(intent);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("AS char(1)", plan.StateExpression, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS uniqueidentifier", plan.StateExpression, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS varbinary(16)", mutation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rejects source drift while allowing only the captured source-to-target transition.
    /// </summary>
    [Fact]
    public void Update_RequiresSourceOrTargetAndChecksTargetAfterMutation()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var intent = new UpdateModelManagedDataIntent(
            "roles", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Caption"], ["nvarchar(80)"],
            new object?[,] { { "Old" } }, new object?[,] { { "New" } }, null, null);

        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var mutation = catalog.BuildModelManagedDataMutationSql(intent);

        // Assert
        Assert.Contains("N'prerequisite_missing'", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("N'different'", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("N'transition_ready'", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("doka_expected.[o0]", mutation, StringComparison.Ordinal);
        Assert.Contains("doka_expected.[n0]", mutation, StringComparison.Ordinal);
        Assert.Contains("UPDATE doka_actual", mutation, StringComparison.Ordinal);
        Assert.Contains("doka_expected.[n0]", plan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects unmodeled incoming foreign keys and counts modeled dependencies.
    /// </summary>
    [Fact]
    public void Delete_RequiresExactSourceAndIncomingDependencyEvidence()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var intent = new DeleteModelManagedDataIntent(
            "roles", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Id", "Caption"], ["int", "nvarchar(80)"],
            new object?[,] { { 1, "Old" } }, null,
            [new ExpectedModelManagedDataForeignKeyDefinition("user_roles", ["RoleId"], ["Id"])]);

        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var mutation = catalog.BuildModelManagedDataMutationSql(intent);

        // Assert
        Assert.Equal(1, plan.ModelManagedDependencyCount);
        Assert.Contains("sys.foreign_keys", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("N'unsupported'", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("N'data_blocked'", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("COUNT_BIG(*)", plan.ModelManagedDependencyCountsExpression,
            StringComparison.Ordinal);
        Assert.Contains("DELETE doka_actual", mutation, StringComparison.Ordinal);
        Assert.Contains("doka_expected.[o1]", mutation, StringComparison.Ordinal);
    }

    /// <summary>Preserves every dependency count without scalar concatenation arity or length limits.</summary>
    /// <param name="count">The number of independently modeled incoming foreign keys.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(254)]
    public void Delete_DependencyCountsHandleEmptySingleAndLargeContracts(
        int count
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var foreignKeys = Enumerable.Range(0, count).Select(index =>
            new ExpectedModelManagedDataForeignKeyDefinition(
                "dependent_" + index.ToString(CultureInfo.InvariantCulture), ["RoleId"], ["Id"])).ToArray();

        var intent = new DeleteModelManagedDataIntent(
            "roles", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Id"], ["int"], new object?[,] { { 1 } }, null, foreignKeys);

        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        var expression = Assert.IsType<string>(plan.ModelManagedDependencyCountsExpression);
        Assert.Equal(count, plan.ModelManagedDependencyCount);
        Assert.DoesNotContain("CONCAT_WS", expression, StringComparison.OrdinalIgnoreCase);
        if (count == 0)
        {
            Assert.Equal("N''", expression);

            return;
        }

        Assert.StartsWith("(CONVERT(varchar(max), ", expression, StringComparison.Ordinal);
        Assert.Equal(count, expression.Split("COUNT_BIG(*)", StringSplitOptions.None).Length - 1);
        Assert.Equal(count - 1, expression.Split(" + ',' + ", StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// Blocks source batches whose distinct CLR values collide in a database collation.
    /// </summary>
    [Fact]
    public void EnsureMultipleRows_ChecksLiveCollationForExpectedKeyCollision()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var intent = new EnsureModelManagedDataIntent(
            "roles", ["Code"], ["nvarchar(40)"], ["Code"], ["nvarchar(40)"],
            new object?[,] { { "Foo" }, { "foo" } }, null, null);

        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.Contains("doka_expected.[r] < doka_other.[r]", plan.StateExpression,
            StringComparison.Ordinal);
        Assert.Contains("doka_expected.[k0] = doka_other.[k0]", plan.StateExpression,
            StringComparison.Ordinal);
        Assert.Contains("N'data_blocked'", plan.StateExpression, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects unproven provider store types before generating a mutation.
    /// </summary>
    [Fact]
    public void UnsupportedStoreType_IsClassifiedBeforeDmlGeneration()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var intent = new EnsureModelManagedDataIntent(
            "locations", ["Id"], ["int"], ["Id", "Shape"], ["int", "geography"],
            new object?[,] { { 1, "POINT(0 0)" } }, null, null);

        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("model_managed_store_type", plan.UnsupportedCode);
    }

    /// <summary>
    /// Retains a client cleanup command around generated ensure-data SQL, including quoted names.
    /// </summary>
    [Fact]
    public void Ensure_GeneratesClientIdentityInsertRecoveryCommand()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "O'Brien", ["Id"], ["int"], ["Id"], ["int"], new object?[,] { { 7 } });

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var identityCommands = commands.OfType<SqlServerSafeMigrationIdentityInsertCommand>().ToArray();
        var guardedBodies = identityCommands.Select(static command =>
            SqlServerGuardedSqlTestContract.DecodeScope(command.CommandText)).ToArray();

        // Assert
        Assert.Single(identityCommands);
        var guardedBody = Assert.Single(guardedBodies);

        Assert.Contains("IDENTITY_INSERT [dbo].[O''Brien] ON", guardedBody, StringComparison.Ordinal);
        Assert.Contains("IDENTITY_INSERT [dbo].[O''Brien] OFF", guardedBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects values that a narrowing SQL CAST would otherwise silently truncate.
    /// </summary>
    [Theory]
    [InlineData("char(5)")]
    [InlineData("varchar(5)")]
    [InlineData("nchar(5)")]
    [InlineData("nvarchar(5)")]
    [InlineData("binary(5)")]
    [InlineData("varbinary(5)")]
    public void BoundedPayload_RejectsOverlongValuesBeforeSqlGeneration(string storeType)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        object value = storeType.Contains("binary", StringComparison.Ordinal)
            ? new byte[] { 1, 2, 3, 4, 5, 6 }
            : "abcdef";

        var intent = new EnsureModelManagedDataIntent(
            "bounded_payloads", ["Id"], ["int"], ["Id", "Payload"], ["int", storeType],
            new object?[,] { { 1, value } }, null, null);

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(operation);
        var generationFailure = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        var mutationFailure = Record.Exception(() => catalog.BuildModelManagedDataMutationSql(intent));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("model_managed_value_size", plan.UnsupportedCode);
        Assert.IsType<NotSupportedException>(generationFailure);
        Assert.IsType<NotSupportedException>(mutationFailure);
    }

    /// <summary>
    /// Accepts payloads exactly at their string or binary boundary without changing their bytes.
    /// </summary>
    [Theory]
    [InlineData("char(5)")]
    [InlineData("varchar(5)")]
    [InlineData("nchar(5)")]
    [InlineData("nvarchar(5)")]
    [InlineData("binary(5)")]
    [InlineData("varbinary(5)")]
    public void BoundedPayload_AcceptsExactMaximumLength(string storeType)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        object value = storeType.Contains("binary", StringComparison.Ordinal)
            ? new byte[] { 1, 2, 3, 4, 5 }
            : "abcde";

        var intent = new EnsureModelManagedDataIntent(
            "bounded_payloads", ["Id"], ["int"], ["Id", "Payload"], ["int", storeType],
            new object?[,] { { 1, value } }, null, null);

        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var mutation = catalog.BuildModelManagedDataMutationSql(intent);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("AS " + storeType, mutation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies the same bounded-value proof to captured source and target update matrices.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateBoundedPayload_RejectsOverlongSourceOrTarget(bool sourceOverlong)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var intent = new UpdateModelManagedDataIntent(
            "bounded_payloads", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Payload"], ["nvarchar(5)"],
            new object?[,] { { sourceOverlong ? "abcdef" : "abcde" } },
            new object?[,] { { sourceOverlong ? "ABCDE" : "ABCDEF" } }, null, null);

        // Act
        var plan = CreateCatalog(context)
            .Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));

        // Assert
        Assert.True(plan.IsStaticallyUnsupported);
        Assert.Equal("model_managed_value_size", plan.UnsupportedCode);
    }

    /// <summary>
    /// Preserves Unicode text until a live ANSI code-page and encoded-byte-length proof succeeds.
    /// </summary>
    [Fact]
    public void AnsiNonAsciiPayload_RequiresLosslessLiveConversion()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var intent = new EnsureModelManagedDataIntent(
            "ansi_payloads", ["Id"], ["int"], ["Id", "Payload"], ["int", "varchar(5)"],
            new object?[,] { { 1, "\u4E2D" } }, null, null);

        var catalog = CreateCatalog(context);

        // Act
        var plan = catalog.Build(new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent));
        var mutation = catalog.BuildModelManagedDataMutationSql(intent);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("CONVERT(varchar(max)", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("CONVERT(nvarchar(max)", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("DATALENGTH", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("DATABASEPROPERTYEX", plan.StateEvaluationGuardExpression, StringComparison.Ordinal);
        Assert.Contains("CAST(N'", mutation, StringComparison.Ordinal);
    }

    private static SqlServerSafeMigrationCatalogSqlBuilder CreateCatalog(DbContext context)
        => new(
            context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource>(),
            context.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>());
}
