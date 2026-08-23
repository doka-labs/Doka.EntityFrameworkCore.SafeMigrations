namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationContractFingerprintTests
{
    [Fact]
    public void Fingerprint_BindsCompleteStrictTableDefinitionAndMode()
    {
        var table = new ExpectedTableDefinition(
            "items",
            [Column(name: "id", clrType: typeof(int), storeType: "integer")],
            schema: "app",
            comment: "canonical",
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"], "app"),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition("uq_items_id", "items", ["id"], "app"),],
            checkConstraints: [new ExpectedCheckConstraintDefinition("ck_items_id", "items", "id > 0", "app"),],
            foreignKeys: []);

        var strict = Fingerprint(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));

        Assert.NotEqual(strict, Fingerprint(new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer)));
        Assert.NotEqual(
            strict,
            Fingerprint(
                new EnsureTableIntent(
                    new ExpectedTableDefinition(
                        "items",
                        [Column(name: "id", clrType: typeof(int), storeType: "integer")],
                        schema: "app",
                        comment: "changed",
                        primaryKey: table.PrimaryKey,
                        uniqueConstraints: table.UniqueConstraints,
                        checkConstraints: table.CheckConstraints,
                        foreignKeys: table.ForeignKeys),
                    SafeMigrationTableMode.StrictDefinition)));
    }
}
