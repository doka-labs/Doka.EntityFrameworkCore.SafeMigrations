namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationContractFingerprintTests
{
    /// <summary>Binds independently changed table mode and comment into an otherwise identical strict shape.</summary>
    /// <param name="changeMode">Whether convergence mode changes instead of the table comment.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fingerprint_BindsCompleteStrictTableDefinitionAndMode(
        bool changeMode
    )
    {
        // Arrange
        var table = new ExpectedTableDefinition(
            "items",
            [Column(name: "id", clrType: typeof(int), storeType: "integer")],
            schema: "app",
            comment: "canonical",
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"], "app"),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition("uq_items_id", "items", ["id"], "app")],
            checkConstraints: [new ExpectedCheckConstraintDefinition("ck_items_id", "items", "id > 0", "app")],
            foreignKeys: []);

        var changedTable = changeMode
            ? table
            : new ExpectedTableDefinition(
                "items",
                [Column(name: "id", clrType: typeof(int), storeType: "integer")],
                schema: "app",
                comment: "changed",
                primaryKey: table.PrimaryKey,
                uniqueConstraints: table.UniqueConstraints,
                checkConstraints: table.CheckConstraints,
                foreignKeys: table.ForeignKeys);

        var changed = new EnsureTableIntent(changedTable,
            changeMode
                ? SafeMigrationTableMode.ConvergenceContainer
                : SafeMigrationTableMode.StrictDefinition);

        // Act
        var strict = Fingerprint(new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        var actual = Fingerprint(changed);

        // Assert
        Assert.NotEqual(strict, actual);
    }
}
