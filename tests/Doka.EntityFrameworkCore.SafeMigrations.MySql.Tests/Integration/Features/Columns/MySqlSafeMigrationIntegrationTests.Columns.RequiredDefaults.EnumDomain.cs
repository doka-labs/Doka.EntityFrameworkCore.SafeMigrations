namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Exact ENUM and SET domains allow only their owned resume or already matching no-op.</summary>
    /// <param name="useSetStorage">Whether the finite string domain uses SET rather than ENUM.</param>
    /// <param name="alreadyRequired">Whether the exact target is already complete without a recovery guard.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RequiredDefaultEnumDomainExactContractPreservesValues(
        bool useSetStorage,
        bool alreadyRequired
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var operation = RequiredDefaultsEnumDomainOperation(useSetStorage);
        var storeType = RequiredDefaultsEnumDomainStoreType(useSetStorage, "exact");
        await ExecuteSqlAsync(connectionString,
            RequiredDefaultsEnumDomainFixtureSql(operation, storeType, alreadyRequired, "Alpha"));

        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();
        var guardName = AtomicRequiredAddGuardName(operation);

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-domain-exact"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        var inventory = await context.GetService<ISafeMigrationProviderAnalyzer>().FindUnexpectedObjectsAsync(
            context, AtomicRequiredAddOwnerInventoryOperations([operation]), CancellationToken.None);

        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-domain-exact-final"), CancellationToken.None);

        var state = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var storedValue = await ScalarStringAsync(connectionString,
            "SELECT `value` FROM `required_enum_domain_items` WHERE `key` = 1;");

        var physicalType = await ScalarStringAsync(connectionString,
            "SELECT COLUMN_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_enum_domain_items' AND COLUMN_NAME = 'value';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(alreadyRequired ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply,
            Assert.Single(preflight.Assessments).Action);
        Assert.DoesNotContain(inventory,
            item => item.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint && item.Name == guardName);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(new AtomicRequiredAddState(1, 0, 0, 1), state);
        Assert.Equal("Alpha", storedValue);
        Assert.Equal(storeType, physicalType);
    }

    /// <summary>Owned-looking guards cannot certify a case-changed or reordered ENUM or SET value domain.</summary>
    /// <param name="useSetStorage">Whether the finite string domain uses SET rather than ENUM.</param>
    /// <param name="domainDifference">The independently observable literal-case or member-order mismatch.</param>
    [Theory]
    [InlineData(false, "literal-case")]
    [InlineData(false, "member-order")]
    [InlineData(true, "literal-case")]
    [InlineData(true, "member-order")]
    public async Task RequiredDefaultEnumDomainMismatchRetainsGuardAndOriginalValue(
        bool useSetStorage,
        string domainDifference
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var operation = RequiredDefaultsEnumDomainOperation(useSetStorage);
        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var guardName = AtomicRequiredAddGuardName(operation);
        var storeType = RequiredDefaultsEnumDomainStoreType(useSetStorage, domainDifference);
        var originalValue = domainDifference == "literal-case" ? "alpha" : "Alpha";
        await ExecuteSqlAsync(connectionString,
            RequiredDefaultsEnumDomainFixtureSql(operation, storeType, alreadyRequired: false, originalValue));

        await using var context = CreateContext(connectionString);
        var before = await ReadAtomicRequiredAddStateAsync(connectionString, operation);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-enum-domain-mismatch"), CancellationToken.None);

        var inventory = await context.GetService<ISafeMigrationProviderAnalyzer>().FindUnexpectedObjectsAsync(
            context, AtomicRequiredAddOwnerInventoryOperations([operation]), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var after = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var storedValue = await ScalarStringAsync(connectionString,
            "SELECT `value` FROM `required_enum_domain_items` WHERE `key` = 1;");

        var physicalType = await ScalarStringAsync(connectionString,
            "SELECT COLUMN_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'required_enum_domain_items' AND COLUMN_NAME = 'value';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        var assessment = Assert.Single(report.Assessments);
        var typeDifference = Assert.Single(assessment.Differences,
            difference => difference.Facet == "column_store_type");

        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(Assert.IsType<EnsureColumnIntent>(operation.Intent).Definition.StoreType, typeDifference.Expected);
        Assert.Equal(storeType, typeDifference.Actual);
        Assert.NotEqual(typeDifference.Expected, typeDifference.Actual);
        Assert.Contains("doka_sm_different", AssertOwnedGuardFailure(failure).Message,
            StringComparison.Ordinal);
        Assert.Contains(inventory,
            item => item.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint && item.Name == guardName);
        Assert.Equal(new AtomicRequiredAddState(1, 1, 1, 1), before);
        Assert.Equal(before, after);
        Assert.Equal(originalValue, storedValue);
        Assert.Equal(storeType, physicalType);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>Captures a no-default required domain without normalizing its case-sensitive members.</summary>
    /// <param name="useSetStorage">Whether the authored keyword is SET rather than ENUM.</param>
    /// <returns>The immutable target whose uppercase keyword and member values remain authored.</returns>
    private static SafeMigrationOperation RequiredDefaultsEnumDomainOperation(bool useSetStorage)
        => new(new EnsureColumnIntent("required_enum_domain_items",
                new ExpectedColumnDefinition("value", typeof(string), false,
                    (useSetStorage ? "SET" : "ENUM") + "('Alpha','Beta')")),
            SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Defines exact live domains independently from the expected target's authored keyword case.</summary>
    /// <param name="useSetStorage">Whether the finite string domain uses SET rather than ENUM.</param>
    /// <param name="difference">The exact, literal-case, or member-order fixture shape.</param>
    /// <returns>The native lower-keyword type with untouched case-sensitive ordered members.</returns>
    private static string RequiredDefaultsEnumDomainStoreType(bool useSetStorage, string difference)
    {
        var members = difference switch
        {
            "exact" => "('Alpha','Beta')",
            "literal-case" => "('alpha','Beta')",
            "member-order" => "('Beta','Alpha')",
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };

        return (useSetStorage ? "set" : "enum") + members;
    }

    /// <summary>Creates a complete or deliberately staged domain with one explicit anonymous row.</summary>
    /// <param name="operation">The authored target which owns the versioned staging guard name.</param>
    /// <param name="storeType">The independent live ordered member domain.</param>
    /// <param name="alreadyRequired">Whether the owner contains the final required column without its guard.</param>
    /// <param name="storedValue">The explicit existing member that must remain unchanged.</param>
    /// <returns>The bounded fixture DDL and explicit row insertion.</returns>
    private static string RequiredDefaultsEnumDomainFixtureSql(
        SafeMigrationOperation operation,
        string storeType,
        bool alreadyRequired,
        string storedValue
    )
    {
        // WHY: Binary column collation makes literal-case differences visible
        // in stored values as well as the ordered schema domain. An exact guard
        // name alone must not grant ownership of a different domain.
        return "CREATE TABLE `required_enum_domain_items` (`key` int NOT NULL PRIMARY KEY, `value` "
            + storeType + (alreadyRequired ? " NOT NULL" : " NULL, CONSTRAINT "
                + AtomicRequiredAddDelimited(AtomicRequiredAddGuardName(operation)) + " CHECK (`value` IS NOT NULL)")
            + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin; "
            + "INSERT INTO `required_enum_domain_items` (`key`, `value`) VALUES (1, "
            + AtomicRequiredAddLiteral(storedValue) + ");";
    }
}
