namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Modeled FK columns use member identity without broadening owners, schemas or ordinals.</summary>
    /// <param name="ignoreColumnCase">Whether the provider treats case variants as the same column member.</param>
    /// <param name="modeledOwner">The source-frozen dependent owner name.</param>
    /// <param name="modeledSchema">The source-frozen dependent schema.</param>
    /// <param name="reverseOrder">Whether the captured dependency reverses its ordinal pairs.</param>
    /// <param name="isModeled">Whether the dependency should match the accepted physical FK.</param>
    [Theory]
    [InlineData(true, "children", null, false, true)]
    [InlineData(false, "children", null, false, false)]
    [InlineData(true, "CHILDREN", null, false, false)]
    [InlineData(true, "children", "archive", false, false)]
    [InlineData(true, "children", null, true, false)]
    public void ModelManagedIncomingForeignKeyUsesColumnOnlyIdentity(
        bool ignoreColumnCase,
        string modeledOwner,
        string? modeledSchema,
        bool reverseOrder,
        bool isModeled
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ForeignKeyMemberIdentityNormalizer(ignoreColumnCase));
        var foreignKey = new ExpectedForeignKeyDefinition("fk_children_parents", "children",
            ["parent_id", "parent_tenant"], "parents", ["id", "tenant"]);

        ObserveAccepted(projection, new EnsureForeignKeyIntent(foreignKey), SafeMigrationObservedState.Missing);

        var modeled = new ExpectedModelManagedDataForeignKeyDefinition(modeledOwner,
            reverseOrder ? ["PARENT_TENANT", "PARENT_ID"] : ["PARENT_ID", "PARENT_TENANT"],
            reverseOrder ? ["TENANT", "ID"] : ["ID", "TENANT"], modeledSchema);
        var deletion = new SafeMigrationOperation(new DeleteModelManagedDataIntent("parents", ["ID"], ["int"],
                new object?[,] { { 1 } }, ["ID", "TENANT"], ["int", "int"], new object?[,] { { 1, 2 } },
                schema: null, foreignKeys: [modeled]),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.TransitionReady);

        // Act
        var analysis = projection.Project(deletion, live);

        // Assert
        Assert.Equal(isModeled ? SafeMigrationObservedState.TransitionReady : SafeMigrationObservedState.Unsupported,
            analysis.ObservedState);

        if (isModeled)
        {
            Assert.Same(live, analysis);
        }
        else
        {
            Assert.Equal("projected_model_managed_dependency_unmodeled", analysis.Code);
        }
    }
}
