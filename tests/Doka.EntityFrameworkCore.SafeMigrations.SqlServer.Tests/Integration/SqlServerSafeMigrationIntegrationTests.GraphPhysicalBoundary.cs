namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Rejects graph deletes without provider errors or uncaptured cascading edge deletion.</summary>
    /// <param name="cascade">Whether the edge constraint cascades node deletion into connecting edges.</param>
    /// <param name="deleteEdge">Whether the captured row belongs to the edge instead of its source node.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GraphTables_ManagedDeleteRejectsBeforeNodeOrEdgeMutation(
        bool cascade,
        bool deleteEdge
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, GraphPhysicalBoundarySetupSql(cascade));
        await using var context = CreateContext(connectionString);
        var table = deleteEdge ? "graph_links" : "graph_sources";
        var id = deleteEdge ? 3 : 1;
        var value = deleteEdge ? 33 : 11;
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        // WHY: Graph edge constraints are deliberately absent from the
        // ordinary FK snapshot; the independent storage boundary must reject.
        builder.DeleteModelManagedDataFromModel(
            table, ["Id"], ["int"], new object?[,] { { id } },
            ["Id", "Value"], ["int", "int"], new object?[,] { { id, value } },
            schema: "dbo", foreignKeys: []);

        var operations = builder.Operations.Cast<SafeMigrationOperation>().ToArray();
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses;
        await using (await analyzer.AcquireAnalysisScopeAsync(context))
        {
            analyses = await analyzer.AnalyzeAsync(context, operations);
        }

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var nodeTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name IN (N'graph_sources', N'graph_targets') AND is_node = 1;");

        var edgeTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'graph_links' AND is_edge = 1;");

        var edgeConstraints = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.edge_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.graph_links');");

        var ordinaryForeignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(N'dbo.graph_sources');");

        var sourceRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.graph_sources WHERE Id = 1 AND Value = 11;");

        var targetRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.graph_targets WHERE Id = 2 AND Value = 22;");

        var edgeRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.graph_links WHERE Id = 3 AND Value = 33;");

        var connections = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.graph_sources source_node, dbo.graph_links graph_edge, "
            + "dbo.graph_targets target_node WHERE MATCH(source_node-(graph_edge)->target_node) "
            + "AND source_node.Id = 1 AND graph_edge.Id = 3 AND target_node.Id = 2;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("physical_table_unproven", analysis.Code);
        Assert.True(analysis.IsInvariantUnsupported);
        Assert.False(analysis.PostconditionSatisfied);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
        Assert.Null(analysis.ModelManagedDataEvidence);

        var sqlFailure = Assert.IsType<SqlException>(failure);
        Assert.Equal(51002, sqlFailure.Number);
        Assert.Contains("doka_sm_unsupported", sqlFailure.Message, StringComparison.Ordinal);

        Assert.Equal(2, nodeTables);
        Assert.Equal(1, edgeTables);
        Assert.Equal(1, edgeConstraints);
        Assert.Equal(0, ordinaryForeignKeys);
        Assert.Equal(1, sourceRows);
        Assert.Equal(1, targetRows);
        Assert.Equal(1, edgeRows);
        Assert.Equal(1, connections);
    }

    private static string GraphPhysicalBoundarySetupSql(
        bool cascade
    )
        => "CREATE TABLE dbo.graph_sources (Id int NOT NULL PRIMARY KEY, Value int NOT NULL) AS NODE; "
            + "CREATE TABLE dbo.graph_targets (Id int NOT NULL PRIMARY KEY, Value int NOT NULL) AS NODE; "
            + "CREATE TABLE dbo.graph_links (Id int NOT NULL PRIMARY KEY, Value int NOT NULL, "
            + "CONSTRAINT EC_graph_links CONNECTION (dbo.graph_sources TO dbo.graph_targets) ON DELETE "
            + (cascade ? "CASCADE" : "NO ACTION") + ") AS EDGE; "
            + "INSERT dbo.graph_sources (Id, Value) VALUES (1, 11); "
            + "INSERT dbo.graph_targets (Id, Value) VALUES (2, 22); "
            + "INSERT dbo.graph_links ($from_id, $to_id, Id, Value) "
            + "SELECT source_node.$node_id, target_node.$node_id, 3, 33 "
            + "FROM dbo.graph_sources source_node CROSS JOIN dbo.graph_targets target_node "
            + "WHERE source_node.Id = 1 AND target_node.Id = 2;";
}
