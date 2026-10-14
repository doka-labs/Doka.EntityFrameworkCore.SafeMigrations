namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises native candidate key semantics with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationNativeCandidateKeySemanticsIntegrationTests
    : SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>Standalone key matching respects the native column-default equality and sort semantics.</summary>
    /// <param name="kind">The native constraint family being ensured.</param>
    /// <param name="keySql">The persisted native key expression.</param>
    /// <param name="matches">Whether the native key matches a plain authored column key.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsurePrimaryKey, "Id", true)]
    [InlineData(SafeMigrationOperationKind.EnsurePrimaryKey, "Id COLLATE NOCASE", false)]
    [InlineData(SafeMigrationOperationKind.EnsurePrimaryKey, "Id DESC", false)]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint, "Id", true)]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint, "Id COLLATE NOCASE", false)]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint, "Id DESC", false)]
    public async Task NativeCandidateKey_DefaultDefinitionCannotMatchDifferentPhysicalSemantics(
        SafeMigrationOperationKind kind,
        string keySql,
        bool matches)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var keyName = kind == SafeMigrationOperationKind.EnsurePrimaryKey ? "pk_native_parent" : "uq_native_parent";
        var keyKind = kind == SafeMigrationOperationKind.EnsurePrimaryKey ? "PRIMARY KEY" : "UNIQUE";

        // WHY: INTEGER table-level primary keys can alias rowid and ignore
        // apparent key clauses. TEXT forces an observable native key index,
        // so collation and sort counterfactuals test real physical semantics.
        await ExecuteSqlAsync(connection,
            $"CREATE TABLE constraint_parents (Id TEXT NOT NULL, CONSTRAINT {keyName} {keyKind}({keySql})); "
            + "INSERT INTO constraint_parents(Id) VALUES(1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (kind == SafeMigrationOperationKind.EnsurePrimaryKey)
        {
            builder.AddPrimaryKeyIfNotExists(keyName, "constraint_parents", ["Id"]);
        }
        else
        {
            builder.AddUniqueConstraintIfNotExists(keyName, "constraint_parents", ["Id"]);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-native-key-semantics"));

        var rows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_parents WHERE Id = 1;");

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(matches ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(matches ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(matches ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(matches ? "classified_matching" : "classified_different", assessment.AnalysisCode);
        if (matches)
        {
            Assert.Empty(assessment.Differences);
        }
        else
        {
            var difference = Assert.Single(assessment.Differences);

            Assert.Equal(kind == SafeMigrationOperationKind.EnsurePrimaryKey
                ? "primary_key_key_semantics"
                : "unique_key_semantics", difference.Facet);
            Assert.Equal("declared_column_collations_ascending", difference.Expected);
            Assert.Equal("different", difference.Actual);
        }

        Assert.Equal(1, rows);
    }
}
