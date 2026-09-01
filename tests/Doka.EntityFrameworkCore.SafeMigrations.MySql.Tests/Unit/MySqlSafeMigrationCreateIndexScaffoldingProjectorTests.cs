namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlSafeMigrationCreateIndexScaffoldingProjectorTests
{
    private readonly MySqlSafeMigrationCreateIndexScaffoldingProjector _projector = new();

    [Fact]
    public void PrefixMetadata_IsProjectedAndRemovedFromTheGeneratedOperation()
    {
        var operation = CreateIndex(["tenant_id", "slug"]);
        operation["Doka:MySql:IndexPrefixLength"] = new[] { 0, 64, };

        var projection = _projector.Project(operation);

        Assert.Equal([0, 64], projection.PrefixLengths);
        Assert.Empty(projection.Operation.GetAnnotations());
        Assert.Equal(operation.Columns, projection.Operation.Columns);
        Assert.NotSame(operation, projection.Operation);
    }

    [Fact]
    public void OperationWithoutProviderMetadata_IsPreserved()
    {
        var operation = CreateIndex(["slug"]);

        var projection = _projector.Project(operation);

        Assert.Null(projection.PrefixLengths);
        Assert.Same(operation, projection.Operation);
    }

    [Fact]
    public void UnknownMetadataAlongsidePrefixes_FailsClosed()
    {
        var operation = CreateIndex(["slug"]);
        operation["Doka:MySql:IndexPrefixLength"] = new[] { 32, };
        operation["Test:Unknown"] = true;

        var exception = Assert.Throws<InvalidOperationException>(() => _projector.Project(operation));

        Assert.Contains("cannot project", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16, 0)]
    public void MalformedPrefixMetadata_FailsClosed(
        params int[] prefixLengths
    )
    {
        var operation = CreateIndex(["slug"]);
        operation["Doka:MySql:IndexPrefixLength"] = prefixLengths;

        Assert.Throws<InvalidOperationException>(() => _projector.Project(operation));
    }

    private static CreateIndexOperation CreateIndex(
        string[] columns
    ) => new()
    {
        Name = "ix_entities_slug",
        Table = "entities",
        Columns = columns,
    };
}
