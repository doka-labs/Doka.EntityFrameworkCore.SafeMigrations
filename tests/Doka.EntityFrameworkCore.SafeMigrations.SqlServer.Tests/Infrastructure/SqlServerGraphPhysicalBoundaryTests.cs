namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies graph storage is rejected by the physical boundary before managed-row probes bind.</summary>
public sealed class SqlServerGraphPhysicalBoundaryTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=graph_boundary;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Gates graph node and edge identities before classifier row reads and runtime mutation.</summary>
    [Theory]
    [InlineData("graph_sources")]
    [InlineData("graph_links")]
    public void ManagedDelete_GraphFlagsPrecedeRowClassificationAndMutation(
        string table
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DeleteModelManagedDataFromModel(
            table, ["Id"], ["int"], new object?[,] { { 1 } },
            ["Id", "Value"], ["int", "int"], new object?[,] { { 1, 7 } }, schema: "dbo");

        var operation = (SafeMigrationOperation)builder.Operations[0];

        // Act
        var plan = catalog.Build(operation);
        var classifier = SqlServerSafeMigrationProviderAnalyzer.BuildDelayedCatalogTemplate(plan);
        var runtime = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);

        var gate = plan.PhysicalTableSupportExpression;
        var physicalGuard = runtime.IndexOf("IF COALESCE((" + gate + "), 0) <> 1\nBEGIN\n    THROW 51002",
            StringComparison.Ordinal);

        var stateEvaluation = runtime.IndexOf("DECLARE @doka_state", StringComparison.Ordinal);

        // Assert
        var physicalGate = Assert.IsType<string>(gate);
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.True(plan.RequiresDelayedBinding);
        Assert.Contains("physical.is_node = 1", physicalGate, StringComparison.Ordinal);
        Assert.Contains("physical.is_edge = 1", physicalGate, StringComparison.Ordinal);
        Assert.Contains("N'[dbo].[" + table + "]'", physicalGate, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.edge_constraints", physicalGate, StringComparison.Ordinal);
        Assert.StartsWith("IF COALESCE((" + gate + "), 0) <> 1 ", classifier, StringComparison.Ordinal);
        Assert.Contains("@doka_ordinal, N'unsupported', 0, 0, N'physical_table_unproven'", classifier,
            StringComparison.Ordinal);
        Assert.True(physicalGuard >= 0);
        Assert.True(stateEvaluation > physicalGuard);
    }
}
