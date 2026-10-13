namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies fail-closed selection before live required-add staging ownership probes.</summary>
public sealed class MySqlRequiredColumnAddRecoveryCandidateTests
{
    /// <summary>An exact guard binds the raw contract while physical lookup normalizes the selected database.</summary>
    /// <param name="schema">The authored qualification, or null for the selected database.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("selected")]
    public void ExactGuardSelectsOriginalOperation(string? schema)
    {
        // Arrange
        var operation = Operation(schema: schema);
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation];
        SafeMigrationUnexpectedObject[] findings =
        [
            new(SafeMigrationDatabaseObjectKind.Column, null, "items", "other", "unexpected_column"),
            Check(guard),
        ];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected");

        // Assert
        var candidate = Assert.Single(candidates);
        Assert.Same(operation, candidate.Operation);
        Assert.Equal(1, candidate.FindingIndex);
    }

    /// <summary>A reserved-looking prefix or differently cased name is not exact ownership.</summary>
    /// <param name="uppercase">Whether to alter the exact guard's case rather than use a prefix lookalike.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookalikeGuardDoesNotAcquireCandidate(bool uppercase)
    {
        // Arrange
        var operation = Operation();
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation];
        SafeMigrationUnexpectedObject[] findings =
        [
            Check(uppercase ? guard.ToUpperInvariant() : "doka_sm_add1_nn_not_owned"),
        ];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>Other owners and other object families cannot borrow a pending column's guard.</summary>
    /// <param name="kind">The reported object family.</param>
    /// <param name="table">The reported owning table.</param>
    [Theory]
    [InlineData(SafeMigrationDatabaseObjectKind.CheckConstraint, "other_items")]
    [InlineData(SafeMigrationDatabaseObjectKind.CheckConstraint, "ITEMS")]
    [InlineData(SafeMigrationDatabaseObjectKind.CheckConstraint, null)]
    [InlineData(SafeMigrationDatabaseObjectKind.Index, "items")]
    public void UnrelatedInventoryObjectDoesNotAcquireCandidate(
        SafeMigrationDatabaseObjectKind kind,
        string? table
    )
    {
        // Arrange
        var operation = Operation();
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation];
        SafeMigrationUnexpectedObject[] findings =
        [
            new(kind, null, table, guard, "unexpected_object"),
        ];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>Foreign database qualification is rejected before any live ownership probe is considered.</summary>
    [Fact]
    public void ForeignQualifiedOperationDoesNotAcquireCandidate()
    {
        // Arrange
        var operation = Operation(schema: "foreign_database");
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation];
        SafeMigrationUnexpectedObject[] findings = [Check(guard, schema: "foreign_database")];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>Repeated physical targets remain ambiguous even when the first target matches the guard.</summary>
    /// <param name="column">The later target name, including case-insensitive column aliases.</param>
    /// <param name="nullable">Whether the later target would itself be ineligible for required-add staging.</param>
    [Theory]
    [InlineData("id", false)]
    [InlineData("ID", false)]
    [InlineData("id", true)]
    public void RepeatedPhysicalColumnDoesNotAcquireCandidate(string column, bool nullable)
    {
        // Arrange
        var operation = Operation();
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation, Operation(column, nullable), operation];
        SafeMigrationUnexpectedObject[] findings = [Check(guard)];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>Qualified and unqualified targets in the selected database cannot evade ambiguity detection.</summary>
    [Fact]
    public void QualifiedPhysicalAliasDoesNotAcquireCandidate()
    {
        // Arrange
        var operation = Operation();
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation, Operation(schema: "selected")];
        SafeMigrationUnexpectedObject[] findings = [Check(guard)];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>Unsupported operation annotations cannot acquire catalog recovery ownership.</summary>
    [Fact]
    public void AnnotatedEnvelopeDoesNotAcquireCandidate()
    {
        // Arrange
        var operation = Operation();
        operation["Unknown:Ownership"] = true;
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation];
        SafeMigrationUnexpectedObject[] findings = [Check(guard)];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>Duplicated physical inventory identities do not silently discard either reported object.</summary>
    [Fact]
    public void DuplicatePhysicalInventoryDoesNotAcquireCandidate()
    {
        // Arrange
        var operation = Operation();
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation];
        SafeMigrationUnexpectedObject[] findings = [Check(guard), Check(guard, schema: "selected")];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>The server's owner mapping preserves the raw authored contract and exact guard hash.</summary>
    [Fact]
    public void NativeCanonicalOwnerSelectsUntouchedAuthoredOperation()
    {
        // Arrange
        var operation = Operation(table: "MixedItems");
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation];
        SafeMigrationUnexpectedObject[] findings = [Check(guard, table: "mixeditems")];
        Dictionary<string, string?> nativeOwners = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = "mixeditems",
        };

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected", nativeOwners);

        // Assert
        var candidate = Assert.Single(candidates);
        Assert.Same(operation, candidate.Operation);
        Assert.Equal("MixedItems", ((EnsureColumnIntent)candidate.Operation.Intent).Table);
        Assert.Equal(0, candidate.FindingIndex);
    }

    /// <summary>Distinct raw table spellings resolving to one physical column make recovery ambiguous.</summary>
    /// <param name="nullable">Whether the second intent is itself ineligible for staging.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativePhysicalTableAliasDoesNotAcquireCandidate(bool nullable)
    {
        // Arrange
        var operation = Operation(table: "MixedItems");
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation, Operation(nullable: nullable, table: "MIXEDITEMS")];
        SafeMigrationUnexpectedObject[] findings = [Check(guard, table: "mixeditems")];
        Dictionary<string, string?> nativeOwners = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = "mixeditems",
            ["MIXEDITEMS"] = "mixeditems",
        };

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected", nativeOwners);

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>An unresolved or ambiguous server mapping cannot fall back to a guessed table-name comparer.</summary>
    [Fact]
    public void UnresolvedNativeOwnerDoesNotAcquireCandidate()
    {
        // Arrange
        var operation = Operation();
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation];
        SafeMigrationUnexpectedObject[] findings = [Check(guard)];
        Dictionary<string, string?> nativeOwners = new(StringComparer.Ordinal)
        {
            ["items"] = null,
        };

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected", nativeOwners);

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>A native case-sensitive non-alias does not create artificial duplicate-target ambiguity.</summary>
    [Fact]
    public void NativeCaseSensitiveNonAliasPreservesExactCandidate()
    {
        // Arrange
        var operation = Operation(table: "MixedItems");
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations = [operation, Operation(table: "MIXEDITEMS")];
        SafeMigrationUnexpectedObject[] findings = [Check(guard, table: "MixedItems")];
        Dictionary<string, string?> nativeOwners = new(StringComparer.Ordinal)
        {
            ["MixedItems"] = "MixedItems",
            ["MIXEDITEMS"] = null,
        };

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryCandidates(
            operations, findings, "selected", nativeOwners);

        // Assert
        Assert.Same(operation, Assert.Single(candidates).Operation);
    }

    /// <summary>No exact guard-name potential means the native owner phase has no query candidates.</summary>
    [Fact]
    public void LookalikeGuardRequiresNoOwnerResolution()
    {
        // Arrange
        MigrationOperation[] operations = [Operation(table: "MixedItems")];
        SafeMigrationUnexpectedObject[] findings = [Check("doka_sm_add1_nn_not_owned", table: "mixeditems")];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryOwnerCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Empty(candidates.Authored);
        Assert.Empty(candidates.Physical);
    }

    /// <summary>Potential owners include ineligible aliases but exclude unrelated columns and foreign owners.</summary>
    [Fact]
    public void NativeOwnerCandidatesIncludeEveryRelevantPhysicalAlias()
    {
        // Arrange
        var operation = Operation(table: "MixedItems");
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            (EnsureColumnIntent)operation.Intent);
        MigrationOperation[] operations =
        [
            operation,
            Operation(column: "ID", nullable: true, table: "MIXEDITEMS"),
            Operation(column: "other", table: "unrelated"),
            Operation(schema: "foreign_database", table: "foreign_items"),
        ];

        SafeMigrationUnexpectedObject[] findings = [Check(guard, table: "mixeditems")];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryOwnerCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Equal(2, candidates.Authored.Length);
        Assert.Contains(("MixedItems", 0), candidates.Authored);
        Assert.Contains(("MIXEDITEMS", 1), candidates.Authored);
        Assert.Equal("mixeditems", Assert.Single(candidates.Physical));
    }

    /// <summary>Non-atomic CHECK inventories produce no owner work even for a large pending stream.</summary>
    [Fact]
    public void OrdinaryCheckInventoryRequiresNoNativeOwnerWork()
    {
        // Arrange
        var operations = Enumerable.Range(0, 1024)
            .Select(index => Operation(table: "items" + index.ToString(CultureInfo.InvariantCulture))).ToArray();

        var findings = Enumerable.Range(0, 1024)
            .Select(index => Check("user_check_" + index.ToString(CultureInfo.InvariantCulture))).ToArray();

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryOwnerCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Empty(candidates.Authored);
        Assert.Empty(candidates.Physical);
    }

    /// <summary>A valid reserved-name shape remains insufficient without the exact authored fingerprint.</summary>
    [Fact]
    public void ValidReservedShapeWithoutMatchingFingerprintRequiresNoOwnerWork()
    {
        // Arrange
        MigrationOperation[] operations = [Operation()];
        var name = "doka_sm_add1_nn_" + new string('0', 48);
        SafeMigrationUnexpectedObject[] findings = [Check(name)];

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryOwnerCandidates(
            operations, findings, "selected");

        // Assert
        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.IsRequiredColumnAddGuardNameCandidate(name));
        Assert.Empty(candidates.Authored);
        Assert.Empty(candidates.Physical);
    }

    /// <summary>Large physical candidate inventories do not need to be copied into every lookup statement.</summary>
    [Fact]
    public void LargeOwnerCandidateInventoryRetainsIndependentLookupSlots()
    {
        // Arrange
        var operations = Enumerable.Range(0, 1024)
            .Select(index => Operation(table: "items" + index.ToString(CultureInfo.InvariantCulture))).ToArray();

        var findings = operations.Select(operation =>
        {
            var intent = (EnsureColumnIntent)operation.Intent;

            return Check(MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(intent),
                table: intent.Table);
        }).ToArray();

        // Act
        var candidates = MySqlSafeMigrationProviderAnalyzer.FindRequiredColumnAddRecoveryOwnerCandidates(
            operations, findings, "selected");

        // Assert
        Assert.Equal(1024, candidates.Authored.Length);
        Assert.Equal(1024, candidates.Physical.Length);
        Assert.Contains(("items0", 0), candidates.Authored);
        Assert.Contains(("items1023", 1023), candidates.Authored);
    }

    /// <summary>Owner SQL uses one current-database catalog lookup, not a Cartesian inventory parameter list.</summary>
    [Fact]
    public void NativeOwnerQueryUsesIndexedCatalogAndExactNativeFilter()
    {
        // Arrange
        var owners = string.Join(SafeMigrationCatalogQueryLimits.Separator,
            Enumerable.Range(0, SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                .Select(index => $"SELECT {index.ToString(CultureInfo.InvariantCulture)} AS ordinal, "
                    + $"@owner{index.ToString(CultureInfo.InvariantCulture)} AS owner"));

        // Act
        var query = MySqlSafeMigrationProviderAnalyzer.BuildRequiredColumnAddRecoveryOwnerQuery(owners);

        // Assert
        Assert.Equal(2, query.Split("INFORMATION_SCHEMA.TABLES", StringSplitOptions.None).Length);
        Assert.Contains("physical_owner.TABLE_SCHEMA = DATABASE()", query, StringComparison.Ordinal);
        Assert.Contains("physical_owner.TABLE_TYPE = 'BASE TABLE'", query, StringComparison.Ordinal);
        Assert.Contains("physical_owner.TABLE_NAME = source_owner.owner", query, StringComparison.Ordinal);
        Assert.Contains("@@lower_case_table_names = 0", query, StringComparison.Ordinal);
        Assert.Contains("@@lower_case_table_names IN (1, 2)", query, StringComparison.Ordinal);
        Assert.Contains("CAST(physical_owner.TABLE_NAME AS BINARY) = CAST(source_owner.owner AS BINARY)", query,
            StringComparison.Ordinal);
        Assert.Contains(
            "CAST(LOWER(physical_owner.TABLE_NAME) AS BINARY) = CAST(LOWER(source_owner.owner) AS BINARY)", query,
            StringComparison.Ordinal);
        Assert.Contains("COUNT(physical_owner.TABLE_NAME) = 1", query, StringComparison.Ordinal);
        Assert.Contains("THEN MIN(physical_owner.TABLE_NAME) ELSE NULL", query, StringComparison.Ordinal);
        Assert.Contains("ELSE NULL END, COUNT(physical_owner.TABLE_NAME) FROM", query, StringComparison.Ordinal);
        Assert.DoesNotContain("physical_owner.owner", query, StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY source_owner.ordinal ORDER BY 1;", query, StringComparison.Ordinal);
    }

    /// <summary>Creates one no-default required column add with the untouched authored database identity.</summary>
    /// <param name="column">The authored column identifier.</param>
    /// <param name="nullable">Whether to create an ineligible nullable target.</param>
    /// <param name="schema">The optional authored database qualifier.</param>
    /// <param name="table">The untouched authored table identifier.</param>
    /// <returns>The immutable SafeMigrations envelope used by the selector.</returns>
    private static SafeMigrationOperation Operation(
        string column = "id",
        bool nullable = false,
        string? schema = null,
        string table = "items"
    ) => new(new EnsureColumnIntent(table, new ExpectedColumnDefinition(column, typeof(int), nullable, "int"),
            schema), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Creates one reported CHECK identity without claiming anything about its live expression.</summary>
    /// <param name="name">The exact reported constraint identifier.</param>
    /// <param name="schema">The optional physical database qualification.</param>
    /// <param name="table">The exact physical owner returned by the live inventory.</param>
    /// <returns>The reported staging-check candidate requiring a separate live proof.</returns>
    private static SafeMigrationUnexpectedObject Check(string name, string? schema = null, string table = "items") =>
        new(SafeMigrationDatabaseObjectKind.CheckConstraint, schema, table, name, "unexpected_check_constraint");
}
