namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the Diagnostics migration contracts in an independent class collection.</summary>
public sealed class MySqlDiagnosticsIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlDiagnosticsIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Reports foreign-key action drift separately from index-shape drift.</summary>
    [Fact]
    public async Task PreflightDiagnostics_DistinguishForeignKeyActionsAndIndexShape()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `diagnostic_principal` (`id` int NOT NULL, PRIMARY KEY (`id`));
            CREATE TABLE `diagnostic_dependent` (`id` int NOT NULL,
            `principal_id` int NULL, `first_value` varchar(40) NULL, `second_value`
            varchar(40) NULL,
            PRIMARY KEY (`id`), CONSTRAINT `fk_diagnostic_dependent_principal` FOREIGN
            KEY (`principal_id`) REFERENCES `diagnostic_principal` (`id`) ON UPDATE
            CASCADE ON DELETE CASCADE,
            INDEX `ix_diagnostic_dependent_values` (`second_value`(4), `first_value`));
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureForeignKey(
            new ExpectedForeignKeyDefinition(
                "fk_diagnostic_dependent_principal",
                "diagnostic_dependent",
                ["principal_id"],
                "diagnostic_principal",
                ["id"],
                onUpdate: ReferentialAction.NoAction,
                onDelete: ReferentialAction.Restrict),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_diagnostic_dependent_values",
                "diagnostic_dependent",
                [
                    new ExpectedIndexKeyDefinition("first_value", prefixLength: 5),
                    new ExpectedIndexKeyDefinition("second_value"),
                ]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("typed-diagnostics"),
                CancellationToken.None);

        // Assert
        var foreignKey = Assert.Single(
            report.Assessments,
            assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureForeignKey);

        var index = Assert.Single(
            report.Assessments,
            assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureIndex);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, foreignKey.ObservedState);
        Assert.Contains(foreignKey.Differences, difference => difference.Facet == "foreign_key_delete_behavior");
        Assert.Contains(foreignKey.Differences, difference => difference.Facet == "foreign_key_update_behavior");
        Assert.Equal(SafeMigrationObservedState.Different, index.ObservedState);
        Assert.Contains(index.Differences, difference => difference.Facet == "index_key_order");
        Assert.Contains(index.Differences, difference => difference.Facet == "index_prefix_length");
    }

    /// <summary>Bounds composite identity diagnostics without leaking unbounded catalog values.</summary>
    [Fact]
    public async Task PreflightDiagnostics_BoundLongCompositeIdentityValues()
    {
        // Arrange
        var principalColumns = Enumerable
            .Range(1, 8)
            .Select(ordinal => $"principal_column_{ordinal:00}_{new string('p', 24)}")

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var dependentColumns = Enumerable
            .Range(1, 8)
            .Select(ordinal => $"dependent_column_{ordinal:00}_{new string('d', 24)}")
            .ToArray();

        var principalDefinitions = string.Join(
            ", ",
            principalColumns.Select(column => $"`{column}` int NOT NULL"));

        var dependentDefinitions = string.Join(
            ", ",
            dependentColumns.Select(column => $"`{column}` int NOT NULL"));

        var reversedPrincipalKeys = string.Join(", ",
            principalColumns
                .Reverse()
                .Select(column => $"`{column}`"));

        var reversedDependentKeys = string.Join(", ",
            dependentColumns
                .Reverse()
                .Select(column => $"`{column}`"));

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            $"""
            CREATE TABLE `long_diagnostic_principal` ({principalDefinitions},
            PRIMARY KEY ({reversedPrincipalKeys})); CREATE TABLE `long_diagnostic_dependent`
            (`id` int NOT NULL,
            {dependentDefinitions}, PRIMARY KEY (`id`), CONSTRAINT `fk_long_diagnostic`
            FOREIGN KEY ({reversedDependentKeys}) REFERENCES `long_diagnostic_principal`
            ({reversedPrincipalKeys}),
            INDEX `ix_long_diagnostic` ({reversedDependentKeys}));
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureForeignKey(
            new ExpectedForeignKeyDefinition(
                "fk_long_diagnostic",
                "long_diagnostic_dependent",
                dependentColumns,
                "long_diagnostic_principal",
                principalColumns),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_long_diagnostic",
                "long_diagnostic_dependent",
                dependentColumns.Select(column => new ExpectedIndexKeyDefinition(column: column))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("long-typed-diagnostics"),
                CancellationToken.None);

        var differences = report
            .Assessments
            .SelectMany(static assessment => assessment.Differences)
            .Where(static difference => difference.Facet is "foreign_key_column_order" or "index_key_order")

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(2, differences.Length);
        Assert.All(
            differences,
            static difference =>
            {
                Assert.StartsWith("sha256:", difference.Expected, StringComparison.Ordinal);
                Assert.StartsWith("sha256:", difference.Actual, StringComparison.Ordinal);
                Assert.NotEqual(difference.Expected, difference.Actual);
                Assert.InRange(difference.Expected.Length, 1, 256);
                Assert.InRange(difference.Actual.Length, 1, 256);
            });
    }

    /// <summary>
    /// Distinguishes default values through hashes without exposing the values in reports or exceptions.
    /// </summary>
    [Fact]
    public async Task PreflightDiagnostics_HashDifferentDefaultsWithoutDisclosingTheirContent()
    {
        // Arrange
        const string liveDefault = "private-live-default";
        const string targetDefault = "private-target-default";

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `diagnostic_defaults` (`value` varchar(40) NULL DEFAULT '{liveDefault}');",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "diagnostic_defaults",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(40)",
                maxLength: 40,
                defaultValue: SafeMigrationDefaultValue.Literal(targetDefault)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("private-default-diagnostic"),
                CancellationToken.None);

        var json = Encoding.UTF8.GetString(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        var failure = Record.Exception(report.ThrowIfBlocked);

        // Assert
        var exception = Assert.IsType<SafeMigrationPreflightException>(failure);

        var assessment = Assert.Single(report.Assessments);
        var difference = Assert.Single(
            assessment.Differences,
            static candidate => candidate.Facet == "column_default_digest");

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.DoesNotContain(assessment.Differences, static candidate => candidate.Facet == "column_default_kind");
        Assert.StartsWith("sha256:", difference.Expected, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", difference.Actual, StringComparison.Ordinal);
        Assert.NotEqual(difference.Expected, difference.Actual);
        Assert.DoesNotContain(liveDefault, json, StringComparison.Ordinal);
        Assert.DoesNotContain(targetDefault, json, StringComparison.Ordinal);
        Assert.DoesNotContain(liveDefault, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(targetDefault, exception.Message, StringComparison.Ordinal);
    }
}
