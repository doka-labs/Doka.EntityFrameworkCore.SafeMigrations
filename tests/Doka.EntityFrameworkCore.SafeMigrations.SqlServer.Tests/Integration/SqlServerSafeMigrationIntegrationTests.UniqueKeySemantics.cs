namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Rejects duplicate-write suppression while preserving default keys without DDL or row changes.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("index", false)]
    [InlineData("index", true)]
    public async Task UniqueKey_IgnoreDuplicateKeyOnIsDifferentAndOffIsMatching(
        string family,
        bool ignoreDuplicateKey
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var name = family switch
        {
            "primary" => "PK_unique_key_semantics",
            "unique" => "UQ_unique_key_semantics",
            "index" => "IX_unique_key_semantics",
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

        var option = ignoreDuplicateKey ? "ON" : "OFF";
        var keySql = family switch
        {
            "primary" => $"ALTER TABLE dbo.unique_key_semantics ADD CONSTRAINT [{name}] PRIMARY KEY ([Id]) "
                + $"WITH (IGNORE_DUP_KEY = {option});",
            "unique" => $"ALTER TABLE dbo.unique_key_semantics ADD CONSTRAINT [{name}] UNIQUE ([Id]) "
                + $"WITH (IGNORE_DUP_KEY = {option});",
            "index" => $"CREATE UNIQUE INDEX [{name}] ON dbo.unique_key_semantics ([Id]) "
                + $"WITH (IGNORE_DUP_KEY = {option});",
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.unique_key_semantics (Id int NOT NULL); "
            + "INSERT INTO dbo.unique_key_semantics VALUES (1); "
            + "CREATE TABLE dbo.unique_key_ddl_audit (Id int IDENTITY NOT NULL); " + keySql);
        var indexQuery = "SELECT index_id FROM sys.indexes "
            + $"WHERE object_id = OBJECT_ID(N'dbo.unique_key_semantics', N'U') AND name = N'{name}';";

        var originalIndexId = await ScalarIntAsync(connectionString, indexQuery);
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER unique_key_semantics_ddl_audit ON DATABASE FOR ALTER_TABLE, CREATE_INDEX, DROP_INDEX "
            + "AS INSERT INTO dbo.unique_key_ddl_audit DEFAULT VALUES;");
        await using var context = CreateContext(connectionString);
        SafeMigrationIntent intent = family switch
        {
            "primary" => new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition(name, "unique_key_semantics", ["Id"], schema: "dbo")),
            "unique" => new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition(name, "unique_key_semantics", ["Id"], schema: "dbo")),
            "index" => new EnsureIndexIntent(new ExpectedIndexDefinition(name, "unique_key_semantics",
                [new ExpectedIndexKeyDefinition("Id")], schema: "dbo", unique: true)),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var observedIndexId = await ScalarIntAsync(connectionString, indexQuery);
        var physicalKeyCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes "
            + $"WHERE object_id = OBJECT_ID(N'dbo.unique_key_semantics', N'U') AND name = N'{name}' "
            + $"AND is_unique = 1 AND ignore_dup_key = {(ignoreDuplicateKey ? 1 : 0)};");

        var unchangedRowCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.unique_key_semantics WHERE Id = 1;");

        var totalRowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.unique_key_semantics;");
        var ddlEventCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.unique_key_ddl_audit;");

        // Assert
        Assert.Equal(ignoreDuplicateKey ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            Assert.Single(analyses).ObservedState);
        if (ignoreDuplicateKey)
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(exception).Number);
        }
        else
        {
            Assert.Null(exception);
        }

        Assert.True(originalIndexId > 0);
        Assert.Equal(originalIndexId, observedIndexId);
        Assert.Equal(1, physicalKeyCount);
        Assert.Equal(1, unchangedRowCount);
        Assert.Equal(1, totalRowCount);
        Assert.Equal(0, ddlEventCount);
    }
}
