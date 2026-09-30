using System.Reflection;

namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Keeps every Core intent tied to a live SQL Server apply/replay or fail-closed regression.
/// </summary>
public sealed class SqlServerOperationMatrixCoverageTests
{
    /// <summary>
    /// Rejects missing, duplicate, or non-live entries in the provider qualification matrix.
    /// </summary>
    [Fact]
    public void EveryCoreIntent_HasALiveBehaviorContract()
    {
        // Arrange
        (SafeMigrationOperationKind Kind, Type TestClass, string TestMethod)[] contracts =
        [
            (SafeMigrationOperationKind.EnsureSchema, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.ExplicitSchema_CreatesOnlyQualifiedTableAndReplays)),
            (SafeMigrationOperationKind.DropSchema, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.DropSchema_DropsOnceAndReplays)),
            (SafeMigrationOperationKind.EnsureTable, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.StrictTable_AppliesReplaysAndPassesPostflight)),
            (SafeMigrationOperationKind.DropTable, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.DropTable_DropsOnceAndReplays)),
            (SafeMigrationOperationKind.RenameTable, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.RenameTable_PreservesRowsAndReplays)),
            (SafeMigrationOperationKind.EnsureColumn, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.AddRequiredColumn_EmptyTableAppliesAndReplays)),
            (SafeMigrationOperationKind.DropColumn, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.DropColumn_PreservesRemainingRowsAndReplays)),
            (SafeMigrationOperationKind.RenameColumn, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.RenameColumn_PreservesValueAndReplays)),
            (SafeMigrationOperationKind.AlterColumn, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.AlterColumn_RejectsDataLossBeforeMutation)),
            (SafeMigrationOperationKind.EnsureIndex, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.IncludedIndex_AppliesAndReplaysWithPhysicalIncludeFacet)),
            (SafeMigrationOperationKind.DropIndex, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.DropIndex_DropsOnceAndReplays)),
            (SafeMigrationOperationKind.RenameIndex, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.RenameIndex_PreservesPhysicalIndexAndReplays)),
            (SafeMigrationOperationKind.EnsurePrimaryKey, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.AddConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.DropPrimaryKey, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.DropConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.EnsureUniqueConstraint, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.AddConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.DropUniqueConstraint, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.DropConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.EnsureCheckConstraint, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.GeneratedCheckConstraint_AppliesAndReplays)),
            (SafeMigrationOperationKind.DropCheckConstraint, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.DropConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.EnsureForeignKey, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.AddConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.DropForeignKey, typeof(SqlServerSafeMigrationIntegrationTests),
                nameof(SqlServerSafeMigrationIntegrationTests.DropConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.EnsureModelManagedData, typeof(SqlServerModelManagedDataIntegrationTests),
                nameof(SqlServerModelManagedDataIntegrationTests
                    .IdentitySeed_AppliesAndReplaysWithoutLeakingIdentityInsert)),
            (SafeMigrationOperationKind.UpdateModelManagedData, typeof(SqlServerModelManagedDataIntegrationTests),
                nameof(SqlServerModelManagedDataIntegrationTests.ManagedUpdate_AppliesAndReplays)),
            (SafeMigrationOperationKind.DeleteModelManagedData, typeof(SqlServerModelManagedDataIntegrationTests),
                nameof(SqlServerModelManagedDataIntegrationTests.ManagedDelete_AppliesAndReplays)),
        ];

        // Act
        var declaredKinds = Enum.GetValues<SafeMigrationOperationKind>().Order().ToArray();
        var coveredKinds = contracts.Select(contract => contract.Kind).Order().ToArray();
        var unqualifiedContracts = contracts
            .Where(contract => contract.TestClass.GetMethod(
                    contract.TestMethod, BindingFlags.Instance | BindingFlags.Public)?
                .GetCustomAttribute<SqlServerLiveFactAttribute>() is null)
            .Select(contract => contract.Kind)
            .ToArray();

        // Assert
        Assert.Equal(declaredKinds, coveredKinds);
        Assert.Empty(unqualifiedContracts);
    }
}
