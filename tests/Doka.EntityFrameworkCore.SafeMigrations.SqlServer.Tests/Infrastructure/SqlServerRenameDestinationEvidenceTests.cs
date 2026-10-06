namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies independent rename destination evidence without requiring an x64 SQL Server instance.</summary>
public sealed class SqlServerRenameDestinationEvidenceTests
{
    /// <summary>Missing-source classification retains catalog-collation-bound destination occupancy.</summary>
    /// <param name="tableExists">Whether a table occupies the rename destination.</param>
    /// <param name="otherObjectExists">Whether another schema object occupies the destination.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CaptureRenameTargetPresence_UsesExistingGraphBindings(
        bool tableExists,
        bool otherObjectExists
    )
    {
        // Arrange
        var rename = new SafeMigrationOperation(new RenameTableIntent("source", "destination"),
            SafeMigrationPolicy.ThrowIfDifferent);

        SafeMigrationOperation[] operations = [rename];
        SafeMigrationProviderAnalysis[] analyses = [Live(SafeMigrationObservedState.Missing)];
        await using var connection = Connection(tableExists, otherObjectExists);
        var graph = await ReadGraphAsync(connection, operations);
        var commandsBefore = connection.Submissions.Count;

        // Act
        graph.CaptureRenameTargetPresence(operations, analyses);

        // Assert
        Assert.Equal(tableExists || otherObjectExists, analyses[0].RenameTargetExists);
        Assert.Null(analyses[0].RenameIntermediateTargetExists);
        Assert.Equal(SafeMigrationObservedState.Missing, analyses[0].ObservedState);
        Assert.Equal(commandsBefore, connection.Submissions.Count);
        Assert.Equal(2, commandsBefore);
        Assert.Contains("COLLATE CATALOG_DEFAULT", connection.Submissions[1].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("occupied.parent_object_id", connection.Submissions[1].Sql, StringComparison.Ordinal);
    }

    /// <summary>A new source cannot reuse missing-source replay classification to overwrite a live target.</summary>
    /// <param name="tableExists">Whether the live destination is occupied.</param>
    /// <param name="dropTarget">Whether an accepted earlier drop removes the destination.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreatedSourceRename_ProjectsCapturedDestinationOccupancy(
        bool tableExists,
        bool dropTarget
    )
    {
        // Arrange
        var rename = new SafeMigrationOperation(new RenameTableIntent("source", "destination"),
            SafeMigrationPolicy.ThrowIfDifferent);

        SafeMigrationOperation[] operations = [rename];
        SafeMigrationProviderAnalysis[] analyses = [Live(SafeMigrationObservedState.Missing)];
        await using var connection = Connection(tableExists, false);
        var graph = await ReadGraphAsync(connection, operations);
        graph.CaptureRenameTargetPresence(operations, analyses);
        var projection = new SafeMigrationPreflightProjection();
        var source = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("source",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")]), SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        Accept(projection, source, Live(SafeMigrationObservedState.Missing));
        if (dropTarget)
        {
            Accept(projection, new SafeMigrationOperation(new DropTableIntent("destination"),
                SafeMigrationPolicy.ThrowIfDifferent), Live(SafeMigrationObservedState.Matching));
        }

        // Act
        var result = projection.Project(rename, analyses[0]);

        // Assert
        Assert.Equal(tableExists && !dropTarget ? SafeMigrationObservedState.Different
            : SafeMigrationObservedState.Matching, result.ObservedState);
    }

    /// <summary>Unknown graph names are not falsely recorded as proven absent.</summary>
    [Fact]
    public void CaptureRenameTargetPresence_LeavesUncapturedNamesUnknown()
    {
        // Arrange
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        SafeMigrationOperation[] operations =
        [
            new(new RenameTableIntent("source", "destination"), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        SafeMigrationProviderAnalysis[] analyses = [Live(SafeMigrationObservedState.Missing)];

        // Act
        graph.CaptureRenameTargetPresence(operations, analyses);

        // Assert
        Assert.Null(analyses[0].RenameTargetExists);
    }

    /// <summary>The EF rename-before-transfer stage retains its own catalog and ordered-drop evidence.</summary>
    /// <param name="intermediateExists">Whether the source schema already contains the new name.</param>
    /// <param name="dropIntermediate">Whether an accepted preceding drop removes that occupant.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CrossSchemaRename_RequiresUnoccupiedIntermediateName(
        bool intermediateExists,
        bool dropIntermediate
    )
    {
        // Arrange
        var rename = new SafeMigrationOperation(new RenameTableIntent("source", "destination", "dbo", "target"),
            SafeMigrationPolicy.ThrowIfDifferent);

        SafeMigrationOperation[] operations = [rename];
        SafeMigrationProviderAnalysis[] analyses = [Live(SafeMigrationObservedState.Missing)];
        await using var connection = Connection(false, false, intermediateExists, crossSchema: true);
        var graph = await ReadGraphAsync(connection, operations);
        graph.CaptureRenameTargetPresence(operations, analyses);
        var projection = new SafeMigrationPreflightProjection();
        var source = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition("source",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], schema: "dbo"),
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        Accept(projection, source, Live(SafeMigrationObservedState.Missing));
        if (dropIntermediate)
        {
            Accept(projection, new SafeMigrationOperation(new DropTableIntent("destination", "dbo"),
                SafeMigrationPolicy.ThrowIfDifferent), Live(SafeMigrationObservedState.Matching));
        }

        // Act
        var result = projection.Project(rename, analyses[0]);

        // Assert
        Assert.False(analyses[0].RenameTargetExists);
        Assert.Equal(intermediateExists, analyses[0].RenameIntermediateTargetExists);
        Assert.Equal(intermediateExists && !dropIntermediate ? SafeMigrationObservedState.Different
            : SafeMigrationObservedState.Matching, result.ObservedState);
        Assert.Equal(2, connection.Submissions.Count);
    }

    /// <summary>The installed EF provider's two-stage mutation is guarded before either DDL statement.</summary>
    [Fact]
    public void CrossSchemaRename_RuntimeGuardIncludesIntermediateNameBeforeEfRenameAndTransfer()
    {
        // Arrange
        using var context = new SafeMigrationDbContext("Server=localhost;Database=catalog;Integrated Security=true;");
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var operation = new SafeMigrationOperation(new RenameTableIntent("source", "destination", "dbo", "target"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var plan = catalog.Build(operation);
        var runtime = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);
        var identifiers = SqlServerIdentifierContract.Collect([operation]);

        // Assert
        const string intermediate = "o.schema_id = SCHEMA_ID(N'dbo') AND o.name = N'destination'";
        const string finalTarget = "o.schema_id = SCHEMA_ID(N'target') AND o.name = N'destination'";
        Assert.Contains(intermediate, plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains(finalTarget, plan.StateExpression, StringComparison.Ordinal);
        var rename = runtime.IndexOf("sp_rename", StringComparison.Ordinal);
        var transfer = runtime.IndexOf("TRANSFER", StringComparison.Ordinal);
        var guard = runtime.IndexOf(intermediate, StringComparison.Ordinal);

        Assert.True(guard >= 0 && rename > guard && transfer > rename);
        Assert.Contains("[dbo].[source]", runtime, StringComparison.Ordinal);
        Assert.Contains("[target] TRANSFER [dbo].[destination]", runtime, StringComparison.Ordinal);
        Assert.Contains(identifiers, static identifier => identifier.Scope == SqlServerIdentifierScope.SchemaObject
            && identifier.Schema == "dbo" && identifier.Name == "destination");
    }

    private static Task<SqlServerProjectedDependencyGraph> ReadGraphAsync(
        CatalogStatementTestConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations
    ) => SqlServerProjectedDependencyGraph.ReadAsync(connection, null, operations, 71,
        static (_, _) => "1=1", static (_, _) => true, static (_, _) => true, CancellationToken.None);

    private static CatalogStatementTestConnection Connection(
        bool targetExists,
        bool otherObjectExists,
        bool intermediateExists = false,
        bool crossSchema = false
    ) => new(false, (sql, parameters) =>
    {
        Assert.Empty(parameters);
        if (sql.StartsWith("SELECT object_id, parent_object_id", StringComparison.Ordinal))
        {
            return new DataTable { Locale = CultureInfo.InvariantCulture };
        }

        Assert.StartsWith("SELECT requested.schema_name", sql, StringComparison.Ordinal);
        var result = new DataTable { Locale = CultureInfo.InvariantCulture };
        Type[] types = [typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(int),
            typeof(byte), typeof(bool), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(int),
            typeof(int), typeof(int), typeof(int)];

        for (var index = 0; index < types.Length; index++)
        {
            result.Columns.Add($"column{index}", types[index]);
        }

        result.Rows.Add("dbo", "source", DBNull.Value, DBNull.Value, 1, DBNull.Value, DBNull.Value, DBNull.Value,
            DBNull.Value, DBNull.Value, false, false, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        result.Rows.Add(crossSchema ? "target" : "dbo", "destination", DBNull.Value, DBNull.Value, 1,
            targetExists ? 42 : DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, false, false,
            DBNull.Value, DBNull.Value, DBNull.Value, targetExists || otherObjectExists ? 42 : DBNull.Value);
        if (crossSchema)
        {
            Assert.Contains("(N'dbo', N'destination', CONVERT(nvarchar(128), NULL), CONVERT(nvarchar(128), NULL))",
                sql, StringComparison.Ordinal);
            result.Rows.Add("dbo", "destination", DBNull.Value, DBNull.Value, 1,
                intermediateExists ? 43 : DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value,
                false, false, DBNull.Value, DBNull.Value, DBNull.Value, intermediateExists ? 43 : DBNull.Value);
        }

        return result;
    });

    private static void Accept(
        SafeMigrationPreflightProjection projection,
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    ) => projection.Observe(operation, analysis, analysis, SafeMigrationDecisionPlanner.Plan(
        operation.Intent.Kind, analysis.ObservedState, operation.Policy, analysis.RepairCapability));

    private static SafeMigrationProviderAnalysis Live(SafeMigrationObservedState state)
        => new(state, SafeMigrationRepairCapability.None, false, "classified_test");
}
