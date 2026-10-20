namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPostflightComputedRenameTests
{
    /// <summary>A certified key rename retains complete table and immutable model-managed row postconditions.</summary>
    /// <param name="kind">The model-managed operation whose final row state must be verified.</param>
    [Theory]
    [InlineData("ensure")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task ManagedRowsAndCompleteTableAreRevalidatedAfterCertifiedRename(string kind)
    {
        // Arrange
        var initial = Table("source");
        var final = Table("renamed");
        var originalData = ManagedRenameIntent(kind, "source");
        var expectedData = ManagedRenameIntent(kind, "renamed");
        var analyzer = new FinalCatalogAnalyzer(final) { FinalManagedContracts = [expectedData], };
        var dataOperation = Safe(originalData);
        MigrationOperation[] operations =
        [
            Safe(new EnsureTableIntent(initial, SafeMigrationTableMode.StrictDefinition)),
            dataOperation,
            Safe(new RenameColumnIntent("source", "source_nodes", "renamed")),
        ];

        var fingerprint = SafeMigrationContractFingerprint.Create(operations);

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal([3, 2], analyzer.Batches.Select(static batch => batch.Length));
        var rows = Assert.Single(analyzer.Batches[1].Select(static operation => operation.Intent)
            .OfType<ModelManagedDataIntent>());

        Assert.Equal(["renamed"], rows.KeyColumns);
        Assert.Same(originalData.KeyValues, rows.KeyValues);
        Assert.Equal(fingerprint, SafeMigrationContractFingerprint.Create(operations));
        Assert.Same(originalData, dataOperation.Intent);
        switch (originalData)
        {
            case EnsureModelManagedDataIntent ensure:
                Assert.Same(ensure.Values, Assert.IsType<EnsureModelManagedDataIntent>(rows).Values);
                break;
            case UpdateModelManagedDataIntent update:
                var rewrittenUpdate = Assert.IsType<UpdateModelManagedDataIntent>(rows);
                Assert.Same(update.OldValues, rewrittenUpdate.OldValues);
                Assert.Same(update.NewValues, rewrittenUpdate.NewValues);
                break;
            case DeleteModelManagedDataIntent delete:
                var rewrittenDelete = Assert.IsType<DeleteModelManagedDataIntent>(rows);
                Assert.Same(delete.OldValues, rewrittenDelete.OldValues);
                Assert.Equal(["renamed"], Assert.Single(rewrittenDelete.ForeignKeys).PrincipalColumns);
                Assert.Equal(["owner"], Assert.Single(rewrittenDelete.ForeignKeys).Columns);
                break;
        }
    }

    /// <summary>Fresh row or unrelated table drift cannot be concealed by renamed key metadata.</summary>
    /// <param name="rowDrift">Whether managed values drift rather than an unrelated table default.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedRenameReclassificationKeepsTableAndRowDriftVisible(bool rowDrift)
    {
        // Arrange
        var initial = Table("source");
        var originalData = ManagedRenameIntent("ensure", "source");
        var analyzer = new FinalCatalogAnalyzer(Table("renamed", rowDrift ? null : "default"))
        {
            FinalManagedContracts = [ManagedRenameIntent("ensure", "renamed", rowDrift ? 8 : 7)],
        };

        MigrationOperation[] operations =
        [
            Safe(new EnsureTableIntent(initial, SafeMigrationTableMode.StrictDefinition)),
            Safe(originalData),
            Safe(new RenameColumnIntent("source", "source_nodes", "renamed")),
        ];

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal([3, 2], analyzer.Batches.Select(static batch => batch.Length));
        Assert.Equal(rowDrift, report.Assessments[0].PostconditionSatisfied);
        Assert.Equal(!rowDrift, report.Assessments[1].PostconditionSatisfied);
    }

    /// <summary>Later intentional managed updates retain last-writer identity across a certified key rename.</summary>
    [Fact]
    public async Task ManagedRenameRetainsLaterUpdateSupersessionWithoutSkippingFinalRows()
    {
        // Arrange
        var initial = Table("source");
        var finalUpdate = ManagedRenameIntent("update", "renamed", 8);
        var analyzer = new FinalCatalogAnalyzer(Table("renamed")) { FinalManagedContracts = [finalUpdate], };
        MigrationOperation[] operations =
        [
            Safe(new EnsureTableIntent(initial, SafeMigrationTableMode.StrictDefinition)),
            Safe(ManagedRenameIntent("ensure", "source")),
            Safe(new RenameColumnIntent("source", "source_nodes", "renamed")),
            Safe(finalUpdate),
        ];

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal("postcondition_superseded", report.Assessments[1].Code);
        Assert.Equal("classified_matching", report.Assessments[3].AnalysisCode);
        Assert.True(report.Assessments[3].PostconditionSatisfied);
    }

    /// <summary>Opaque work, changed structure and unknown owner identity cannot authorize row rebinding.</summary>
    /// <param name="boundary">The independently unproven lineage boundary.</param>
    [Theory]
    [InlineData("sql")]
    [InlineData("structure")]
    [InlineData("identity")]
    public async Task ManagedRenameRejectsUnprovenLineage(string boundary)
    {
        // Arrange
        var analyzer = new FinalCatalogAnalyzer(Table("renamed"))
        {
            FinalManagedContracts = [ManagedRenameIntent("ensure", "renamed")],
            HistoricalIdentityProven = boundary != "identity",
        };

        var operations = new List<MigrationOperation>
        {
            Safe(new EnsureTableIntent(Table("source"), SafeMigrationTableMode.StrictDefinition)),
            Safe(ManagedRenameIntent("ensure", "source")),
        };

        if (boundary != "identity")
        {
            operations.Add(boundary == "sql" ? new SqlOperation { Sql = "SELECT 1;", }
                : Safe(new EnsureColumnIntent("source_nodes",
                    new ExpectedColumnDefinition("added", typeof(int), true))));
        }

        operations.Add(Safe(new RenameColumnIntent("source", "source_nodes", "renamed")));

        // Act
        var report = await VerifyAsync(operations, analyzer);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[1].PostconditionSatisfied);
        Assert.Single(analyzer.Batches);
    }

    private static ModelManagedDataIntent ManagedRenameIntent(string kind, string keyColumn, int value = 7)
        => kind switch
        {
            "ensure" => new EnsureModelManagedDataIntent("source_nodes", [keyColumn], ["int"],
                [keyColumn, "payload"], ["int", "int"], new object?[,] { { 42, value } }, null,
                [new ExpectedModelManagedDataUniqueKeyDefinition(["payload"])]),
            "update" => new UpdateModelManagedDataIntent("source_nodes", [keyColumn], ["int"],
                new object?[,] { { 42 } }, ["payload"], ["int"], new object?[,] { { 6 } },
                new object?[,] { { value } }, null, [new ExpectedModelManagedDataUniqueKeyDefinition(["payload"])]),
            "delete" => new DeleteModelManagedDataIntent("source_nodes", [keyColumn], ["int"],
                new object?[,] { { 42 } }, [keyColumn, "payload"], ["int", "int"],
                new object?[,] { { 42, 6 } }, null,
                [new ExpectedModelManagedDataForeignKeyDefinition("dependents", ["owner"], [keyColumn])]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
