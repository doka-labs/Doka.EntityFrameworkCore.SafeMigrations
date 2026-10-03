namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Pins native and sequential dispatch bounds independently of unmeasured engine performance.
/// </summary>
public sealed class SqlServerCatalogQueryLimitsTests
{
    /// <summary>The engine shape keeps the shared operations-per-batch budget.</summary>
    /// <remarks>
    /// WHY: The classifier capacity remains stable when native statement width changes.
    /// Parameter and payload limits can still split any otherwise full group.
    /// </remarks>
    [Fact]
    public void CaptureShape_KeepsTheSharedOperationsPerBatchBudget()
    {
        // Arrange
        const int sharedOperationsPerBatch = SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
            * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch;

        // Act
        var operationsPerBatch = SqlServerCatalogQueryLimits.MaximumOperationsPerBatch;

        // Assert
        Assert.Equal(sharedOperationsPerBatch, operationsPerBatch);
    }

    /// <summary>The engine statement stays narrower than the provider-neutral default.</summary>
    [Fact]
    public void CaptureShape_UsesNarrowerStatementsThanTheSharedDefault()
    {
        // Arrange
        const int shared = SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;

        // Act
        var sqlServer = SqlServerCatalogQueryLimits.MaximumOperationsPerStatement;

        // Assert
        Assert.InRange(sqlServer, 1, shared - 1);
    }

    /// <summary>A capture never needs more batches than the plan-capture bound allows.</summary>
    [Fact]
    public void CaptureShape_CoversAFullPlanCaptureWithBoundedBatches()
    {
        // Arrange
        const int planCapture = SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture;

        // Act
        var batches = (planCapture + SqlServerCatalogQueryLimits.MaximumOperationsPerBatch - 1)
            / SqlServerCatalogQueryLimits.MaximumOperationsPerBatch;

        // Assert
        Assert.InRange(batches, 1, 2);
    }

    /// <summary>Sequential connections retain the shared width and classifier capacity.</summary>
    [Fact]
    public void SequentialCaptureShape_KeepsSharedStatementAndBatchBounds()
    {
        // Arrange
        const int sharedWidth = SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;
        const int sharedStatements = SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch;

        // Act
        var width = SqlServerCatalogQueryLimits.MaximumSequentialOperationsPerStatement;
        var statements = SqlServerCatalogQueryLimits.MaximumSequentialStatementsPerBatch;

        // Assert
        Assert.Equal(sharedWidth, width);
        Assert.Equal(sharedStatements, statements);
        Assert.Equal(SqlServerCatalogQueryLimits.MaximumOperationsPerBatch, width * statements);
    }
}
