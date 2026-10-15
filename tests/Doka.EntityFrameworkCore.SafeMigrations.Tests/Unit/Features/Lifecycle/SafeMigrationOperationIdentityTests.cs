namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Guards identifier coverage, immutable collection reuse and diagnostic payload isolation.</summary>
public sealed class SafeMigrationOperationIdentityTests
{
    /// <summary>Every closed intent family supplies a truthful authored owner and unchanged object name.</summary>
    /// <param name="intent">The synthetic safe intent.</param>
    /// <param name="expectedTable">The expected owner, or null for schema operations.</param>
    [Theory]
    [MemberData(nameof(AllIntents))]
    public void EveryIntentFamilyPreservesAuthoredOwner(SafeMigrationIntent intent, string? expectedTable)
    {
        // Arrange
        var expectedSchema = expectedTable is null ? "application" : "authored";

        // Act
        var identity = SafeMigrationOperationIdentity.Create(intent);

        // Assert
        Assert.Equal(intent.Kind, identity.OperationKind);
        Assert.Equal(intent.ObjectName, identity.ObjectName);
        Assert.Equal(expectedTable, identity.Table);
        Assert.Equal(expectedSchema, identity.Schema);
    }

    /// <summary>Provides every closed intent family without depending on production models.</summary>
    /// <returns>Synthetic intents and their expected table owner.</returns>
    public static IEnumerable<object?[]> AllIntents()
    {
        var column = new ExpectedColumnDefinition("Id", typeof(int), isNullable: false, storeType: "int");
        var table = new ExpectedTableDefinition("Items", [column], schema: "authored");
        var index = new ExpectedIndexDefinition("IX_Items_Id", "Items",
            [new ExpectedIndexKeyDefinition(column: "Id")], schema: "authored");

        var primary = new ExpectedPrimaryKeyDefinition("PK_Items", "Items", ["Id"], "authored");
        var unique = new ExpectedUniqueConstraintDefinition("UQ_Items", "Items", ["Id"], "authored");
        var check = new ExpectedCheckConstraintDefinition("CK_Items", "Items", "Id > 0", "authored");
        var foreign = new ExpectedForeignKeyDefinition("FK_Items_Parents", "Items", ["Id"],
            "Parents", ["Id"], "authored", "references");

        var intents = new SafeMigrationIntent[]
        {
            new EnsureSchemaIntent("application"),
            new DropSchemaIntent("application"),
            new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            new DropTableIntent("Items", "authored"),
            new RenameTableIntent("Items", "NewItems", "authored", "target"),
            new EnsureColumnIntent("Items", column, "authored"),
            new DropColumnIntent("Id", "Items", "authored"),
            new RenameColumnIntent("Id", "Items", "NewId", "authored"),
            new AlterColumnIntent("Items", column, schema: "authored"),
            new EnsureIndexIntent(index),
            new DropIndexIntent("IX_Items_Id", "Items", "authored"),
            new RenameIndexIntent("IX_Items_Id", "Items", "IX_New", "authored"),
            new EnsurePrimaryKeyIntent(primary),
            new DropPrimaryKeyIntent("PK_Items", "Items", "authored"),
            new EnsureUniqueConstraintIntent(unique),
            new DropUniqueConstraintIntent("UQ_Items", "Items", "authored"),
            new EnsureCheckConstraintIntent(check),
            new DropCheckConstraintIntent("CK_Items", "Items", "authored"),
            new EnsureForeignKeyIntent(foreign),
            new DropForeignKeyIntent("FK_Items_Parents", "Items", "authored"),
            new EnsureModelManagedDataIntent("Items", ["Id"], ["int"], ["Id", "Value"],
                ["int", "text"], new object?[,] { { 1, "private-value" } }, "authored", null),
            new UpdateModelManagedDataIntent("Items", ["Id"], ["int"], new object?[,] { { 1 } },
                ["Value"], ["text"], new object?[,] { { "private-old" } },
                new object?[,] { { "private-new" } }, "authored", null),
            new DeleteModelManagedDataIntent("Items", ["Id"], ["int"], new object?[,] { { 1 } },
                ["Id", "Value"], ["int", "text"], new object?[,] { { 1, "private-value" } }, "authored", null),
        };

        foreach (var intent in intents)
        {
            yield return [intent, intent is EnsureSchemaIntent or DropSchemaIntent ? null : "Items"];
        }

    }

    /// <summary>The family coverage cannot silently remain green when a new operation kind is added.</summary>
    [Fact]
    public void IntentCasesCoverTheCompleteClosedOperationSet()
    {
        // Arrange
        var expected = Enum.GetValues<SafeMigrationOperationKind>();

        // Act
        var covered = AllIntents().Select(row => ((SafeMigrationIntent)row[0]!).Kind).Order().ToArray();

        // Assert
        Assert.Equal(expected, covered);
    }

    /// <summary>Both sides of a composite foreign key retain their exact declared order and qualifiers.</summary>
    [Fact]
    public void CompositeForeignKeyRetainsBothEndpointsWithoutRecloningColumns()
    {
        // Arrange
        var definition = new ExpectedForeignKeyDefinition("FK_Children_Parents", "Children", ["TenantId", "ParentId"],
            "Parents", ["TenantId", "Id"], schema: "child", principalSchema: "parent");

        // Act
        var identity = SafeMigrationOperationIdentity.Create(new EnsureForeignKeyIntent(definition));

        // Assert
        Assert.Equal("Children", identity.Table);
        Assert.Equal("parent", identity.PrincipalSchema);
        Assert.Equal("Parents", identity.PrincipalTable);
        Assert.Equal(["TenantId", "ParentId"], identity.Columns);
        Assert.Equal(["TenantId", "Id"], identity.PrincipalColumns);
        Assert.Same(definition.Columns, identity.Columns);
        Assert.Same(definition.PrincipalColumns, identity.PrincipalColumns);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)identity.Columns)[0] = "Changed");
    }

    /// <summary>A qualifier-only rename retains the known name without resolving an unqualified database.</summary>
    [Fact]
    public void QualifierOnlyRenameRetainsAuthoredSourceAndTarget()
    {
        // Arrange
        var intent = new RenameTableIntent("Items", newSchema: "target");

        // Act
        var identity = SafeMigrationOperationIdentity.Create(intent);

        // Assert
        Assert.Null(identity.Schema);
        Assert.Equal("Items", identity.Table);
        Assert.Equal("target", identity.NewSchema);
        Assert.Equal("Items", identity.NewTable);
        Assert.Null(identity.NewObjectName);
    }

    /// <summary>Captures named index columns without treating functional expressions as diagnostic values.</summary>
    [Fact]
    public void FunctionalIndexPayloadIsNotCapturedOrRendered()
    {
        // Arrange
        const string sensitiveSql = "private_function('private-sentinel')";
        var definition = new ExpectedIndexDefinition("IX_Items", "Items",
            [new ExpectedIndexKeyDefinition(column: "Id"), new ExpectedIndexKeyDefinition(expression: sensitiveSql)]);

        // Act
        var identity = SafeMigrationOperationIdentity.Create(new EnsureIndexIntent(definition));
        var label = SafeMigrationDiagnosticMessages.FormatIdentity(identity);

        // Assert
        Assert.Equal(["Id"], identity.Columns);
        Assert.Null(identity.Schema);
        Assert.Contains("<unqualified>.[Items]", label, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveSql, label, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sentinel", label, StringComparison.Ordinal);
    }

    /// <summary>Null intent input is rejected at the structural factory boundary.</summary>
    [Fact]
    public void NullIntentIsRejected()
    {
        // Arrange
        SafeMigrationIntent? intent = null;

        // Act
        var exception = Assert.Throws<ArgumentNullException>(() => SafeMigrationOperationIdentity.Create(intent!));

        // Assert
        Assert.Equal("intent", exception.ParamName);
    }

    /// <summary>The production one-column view supports both enumeration contracts without mutation.</summary>
    [Fact]
    public void SingleColumnIdentityRemainsImmutableAndEnumerable()
    {
        // Arrange
        var intent = new EnsureColumnIntent("Items", new ExpectedColumnDefinition("Id", typeof(int), false));

        // Act
        var identity = SafeMigrationOperationIdentity.Create(intent);
        var generic = identity.Columns.ToArray();
        var nonGeneric = ((System.Collections.IEnumerable)identity.Columns).Cast<string>().ToArray();

        // Assert
        Assert.Equal("Id", Assert.Single(identity.Columns));
        Assert.Equal("Id", identity.Columns[0]);
        Assert.Equal("Id", Assert.Single(generic));
        Assert.Equal(generic, nonGeneric);
        Assert.False(identity.Columns is IList<string>);
        Assert.False(identity.Columns is string[]);
    }

    /// <summary>The production one-element collection rejects every out-of-range ordinal.</summary>
    /// <param name="index">The invalid requested element ordinal.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void SingleColumnIdentityRejectsInvalidOrdinals(int index)
    {
        // Arrange
        var identity = SafeMigrationOperationIdentity.Create(new DropColumnIntent("Id", "Items"));

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => identity.Columns[index]);

        // Assert
        Assert.Equal("index", exception.ParamName);
    }
}
