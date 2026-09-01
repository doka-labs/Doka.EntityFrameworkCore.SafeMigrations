namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationPostflightProjectionTests
{
    [Fact]
    public void LaterWriterSupersedesAnEarlierPostconditionAcrossProviderOperations()
    {
        const string indexName = "ix_records_tenant_id_code";
        var operations = new MigrationOperation[]
        {
            Safe(new DropIndexIntent(indexName, "records")),
            new AlterTableOperation { Name = "records", Comment = "target" },
            new AlterColumnOperation
            {
                Name = "code",
                Table = "records",
                ClrType = typeof(string),
                ColumnType = "varchar(180)",
                IsNullable = false,
            },
            Safe(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        indexName,
                        "records",
                        [
                            new ExpectedIndexKeyDefinition(column: "tenant_id"),
                            new ExpectedIndexKeyDefinition(column: "code", prefixLength: 48),
                        ]))),
        };

        var projection = new SafeMigrationPostflightProjection(operations);

        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(3));
    }

    [Fact]
    public void SameObjectNameOnAnotherTableDoesNotSupersedeThePostcondition()
    {
        const string indexName = "ix_code";
        var operations = new MigrationOperation[]
        {
            Safe(new DropIndexIntent(indexName, "source_records")),
            Safe(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        indexName,
                        "target_records",
                        [new ExpectedIndexKeyDefinition(column: "code")]))),
        };

        var projection = new SafeMigrationPostflightProjection(operations);

        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    [Fact]
    public void ProviderOperationCannotSupersedeAnEarlierSafePostcondition()
    {
        const string indexName = "ix_records_code";
        var operations = new MigrationOperation[]
        {
            Safe(new DropIndexIntent(indexName, "records")),
            new CreateIndexOperation
            {
                Name = indexName,
                Table = "records",
                Columns = ["code"],
            },
        };

        var projection = new SafeMigrationPostflightProjection(operations);

        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    [Fact]
    public void OnlyTheFinalWriterRemainsAuthoritativeForOneResource()
    {
        var operations = new MigrationOperation[]
        {
            Safe(new EnsureColumnIntent("records", Column("code", "varchar(80)"))),
            Safe(new AlterColumnIntent("records", Column("code", "varchar(120)"))),
            Safe(new DropColumnIntent("code", "records")),
        };

        var projection = new SafeMigrationPostflightProjection(operations);

        Assert.True(projection.IsSuperseded(0));
        Assert.True(projection.IsSuperseded(1));
        Assert.False(projection.IsSuperseded(2));
    }

    private static SafeMigrationOperation Safe(
        SafeMigrationIntent intent
    ) => new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    private static ExpectedColumnDefinition Column(
        string name,
        string storeType
    ) => new(name, typeof(string), isNullable: false, storeType: storeType);
}
