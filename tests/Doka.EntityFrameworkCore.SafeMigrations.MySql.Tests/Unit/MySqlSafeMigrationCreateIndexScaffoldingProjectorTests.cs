namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects native prefix metadata projection without silently dropping unknown annotations.</summary>
public sealed class MySqlSafeMigrationCreateIndexScaffoldingProjectorTests
{
    private readonly MySqlSafeMigrationCreateIndexScaffoldingProjector _projector = new();

    /// <summary>Projects prefix lengths while removing consumed annotations from the provider operation.</summary>
    [Fact]
    public void PrefixMetadata_IsProjectedAndRemovedFromTheGeneratedOperation()
    {
        // Arrange
        var operation = CreateIndex(["tenant_id", "slug"]);
        operation["Doka:MySql:IndexPrefixLength"] = new[] { 0, 64 };

        // Act
        var projection = _projector.Project(operation);

        // Assert
        Assert.Equal([0, 64], projection.PrefixLengths);
        Assert.Empty(projection.Operation.GetAnnotations());
        Assert.Equal(operation.Columns, projection.Operation.Columns);
        Assert.NotSame(operation, projection.Operation);
    }

    /// <summary>Preserves the original operation instance when no provider metadata needs projection.</summary>
    [Fact]
    public void OperationWithoutProviderMetadata_IsPreserved()
    {
        // Arrange
        var operation = CreateIndex(["slug"]);

        // Act
        var projection = _projector.Project(operation);

        // Assert
        Assert.Null(projection.PrefixLengths);
        Assert.Same(operation, projection.Operation);
    }

    /// <summary>Rejects unknown annotations even when otherwise valid prefix metadata is present.</summary>
    [Fact]
    public void UnknownMetadataAlongsidePrefixes_FailsClosed()
    {
        // Arrange
        var operation = CreateIndex(["slug"]);
        operation["Doka:MySql:IndexPrefixLength"] = new[] { 32 };
        operation["Test:Unknown"] = true;

        // Act
        var capturedException = Record.Exception(() => _projector.Project(operation));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(capturedException);
        Assert.Contains("cannot project", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects malformed prefix metadata instead of inventing key-part lengths.</summary>
    /// <param name="prefixLengths">The ordered provider prefix metadata for every index key part.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(16, 0)]
    public void MalformedPrefixMetadata_FailsClosed(
        params int[] prefixLengths
    )
    {
        // Arrange
        var operation = CreateIndex(["slug"]);
        operation["Doka:MySql:IndexPrefixLength"] = prefixLengths;

        // Act
        var capturedException = Record.Exception(() => _projector.Project(operation));

        // Assert
        Assert.IsType<InvalidOperationException>(capturedException);
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
