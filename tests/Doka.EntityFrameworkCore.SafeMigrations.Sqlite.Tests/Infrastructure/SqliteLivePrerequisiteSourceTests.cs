namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Verifies immutable native metadata boundaries independently of Core row projection.</summary>
public sealed class SqliteLivePrerequisiteSourceTests : SqliteIntegrationTestBase
{
    /// <summary>Only an unchanged accepted principal collation may retain an old native key certificate.</summary>
    /// <param name="projectedCollation">The accepted new collation, or null for an untouched native principal.</param>
    /// <param name="expectedProof">Whether the original native key remains structurally valid.</param>
    [Theory]
    [InlineData(null, true)]
    [InlineData("NOCASE", true)]
    [InlineData("BINARY", false)]
    public async Task CapturedKeyCannotApproveProjectedPrincipalCollationChange(
        string? projectedCollation,
        bool expectedProof)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE proof_parent (Id TEXT COLLATE NOCASE NOT NULL, "
            + "CONSTRAINT pk_proof_parent PRIMARY KEY(Id)); "
            + "CREATE TABLE proof_child (Id INTEGER PRIMARY KEY, ParentId INTEGER NULL);");
        var source = new SqliteLivePrerequisiteSource(SqliteSafeMigrationCatalog.Read(connection, transaction: null));
        var intent = new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "fk_proof_child_parent", "proof_child", ["ParentId"], "proof_parent", ["Id"]));
        var columns = new PrincipalColumnSource(projectedCollation);

        var candidates = source.GetLiveForeignKeyCandidateKeys(intent, columns);

        Assert.Equal(expectedProof, candidates is { Count: 1 });
        if (expectedProof)
        {
            var candidate = Assert.Single(
                Assert.IsAssignableFrom<IReadOnlyList<SafeMigrationLiveCandidateKey>>(candidates));
            Assert.Equal(SafeMigrationDatabaseObjectKind.PrimaryKey, candidate.Kind);
            Assert.Equal("pk_proof_parent", candidate.PhysicalName);
        }
    }

    /// <summary>Snapshot presence neither fabricates attached ownership nor reads target-model columns.</summary>
    /// <param name="schema">The requested physical schema identity.</param>
    /// <param name="column">The requested column identity.</param>
    /// <param name="expectedPresence">Whether native main-catalog metadata proves the member.</param>
    [Theory]
    [InlineData(null, "Id", true)]
    [InlineData("MAIN", "id", true)]
    [InlineData("other", "Id", false)]
    [InlineData(null, "FutureColumn", false)]
    public async Task HasLiveColumnRequiresExactMainCatalogPresence(
        string? schema,
        string column,
        bool expectedPresence)
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE proof_parent (Id INTEGER NOT NULL PRIMARY KEY);");
        var source = new SqliteLivePrerequisiteSource(SqliteSafeMigrationCatalog.Read(connection, transaction: null));

        var present = source.HasLiveColumn("proof_parent", schema, column);

        Assert.Equal(expectedPresence, present);
    }

    /// <summary>Supplies only an explicitly accepted principal column, never a target-model surrogate.</summary>
    private sealed class PrincipalColumnSource(string? collation) : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            definition = collation is not null && table == "proof_parent" && column == "Id"
                ? new ExpectedColumnDefinition(
                    "Id", typeof(string), false, storeType: "TEXT",
                    collation: new SafeMigrationCollationIdentifier(collation))
                : null;

            return definition is not null;
        }
    }
}
