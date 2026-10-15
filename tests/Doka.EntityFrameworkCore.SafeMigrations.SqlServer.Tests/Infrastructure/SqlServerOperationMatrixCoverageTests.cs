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
            (SafeMigrationOperationKind.EnsureSchema, typeof(SqlServerSafeMigrationBasicIntegrationTests),
                nameof(SqlServerSafeMigrationBasicIntegrationTests.ExplicitSchema_CreatesOnlyQualifiedTableAndReplays)),
            (SafeMigrationOperationKind.DropSchema, typeof(SqlServerSafeMigrationLifecycleIntegrationTests),
                nameof(SqlServerSafeMigrationLifecycleIntegrationTests.DropSchema_DropsOnceAndReplays)),
            (SafeMigrationOperationKind.EnsureTable, typeof(SqlServerSafeMigrationBasicIntegrationTests),
                nameof(SqlServerSafeMigrationBasicIntegrationTests.StrictTable_AppliesReplaysAndPassesPostflight)),
            (SafeMigrationOperationKind.DropTable, typeof(SqlServerSafeMigrationLifecycleIntegrationTests),
                nameof(SqlServerSafeMigrationLifecycleIntegrationTests.DropTable_DropsOnceAndReplays)),
            (SafeMigrationOperationKind.RenameTable, typeof(SqlServerSafeMigrationLifecycleIntegrationTests),
                nameof(SqlServerSafeMigrationLifecycleIntegrationTests.RenameTable_PreservesRowsAndReplays)),
            (SafeMigrationOperationKind.EnsureColumn, typeof(SqlServerSafeMigrationLifecycleIntegrationTests),
                nameof(SqlServerSafeMigrationLifecycleIntegrationTests.AddRequiredColumn_EmptyTableAppliesAndReplays)),
            (SafeMigrationOperationKind.DropColumn, typeof(SqlServerSafeMigrationLifecycleIntegrationTests),
                nameof(SqlServerSafeMigrationLifecycleIntegrationTests.DropColumn_PreservesRemainingRowsAndReplays)),
            (SafeMigrationOperationKind.RenameColumn, typeof(SqlServerSafeMigrationLifecycleIntegrationTests),
                nameof(SqlServerSafeMigrationLifecycleIntegrationTests.RenameColumn_PreservesValueAndReplays)),
            (SafeMigrationOperationKind.AlterColumn, typeof(SqlServerSafeMigrationLifecycleIntegrationTests),
                nameof(SqlServerSafeMigrationLifecycleIntegrationTests.AlterColumn_RejectsDataLossBeforeMutation)),
            (SafeMigrationOperationKind.EnsureIndex, typeof(SqlServerSafeMigrationIncludedIndexesIntegrationTests),
                nameof(SqlServerSafeMigrationIncludedIndexesIntegrationTests
                    .IncludedIndex_AppliesAndReplaysWithPhysicalIncludeFacet)),
            (SafeMigrationOperationKind.DropIndex, typeof(SqlServerSafeMigrationLifecycleIntegrationTests),
                nameof(SqlServerSafeMigrationLifecycleIntegrationTests.DropIndex_DropsOnceAndReplays)),
            (SafeMigrationOperationKind.RenameIndex, typeof(SqlServerSafeMigrationLifecycleIntegrationTests),
                nameof(SqlServerSafeMigrationLifecycleIntegrationTests.RenameIndex_PreservesPhysicalIndexAndReplays)),
            (SafeMigrationOperationKind.EnsurePrimaryKey, typeof(SqlServerSafeMigrationConstraintsIntegrationTests),
                nameof(SqlServerSafeMigrationConstraintsIntegrationTests
                    .AddConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.DropPrimaryKey, typeof(SqlServerSafeMigrationConstraintsIntegrationTests),
                nameof(SqlServerSafeMigrationConstraintsIntegrationTests
                    .DropConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.EnsureUniqueConstraint,
                typeof(SqlServerSafeMigrationConstraintsIntegrationTests),
                nameof(SqlServerSafeMigrationConstraintsIntegrationTests
                    .AddConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.DropUniqueConstraint, typeof(SqlServerSafeMigrationConstraintsIntegrationTests),
                nameof(SqlServerSafeMigrationConstraintsIntegrationTests
                    .DropConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.EnsureCheckConstraint,
                typeof(SqlServerSafeMigrationDefaultsAndChecksIntegrationTests),
                nameof(SqlServerSafeMigrationDefaultsAndChecksIntegrationTests
                    .GeneratedCheckConstraint_AppliesAndReplays)),
            (SafeMigrationOperationKind.DropCheckConstraint, typeof(SqlServerSafeMigrationConstraintsIntegrationTests),
                nameof(SqlServerSafeMigrationConstraintsIntegrationTests
                    .DropConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.EnsureForeignKey, typeof(SqlServerSafeMigrationConstraintsIntegrationTests),
                nameof(SqlServerSafeMigrationConstraintsIntegrationTests
                    .AddConstraints_AppliesAndReplaysEachConstraintFamily)),
            (SafeMigrationOperationKind.DropForeignKey, typeof(SqlServerSafeMigrationConstraintsIntegrationTests),
                nameof(SqlServerSafeMigrationConstraintsIntegrationTests
                    .DropConstraints_AppliesAndReplaysEachConstraintFamily)),
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
