namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationContractFingerprintTests
{
    [Fact]
    public void Fingerprint_BindsOperationOrderAndPolicy()
    {
        var first = new SafeMigrationOperation(new EnsureSchemaIntent("app"), SafeMigrationPolicy.ThrowIfDifferent);
        var second = new SafeMigrationOperation(new DropSchemaIntent("legacy"), SafeMigrationPolicy.ThrowIfDifferent);

        Assert.NotEqual(
            SafeMigrationContractFingerprint.Create([first, second]),
            SafeMigrationContractFingerprint.Create([second, first]));
        Assert.NotEqual(
            SafeMigrationContractFingerprint.Create([first]),
            SafeMigrationContractFingerprint.Create(
                [new SafeMigrationOperation(first.Intent, SafeMigrationPolicy.ExistenceOnly),]));
    }

    [Fact]
    public void Fingerprint_BindsEveryIndexAndConstraintField()
    {
        var baselineIndex = Fingerprint(new EnsureIndexIntent(Index()));
        var indexVariants = new SafeMigrationIntent[]
        {
            new EnsureIndexIntent(Index(name: "ix_other")), new EnsureIndexIntent(Index(table: "other")),
            new EnsureIndexIntent(Index(schema: "app")),
            new EnsureIndexIntent(Index(unique: false, nullsDistinct: null)),
            new EnsureIndexIntent(Index(filter: "value IS NOT NULL")),
            new EnsureIndexIntent(Index(includedColumns: ["payload", "other"])),
            new EnsureIndexIntent(Index(method: "hash")), new EnsureIndexIntent(Index(nullsDistinct: false)),
            new EnsureIndexIntent(Index(keys: [new ExpectedIndexKeyDefinition(column: "other")])),
            new EnsureIndexIntent(Index(keys: [new ExpectedIndexKeyDefinition(expression: "lower(value)")])),
            new EnsureIndexIntent(
                Index(
                    keys:
                    [
                        new ExpectedIndexKeyDefinition(
                            column: "value",
                            sortOrder: SafeMigrationIndexSortOrder.Descending)
                    ])),
            new EnsureIndexIntent(Index(keys: [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 8)])),
            new EnsureIndexIntent(
                Index(
                    keys:
                    [
                        new ExpectedIndexKeyDefinition(
                            column: "value",
                            collation: new SafeMigrationCollationIdentifier("binary"))
                    ])),
            new EnsureIndexIntent(
                Index(keys: [new ExpectedIndexKeyDefinition(column: "value", operatorClass: "text_ops")])),
        };

        Assert.All(indexVariants, variant => Assert.NotEqual(baselineIndex, Fingerprint(variant)));

        AssertDifferent(
            new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk", "items", ["id"])),
            new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk", "items", ["tenant_id", "id"])));
        AssertDifferent(
            new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("uq", "items", ["value"])),
            new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("uq_other", "items", ["value"])));
        AssertDifferent(
            new EnsureCheckConstraintIntent(new ExpectedCheckConstraintDefinition("ck", "items", "value > 0")),
            new EnsureCheckConstraintIntent(new ExpectedCheckConstraintDefinition("ck", "items", "value >= 0")));
        AssertDifferent(
            ForeignKey(onDelete: ReferentialAction.NoAction),
            ForeignKey(onDelete: ReferentialAction.Cascade));
        AssertDifferent(
            ForeignKey(onUpdate: ReferentialAction.NoAction),
            ForeignKey(onUpdate: ReferentialAction.Cascade));
        AssertDifferent(ForeignKey(principalTable: "parents"), ForeignKey(principalTable: "other_parents"));
    }

    [Fact]
    public void Fingerprint_BindsProviderOwnedOperationType()
    {
        Assert.NotEqual(
            SafeMigrationContractFingerprint.Create([new SqlOperation { Sql = "SELECT 1" }]),
            SafeMigrationContractFingerprint.Create([new AddColumnOperation()]));
    }
}
