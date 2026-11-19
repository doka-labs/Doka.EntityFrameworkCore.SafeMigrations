namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationContractFingerprintTests
{
    /// <summary>Binds independently changed operation order and policy into the stream contract.</summary>
    /// <param name="changeOrder">Whether order changes instead of the first operation's policy.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fingerprint_BindsOperationOrderAndPolicy(
        bool changeOrder
    )
    {
        // Arrange
        var first = new SafeMigrationOperation(new EnsureSchemaIntent("app"), SafeMigrationPolicy.ThrowIfDifferent);
        var second = new SafeMigrationOperation(new DropSchemaIntent("legacy"), SafeMigrationPolicy.ThrowIfDifferent);
        MigrationOperation[] original = changeOrder
            ? [first, second]
            : [first];

        MigrationOperation[] changed = changeOrder
            ? [second, first]
            : [new SafeMigrationOperation(first.Intent, SafeMigrationPolicy.ExistenceOnly)];

        // Act
        var baseline = SafeMigrationContractFingerprint.Create(original);
        var actual = SafeMigrationContractFingerprint.Create(changed);

        // Assert
        Assert.NotEqual(baseline, actual);
    }

    /// <summary>Binds each independent index facet into the stream contract.</summary>
    /// <param name="variant">The changed index-facet ordinal.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Fingerprint_BindsEveryIndexAndConstraintField(
        int variant
    )
    {
        // Arrange
        var baselineIntent = new EnsureIndexIntent(Index());
        var indexVariants = new SafeMigrationIntent[]
        {
            new EnsureIndexIntent(Index(name: "ix_other")),
            new EnsureIndexIntent(Index(table: "other")),
            new EnsureIndexIntent(Index(schema: "app")),
            new EnsureIndexIntent(Index(unique: false, nullsDistinct: null)),
            new EnsureIndexIntent(Index(filter: "value IS NOT NULL")),
            new EnsureIndexIntent(Index(includedColumns: ["payload", "other"])),
            new EnsureIndexIntent(Index(method: "hash")),
            new EnsureIndexIntent(Index(nullsDistinct: false)),
            new EnsureIndexIntent(Index(keys: [new ExpectedIndexKeyDefinition(column: "other")])),
            new EnsureIndexIntent(Index(keys: [new ExpectedIndexKeyDefinition(expression: "lower(value)")])),
            new EnsureIndexIntent(
                Index(
                    keys:
                    [
                        new ExpectedIndexKeyDefinition(
                            column: "value",
                            sortOrder: SafeMigrationIndexSortOrder.Descending),
                    ])),
            new EnsureIndexIntent(Index(keys: [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 8)])),
            new EnsureIndexIntent(
                Index(
                    keys:
                    [
                        new ExpectedIndexKeyDefinition(
                            column: "value",
                            collation: new SafeMigrationCollationIdentifier("binary")),
                    ])),
            new EnsureIndexIntent(
                Index(keys: [new ExpectedIndexKeyDefinition(column: "value", operatorClass: "text_ops")])),
        };

        // Act
        var baseline = Fingerprint(baselineIntent);
        var actual = Fingerprint(indexVariants[variant]);

        // Assert
        Assert.NotEqual(baseline, actual);
    }

    /// <summary>Binds each independent candidate-key, CHECK and foreign-key facet into the contract.</summary>
    /// <param name="facet">The independently changed constraint facet.</param>
    [Theory]
    [InlineData("primary_members")]
    [InlineData("unique_name")]
    [InlineData("check_expression")]
    [InlineData("foreign_delete")]
    [InlineData("foreign_update")]
    [InlineData("foreign_owner")]
    public void Fingerprint_BindsEachConstraintField(
        string facet
    )
    {
        // Arrange
        (SafeMigrationIntent Original, SafeMigrationIntent Changed) pair = facet switch
        {
            "primary_members" => (
                new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk", "items", ["id"])),
                new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk", "items", ["tenant_id", "id"]))),
            "unique_name" => (
                new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("uq", "items", ["value"])),
                new EnsureUniqueConstraintIntent(
                    new ExpectedUniqueConstraintDefinition("uq_other", "items", ["value"]))),
            "check_expression" => (
                new EnsureCheckConstraintIntent(new ExpectedCheckConstraintDefinition("ck", "items", "value > 0")),
                new EnsureCheckConstraintIntent(new ExpectedCheckConstraintDefinition("ck", "items", "value >= 0"))),
            "foreign_delete" => (
                ForeignKey(onDelete: ReferentialAction.NoAction),
                ForeignKey(onDelete: ReferentialAction.Cascade)),
            "foreign_update" => (
                ForeignKey(onUpdate: ReferentialAction.NoAction),
                ForeignKey(onUpdate: ReferentialAction.Cascade)),
            "foreign_owner" => (ForeignKey(principalTable: "parents"), ForeignKey(principalTable: "other_parents")),
            _ => throw new ArgumentOutOfRangeException(nameof(facet)),
        };

        // Act
        var baseline = Fingerprint(pair.Original);
        var actual = Fingerprint(pair.Changed);

        // Assert
        Assert.NotEqual(baseline, actual);
    }

    /// <summary>
    /// Distinguishes ordinary provider SQL from column DDL even when neither operation has a safe intent wrapper.
    /// </summary>
    [Fact]
    public void Fingerprint_BindsProviderOwnedOperationType()
    {
        // Arrange
        var sql = new SqlOperation { Sql = "SELECT 1" };
        var column = new AddColumnOperation();

        // Act
        var sqlHash = SafeMigrationContractFingerprint.Create([sql]);
        var columnHash = SafeMigrationContractFingerprint.Create([column]);

        // Assert
        Assert.NotEqual(sqlHash, columnHash);
    }

    /// <summary>
    /// Binds differing safe-envelope annotations without changing the underlying schema intent or policy.
    /// </summary>
    [Fact]
    public void Fingerprint_BindsSafeOperationAnnotations()
    {
        // Arrange
        var first = new SafeMigrationOperation(
            new EnsureSchemaIntent("app"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var second = new SafeMigrationOperation(
            new EnsureSchemaIntent("app"),
            SafeMigrationPolicy.ThrowIfDifferent);

        first["Test:Facet"] = "first";
        second["Test:Facet"] = "second";

        // Act
        var firstHash = SafeMigrationContractFingerprint.Create([first]);
        var secondHash = SafeMigrationContractFingerprint.Create([second]);

        // Assert
        Assert.NotEqual(firstHash, secondHash);
    }
}
